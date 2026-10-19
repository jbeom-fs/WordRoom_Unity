using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using WordRoom.Data.Documents;

namespace WordRoom.Data
{
    public sealed class LoadOptions
    {
        /// <summary>SHA-256 of every file against the manifest (detects damage and files mixed from other exports).</summary>
        public bool VerifyHashes = true;
        /// <summary>Allowed deviation of unit-vector norms from 1 (Python verify_vocabulary uses 1e-3).</summary>
        public double NormTolerance = 1e-3;
    }

    public readonly struct LoadProgress
    {
        public readonly string Stage;
        public readonly float Fraction;

        public LoadProgress(string stage, float fraction)
        {
            Stage = stage;
            Fraction = fraction;
        }
    }

    /// <summary>
    /// Loads and validates a package. Runs on any thread (no scene or GameObject access; JsonUtility.FromJson is
    /// thread-safe). Every check failure throws WordRoomDataException with the file and the reason.
    /// </summary>
    public static class WordRoomDataLoader
    {
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static WordRoomDataset Load(IPackageFileSource source, LoadOptions options = null, IProgress<LoadProgress> progress = null,
            CancellationToken cancel = default)
        {
            options = options ?? new LoadOptions();
            if (!BitConverter.IsLittleEndian)
                throw new WordRoomDataException(DataErrorCode.UnsupportedPlatform, null, "little-endian 플랫폼만 지원합니다.");
            var report = new LoadReport { HashesVerified = options.VerifyHashes, Source = source.Description, ManagedMemoryBefore = GC.GetTotalMemory(false) };
            var total = Stopwatch.StartNew();
            var stage = Stopwatch.StartNew();
            var ctx = new Context(source, options, progress, cancel, report);

            void Stage(string name)
            {
                report.StageSeconds.Add(new KeyValuePair<string, double>(name, stage.Elapsed.TotalSeconds));
                stage.Restart();
            }

            ctx.Report("manifest", 0f);
            var manifest = ReadManifest(ctx);
            var counts = manifest.counts;
            var data = new WordRoomDataset { Manifest = manifest, PackageId = manifest.packageId };
            Stage("manifest");

            var wordsDoc = ctx.Json<WordsDocument>(PackageFormat.Words, d => d.packageId);
            var sensesDoc = ctx.Json<SensesDocument>(PackageFormat.Senses, d => d.packageId);
            BuildWordsAndSenses(data, wordsDoc, sensesDoc, counts);
            Stage("words+senses");

            data.e5 = ctx.Matrix(PackageFormat.E5Vectors, PackageFormat.RowKeySense, counts.senses, counts.e5Dimensions, out data.e5Dim);
            Stage("e5");
            data.fastText = ctx.Matrix(PackageFormat.FastTextVectors, PackageFormat.RowKeyWord, counts.words, counts.fasttextDimensions, out data.fastTextDim);
            Stage("fasttext");
            ValidateVectors(data, wordsDoc, counts, options.NormTolerance);
            Stage("vector checks");

            var relDoc = ctx.Json<RelationsDocument>(PackageFormat.RelationsMeta, d => d.packageId);
            var records = ctx.Relations(relDoc, counts.relationRecords, data.SenseCount);
            data.RelationInfo = relDoc;
            data.Relations = new RelationIndex(data, records, relDoc.statusNames, relDoc.answerNames, relDoc.methodNames,
                relDoc.createdAtValues, relDoc.approvedStatusName);
            Stage("relations");

            var restrictedDoc = ctx.Json<RestrictedDocument>(PackageFormat.Restricted, d => d.packageId);
            BuildRestricted(data, restrictedDoc);
            var settingsDoc = ctx.Json<SettingsDocument>(PackageFormat.Settings, d => d.packageId);
            BuildSettings(data, settingsDoc, relDoc);
            Stage("restricted+settings");

            report.TotalSeconds = total.Elapsed.TotalSeconds;
            report.ManagedMemoryAfter = GC.GetTotalMemory(false);
            data.LoadReport = report;
            ctx.Report("완료", 1f);
            return data;
        }

        static ManifestDocument ReadManifest(Context ctx)
        {
            var src = ctx.Source;
            if (!src.Exists(PackageFormat.Manifest))
                throw new WordRoomDataException(DataErrorCode.MissingFile, PackageFormat.Manifest, $"데이터 패키지가 없습니다: {src.Description}");
            ManifestDocument m;
            try
            {
                m = JsonUtility.FromJson<ManifestDocument>(ctx.Text(PackageFormat.Manifest, null));
            }
            catch (WordRoomDataException) { throw; }
            catch (Exception e)
            {
                throw new WordRoomDataException(DataErrorCode.InvalidFormat, PackageFormat.Manifest, "JSON을 읽을 수 없습니다.", e);
            }
            if (m == null || m.format != PackageFormat.FormatName)
                throw new WordRoomDataException(DataErrorCode.InvalidFormat, PackageFormat.Manifest, $"형식 식별자가 {PackageFormat.FormatName}이 아닙니다: {m?.format}");
            if (m.schemaVersion != PackageFormat.SupportedSchema)
                throw new WordRoomDataException(DataErrorCode.UnsupportedSchema, PackageFormat.Manifest,
                    $"스키마 {m.schemaVersion}은 지원하지 않습니다 (지원: {PackageFormat.SupportedSchema}). 내보내기 도구와 Unity 로더 버전을 맞추세요.");
            if (m.packageId == null || m.packageId.Length != 64)
                throw new WordRoomDataException(DataErrorCode.InvalidFormat, PackageFormat.Manifest, "packageId가 없습니다.");
            if (m.identity == null || m.counts == null || m.files == null)
                throw new WordRoomDataException(DataErrorCode.InvalidFormat, PackageFormat.Manifest, "identity/counts/files 항목이 없습니다.");
            foreach (var name in PackageFormat.RequiredFiles)
            {
                var found = 0;
                foreach (var f in m.files) if (f.name == name) found++;
                if (found != 1)
                    throw new WordRoomDataException(DataErrorCode.InvalidFormat, PackageFormat.Manifest, $"파일 목록에 {name}이 {found}번 있습니다.");
            }
            ctx.Manifest = m;
            long all = 0;
            foreach (var f in m.files)
            {
                if (!src.Exists(f.name))
                    throw new WordRoomDataException(DataErrorCode.MissingFile, f.name, "파일이 없습니다.");
                var length = src.GetLength(f.name);
                if (length != f.bytes)
                    throw new WordRoomDataException(DataErrorCode.SizeMismatch, f.name, $"크기 {length:N0}바이트, manifest {f.bytes:N0}바이트 (잘림·손상·다른 버전).");
                all += f.bytes;
            }
            ctx.TotalBytes = all;
            return m;
        }

        static void BuildWordsAndSenses(WordRoomDataset data, WordsDocument w, SensesDocument s, PackageCounts counts)
        {
            const string WF = PackageFormat.Words, SF = PackageFormat.Senses;
            Require(w.words != null && w.lengths != null && w.senseOffsets != null && w.senseRows != null && w.fasttextValid != null, WF, "필수 배열 누락");
            var nw = w.words.Length;
            if (nw != counts.words || w.count != nw || w.lengths.Length != nw || w.fasttextValid.Length != nw || w.senseOffsets.Length != nw + 1)
                throw new WordRoomDataException(DataErrorCode.CountMismatch, WF, $"표기 수 불일치 (manifest {counts.words}, 목록 {nw}).");
            Require(s.senseIds != null && s.wordRanks != null && s.entryIds != null && s.raws != null && s.pos != null && s.kinds != null
                    && s.categories != null && s.definitions != null, SF, "필수 배열 누락");
            var ns = s.senseIds.Length;
            if (ns != counts.senses || s.count != ns || s.wordRanks.Length != ns || s.entryIds.Length != ns || s.raws.Length != ns ||
                s.pos.Length != ns || s.kinds.Length != ns || s.categories.Length != ns || s.definitions.Length != ns || w.senseRows.Length != ns)
                throw new WordRoomDataException(DataErrorCode.CountMismatch, SF, $"뜻 수 불일치 (manifest {counts.senses}, 목록 {ns}).");

            data.words = w.words;
            data.wordLengths = w.lengths;
            data.wordIndex = new Dictionary<string, int>(nw, StringComparer.Ordinal);
            for (var i = 0; i < nw; i++)
            {
                var word = w.words[i];
                if (string.IsNullOrEmpty(word)) throw new WordRoomDataException(DataErrorCode.InvalidValue, WF, $"빈 표기 (순위 {i}).");
                if (data.wordIndex.ContainsKey(word)) throw new WordRoomDataException(DataErrorCode.DuplicateEntry, WF, $"표기 중복: {word}");
                if (WordNormalizer.CodePointLength(word) != w.lengths[i])
                    throw new WordRoomDataException(DataErrorCode.InvalidValue, WF, $"표기 길이 불일치(문자열 손상): {word}");
                data.wordIndex.Add(word, i);
            }
            data.senseIds = s.senseIds;
            data.senseIndex = new Dictionary<string, int>(ns, StringComparer.Ordinal);
            data.wordOfSense = new int[ns];
            for (var r = 0; r < ns; r++)
            {
                var id = s.senseIds[r];
                if (string.IsNullOrEmpty(id)) throw new WordRoomDataException(DataErrorCode.InvalidValue, SF, $"빈 뜻 ID (행 {r}).");
                if (data.senseIndex.ContainsKey(id)) throw new WordRoomDataException(DataErrorCode.DuplicateEntry, SF, $"뜻 ID 중복: {id}");
                data.senseIndex.Add(id, r);
                var rank = s.wordRanks[r];
                if ((uint)rank >= (uint)nw) throw new WordRoomDataException(DataErrorCode.InvalidReference, SF, $"뜻 {id}의 표기 순위 {rank}가 범위 밖입니다.");
                data.wordOfSense[r] = rank;
            }
            // Word → senses: offsets must be monotonic and cover every sense exactly once, each pointing back to its word.
            var seen = new bool[ns];
            if (w.senseOffsets[0] != 0 || w.senseOffsets[nw] != ns)
                throw new WordRoomDataException(DataErrorCode.InvalidReference, WF, "단어별 뜻 구간이 전체 뜻을 덮지 않습니다.");
            for (var i = 0; i < nw; i++)
            {
                int a = w.senseOffsets[i], b = w.senseOffsets[i + 1];
                if (b <= a) throw new WordRoomDataException(DataErrorCode.InvalidReference, WF, $"뜻이 없는 표기: {w.words[i]}");
                for (var k = a; k < b; k++)
                {
                    var row = w.senseRows[k];
                    if ((uint)row >= (uint)ns || seen[row])
                        throw new WordRoomDataException(DataErrorCode.InvalidReference, WF, $"{w.words[i]}의 뜻 행 {row}가 범위 밖이거나 중복입니다.");
                    if (data.wordOfSense[row] != i)
                        throw new WordRoomDataException(DataErrorCode.InvalidReference, WF, $"{w.words[i]}의 뜻 {s.senseIds[row]}가 다른 표기에 속합니다.");
                    if (k > a && row <= w.senseRows[k - 1])
                        throw new WordRoomDataException(DataErrorCode.InvalidReference, WF, $"{w.words[i]}의 뜻 행이 오름차순이 아닙니다.");
                    seen[row] = true;
                }
            }
            data.senseOffsets = w.senseOffsets;
            data.senseRowsByWord = w.senseRows;
            data.entryIds = s.entryIds;
            data.raws = s.raws;
            data.pos = s.pos;
            data.kinds = s.kinds;
            data.categories = s.categories;
            data.definitions = s.definitions;
        }

        static void ValidateVectors(WordRoomDataset data, WordsDocument w, PackageCounts counts, double tolerance)
        {
            var e5 = data.e5;
            var d = data.e5Dim;
            for (var r = 0; r < data.SenseCount; r++)
            {
                var norm = 0.0;
                for (var i = r * d; i < (r + 1) * d; i++)
                {
                    var v = e5[i];
                    if (float.IsNaN(v) || float.IsInfinity(v))
                        throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.E5Vectors, $"NaN/무한대 (뜻 {data.senseIds[r]}).");
                    norm += (double)v * v;
                }
                if (Math.Abs(Math.Sqrt(norm) - 1) > tolerance)
                    throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.E5Vectors, $"단위 벡터가 아닙니다 (뜻 {data.senseIds[r]}, 노름 {Math.Sqrt(norm):R}).");
            }
            var ft = data.fastText;
            var f = data.fastTextDim;
            data.fastTextValid = new bool[data.WordCount];
            var valid = 0;
            for (var r = 0; r < data.WordCount; r++)
            {
                var flag = w.fasttextValid[r];
                if (flag != 0 && flag != 1) throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.Words, $"fastText 유효성 값 {flag}");
                var norm = 0.0;
                for (var i = r * f; i < (r + 1) * f; i++)
                {
                    var v = ft[i];
                    if (float.IsNaN(v) || float.IsInfinity(v))
                        throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.FastTextVectors, $"NaN/무한대 (표기 {data.words[r]}).");
                    norm += (double)v * v;
                }
                if (flag == 1 && Math.Abs(Math.Sqrt(norm) - 1) > tolerance)
                    throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.FastTextVectors, $"유효 표시인데 단위 벡터가 아닙니다 (표기 {data.words[r]}).");
                if (flag == 0 && norm != 0)
                    throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.FastTextVectors, $"벡터 없음 표시인데 값이 있습니다 (표기 {data.words[r]}).");
                data.fastTextValid[r] = flag == 1;
                valid += flag;
            }
            if (valid != counts.fasttextValidWords)
                throw new WordRoomDataException(DataErrorCode.CountMismatch, PackageFormat.Words, $"fastText 유효 표기 {valid}개, manifest {counts.fasttextValidWords}개.");
        }

        static void BuildRestricted(WordRoomDataset data, RestrictedDocument doc)
        {
            Require(doc.entries != null, PackageFormat.Restricted, "entries 누락");
            data.restricted = new bool[data.WordCount];
            var inVocabulary = 0;
            foreach (var e in doc.entries)
            {
                if (e.rank == -1)
                {
                    if (data.wordIndex.ContainsKey(e.word ?? ""))
                        throw new WordRoomDataException(DataErrorCode.InvalidReference, PackageFormat.Restricted, $"어휘에 있는 표기가 어휘 밖으로 표시됨: {e.word}");
                    continue;
                }
                if ((uint)e.rank >= (uint)data.WordCount || data.words[e.rank] != e.word)
                    throw new WordRoomDataException(DataErrorCode.InvalidReference, PackageFormat.Restricted, $"제한 표기 {e.word}의 순위 {e.rank}가 맞지 않습니다.");
                data.restricted[e.rank] = true;
                inVocabulary++;
            }
            if (inVocabulary != doc.inVocabulary || inVocabulary != data.Manifest.counts.restrictedInVocabulary)
                throw new WordRoomDataException(DataErrorCode.CountMismatch, PackageFormat.Restricted, $"어휘 안 제한 표기 {inVocabulary}개, 기록 {doc.inVocabulary}개.");
            data.RestrictedInfo = doc;
        }

        static double[] Ladder(ThresholdValue[] values, string file)
        {
            Require(values != null && values.Length > 0, file, "thresholdLadder 누락");
            var ladder = new double[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                try
                {
                    ladder[i] = PackageFormat.ParseDoubleBits(values[i].bits);
                }
                catch (FormatException e)
                {
                    throw new WordRoomDataException(DataErrorCode.InvalidFormat, file, e.Message, e);
                }
                if (double.Parse(values[i].text, NumberStyles.Float, CultureInfo.InvariantCulture) != ladder[i])
                    throw new WordRoomDataException(DataErrorCode.InvalidValue, file, $"기준 {values[i].text}의 비트 값이 다릅니다.");
            }
            return ladder;
        }

        static void BuildSettings(WordRoomDataset data, SettingsDocument doc, RelationsDocument rel)
        {
            Require(doc.entries != null && doc.entries.Length > 0, PackageFormat.Settings, "entries 누락");
            var ladder = Ladder(doc.thresholdLadder, PackageFormat.Settings);
            var relLadder = Ladder(rel.thresholdLadder, PackageFormat.RelationsMeta);
            if (ladder.Length != relLadder.Length)
                throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.Settings, "설정과 관계 메타의 기준 단계가 다릅니다.");
            for (var i = 0; i < ladder.Length; i++)
                if (ladder[i] != relLadder[i]) throw new WordRoomDataException(DataErrorCode.InvalidValue, PackageFormat.Settings, "설정과 관계 메타의 기준 단계가 다릅니다.");
            data.thresholdLadder = ladder;
            data.Settings = new EffectiveSettings(doc);
            var prepared = rel.preparedSources;
            Require(prepared != null && prepared.senseRows != null && prepared.scopeIndex != null && prepared.topK != null && prepared.scopes != null,
                PackageFormat.RelationsMeta, "preparedSources 누락");
            if (prepared.scopeIndex.Length != prepared.senseRows.Length || prepared.topK.Length != prepared.senseRows.Length)
                throw new WordRoomDataException(DataErrorCode.CountMismatch, PackageFormat.RelationsMeta, "preparedSources 배열 길이 불일치");
            for (var i = 0; i < prepared.senseRows.Length; i++)
            {
                if ((uint)prepared.senseRows[i] >= (uint)data.SenseCount || (uint)prepared.scopeIndex[i] >= (uint)prepared.scopes.Length)
                    throw new WordRoomDataException(DataErrorCode.InvalidReference, PackageFormat.RelationsMeta, $"preparedSources {i}번 참조가 범위 밖입니다.");
            }
        }

        static void Require(bool ok, string file, string message)
        {
            if (!ok) throw new WordRoomDataException(DataErrorCode.InvalidFormat, file, message);
        }

        sealed class Context
        {
            public readonly IPackageFileSource Source;
            readonly LoadOptions options;
            readonly IProgress<LoadProgress> progress;
            readonly CancellationToken cancel;
            readonly LoadReport report;
            public ManifestDocument Manifest;
            public long TotalBytes;
            long done;

            public Context(IPackageFileSource source, LoadOptions options, IProgress<LoadProgress> progress, CancellationToken cancel, LoadReport report)
            {
                Source = source;
                this.options = options;
                this.progress = progress;
                this.cancel = cancel;
                this.report = report;
            }

            public void Report(string stage, float fraction) => progress?.Report(new LoadProgress(stage, fraction));

            void Advance(string stage, long bytes)
            {
                done += bytes;
                report.BytesRead += bytes;
                if (cancel.IsCancellationRequested) throw new WordRoomDataException(DataErrorCode.Cancelled, stage, "로딩이 취소되었습니다.");
                Report(stage, TotalBytes > 0 ? (float)Math.Min(1.0, (double)done / TotalBytes) : 0f);
            }

            ManifestFile Entry(string name)
            {
                foreach (var f in Manifest.files) if (f.name == name) return f;
                throw new WordRoomDataException(DataErrorCode.InvalidFormat, PackageFormat.Manifest, $"{name} 항목 없음");
            }

            void CheckHash(string name, SHA256 sha, ManifestFile entry)
            {
                if (!options.VerifyHashes) return;
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hex = Hex(sha.Hash);
                if (!string.Equals(hex, entry.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new WordRoomDataException(DataErrorCode.HashMismatch, name, $"SHA-256 {hex.Substring(0, 12)}…가 manifest {entry.sha256?.Substring(0, Math.Min(12, entry.sha256?.Length ?? 0))}…와 다릅니다 (손상·다른 버전).");
            }

            /// <summary>Whole small file, hashed against the manifest (manifest itself: entry == null).</summary>
            public byte[] Bytes(string name, ManifestFile entry)
            {
                using (var s = Source.OpenRead(name))
                {
                    var length = s.Length;
                    var bytes = new byte[length];
                    ReadExactly(s, bytes, 0, bytes.Length, name);
                    if (entry != null)
                    {
                        using (var sha = SHA256.Create())
                        {
                            if (options.VerifyHashes) sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                            CheckHash(name, sha, entry);
                        }
                        Advance(name, bytes.Length);
                    }
                    return bytes;
                }
            }

            public string Text(string name, ManifestFile entry)
            {
                var bytes = Bytes(name, entry);
                try
                {
                    var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                    return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
                }
                catch (DecoderFallbackException e)
                {
                    throw new WordRoomDataException(DataErrorCode.InvalidFormat, name, "UTF-8이 아닙니다.", e);
                }
            }

            public T Json<T>(string name, Func<T, string> packageIdOf) where T : class
            {
                var text = Text(name, Entry(name));
                T doc;
                try
                {
                    doc = JsonUtility.FromJson<T>(text);
                }
                catch (Exception e)
                {
                    throw new WordRoomDataException(DataErrorCode.InvalidFormat, name, "JSON을 읽을 수 없습니다.", e);
                }
                if (doc == null) throw new WordRoomDataException(DataErrorCode.InvalidFormat, name, "빈 JSON");
                if (packageIdOf(doc) != Manifest.packageId)
                    throw new WordRoomDataException(DataErrorCode.PackageIdMismatch, name,
                        $"다른 내보내기의 파일입니다 (파일 {Short(packageIdOf(doc))}, manifest {Short(Manifest.packageId)}).");
                return doc;
            }

            BinaryHeader Header(string name, Stream s, SHA256 sha, string magic, uint dtype, uint rowKey, ManifestFile entry)
            {
                var head = new byte[PackageFormat.HeaderBytes];
                ReadExactly(s, head, 0, head.Length, name);
                if (options.VerifyHashes) sha.TransformBlock(head, 0, head.Length, null, 0);
                var h = BinaryHeader.Parse(head);
                if (h.Magic != magic)
                    throw new WordRoomDataException(DataErrorCode.InvalidFormat, name, $"형식 식별자 {h.Magic}, 기대 {magic}");
                if (h.Schema != PackageFormat.SupportedSchema)
                    throw new WordRoomDataException(DataErrorCode.UnsupportedSchema, name, $"스키마 {h.Schema}은 지원하지 않습니다.");
                if (h.HeaderBytes != PackageFormat.HeaderBytes || h.ByteOrder != PackageFormat.ByteOrderMark || h.Layout != PackageFormat.LayoutRowMajor ||
                    h.Dtype != dtype || h.RowKey != rowKey)
                    throw new WordRoomDataException(DataErrorCode.InvalidFormat, name, "헤더의 숫자 형식·바이트 순서·배치·행 키가 규격과 다릅니다.");
                if (!h.PrefixMatches(Manifest.packageId))
                    throw new WordRoomDataException(DataErrorCode.PackageIdMismatch, name, "다른 내보내기의 파일입니다 (헤더 패키지 ID 불일치).");
                if ((long)h.Rows != entry.rows || h.Cols != entry.cols)
                    throw new WordRoomDataException(DataErrorCode.CountMismatch, name, $"헤더 {h.Rows}×{h.Cols}, manifest {entry.rows}×{entry.cols}.");
                return h;
            }

            public float[] Matrix(string name, uint rowKey, int expectedRows, int expectedCols, out int cols)
            {
                var entry = Entry(name);
                using (var s = Source.OpenRead(name))
                using (var sha = SHA256.Create())
                {
                    var h = Header(name, s, sha, PackageFormat.MagicMatrix, PackageFormat.DtypeFloat32, rowKey, entry);
                    if (h.ElementBytes != 4 || (long)h.Rows != expectedRows || h.Cols != expectedCols || expectedCols <= 0)
                        throw new WordRoomDataException(DataErrorCode.CountMismatch, name, $"행·열 {h.Rows}×{h.Cols}, 기대 {expectedRows}×{expectedCols}.");
                    var payload = (long)h.Rows * h.Cols * 4;
                    if (payload > int.MaxValue) throw new WordRoomDataException(DataErrorCode.InvalidFormat, name, "2GB를 넘는 배열은 지원하지 않습니다.");
                    if (PackageFormat.HeaderBytes + payload != s.Length)
                        throw new WordRoomDataException(DataErrorCode.SizeMismatch, name, $"본문 크기가 행·열과 맞지 않습니다 ({s.Length:N0}바이트).");
                    var data = new float[h.Rows * h.Cols];
                    var buffer = new byte[1 << 22];
                    long offset = 0;
                    Advance(name, PackageFormat.HeaderBytes);
                    while (offset < payload)
                    {
                        var n = (int)Math.Min(buffer.Length, payload - offset);
                        ReadExactly(s, buffer, 0, n, name);
                        if (options.VerifyHashes) sha.TransformBlock(buffer, 0, n, null, 0);
                        Buffer.BlockCopy(buffer, 0, data, (int)offset, n);
                        offset += n;
                        Advance(name, n);
                    }
                    CheckHash(name, sha, entry);
                    cols = (int)h.Cols;
                    return data;
                }
            }

            public RelationRecords Relations(RelationsDocument doc, int expected, int senses)
            {
                const string name = PackageFormat.Relations;
                var entry = Entry(name);
                Require(doc.statusNames != null && doc.answerNames != null && doc.methodNames != null && doc.createdAtValues != null,
                    PackageFormat.RelationsMeta, "문자열 표 누락");
                byte[] bytes;
                BinaryHeader h;
                using (var s = Source.OpenRead(name))
                using (var sha = SHA256.Create())
                {
                    h = Header(name, s, sha, PackageFormat.MagicRelations, PackageFormat.DtypeRelationV1, PackageFormat.RowKeyRelation, entry);
                    if (h.ElementBytes != PackageFormat.RelationRecordBytes || h.Cols != PackageFormat.RelationRecordBytes || (long)h.Rows != expected ||
                        doc.recordCount != expected)
                        throw new WordRoomDataException(DataErrorCode.CountMismatch, name, $"레코드 {h.Rows}개, manifest {expected}개, 메타 {doc.recordCount}개.");
                    var payload = (long)h.Rows * PackageFormat.RelationRecordBytes;
                    if (PackageFormat.HeaderBytes + payload != s.Length)
                        throw new WordRoomDataException(DataErrorCode.SizeMismatch, name, "본문 크기가 레코드 수와 맞지 않습니다.");
                    bytes = new byte[payload];
                    ReadExactly(s, bytes, 0, bytes.Length, name);
                    if (options.VerifyHashes) sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                    CheckHash(name, sha, entry);
                    Advance(name, bytes.Length + PackageFormat.HeaderBytes);
                }
                var n = expected;
                var r = new RelationRecords
                {
                    A = new int[n], B = new int[n], Status = new byte[n], Valid = new byte[n], Fa = new byte[n], Ba = new byte[n],
                    Method = new byte[n], Flags = new byte[n], Cfa = new byte[n], Cba = new byte[n], Created = new ushort[n],
                    Score = new double[n], Fs = new double[n], Bs = new double[n], Cfs = new double[n], Cbs = new double[n],
                };
                var span = new ReadOnlySpan<byte>(bytes);
                long previous = -1;
                for (var k = 0; k < n; k++)
                {
                    var b = span.Slice(k * PackageFormat.RelationRecordBytes, PackageFormat.RelationRecordBytes);
                    r.A[k] = BinaryPrimitives.ReadInt32LittleEndian(b);
                    r.B[k] = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(4));
                    r.Status[k] = b[8]; r.Valid[k] = b[9]; r.Fa[k] = b[10]; r.Ba[k] = b[11]; r.Method[k] = b[12]; r.Flags[k] = b[13];
                    r.Cfa[k] = b[14]; r.Cba[k] = b[15];
                    r.Created[k] = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(16));
                    r.Score[k] = Double(b, 24); r.Fs[k] = Double(b, 32); r.Bs[k] = Double(b, 40); r.Cfs[k] = Double(b, 48); r.Cbs[k] = Double(b, 56);
                    if ((uint)r.A[k] >= (uint)senses || (uint)r.B[k] >= (uint)senses || r.A[k] == r.B[k])
                        throw new WordRoomDataException(DataErrorCode.InvalidReference, name, $"레코드 {k}의 뜻 행 ({r.A[k]}, {r.B[k]})이 범위 밖이거나 같습니다.");
                    if (r.Status[k] >= doc.statusNames.Length || r.Method[k] >= doc.methodNames.Length || r.Created[k] >= doc.createdAtValues.Length ||
                        r.Fa[k] > doc.answerNames.Length || r.Ba[k] > doc.answerNames.Length || r.Cfa[k] > doc.answerNames.Length ||
                        r.Cba[k] > doc.answerNames.Length || r.Valid[k] > 1)
                        throw new WordRoomDataException(DataErrorCode.InvalidReference, name, $"레코드 {k}의 상태·답·방법 코드가 표 범위 밖입니다.");
                    if (double.IsNaN(r.Score[k]) || double.IsInfinity(r.Score[k]))
                        throw new WordRoomDataException(DataErrorCode.InvalidValue, name, $"레코드 {k}의 점수가 유한값이 아닙니다.");
                    var key = ((long)Math.Min(r.A[k], r.B[k]) << 32) | (uint)Math.Max(r.A[k], r.B[k]);
                    if (key <= previous)
                        throw new WordRoomDataException(key == previous ? DataErrorCode.DuplicateEntry : DataErrorCode.InvalidFormat, name,
                            $"레코드 {k}: 뜻 쌍이 정렬되지 않았거나 중복입니다.");
                    previous = key;
                }
                return r;
            }

            static double Double(ReadOnlySpan<byte> b, int offset) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(b.Slice(offset)));
        }

        static void ReadExactly(Stream s, byte[] buffer, int offset, int count, string name)
        {
            while (count > 0)
            {
                var n = s.Read(buffer, offset, count);
                if (n <= 0) throw new WordRoomDataException(DataErrorCode.SizeMismatch, name, "파일이 예상보다 짧습니다.");
                offset += n;
                count -= n;
            }
        }

        static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static string Short(string id) => string.IsNullOrEmpty(id) ? "(없음)" : id.Substring(0, Math.Min(12, id.Length));
    }
}
