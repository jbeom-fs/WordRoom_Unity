using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using WordRoom.Data.Documents;

namespace WordRoom.Data
{
    /// <summary>Sense metadata (one original dictionary sense). Row is the package's array index, not the sense ID.</summary>
    public readonly struct SenseInfo
    {
        public readonly int Row;
        public readonly string SenseId;
        public readonly int WordRank;
        public readonly string Word;
        public readonly string Pos;
        public readonly string Category;
        public readonly string Definition;
        public readonly string EntryId;
        public readonly string Raw;
        public readonly string Kind;

        public SenseInfo(int row, string senseId, int wordRank, string word, string pos, string category, string definition,
            string entryId, string raw, string kind)
        {
            Row = row;
            SenseId = senseId;
            WordRank = wordRank;
            Word = word;
            Pos = pos;
            Category = category;
            Definition = definition;
            EntryId = entryId;
            Raw = raw;
            Kind = kind;
        }
    }

    /// <summary>
    /// Allowed surfaces of a vocabulary setting: the top `Limit` surfaces by frequency whose length is within
    /// [MinLength, MaxLength] (kw_semantic.SemanticLexicon.eligible_ids / WordView.eligible_ids).
    /// Ranks and sense rows are those of the full package, so a 10,000 scope keeps every sense and vector mapping.
    /// </summary>
    public sealed class VocabularyScope
    {
        readonly bool[] mask;
        readonly int[] ranks;

        public int Limit { get; }
        public int MinLength { get; }
        public int MaxLength { get; }
        public int Count => ranks.Length;
        public IReadOnlyList<int> Ranks => ranks;

        internal VocabularyScope(int limit, int minLength, int maxLength, bool[] mask, int[] ranks)
        {
            Limit = limit;
            MinLength = minLength;
            MaxLength = maxLength;
            this.mask = mask;
            this.ranks = ranks;
        }

        public bool Contains(int rank) => rank >= 0 && rank < mask.Length && mask[rank];

        public override string ToString() => $"상위 {Limit:N0}개 · {MinLength}~{MaxLength}글자 · {Count:N0}개 표기";
    }

    /// <summary>Settings as Python applies them after its loader corrections, with the raw values they came from.</summary>
    public sealed class EffectiveSettings
    {
        readonly Dictionary<string, SettingEntryDocument> byKey = new Dictionary<string, SettingEntryDocument>();

        public SettingsDocument Document { get; }
        public IReadOnlyList<SettingEntryDocument> Entries => Document.entries;
        public IReadOnlyList<DroppedSettingDocument> DroppedRawKeys => Document.droppedRawKeys ?? Array.Empty<DroppedSettingDocument>();
        public IReadOnlyList<string> Notices => Document.notices ?? Array.Empty<string>();

        internal EffectiveSettings(SettingsDocument doc)
        {
            Document = doc;
            foreach (var e in doc.entries) byKey[e.key] = e;
        }

        public IEnumerable<SettingEntryDocument> Differences()
        {
            foreach (var e in Document.entries) if (e.change != "same") yield return e;
        }

        public bool TryGetJson(string key, out string json)
        {
            json = null;
            if (!byKey.TryGetValue(key, out var e)) return false;
            json = e.valueJson;
            return true;
        }

        string Json(string key)
        {
            if (!TryGetJson(key, out var json)) throw new KeyNotFoundException($"설정 항목이 없습니다: {key}");
            return json;
        }

        public int GetInt(string key) => int.Parse(Json(key), NumberStyles.Integer, CultureInfo.InvariantCulture);
        public double GetDouble(string key) => double.Parse(Json(key), NumberStyles.Float, CultureInfo.InvariantCulture);
        public bool GetBool(string key) => Json(key) == "true";
        public string GetString(string key) => JsonScalar.ParseString(Json(key));
    }

    /// <summary>The loaded package. All queries are read-only and allocation-light; vectors are never copied.</summary>
    public sealed partial class WordRoomDataset
    {
        internal string[] words;
        internal int[] wordLengths;
        internal Dictionary<string, int> wordIndex;
        internal int[] senseOffsets, senseRowsByWord, wordOfSense;
        internal string[] senseIds, entryIds, raws, pos, kinds, categories, definitions;
        internal Dictionary<string, int> senseIndex;
        internal float[] e5;
        internal int e5Dim;
        internal float[] fastText;
        internal int fastTextDim;
        internal bool[] fastTextValid;
        internal bool[] restricted;
        internal double[] thresholdLadder;

        public string PackageId { get; internal set; }
        public ManifestDocument Manifest { get; internal set; }
        public PackageIdentity Identity => Manifest.identity;
        public RelationIndex Relations { get; internal set; }
        public RelationsDocument RelationInfo { get; internal set; }
        public RestrictedDocument RestrictedInfo { get; internal set; }
        public EffectiveSettings Settings { get; internal set; }
        public PreparedSourcesDocument PreparedSources => RelationInfo.preparedSources;
        public LoadReport LoadReport { get; internal set; }

        public int WordCount => words.Length;
        public int SenseCount => senseIds.Length;
        public int E5Dimensions => e5Dim;
        public int FastTextDimensions => fastTextDim;
        /// <summary>Relation thresholds of the effective settings (0.8 → 0.1), exact float64 values.</summary>
        public IReadOnlyList<double> ThresholdLadder => thresholdLadder;

        internal WordRoomDataset()
        {
        }

        // ---- surfaces ----------------------------------------------------------------------------------------
        public string GetWord(int rank)
        {
            CheckWord(rank);
            return words[rank];
        }

        /// <summary>Python len() of the surface (code points).</summary>
        public int GetWordLength(int rank)
        {
            CheckWord(rank);
            return wordLengths[rank];
        }

        /// <summary>Frequency rank of a surface; the input is normalized like Python (NFC, homograph digits, '-', '^').</summary>
        public bool TryGetWordRank(string surface, out int rank)
        {
            rank = -1;
            if (string.IsNullOrEmpty(surface)) return false;
            if (wordIndex.TryGetValue(surface, out rank)) return true;
            return wordIndex.TryGetValue(WordNormalizer.Normalize(surface), out rank);
        }

        public ReadOnlySpan<int> SenseRowsOf(int rank)
        {
            CheckWord(rank);
            return new ReadOnlySpan<int>(senseRowsByWord, senseOffsets[rank], senseOffsets[rank + 1] - senseOffsets[rank]);
        }

        public int[] GetSenseRows(int rank) => SenseRowsOf(rank).ToArray();

        // ---- senses ------------------------------------------------------------------------------------------
        public int WordOfSense(int senseRow) => wordOfSense[senseRow];

        public bool TryGetSenseRow(string senseId, out int row)
        {
            row = -1;
            return senseId != null && senseIndex.TryGetValue(senseId, out row);
        }

        public SenseInfo GetSense(int row)
        {
            CheckSense(row);
            return new SenseInfo(row, senseIds[row], wordOfSense[row], words[wordOfSense[row]], pos[row], categories[row],
                definitions[row], entryIds[row], raws[row], kinds[row]);
        }

        public string GetSenseId(int row)
        {
            CheckSense(row);
            return senseIds[row];
        }

        // ---- vectors -----------------------------------------------------------------------------------------
        /// <summary>E5 definition vector of a sense row (view into the loaded array, not a copy).</summary>
        public ReadOnlySpan<float> GetE5Vector(int senseRow)
        {
            CheckSense(senseRow);
            return new ReadOnlySpan<float>(e5, senseRow * e5Dim, e5Dim);
        }

        /// <summary>Dot product of two stored unit E5 vectors (the engine's E5 similarity), summed in float64.</summary>
        public double E5Similarity(int senseRowA, int senseRowB)
        {
            CheckSense(senseRowA);
            CheckSense(senseRowB);
            return Dot(e5, senseRowA * e5Dim, senseRowB * e5Dim, e5Dim);
        }

        public bool HasFastText(int rank)
        {
            CheckWord(rank);
            return fastTextValid[rank];
        }

        /// <summary>fastText surface vector; empty when the surface has no usable vector (masked in Python).</summary>
        public ReadOnlySpan<float> GetFastTextVector(int rank)
        {
            CheckWord(rank);
            return fastTextValid[rank] ? new ReadOnlySpan<float>(fastText, rank * fastTextDim, fastTextDim) : ReadOnlySpan<float>.Empty;
        }

        /// <summary>
        /// Cosine of two stored unit fastText vectors = their dot product, summed in float64 (Python uses ft@ft[w]).
        /// The engine's supplement clips it to [0, 1]; target closeness uses it unclipped. Fails without vectors.
        /// </summary>
        public bool TryFastTextSimilarity(int rankA, int rankB, out double similarity)
        {
            similarity = double.NaN;
            if (!HasFastText(rankA) || !HasFastText(rankB)) return false;
            similarity = Dot(fastText, rankA * fastTextDim, rankB * fastTextDim, fastTextDim);
            return true;
        }

        public double FastTextSimilarity(int rankA, int rankB)
        {
            if (!TryFastTextSimilarity(rankA, rankB, out var s))
                throw new InvalidOperationException($"fastText 벡터가 없는 표기입니다: {GetWord(HasFastText(rankA) ? rankB : rankA)}");
            return s;
        }

        static double Dot(float[] m, int a, int b, int n)
        {
            var s = 0.0;
            for (var i = 0; i < n; i++) s += (double)m[a + i] * m[b + i];
            return s;
        }

        // ---- restriction list, vocabulary range ----------------------------------------------------------------
        /// <summary>On the fastText restriction list (weak fastText evidence; does not affect relation-DB approval).</summary>
        public bool IsRestricted(int rank)
        {
            CheckWord(rank);
            return restricted[rank];
        }

        public RestrictedEntryDocument GetRestrictedEntry(int rank)
        {
            CheckWord(rank);
            if (!restricted[rank]) return null;
            foreach (var e in RestrictedInfo.entries) if (e.rank == rank) return e;
            return null;
        }

        /// <summary>Same limits as kw_semantic.SemanticLexicon.eligible_ids: a subset of the prepared vocabulary only.</summary>
        public VocabularyScope CreateScope(int limit, int minLength, int maxLength)
        {
            var id = Identity;
            if (limit < 1 || minLength < 0 || maxLength < minLength)
                throw new ArgumentException($"어휘 범위가 올바르지 않습니다 (상위 {limit}, {minLength}~{maxLength}글자).");
            if (minLength < id.minLength || maxLength > id.maxLength || limit > id.vocabularyLimit)
                throw new ArgumentException($"준비된 어휘({id.vocabularyName})는 빈도 상위 {id.vocabularyLimit:N0}개, {id.minLength}~{id.maxLength}글자입니다. " +
                                            $"이 범위를 넘는 설정(상위 {limit:N0}, {minLength}~{maxLength}글자)은 Python에서 해당 어휘를 준비한 뒤 다시 내보내야 합니다.");
            var mask = new bool[words.Length];
            var list = new List<int>();
            for (var r = 0; r < Math.Min(limit, words.Length); r++)
            {
                if (wordLengths[r] < minLength || wordLengths[r] > maxLength) continue;
                mask[r] = true;
                list.Add(r);
            }
            return new VocabularyScope(limit, minLength, maxLength, mask, list.ToArray());
        }

        /// <summary>Scope of the effective settings (vocabulary_limit, min_word_length, max_word_length).</summary>
        public VocabularyScope CreateDefaultScope() =>
            CreateScope(Settings.GetInt("vocabulary_limit"), Settings.GetInt("min_word_length"), Settings.GetInt("max_word_length"));

        void CheckWord(int rank)
        {
            if ((uint)rank >= (uint)words.Length) throw new ArgumentOutOfRangeException(nameof(rank), rank, $"표기 순위 범위 0~{words.Length - 1}");
        }

        void CheckSense(int row)
        {
            if ((uint)row >= (uint)senseIds.Length) throw new ArgumentOutOfRangeException(nameof(row), row, $"뜻 행 범위 0~{senseIds.Length - 1}");
        }

        /// <summary>Raw float arrays for whole-array checks (hashing); not for modification.</summary>
        internal float[] E5Data => e5;
        internal float[] FastTextData => fastText;
    }

    /// <summary>Timing and size of one load.</summary>
    public sealed class LoadReport
    {
        public readonly List<KeyValuePair<string, double>> StageSeconds = new List<KeyValuePair<string, double>>();
        public double TotalSeconds;
        public bool HashesVerified;
        public long BytesRead;
        public long ManagedMemoryBefore;
        public long ManagedMemoryAfter;
        public string Source;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"로딩 {TotalSeconds:F2}초 · {BytesRead / 1048576.0:F1} MB · 해시 검사 {(HashesVerified ? "함" : "생략")}");
            foreach (var kv in StageSeconds) sb.Append($" · {kv.Key} {kv.Value:F2}s");
            return sb.ToString();
        }
    }

    internal static class JsonScalar
    {
        public static string ParseString(string json)
        {
            if (json == null || json.Length < 2 || json[0] != '"' || json[json.Length - 1] != '"')
                throw new FormatException($"JSON 문자열이 아닙니다: {json}");
            var sb = new StringBuilder(json.Length);
            for (var i = 1; i < json.Length - 1; i++)
            {
                var c = json[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                c = json[++i];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(json.Substring(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
