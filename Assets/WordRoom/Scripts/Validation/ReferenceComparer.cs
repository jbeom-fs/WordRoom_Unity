using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace WordRoom.Data.Validation
{
    public sealed class CheckResult
    {
        public string Name;
        public bool Passed;
        public long Compared;
        public long Mismatches;
        public string Detail;
        public readonly List<string> Examples = new List<string>();

        public override string ToString() =>
            $"{(Passed ? "PASS" : "FAIL")} {Name}: {Compared:N0}개 비교, 불일치 {Mismatches:N0}{(string.IsNullOrEmpty(Detail) ? "" : " · " + Detail)}" +
            (Examples.Count > 0 ? " · 예: " + string.Join(" | ", Examples) : "");
    }

    public sealed class ComparisonReport
    {
        public readonly List<CheckResult> Checks = new List<CheckResult>();
        public double Seconds;
        public double FastTextMaxErrorVsFloat64, FastTextMaxErrorVsNumpyFloat32;
        public double E5MaxErrorVsFloat64, E5MaxErrorVsNumpyFloat32;

        public bool Passed
        {
            get
            {
                foreach (var c in Checks) if (!c.Passed) return false;
                return Checks.Count > 0;
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Python 기준값 비교 {(Passed ? "통과" : "실패")} ({Checks.Count}개 항목, {Seconds:F1}초)");
            foreach (var c in Checks) sb.AppendLine(c.ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// Compares a loaded dataset with the Python baseline computed from the original sources by the original
    /// Python code (Tools/DataExport/wr_reference.py). Similarity tolerances: 1e-12 against float64 sums of the
    /// stored float32 values, 1e-5 against numpy's float32 results.
    /// </summary>
    public static class ReferenceComparer
    {
        public const double Float64Tolerance = 1e-12;
        public const double NumpyFloat32Tolerance = 1e-5;

        public static ReferenceDocument LoadReference(string path)
        {
            var doc = JsonUtility.FromJson<ReferenceDocument>(File.ReadAllText(path, new UTF8Encoding(false, true)));
            if (doc == null || doc.referenceSchema != 1) throw new InvalidDataException($"Python 기준값 형식이 아닙니다: {path}");
            return doc;
        }

        public static ComparisonReport Compare(WordRoomDataset d, ReferenceDocument r)
        {
            var report = new ComparisonReport();
            var watch = Stopwatch.StartNew();
            var rel = d.Relations;
            var ladder = d.ThresholdLadder;

            var c = Check(report, "패키지·기준값 식별");
            Expect(c, r.packageId == d.PackageId, $"packageId 기준값 {Short(r.packageId)} / 패키지 {Short(d.PackageId)}");
            Expect(c, r.relationSnapshotId == d.Identity.relationSnapshotId, $"관계 스냅샷 {r.relationSnapshotId} / {d.Identity.relationSnapshotId}");

            c = Check(report, "전체 수량");
            Expect(c, r.counts.words == d.WordCount, $"표기 {r.counts.words}/{d.WordCount}");
            Expect(c, r.counts.senses == d.SenseCount, $"뜻 {r.counts.senses}/{d.SenseCount}");
            Expect(c, r.counts.e5Dimensions == d.E5Dimensions, $"E5 차원 {r.counts.e5Dimensions}/{d.E5Dimensions}");
            Expect(c, r.counts.fasttextDimensions == d.FastTextDimensions, $"fastText 차원 {r.counts.fasttextDimensions}/{d.FastTextDimensions}");
            Expect(c, r.counts.compatibleSensePairs == rel.RecordCount, $"호환 뜻 쌍 {r.counts.compatibleSensePairs}/{rel.RecordCount}");
            c.Detail = $"표기 {d.WordCount:N0} · 뜻 {d.SenseCount:N0} · 관계 {rel.RecordCount:N0} · E5 {d.E5Dimensions} · fastText {d.FastTextDimensions}";

            c = Check(report, "표기 목록·빈도 순서·글자 수");
            for (var i = 0; i < r.words.Length; i++)
                Expect(c, i < d.WordCount && r.words[i] == d.GetWord(i) && r.wordLengths[i] == d.GetWordLength(i), $"순위 {i}: {r.words[i]}");

            c = Check(report, "단어별 뜻 ID 목록");
            for (var i = 0; i < r.words.Length && i < d.WordCount; i++)
            {
                var rows = d.SenseRowsOf(i);
                var expected = r.wordSenseOffsets[i + 1] - r.wordSenseOffsets[i];
                var ok = rows.Length == expected;
                for (var k = 0; ok && k < expected; k++) ok = d.GetSenseId(rows[k]) == r.wordSenseIds[r.wordSenseOffsets[i] + k];
                Expect(c, ok, r.words[i]);
            }

            c = Check(report, "뜻 ID별 표기·품사·분야·뜻풀이 문자열");
            var s = r.senses;
            for (var k = 0; k < s.senseIds.Length; k++)
            {
                if (!d.TryGetSenseRow(s.senseIds[k], out var row))
                {
                    Expect(c, false, $"뜻 {s.senseIds[k]} 없음");
                    continue;
                }
                var info = d.GetSense(row);
                Expect(c, info.Word == s.words[k] && info.Pos == s.pos[k] && info.Category == s.categories[k] && info.Definition == s.definitions[k]
                          && info.EntryId == s.entryIds[k] && info.Raw == s.raws[k] && info.Kind == s.kinds[k], $"뜻 {s.senseIds[k]}");
            }
            Expect(c, s.senseIds.Length == d.SenseCount, "뜻 수");

            CompareMatrix(report, "E5 뜻 벡터 바이트·행 대응", d.E5Data, d.E5Dimensions, r.e5, key => d.TryGetSenseRow(key, out var row) ? row : -1);
            CompareMatrix(report, "fastText 표기 벡터 바이트·행 대응", d.FastTextData, d.FastTextDimensions, r.fasttext, key => d.TryGetWordRank(key, out var rank) ? rank : -1);
            c = Check(report, "fastText 유효 표기");
            for (var i = 0; i < r.fasttext.valid.Length; i++) Expect(c, (r.fasttext.valid[i] == 1) == d.HasFastText(i), r.words[i]);

            c = Check(report, "관계 기준 단계(0.8→0.1) 비트");
            Expect(c, r.thresholds.Length == ladder.Count, "단계 수");
            for (var i = 0; i < r.thresholds.Length && i < ladder.Count; i++)
                Expect(c, PackageFormat.ParseDoubleBits(r.thresholds[i].bits) == ladder[i], r.thresholds[i].text);
            c.Detail = string.Join(", ", Array.ConvertAll(r.thresholds, t => t.text));

            c = Check(report, "어휘 범위(10,000/15,000·글자 수) 필터");
            var scopes = new Dictionary<string, VocabularyScope>();
            foreach (var sc in r.scopes)
            {
                VocabularyScope scope = null;
                try
                {
                    scope = d.CreateScope(sc.limit, sc.minLength, sc.maxLength);
                }
                catch (ArgumentException)
                {
                }
                if (sc.expectError)
                {
                    Expect(c, scope == null, $"{sc.name}: Python은 오류, Unity는 허용");
                    continue;
                }
                if (scope == null)
                {
                    Expect(c, false, $"{sc.name}: Unity 오류");
                    continue;
                }
                scopes[sc.name] = scope;
                var json = "[" + string.Join(", ", scope.Ranks) + "]";
                Expect(c, scope.Count == sc.count && Sha(Encoding.UTF8.GetBytes(json)) == sc.wordsSha256, $"{sc.name}: {scope.Count}/{sc.count}");
                if (sc.ranks != null && sc.ranks.Length > 0) Expect(c, SameSequence(scope.Ranks, sc.ranks), $"{sc.name} 순위 목록");
            }
            c.Detail = string.Join(", ", Array.ConvertAll(r.scopes, x => $"{x.name}={(x.expectError ? "오류" : x.count.ToString("N0"))}"));

            c = Check(report, "기준별 승인 이웃 전체(0.8~0.1)");
            var buffer = new List<int>();
            var pairCounts = new List<string>();
            foreach (var g in r.neighbors)
            {
                var t = ladder[g.thresholdIndex];
                for (var w = 0; w < d.WordCount; w++)
                {
                    buffer.Clear();
                    rel.GetApprovedNeighbors(w, t, buffer);
                    Expect(c, SameSequence(buffer, g.neighbors, g.offsets[w], g.offsets[w + 1] - g.offsets[w]), $"{r.thresholds[g.thresholdIndex].text} {d.GetWord(w)}");
                }
                var count = rel.CountApprovedWordPairs(t);
                Expect(c, count == g.approvedWordPairs, $"{r.thresholds[g.thresholdIndex].text} 승인 단어 쌍 {count}/{g.approvedWordPairs}");
                pairCounts.Add($"{r.thresholds[g.thresholdIndex].text}:{count:N0}");
            }
            c.Detail = "승인 단어 쌍 " + string.Join(", ", pairCounts);

            c = Check(report, "어휘 범위 안 승인 이웃");
            foreach (var x in r.scopedNeighbors)
            {
                buffer.Clear();
                rel.GetApprovedNeighbors(x.word, ladder[x.thresholdIndex], buffer, scopes[x.scope]);
                Expect(c, SameSequence(buffer, x.neighbors, 0, x.neighbors.Length), $"{x.scope} {d.GetWord(x.word)}");
            }

            c = Check(report, "단어 쌍 관계 상태(B 규칙, 기준별)");
            var ws = r.wordStates;
            var chars = new char[ladder.Count];
            for (var k = 0; k < ws.a.Length; k++)
            {
                for (var t = 0; t < ladder.Count; t++) chars[t] = WordPairStateCodes.Code(rel.GetWordPairState(ws.a[k], ws.b[k], ladder[t]));
                var got = new string(chars);
                Expect(c, got == ws.states[k], $"{d.GetWord(ws.a[k])}–{d.GetWord(ws.b[k])} Python {ws.states[k]} Unity {got}");
            }
            c.Detail = $"기록 있는 단어 쌍 {ws.touchedPairs:N0} + 역순 표본 + 기록 없는 표본 {ws.untouchedSamples:N0}";

            c = Check(report, "최고 근거 뜻 쌍·원본 YES 점수");
            var ev = r.evidence;
            for (var k = 0; k < ev.word.Length; k++)
            {
                var ok = rel.TryGetBestEvidence(ev.word[k], ev.neighbor[k], ladder[ev.thresholdIndex[k]], out var best);
                Expect(c, ok && d.GetSenseId(best.SenseRow) == ev.senseId[k] && d.GetSenseId(best.NeighborSenseRow) == ev.neighborSenseId[k]
                          && PackageFormat.DoubleBits(best.Score) == ev.scoreBits[k],
                    $"{d.GetWord(ev.word[k])}–{d.GetWord(ev.neighbor[k])} Python {ev.senseId[k]}/{ev.neighborSenseId[k]}" +
                    (ok ? $" Unity {d.GetSenseId(best.SenseRow)}/{d.GetSenseId(best.NeighborSenseRow)}" : " Unity 없음"));
            }

            c = Check(report, "모든 뜻 조합이 NO인 단어 쌍");
            var ours = new HashSet<long>();
            for (var w = 0; w < d.WordCount; w++)
                foreach (var x in rel.GetRejectedPartners(w))
                    if (w < x) ours.Add(((long)w << 32) | (uint)x);
            var theirs = new HashSet<long>();
            for (var k = 0; k < r.rejected.a.Length; k++) theirs.Add(((long)r.rejected.a[k] << 32) | (uint)r.rejected.b[k]);
            Expect(c, ours.SetEquals(theirs), $"Unity {ours.Count:N0} / Python {theirs.Count:N0}");
            c.Compared = theirs.Count;
            c.Detail = $"{theirs.Count:N0}쌍";

            c = Check(report, "뜻 쌍 판정 레코드·순서별 답·점수 비트");
            var sp = r.sensePairs;
            var payloads = 0;
            for (var k = 0; k < sp.senseA.Length; k++)
            {
                if (!d.TryGetSenseRow(sp.senseA[k], out var ra) || !d.TryGetSenseRow(sp.senseB[k], out var rb) || !rel.TryGetSensePair(ra, rb, out var j))
                {
                    Expect(c, false, $"{sp.senseA[k]}–{sp.senseB[k]} 없음");
                    continue;
                }
                var ok = j.Status == sp.status[k] && (j.ValidYes ? 1 : 0) == sp.validYes[k] && PackageFormat.DoubleBits(j.Score) == sp.scoreBits[k];
                if (sp.hasPayload[k] == 1)
                {
                    payloads++;
                    ok &= (j.ForwardAnswer ?? "") == sp.forwardAnswer[k] && (j.BackwardAnswer ?? "") == sp.backwardAnswer[k]
                          && BitsOrEmpty(j.ForwardScore, j.ForwardAnswer) == sp.forwardScoreBits[k]
                          && BitsOrEmpty(j.BackwardScore, j.BackwardAnswer) == sp.backwardScoreBits[k]
                          && j.Method == sp.method[k] && j.CreatedAt == sp.createdAt[k];
                }
                Expect(c, ok, $"{sp.senseA[k]}–{sp.senseB[k]}");
            }
            c.Detail = $"상태·점수 {sp.senseA.Length:N0}쌍, 순서별 답·점수·방법·시각 {payloads:N0}쌍";

            CompareCategories(report, d, r);

            c = Check(report, "fastText 유사도(표기 벡터 코사인)");
            var fts = r.fasttextSimilarity;
            for (var k = 0; k < fts.a.Length; k++)
            {
                var ok = d.TryFastTextSimilarity(fts.a[k], fts.b[k], out var v);
                var e64 = Math.Abs(v - PackageFormat.ParseDoubleBits(fts.f64Bits[k]));
                var e32 = Math.Abs(v - PackageFormat.FloatFromBits(fts.f32Bits[k]));
                report.FastTextMaxErrorVsFloat64 = Math.Max(report.FastTextMaxErrorVsFloat64, e64);
                report.FastTextMaxErrorVsNumpyFloat32 = Math.Max(report.FastTextMaxErrorVsNumpyFloat32, e32);
                Expect(c, ok && e64 <= Float64Tolerance && e32 <= NumpyFloat32Tolerance, $"{d.GetWord(fts.a[k])}–{d.GetWord(fts.b[k])}");
            }
            c.Detail = $"최대 오차: float64 기준 {report.FastTextMaxErrorVsFloat64:E2} (허용 {Float64Tolerance:E0}), numpy float32 기준 {report.FastTextMaxErrorVsNumpyFloat32:E2} (허용 {NumpyFloat32Tolerance:E0})";

            c = Check(report, "E5 뜻 유사도");
            var es = r.e5Similarity;
            for (var k = 0; k < es.senseA.Length; k++)
            {
                var ok = d.TryGetSenseRow(es.senseA[k], out var ra) & d.TryGetSenseRow(es.senseB[k], out var rb);
                var v = ok ? d.E5Similarity(ra, rb) : double.NaN;
                var e64 = Math.Abs(v - PackageFormat.ParseDoubleBits(es.f64Bits[k]));
                var e32 = Math.Abs(v - PackageFormat.FloatFromBits(es.f32Bits[k]));
                report.E5MaxErrorVsFloat64 = Math.Max(report.E5MaxErrorVsFloat64, e64);
                report.E5MaxErrorVsNumpyFloat32 = Math.Max(report.E5MaxErrorVsNumpyFloat32, e32);
                Expect(c, ok && e64 <= Float64Tolerance && e32 <= NumpyFloat32Tolerance, $"{es.senseA[k]}–{es.senseB[k]}");
            }
            c.Detail = $"최대 오차: float64 기준 {report.E5MaxErrorVsFloat64:E2}, numpy float32 기준 {report.E5MaxErrorVsNumpyFloat32:E2}";

            c = Check(report, "fastText 제한 표기");
            var restricted = new List<int>();
            for (var w = 0; w < d.WordCount; w++) if (d.IsRestricted(w)) restricted.Add(w);
            Expect(c, SameSequence(restricted, r.restricted.ranks, 0, r.restricted.ranks.Length), $"Unity {restricted.Count} / Python {r.restricted.ranks.Length}");
            var outside = new List<string>();
            foreach (var e in d.RestrictedInfo.entries) if (e.rank < 0) outside.Add(e.word);
            outside.Sort(string.CompareOrdinal);
            Expect(c, string.Join("\n", outside) == string.Join("\n", r.restricted.outsideVocabulary), "어휘 밖 제한 표기");
            c.Detail = $"어휘 안 {restricted.Count}개, 어휘 밖 {outside.Count}개";

            c = Check(report, "실제 적용 설정(load_config 결과)");
            foreach (var x in r.settings)
                Expect(c, d.Settings.TryGetJson(x.key, out var json) && json == x.valueJson, $"{x.key}: Python {x.valueJson}");
            Expect(c, r.settings.Length == d.Settings.Entries.Count, "항목 수");

            report.Seconds = watch.Elapsed.TotalSeconds;
            return report;
        }

        static void CompareCategories(ComparisonReport report, WordRoomDataset d, ReferenceDocument r)
        {
            var rel = d.Relations;
            var t0 = d.ThresholdLadder[0];
            // Word pairs with at least one stored record, recomputed here from the package records.
            var touched = new SortedSet<long>();
            for (var k = 0; k < rel.RecordCount; k++)
            {
                var j = rel.GetRecord(k);
                int a = d.WordOfSense(j.SenseRowA), b = d.WordOfSense(j.SenseRowB);
                if (a != b) touched.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
            }
            var counts = new Dictionary<string, int>();
            foreach (var key in touched)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
                int approved = 0, no = 0, missing = 0, combos = 0;
                foreach (var x in rel.GetCombinations(a, b, t0))
                {
                    combos++;
                    if (x.State == WordPairState.Approved) approved++;
                    else if (x.State == WordPairState.Rejected) no++;
                    else if (x.State == WordPairState.Unchecked) missing++;
                }
                var state = rel.GetWordPairState(a, b, t0);
                void Tag(string name, bool on)
                {
                    if (on) counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
                }
                Tag("multi_combo_one_approved", approved == 1 && combos > 1);
                Tag("approved_with_no", state == WordPairState.Approved && no > 0);
                Tag("approved_with_unchecked", state == WordPairState.Approved && missing > 0);
                Tag("all_no", state == WordPairState.Rejected);
                Tag("unchecked_remaining", state != WordPairState.Approved && missing > 0);
                Tag("below_threshold_yes", state == WordPairState.BelowThreshold);
                Tag("error_or_partial", state == WordPairState.ErrorOrIncomplete);
            }
            foreach (var cat in r.categories)
            {
                var c = Check(report, $"범주 {Korean(cat.name)}");
                counts.TryGetValue(cat.name, out var ours);
                Expect(c, ours == cat.count, $"개수 Unity {ours:N0} / Python {cat.count:N0}");
                for (var k = 0; k < cat.a.Length; k++)
                {
                    var got = rel.GetWordPairState(cat.a[k], cat.b[k], d.ThresholdLadder[cat.thresholdIndex]);
                    Expect(c, PythonName(got) == cat.states[k], $"{d.GetWord(cat.a[k])}–{d.GetWord(cat.b[k])}");
                }
                c.Detail = $"{cat.count:N0}쌍 (기준 {d.ThresholdLadder[cat.thresholdIndex]}), 예시 {cat.a.Length}쌍 상태 확인" + (cat.count == 0 ? " · 실데이터에 없음" : "");
            }
        }

        static string Korean(string name)
        {
            switch (name)
            {
                case "multi_combo_one_approved": return "여러 뜻 조합 중 하나만 승인";
                case "approved_with_no": return "NO 조합이 있어도 승인";
                case "approved_with_unchecked": return "미검사 조합이 있어도 승인";
                case "all_no": return "전부 NO";
                case "unchecked_remaining": return "미검사 조합이 남은 미승인";
                case "below_threshold_yes": return "기준 미달 YES";
                case "error_or_partial": return "오류·불완전 판정";
                default: return name;
            }
        }

        static string PythonName(WordPairState s)
        {
            switch (s)
            {
                case WordPairState.Approved: return "approved";
                case WordPairState.BelowThreshold: return "below";
                case WordPairState.ErrorOrIncomplete: return "error";
                case WordPairState.Unchecked: return "unchecked";
                default: return "rejected";
            }
        }

        static void CompareMatrix(ComparisonReport report, string name, float[] data, int cols, RefMatrix m, Func<string, int> rowOf)
        {
            var c = Check(report, name);
            Expect(c, data.Length == (long)m.rows * m.cols && cols == m.cols, $"{m.rows}×{m.cols}");
            var sha = Sha(data, 0, data.Length);
            Expect(c, sha == m.sha256, $"전체 바이트 SHA-256 {Short(sha)} / Python {Short(m.sha256)}");
            foreach (var x in m.samples)
            {
                var row = rowOf(x.key);
                var ok = row >= 0 && Sha(data, row * cols, cols) == x.rowSha256;
                for (var i = 0; ok && i < x.leadingBits.Length; i++) ok = BitConverter.SingleToInt32Bits(data[row * cols + i]) == x.leadingBits[i];
                Expect(c, ok, $"{x.key} 행");
            }
            c.Detail = $"전체 {data.Length * 4L:N0}바이트 해시 일치 + ID로 찾은 표본 {m.samples.Length}행";
        }

        static string BitsOrEmpty(double value, string answer) => answer == null ? "" : PackageFormat.DoubleBits(value);

        static CheckResult Check(ComparisonReport report, string name)
        {
            var c = new CheckResult { Name = name, Passed = true };
            report.Checks.Add(c);
            return c;
        }

        static void Expect(CheckResult c, bool ok, string what)
        {
            c.Compared++;
            if (ok) return;
            c.Passed = false;
            c.Mismatches++;
            if (c.Examples.Count < 5) c.Examples.Add(what);
        }

        static bool SameSequence(IReadOnlyList<int> a, int[] b, int offset, int count)
        {
            if (a.Count != count) return false;
            for (var i = 0; i < count; i++) if (a[i] != b[offset + i]) return false;
            return true;
        }

        static bool SameSequence(IReadOnlyList<int> a, int[] b) => SameSequence(a, b, 0, b.Length);

        static string Short(string id) => string.IsNullOrEmpty(id) ? "(없음)" : id.Substring(0, Math.Min(12, id.Length));

        internal static string Sha(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        internal static string Sha(float[] data, int start, int count)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1 << 20];
                var perChunk = buffer.Length / 4;
                for (var i = 0; i < count; i += perChunk)
                {
                    var n = Math.Min(perChunk, count - i);
                    Buffer.BlockCopy(data, (start + i) * 4, buffer, 0, n * 4);
                    sha.TransformBlock(buffer, 0, n * 4, null, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Hex(sha.Hash);
            }
        }

        static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
