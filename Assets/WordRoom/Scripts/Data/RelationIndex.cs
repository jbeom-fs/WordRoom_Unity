using System;
using System.Collections.Generic;

namespace WordRoom.Data
{
    /// <summary>Word-pair relation state, same meaning and priority as kw_relations.RelationDB.word_state.</summary>
    public enum WordPairState
    {
        /// <summary>At least one sense combination is judged, YES (B rule) and its score is at or above the threshold.</summary>
        Approved,
        /// <summary>A stored YES exists but every such score is under the threshold.</summary>
        BelowThreshold,
        /// <summary>An invalid answer or a judgment of one input order only (partial).</summary>
        ErrorOrIncomplete,
        /// <summary>Some current sense combination was never judged.</summary>
        Unchecked,
        /// <summary>Every current sense combination was judged and none answered YES.</summary>
        Rejected,
    }

    public static class WordPairStateCodes
    {
        /// <summary>One-letter codes used by the Python baseline (A/B/E/U/R).</summary>
        public static char Code(WordPairState s) => "ABEUR"[(int)s];

        public static string Korean(WordPairState s)
        {
            switch (s)
            {
                case WordPairState.Approved: return "승인";
                case WordPairState.BelowThreshold: return "저장된 YES 점수가 기준 미만";
                case WordPairState.ErrorOrIncomplete: return "판정 오류·불완전";
                case WordPairState.Unchecked: return "미검사";
                default: return "NO 판정(모든 뜻 조합)";
            }
        }
    }

    /// <summary>One stored sense-pair judgment. SenseRowA/B follow the stored column order: forward = A then B.</summary>
    public readonly struct SensePairJudgment
    {
        public readonly int RecordIndex;
        public readonly int SenseRowA;
        public readonly int SenseRowB;
        public readonly string Status;
        public readonly bool IsJudged;
        public readonly bool ValidYes;
        /// <summary>Stored score: the highest YES score when ValidYes, otherwise the stored maximum (not a probability).</summary>
        public readonly double Score;
        public readonly string ForwardAnswer;
        public readonly double ForwardScore;
        public readonly string BackwardAnswer;
        public readonly double BackwardScore;
        public readonly string Method;
        public readonly string CreatedAt;
        public readonly bool HasBackwardSkipped;
        public readonly bool BackwardSkipped;
        public readonly bool HasCachedScores;
        public readonly string CachedForwardAnswer;
        public readonly double CachedForwardScore;
        public readonly string CachedBackwardAnswer;
        public readonly double CachedBackwardScore;

        internal SensePairJudgment(RelationIndex r, int k)
        {
            RecordIndex = k;
            SenseRowA = r.recA[k];
            SenseRowB = r.recB[k];
            Status = r.statusNames[r.recStatus[k]];
            IsJudged = r.recStatus[k] == r.judgedCode;
            ValidYes = r.recValid[k] != 0;
            Score = r.recScore[k];
            ForwardAnswer = r.Answer(r.recFa[k]);
            ForwardScore = r.recFs[k];
            BackwardAnswer = r.Answer(r.recBa[k]);
            BackwardScore = r.recBs[k];
            Method = r.methodNames[r.recMethod[k]];
            CreatedAt = r.createdAt[r.recCreated[k]];
            var f = r.recFlags[k];
            HasBackwardSkipped = (f & PackageFormat.FlagSkippedPresent) != 0;
            BackwardSkipped = (f & PackageFormat.FlagSkippedValue) != 0;
            HasCachedScores = (f & PackageFormat.FlagCached) != 0;
            CachedForwardAnswer = r.Answer(r.recCfa[k]);
            CachedForwardScore = r.recCfs[k];
            CachedBackwardAnswer = r.Answer(r.recCba[k]);
            CachedBackwardScore = r.recCbs[k];
        }

        /// <summary>B rule (either-order-yes-v1) at a threshold.</summary>
        public bool IsApprovedAt(double threshold) => IsJudged && ValidYes && Score >= threshold;
    }

    /// <summary>Evidence of one approved word pair: the sense of the word, the sense of the neighbor and the YES score.</summary>
    public readonly struct RelationEvidence
    {
        public readonly int SenseRow;
        public readonly int NeighborSenseRow;
        public readonly double Score;
        public readonly int RecordIndex;

        public RelationEvidence(int senseRow, int neighborSenseRow, double score, int recordIndex)
        {
            SenseRow = senseRow;
            NeighborSenseRow = neighborSenseRow;
            Score = score;
            RecordIndex = recordIndex;
        }
    }

    /// <summary>State of one sense combination of a word pair (for display).</summary>
    public readonly struct SenseCombination
    {
        public readonly int SenseRow;
        public readonly int NeighborSenseRow;
        /// <summary>-1 when the combination was never judged.</summary>
        public readonly int RecordIndex;
        public readonly WordPairState State;

        public SenseCombination(int senseRow, int neighborSenseRow, int recordIndex, WordPairState state)
        {
            SenseRow = senseRow;
            NeighborSenseRow = neighborSenseRow;
            RecordIndex = recordIndex;
            State = state;
        }
    }

    /// <summary>
    /// Stored sense-pair judgments and the word graph derived from them. Unchecked combinations have no record;
    /// a word-pair state is computed from the records that exist and the sense lists of both words.
    /// </summary>
    public sealed class RelationIndex
    {
        internal readonly int[] recA, recB;
        internal readonly byte[] recStatus, recValid, recFa, recBa, recMethod, recFlags, recCfa, recCba;
        internal readonly ushort[] recCreated;
        internal readonly double[] recScore, recFs, recBs, recCfs, recCbs;
        internal readonly string[] statusNames, answerNames, methodNames, createdAt;
        internal readonly byte judgedCode;

        readonly WordRoomDataset data;
        readonly Dictionary<long, int> pairIndex;
        // Word graph over judged YES records between different surfaces (CSR): per word, neighbors ascending by rank,
        // each with its highest YES score and the evidence records of that word pair.
        readonly int[] adjStart, adjNeighbor, evStart, evRecord;
        readonly double[] adjMaxScore;
        Dictionary<int, HashSet<int>> rejected;

        internal RelationIndex(WordRoomDataset data, RelationRecords r, string[] statusNames, string[] answerNames,
            string[] methodNames, string[] createdAt, string approvedStatusName)
        {
            this.data = data;
            recA = r.A; recB = r.B; recStatus = r.Status; recValid = r.Valid; recFa = r.Fa; recBa = r.Ba; recMethod = r.Method;
            recFlags = r.Flags; recCfa = r.Cfa; recCba = r.Cba; recCreated = r.Created; recScore = r.Score; recFs = r.Fs;
            recBs = r.Bs; recCfs = r.Cfs; recCbs = r.Cbs;
            this.statusNames = statusNames;
            this.answerNames = answerNames;
            this.methodNames = methodNames;
            this.createdAt = createdAt;
            var judged = Array.IndexOf(statusNames, approvedStatusName);
            judgedCode = judged < 0 ? byte.MaxValue : (byte)judged;

            var n = recA.Length;
            pairIndex = new Dictionary<long, int>(n);
            for (var k = 0; k < n; k++)
            {
                if (!pairIndex.ContainsKey(Key(recA[k], recB[k]))) pairIndex.Add(Key(recA[k], recB[k]), k);
                else throw new WordRoomDataException(DataErrorCode.DuplicateEntry, PackageFormat.Relations,
                    $"같은 뜻 쌍 레코드가 두 번 있습니다 (뜻 행 {recA[k]}, {recB[k]}).");
            }

            // Directed edges (word, neighbor, record) for every judged YES record between two different surfaces.
            var words = data.WordCount;
            var degree = new int[words + 1];
            var edges = 0;
            for (var k = 0; k < n; k++)
            {
                if (!IsYes(k)) continue;
                var wa = data.WordOfSense(recA[k]);
                var wb = data.WordOfSense(recB[k]);
                if (wa == wb) continue;
                degree[wa]++;
                degree[wb]++;
                edges += 2;
            }
            var start = new int[words + 1];
            for (var w = 0; w < words; w++) start[w + 1] = start[w] + degree[w];
            var fill = (int[])start.Clone();
            var edgeNeighbor = new int[edges];
            var edgeRecord = new int[edges];
            for (var k = 0; k < n; k++)
            {
                if (!IsYes(k)) continue;
                var wa = data.WordOfSense(recA[k]);
                var wb = data.WordOfSense(recB[k]);
                if (wa == wb) continue;
                edgeNeighbor[fill[wa]] = wb; edgeRecord[fill[wa]++] = k;
                edgeNeighbor[fill[wb]] = wa; edgeRecord[fill[wb]++] = k;
            }
            // Sort each word's edges by (neighbor, record) and group them per neighbor.
            var groupStart = new List<int>();
            var groupNeighbor = new List<int>();
            var groupMax = new List<double>();
            adjStart = new int[words + 1];
            var keys = new long[edges];
            for (var w = 0; w < words; w++)
            {
                var s = start[w];
                var e = start[w + 1];
                for (var i = s; i < e; i++) keys[i] = ((long)edgeNeighbor[i] << 32) | (uint)edgeRecord[i];
                Array.Sort(keys, s, e - s);
                adjStart[w] = groupNeighbor.Count;
                for (var i = s; i < e; i++)
                {
                    var nb = (int)(keys[i] >> 32);
                    var rec = (int)(keys[i] & 0xffffffff);
                    edgeRecord[i] = rec;
                    if (groupNeighbor.Count == adjStart[w] || groupNeighbor[groupNeighbor.Count - 1] != nb)
                    {
                        groupNeighbor.Add(nb);
                        groupStart.Add(i);
                        groupMax.Add(recScore[rec]);
                    }
                    else if (recScore[rec] > groupMax[groupMax.Count - 1])
                    {
                        groupMax[groupMax.Count - 1] = recScore[rec];
                    }
                }
            }
            adjStart[words] = groupNeighbor.Count;
            groupStart.Add(edges);
            adjNeighbor = groupNeighbor.ToArray();
            adjMaxScore = groupMax.ToArray();
            evStart = groupStart.ToArray();
            evRecord = edgeRecord;
        }

        static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        bool IsYes(int k) => recStatus[k] == judgedCode && recValid[k] != 0;

        internal string Answer(byte code) => code == 0 ? null : answerNames[code - 1];

        public int RecordCount => recA.Length;
        public IReadOnlyList<string> StatusNames => statusNames;

        public SensePairJudgment GetRecord(int recordIndex) => new SensePairJudgment(this, recordIndex);

        /// <summary>Stored judgment of a sense pair in either order; false when the pair was never judged.</summary>
        public bool TryGetSensePair(int senseRowA, int senseRowB, out SensePairJudgment judgment)
        {
            if (pairIndex.TryGetValue(Key(senseRowA, senseRowB), out var k))
            {
                judgment = new SensePairJudgment(this, k);
                return true;
            }
            judgment = default;
            return false;
        }

        int FindGroup(int word, int neighbor)
        {
            int lo = adjStart[word], hi = adjStart[word + 1] - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var v = adjNeighbor[mid];
                if (v == neighbor) return mid;
                if (v < neighbor) lo = mid + 1;
                else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>True when some sense combination of the two surfaces is approved at the threshold (no transitivity).</summary>
        public bool IsApproved(int word, int neighbor, double threshold)
        {
            var g = FindGroup(word, neighbor);
            return g >= 0 && adjMaxScore[g] >= threshold;
        }

        /// <summary>Highest stored YES score between two surfaces, or NaN when there is none.</summary>
        public double MaxYesScore(int word, int neighbor)
        {
            var g = FindGroup(word, neighbor);
            return g >= 0 ? adjMaxScore[g] : double.NaN;
        }

        /// <summary>Same order of checks as RelationDB.word_state: approved, then below, error, unchecked, rejected.</summary>
        public WordPairState GetWordPairState(int word, int neighbor, double threshold)
        {
            if (IsApproved(word, neighbor, threshold)) return WordPairState.Approved;
            bool below = false, error = false, unchecked_ = false;
            var a = data.SenseRowsOf(word);
            var b = data.SenseRowsOf(neighbor);
            for (var x = 0; x < a.Length; x++)
            {
                for (var y = 0; y < b.Length; y++)
                {
                    if (!pairIndex.TryGetValue(Key(a[x], b[y]), out var k)) unchecked_ = true;
                    else if (recStatus[k] != judgedCode) error = true;
                    else if (recValid[k] != 0) below = true;
                }
            }
            if (below) return WordPairState.BelowThreshold;
            if (error) return WordPairState.ErrorOrIncomplete;
            if (unchecked_) return WordPairState.Unchecked;
            return WordPairState.Rejected;
        }

        /// <summary>Every sense combination of the pair with its own state at the threshold, in sense-row order.</summary>
        public List<SenseCombination> GetCombinations(int word, int neighbor, double threshold)
        {
            var list = new List<SenseCombination>();
            var a = data.SenseRowsOf(word);
            var b = data.SenseRowsOf(neighbor);
            for (var x = 0; x < a.Length; x++)
            {
                for (var y = 0; y < b.Length; y++)
                {
                    WordPairState s;
                    if (!pairIndex.TryGetValue(Key(a[x], b[y]), out var k)) { k = -1; s = WordPairState.Unchecked; }
                    else if (recStatus[k] != judgedCode) s = WordPairState.ErrorOrIncomplete;
                    else if (recValid[k] == 0) s = WordPairState.Rejected;
                    else s = recScore[k] >= threshold ? WordPairState.Approved : WordPairState.BelowThreshold;
                    list.Add(new SenseCombination(a[x], b[y], k, s));
                }
            }
            return list;
        }

        /// <summary>
        /// Approved neighbors of a surface at a threshold, ascending by frequency rank, optionally limited to a
        /// vocabulary scope (the generator's candidate rule: graph ∩ allowed surfaces). Results are appended.
        /// </summary>
        public void GetApprovedNeighbors(int word, double threshold, List<int> results, VocabularyScope scope = null)
        {
            for (var g = adjStart[word]; g < adjStart[word + 1]; g++)
            {
                if (adjMaxScore[g] >= threshold && (scope == null || scope.Contains(adjNeighbor[g]))) results.Add(adjNeighbor[g]);
            }
        }

        public List<int> GetApprovedNeighbors(int word, double threshold, VocabularyScope scope = null)
        {
            var list = new List<int>();
            GetApprovedNeighbors(word, threshold, list, scope);
            return list;
        }

        public int CountApprovedNeighbors(int word, double threshold, VocabularyScope scope = null)
        {
            var c = 0;
            for (var g = adjStart[word]; g < adjStart[word + 1]; g++)
                if (adjMaxScore[g] >= threshold && (scope == null || scope.Contains(adjNeighbor[g]))) c++;
            return c;
        }

        /// <summary>Approved sense pairs of an approved word pair, oriented (sense of word, sense of neighbor).</summary>
        public List<RelationEvidence> GetEvidence(int word, int neighbor, double threshold)
        {
            var list = new List<RelationEvidence>();
            var g = FindGroup(word, neighbor);
            if (g < 0) return list;
            for (var i = evStart[g]; i < evStart[g + 1]; i++)
            {
                var k = evRecord[i];
                if (recScore[k] < threshold) continue;
                var aIsWord = data.WordOfSense(recA[k]) == word;
                list.Add(new RelationEvidence(aIsWord ? recA[k] : recB[k], aIsWord ? recB[k] : recA[k], recScore[k], k));
            }
            return list;
        }

        /// <summary>
        /// Best evidence as kw_semantic_engine.Simulation.evidence chooses it: highest YES score, then highest E5
        /// similarity of the two senses, then lower sense rows. False when the pair is not approved at the threshold.
        /// </summary>
        public bool TryGetBestEvidence(int word, int neighbor, double threshold, out RelationEvidence best)
        {
            best = default;
            var found = false;
            var bestE5 = 0.0;
            foreach (var e in GetEvidence(word, neighbor, threshold))
            {
                var e5 = data.E5Similarity(e.SenseRow, e.NeighborSenseRow);
                if (!found || e.Score > best.Score || (e.Score == best.Score && (e5 > bestE5 || (e5 == bestE5 &&
                    (e.SenseRow < best.SenseRow || (e.SenseRow == best.SenseRow && e.NeighborSenseRow < best.NeighborSenseRow))))))
                {
                    best = e;
                    bestE5 = e5;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>Surfaces whose every current sense combination with `word` was judged NO (RelationDB.rejected).</summary>
        public IReadOnlyCollection<int> GetRejectedPartners(int word)
        {
            if (rejected == null) rejected = BuildRejected();
            return rejected.TryGetValue(word, out var set) ? (IReadOnlyCollection<int>)set : Array.Empty<int>();
        }

        Dictionary<int, HashSet<int>> BuildRejected()
        {
            var no = new Dictionary<long, int>();
            var yes = new HashSet<long>();
            for (var k = 0; k < recA.Length; k++)
            {
                var a = data.WordOfSense(recA[k]);
                var b = data.WordOfSense(recB[k]);
                if (a == b || recStatus[k] != judgedCode) continue;
                var key = Key(a, b);
                if (recValid[k] != 0) yes.Add(key);
                else no[key] = no.TryGetValue(key, out var c) ? c + 1 : 1;
            }
            var result = new Dictionary<int, HashSet<int>>();
            foreach (var kv in no)
            {
                var a = (int)(kv.Key >> 32);
                var b = (int)(kv.Key & 0xffffffff);
                if (yes.Contains(kv.Key) || kv.Value != data.SenseRowsOf(a).Length * data.SenseRowsOf(b).Length) continue;
                if (!result.TryGetValue(a, out var sa)) result[a] = sa = new HashSet<int>();
                if (!result.TryGetValue(b, out var sb)) result[b] = sb = new HashSet<int>();
                sa.Add(b);
                sb.Add(a);
            }
            return result;
        }

        /// <summary>Number of approved word pairs (each pair once) at a threshold.</summary>
        public int CountApprovedWordPairs(double threshold)
        {
            var c = 0;
            for (var g = 0; g < adjNeighbor.Length; g++) if (adjMaxScore[g] >= threshold) c++;
            return c / 2;
        }

    }

    internal sealed class RelationRecords
    {
        public int[] A, B;
        public byte[] Status, Valid, Fa, Ba, Method, Flags, Cfa, Cba;
        public ushort[] Created;
        public double[] Score, Fs, Bs, Cfs, Cbs;
    }
}
