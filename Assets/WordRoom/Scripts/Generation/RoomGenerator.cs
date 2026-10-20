using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Generation
{
    /// <summary>
    /// Port of kw_semantic_engine.Simulation 1.1.0 (generation and moves, without the automatic player).
    /// Rooms are surfaces; every neighbor needs its own directly approved sense pair (B rule, no transitivity).
    /// The empty neighbors of the room the player stands in are planned together: relation DB with the threshold
    /// ladder (backtracking within placement_budget per step), then the fastText supplement. Committed rooms never
    /// change. Requests are serialized; all reads come from WordRoomDataset (no second data load).
    /// Python names are given in the comments of each method.
    /// </summary>
    public sealed class RoomGenerator
    {
        const int FtTop = 64;            // FT_TOP
        const double TierDelta = 0.05;   // TIER_DELTA

        readonly WordRoomDataset d;
        readonly RelationIndex rel;
        readonly GenerationConfig cfg;
        readonly double[] ladder;
        readonly bool[] allowedMask;
        readonly bool[] restricted;
        readonly double[] closeness;
        readonly object gate = new object();
        readonly Dictionary<(int, int, double), (int i, int j, double s)> evidenceCache = new Dictionary<(int, int, double), (int, int, double)>();
        readonly Dictionary<int, float[]> ftSimsCache = new Dictionary<int, float[]>();
        readonly Queue<int> ftSimsOrder = new Queue<int>();
        readonly Dictionary<int, HashSet<int>> rejectedCache = new Dictionary<int, HashSet<int>>();
        Dictionary<long, float> noveltyCache = new Dictionary<long, float>();
        CancellationToken cancel;

        public RoomMap Map { get; }
        public GenerationConfig Config => cfg;
        public IReadOnlyList<double> Ladder => ladder;
        public VocabularyScope Scope { get; }
        public WordRoomDataset Data => d;
        public bool Started { get; private set; }

        RoomGenerator(WordRoomDataset data, GenerationConfig config, VocabularyScope scope, int start, int target, double[] closeness, bool[] restricted)
        {
            d = data;
            rel = data.Relations;
            cfg = config;
            Scope = scope;
            ladder = config.ThresholdLadder(data);
            allowedMask = new bool[data.WordCount];
            foreach (var w in scope.Ranks) allowedMask[w] = true;
            this.restricted = restricted;
            this.closeness = closeness;
            Map = new RoomMap(start, target, config.RecentWindow);
        }

        /// <summary>
        /// Simulation.__init__: validates the settings and the two surfaces, copies the configuration and computes the
        /// target closeness when guidance is on. Throws ArgumentException with the reason for invalid input.
        /// </summary>
        public static RoomGenerator Create(WordRoomDataset data, GenerationConfig config, int start, int target, CancellationToken cancel = default)
        {
            var cfg = config.Clone();
            cfg.Validate();
            if (start == target) throw new ArgumentException("시작과 목표 단어가 같을 수 없습니다.");
            if ((uint)start >= (uint)data.WordCount || (uint)target >= (uint)data.WordCount) throw new ArgumentException("잘못된 단어 번호");
            var scope = data.CreateScope(cfg.VocabularyLimit, cfg.MinWordLength, cfg.MaxWordLength);
            if (!scope.Contains(start) || !scope.Contains(target))
                throw new ArgumentException("A 또는 B가 현재 어휘 범위에 없습니다. 범위를 넓히거나 다른 단어를 선택하세요. " +
                                            $"({data.GetWord(start)}: {(scope.Contains(start) ? "범위 안" : "범위 밖")}, {data.GetWord(target)}: {(scope.Contains(target) ? "범위 안" : "범위 밖")}, {scope})");
            var restricted = new bool[data.WordCount];
            for (var w = 0; w < data.WordCount; w++) restricted[w] = data.IsRestricted(w);
            var allowed = new bool[data.WordCount];
            foreach (var w in scope.Ranks) allowed[w] = true;
            var close = cfg.TargetGuidance ? TargetCloseness.Compute(data, target, restricted, allowed, cancel) : null;
            return new RoomGenerator(data, cfg, scope, start, target, close, restricted);
        }

        // ---- requests (serialized) ------------------------------------------------------------------------
        /// <summary>Simulation.reserve: fill the start room's neighbors. Call once.</summary>
        public GenerationUpdate Start(CancellationToken token = default)
        {
            lock (gate)
            {
                if (Started) throw new InvalidOperationException("이미 시작한 실험입니다.");
                cancel = token;
                Started = true;
                var update = new GenerationUpdate { Kind = "start", From = Map.Position, To = Map.Position, Moved = false, FirstVisit = true };
                update.Fill = Fill(Map.Position);
                if (!HasAnyDoor(Map.Position))
                {
                    Map.Status = "no_exit";
                    Map.FailureReason = "시작 방 주변에 생성된 방이 하나도 없어 이동할 수 없습니다. " + Describe(update.Fill);
                }
                Finish(update);
                return update;
            }
        }

        /// <summary>
        /// Simulation.step(destination): move into an existing adjacent room, then fill its empty neighbors.
        /// A failed fill never undoes the move or an arrival. Returns null when the move is not possible (reason).
        /// </summary>
        public GenerationUpdate Move(Direction direction, out string refusal, CancellationToken token = default)
        {
            lock (gate)
            {
                cancel = token;
                refusal = null;
                if (!Started)
                {
                    refusal = "실험이 아직 시작되지 않았습니다.";
                    return null;
                }
                if (Map.Status != "running")
                {
                    refusal = $"실험이 끝났습니다 ({StatusText.Korean(Map.Status)}).";
                    return null;
                }
                if (!HasAnyDoor(Map.Position))
                {
                    Map.Status = "no_exit";
                    Map.FailureReason = "현재 방에 생성된 인접 방이 없습니다.";
                    refusal = Map.FailureReason;
                    return null;
                }
                var dest = Map.Position.Add(Directions.Offset(direction));
                if (!Map.TryGetWord(dest, out var word))
                {
                    refusal = $"{Directions.Korean(direction)} 방향 {dest}은 미생성 좌표라 이동할 수 없습니다." +
                              (Map.IsUnfilled(Map.Position) ? " (이 방의 주변 생성이 완성되지 못한 위치)" : "");
                    return null;
                }
                var from = Map.Position;
                var fresh = Map.Enter(dest);
                if (word == Map.Target) Map.Status = "success";
                else if (cfg.UseMoveLimit && Map.Moves >= cfg.MaxMoves) Map.Status = "move_limit";   // test-only; normal play has no limit
                var update = new GenerationUpdate { Kind = "move", From = from, To = dest, Moved = true, FirstVisit = fresh };
                update.Fill = Fill(dest);       // also for the target room (1.1.0: arrival is kept, neighbors filled anyway)
                Finish(update);
                return update;
            }
        }

        void Finish(GenerationUpdate u)
        {
            u.Moves = Map.Moves;
            u.Status = Map.Status;
            u.Arrived = Map.Status == "success";
            u.GuidanceStrength = Guidance();
        }

        bool HasAnyDoor(GridPos p)
        {
            foreach (var q in Directions.Around(p))
                if (Map.HasRoom(q)) return true;
            return false;
        }

        void CheckCancel() => cancel.ThrowIfCancellationRequested();

        // ---- target guidance ------------------------------------------------------------------------------
        /// <summary>Simulation.guidance: strength until guidance_start, then a linear ramp to guidance_max.</summary>
        public double Guidance()
        {
            if (!cfg.TargetGuidance) return 0.0;
            var progress = cfg.GuidanceBasis == "unique" ? Map.UniqueVisited : Map.Moves;
            if (progress < cfg.GuidanceStart) return cfg.GuidanceStrength;
            var part = cfg.GuidanceRamp == 0 ? 1.0 : Math.Min(1.0, (double)(progress - cfg.GuidanceStart) / cfg.GuidanceRamp);
            return cfg.GuidanceStrength + (cfg.GuidanceMax - cfg.GuidanceStrength) * part;
        }

        double Closeness(int word) => closeness != null ? closeness[word] : 0.0;

        // ---- relation lookup ------------------------------------------------------------------------------
        sealed class Assigned
        {
            public GridPos Pos;
            public int Word;
            public RankedCandidate Info;
            public int Count;
        }

        sealed class RankedCandidate
        {
            public int Word;
            public double Score;
            public double BaseScore;
            public double Bonus;
            public List<(GridPos q, int i, int j, double s)> Evidence;
        }

        /// <summary>Simulation.evidence: highest YES score, then E5 similarity (float32), then sense rows.</summary>
        (int i, int j, double s) Evidence(int word, int neighbor, double threshold)
        {
            if (evidenceCache.TryGetValue((word, neighbor, threshold), out var cached)) return cached;
            var found = false;
            (int i, int j, double s) best = default;
            var bestE5 = 0f;
            foreach (var e in rel.GetEvidence(word, neighbor, threshold))
            {
                var e5 = EngineMath.Dot32(d.GetE5Vector(e.SenseRow), d.GetE5Vector(e.NeighborSenseRow));
                var better = !found || e.Score > best.s || (e.Score == best.s && (e5 > bestE5 || (e5 == bestE5 &&
                             (e.SenseRow < best.i || (e.SenseRow == best.i && e.NeighborSenseRow < best.j)))));
                if (!better) continue;
                best = (e.SenseRow, e.NeighborSenseRow, e.Score);
                bestE5 = e5;
                found = true;
            }
            if (!found) throw new InvalidOperationException($"승인되지 않은 단어 쌍의 근거 요청: {d.GetWord(word)}–{d.GetWord(neighbor)} @ {threshold}");
            evidenceCache[(word, neighbor, threshold)] = best;
            return best;
        }

        HashSet<int> Rejected(int word)
        {
            if (!rejectedCache.TryGetValue(word, out var set)) rejectedCache[word] = set = new HashSet<int>(rel.GetRejectedPartners(word));
            return set;
        }

        static string StateName(WordPairState s)
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

        // ---- planning (relation DB) -----------------------------------------------------------------------
        /// <summary>Simulation.neighbors_of: committed and tentative rooms adjacent to p, in DIRECTIONS order.</summary>
        List<(GridPos q, int w)> NeighborsOf(GridPos p, List<Assigned> assign)
        {
            var list = new List<(GridPos, int)>(4);
            foreach (var q in Directions.Around(p))
            {
                if (Map.TryGetWord(q, out var w)) list.Add((q, w));
                else
                    foreach (var a in assign)
                        if (a.Pos == q)
                        {
                            list.Add((q, a.Word));
                            break;
                        }
            }
            return list;
        }

        bool Near(GridPos p, GridPos q) => cfg.RepeatScope == "global" || GridPos.Distance(p, q) <= cfg.RepeatRadius;

        /// <summary>Surfaces of committed rooms within the repeat distance of p (avoid_adjacent_repeat).</summary>
        HashSet<int> CommittedBanned(GridPos p)
        {
            var banned = new HashSet<int>();
            if (!cfg.AvoidAdjacentRepeat) return banned;
            if (cfg.RepeatScope == "global")
            {
                foreach (var kv in Map.Rooms()) banned.Add(kv.Value);
                return banned;
            }
            var r = cfg.RepeatRadius;
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r + Math.Abs(dx); dy <= r - Math.Abs(dx); dy++)
                    if (Map.TryGetWord(new GridPos(p.X + dx, p.Y + dy), out var w)) banned.Add(w);
            return banned;
        }

        /// <summary>Simulation.candidates: approved with every neighbor, allowed, not repeated within the distance.</summary>
        (List<int> cands, List<(GridPos q, int w)> nb) Candidates(GridPos p, List<Assigned> assign, double threshold)
        {
            var nb = NeighborsOf(p, assign);
            var result = new List<int>();
            if (nb.Count == 0) return (result, nb);
            var found = new HashSet<int>(rel.GetApprovedNeighbors(nb[0].w, threshold));
            for (var k = 1; k < nb.Count; k++) found.IntersectWith(rel.GetApprovedNeighbors(nb[k].w, threshold));
            var banned = CommittedBanned(p);
            if (cfg.AvoidAdjacentRepeat)
                foreach (var a in assign)
                    if (Near(p, a.Pos)) banned.Add(a.Word);
            foreach (var w in found)
                if (allowedMask[w] && !banned.Contains(w)) result.Add(w);
            result.Sort();
            return (result, nb);
        }

        /// <summary>max over the senses of `word` of clip(v[sense]·v[other], 0, 1), float32.</summary>
        float MaxSim(int sense, int word)
        {
            var key = ((long)sense << 32) | (uint)word;
            if (noveltyCache.TryGetValue(key, out var v)) return v;
            var a = d.GetE5Vector(sense);
            var best = float.NegativeInfinity;
            foreach (var r in d.SenseRowsOf(word))
            {
                var s = EngineMath.Clip01(EngineMath.Dot32(a, d.GetE5Vector(r)));
                if (s > best) best = s;
            }
            noveltyCache[key] = best;
            return best;
        }

        /// <summary>
        /// Simulation.rank: candidates in try order -- the coordinate-seeded draw among the top selection_top first,
        /// then the rest by score. base = .5*relation + .5*quality + .1*diversity*novelty; score = base + guidance*closeness.
        /// </summary>
        List<RankedCandidate> Rank(GridPos p, List<int> cands, List<(GridPos q, int w)> nb, List<Assigned> assign, double threshold)
        {
            var recentWords = new HashSet<int>(Map.Recent);
            foreach (var a in assign) recentWords.Add(a.Word);
            var g = Guidance();
            var div = (float)(0.1 * cfg.Diversity);
            var list = new List<RankedCandidate>(cands.Count);
            foreach (var w in cands)
            {
                var ev = new List<(GridPos q, int i, int j, double s)>(nb.Count);
                var embSum = 0.0;
                var embMin = double.PositiveInfinity;
                var quality = double.PositiveInfinity;
                var senses = new SortedSet<int>();
                foreach (var (q, n) in nb)
                {
                    var (i, j, s) = Evidence(w, n, threshold);
                    ev.Add((q, i, j, s));
                    double emb = EngineMath.Dot32(d.GetE5Vector(i), d.GetE5Vector(j));
                    embSum += emb;
                    if (emb < embMin) embMin = emb;
                    if (s < quality) quality = s;
                    senses.Add(i);
                }
                var relation = (1 - cfg.NeighborWeight) * (embSum / nb.Count) + cfg.NeighborWeight * embMin;
                var maxRecent = float.NegativeInfinity;
                foreach (var s in senses)
                    foreach (var r in recentWords)
                    {
                        var m = MaxSim(s, r);
                        if (m > maxRecent) maxRecent = m;
                    }
                var novelty = (float)(1f - maxRecent);
                var baseScore = 0.5 * relation + 0.5 * quality + (float)(div * novelty);
                var bonus = g != 0 ? g * Closeness(w) : 0.0;
                list.Add(new RankedCandidate { Word = w, Score = baseScore + bonus, BaseScore = baseScore, Bonus = bonus, Evidence = ev });
            }
            list.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Word.CompareTo(b.Word));
            if (list.Count == 0) return list;
            var take = Math.Min(cfg.SelectionTop, list.Count);
            var first = 0;
            if (cfg.Temperature != 0)
            {
                var top = list[0].Score;
                var weights = new double[take];
                var sum = 0.0;
                for (var k = 0; k < take; k++)
                {
                    weights[k] = Math.Exp((list[k].Score - top) / cfg.Temperature);
                    sum += weights[k];
                }
                for (var k = 0; k < take; k++) weights[k] /= sum;
                first = PythonRandom.Choice(weights, PythonRandom.CoordinateUniform(cfg.Seed, p));
            }
            if (first != 0)
            {
                var chosen = list[first];
                list.RemoveAt(first);
                list.Insert(0, chosen);
            }
            return list;
        }

        sealed class BudgetExhausted : Exception
        {
        }

        sealed class PlanOutcome
        {
            public bool Ok;
            public double Threshold = double.NaN;
            public List<Assigned> Assign;
            public string Message;
        }

        /// <summary>Simulation.plan: thresholds high to low; each step backtracks within placement_budget nodes.</summary>
        PlanOutcome Plan(GridPos center, List<GridPos> needed, FillReport report)
        {
            foreach (var threshold in ladder)
            {
                CheckCancel();
                var step = new ThresholdStep { Threshold = threshold };
                report.Steps.Add(step);
                var empty = new List<Assigned>();
                var allHave = true;
                foreach (var p in needed)
                {
                    var c = Candidates(p, empty, threshold).cands.Count;
                    step.CandidateCounts.Add(new KeyValuePair<GridPos, int>(p, c));
                    if (c == 0) allHave = false;
                }
                if (!allHave)
                {
                    step.Result = "no_candidate";
                    foreach (var kv in step.CandidateCounts)
                        if (kv.Value == 0) step.Positions.Add(Report(kv.Key, threshold));
                    continue;
                }
                var assign = new List<Assigned>();
                var nodes = 0;
                bool Search(int k)
                {
                    if (k == needed.Count) return true;
                    var p = needed[k];
                    var (cands, nb) = Candidates(p, assign, threshold);
                    if (cands.Count == 0) return false;
                    foreach (var r in Rank(p, cands, nb, assign, threshold))
                    {
                        CheckCancel();
                        nodes++;
                        if (nodes > cfg.PlacementBudget) throw new BudgetExhausted();
                        assign.Add(new Assigned { Pos = p, Word = r.Word, Info = r, Count = cands.Count });
                        if (Search(k + 1)) return true;
                        assign.RemoveAt(assign.Count - 1);
                    }
                    return false;
                }
                bool? ok;
                try
                {
                    ok = Search(0);
                }
                catch (BudgetExhausted)
                {
                    ok = null;
                }
                step.Nodes = nodes;
                step.Result = ok == true ? "placed" : ok == null ? "budget_exhausted" : "placement_conflict";
                if (ok == true) return new PlanOutcome { Ok = true, Threshold = threshold, Assign = assign };
            }
            return new PlanOutcome { Ok = false, Message = Explain(center, report) };
        }

        /// <summary>Simulation.report: why p has no candidate at this threshold (committed neighbors only).</summary>
        PositionReport Report(GridPos p, double threshold)
        {
            var nb = NeighborsOf(p, new List<Assigned>());
            var outp = new PositionReport { Position = p };
            foreach (var (_, w) in nb) outp.Neighbors.Add(w);
            var pools = new List<HashSet<int>>();
            foreach (var (_, w) in nb)
            {
                var set = new HashSet<int>();
                foreach (var x in rel.GetApprovedNeighbors(w, threshold))
                    if (allowedMask[x]) set.Add(x);
                pools.Add(set);
            }
            var both = pools.Count > 0 ? new HashSet<int>(pools[0]) : new HashSet<int>();
            for (var k = 1; k < pools.Count; k++) both.IntersectWith(pools[k]);
            var banned = CommittedBanned(p);
            outp.Candidates = both.Count(w => !banned.Contains(w));
            outp.RepeatBanned = both.Count(banned.Contains);
            var partial = new HashSet<int>();
            foreach (var s in pools) partial.UnionWith(s);
            partial.ExceptWith(both);
            partial.ExceptWith(banned);
            outp.PartialCandidates = partial.Count;
            var counts = new List<KeyValuePair<string, int>>();
            foreach (var w in partial.OrderBy(x => x))
            {
                var states = new HashSet<string>();
                foreach (var (_, n) in nb) states.Add(StateName(rel.GetWordPairState(w, n, threshold)));
                var worst = "approved";
                if (states.Any(s => s != "approved"))
                    foreach (var s in new[] { "rejected", "below", "error", "unchecked" })
                        if (states.Contains(s))
                        {
                            worst = s;
                            break;
                        }
                var at = counts.FindIndex(kv => kv.Key == worst);
                if (at < 0) counts.Add(new KeyValuePair<string, int>(worst, 1));
                else counts[at] = new KeyValuePair<string, int>(worst, counts[at].Value + 1);
            }
            outp.BlockedBy.AddRange(counts);
            return outp;
        }

        static string G(double x) => x.ToString("0.##########", CultureInfo.InvariantCulture);

        /// <summary>Simulation.explain (same Korean text).</summary>
        string Explain(GridPos center, FillReport report)
        {
            var last = report.Steps[report.Steps.Count - 1];
            var kinds = new HashSet<string>(report.Steps.Select(s => s.Result));
            var text = new StringBuilder($"{center} 주변 4방향을 기준 {G(ladder[0])}~{G(ladder[ladder.Length - 1])}의 모든 단계에서 완성하지 못했습니다");
            if (last.Result == "no_candidate")
            {
                var parts = new List<string>();
                foreach (var r in last.Positions)
                {
                    var why = string.Join(", ", r.BlockedBy.Select(kv => $"{StatusText.State(kv.Key)} {kv.Value}"));
                    parts.Add($"{r.Position}: 후보 0 (이웃 {string.Join("·", r.Neighbors.Select(d.GetWord))}; 일부 이웃과만 승인된 후보 {r.PartialCandidates}개 — " +
                              $"{(why.Length > 0 ? why : "없음")}{(r.RepeatBanned > 0 ? $"; 거리 제한으로 제외 {r.RepeatBanned}" : "")})");
                }
                text.Append(". ").Append(string.Join(" / ", parts));
            }
            if (kinds.Contains("budget_exhausted")) text.Append(". 일부 단계는 검사 한도 내에서 배치를 찾지 못했습니다(가능한 배치가 없다는 뜻은 아님)");
            if (kinds.Contains("placement_conflict")) text.Append(". 일부 단계는 위치별 후보는 있었지만 서로 다른 표기로 함께 배치할 수 없었습니다");
            return text.Append(". 미검사 관계는 관계 DB 준비(교차 보충)가 필요합니다.").ToString();
        }

        /// <summary>Simulation.db_room: relation-DB links of a planned room.</summary>
        PlacedRoom DbRoom(Assigned a, double threshold)
        {
            var room = new PlacedRoom
            {
                Position = a.Pos, Word = a.Word, Stage = "relation_db", Threshold = threshold, Candidates = a.Count, Score = a.Info.Score,
                GuidanceStrength = Guidance(), GuidanceBonus = a.Info.Bonus,
            };
            foreach (var (q, i, j, s) in a.Info.Evidence)
            {
                room.Links.Add(new LinkEvidence
                {
                    Room = a.Pos, Neighbor = q, Source = "relation_db", SenseId = d.GetSenseId(i), NeighborSenseId = d.GetSenseId(j),
                    YesScore = s, Threshold = threshold, Relaxed = s < ladder[0] - 1e-9,
                });
            }
            return room;
        }

        /// <summary>Simulation.commit: all planned rooms at once; links keep their own source and evidence.</summary>
        void Commit(List<PlacedRoom> rooms, FillReport report)
        {
            foreach (var room in rooms)
            {
                Map.Commit(room);
                foreach (var l in room.Links)
                    if (Map.TryGetWord(l.Neighbor, out var nw)) l.NeighborWord = nw;
                room.Relaxed = room.Links.Any(l => l.Relaxed);
                room.FastText = room.Links.Any(l => l.Source == "fasttext");
                report.Added.Add(room);
            }
        }

        // ---- fastText supplement --------------------------------------------------------------------------
        /// <summary>Simulation.ft_sims: clip(ft @ ft[word], 0, 1), float32, for every surface (bounded cache).</summary>
        float[] FtSims(int word)
        {
            if (ftSimsCache.TryGetValue(word, out var sims)) return sims;
            var n = d.WordCount;
            sims = new float[n];
            var tv = d.GetFastTextVector(word);
            for (var x = 0; x < n; x++)
                sims[x] = d.HasFastText(x) ? EngineMath.Clip01(EngineMath.Dot32(d.GetFastTextVector(x), tv)) : 0f;
            ftSimsCache[word] = sims;
            ftSimsOrder.Enqueue(word);
            if (ftSimsOrder.Count > 256) ftSimsCache.Remove(ftSimsOrder.Dequeue());
            return sims;
        }

        sealed class FtCandidate
        {
            public int Word;
            public int Weak;
            public float Low;
            public int Count;
            public double Mean;
            public float DbMin;
            public bool[] Db;
            public float[] Sims;
            public bool[] Restricted;
            public bool[] StoredNo;
            public double Adjusted;
            public double Guidance;
            public double Closeness;
        }

        sealed class FtSummary
        {
            public int Candidates;
            public int Good;
        }

        /// <summary>
        /// Simulation.ft_candidates: candidates for p against its committed neighbors, best first. Approval and
        /// quality are kept apart: a relation-DB link means a stored YES at or above the floor and adds nothing to the
        /// quality. Order: weak fastText links, fastText links, weakest similarity (+ guidance), mean, DB YES, rank.
        /// </summary>
        (List<FtCandidate> list, FtSummary summary) FtCandidates(GridPos p, bool fastText)
        {
            var n = d.WordCount;
            var pref = (float)cfg.FastTextPreferredMin;
            var floor = ladder[ladder.Length - 1];
            var nb = NeighborsOf(p, new List<Assigned>());
            var k = nb.Count;
            var db = new bool[k][];
            var yes = new float[k][];
            var sims = new float[k][];
            var usable = new bool[n];
            for (var x = 0; x < n; x++) usable[x] = true;
            for (var t = 0; t < k; t++)
            {
                var w = nb[t].w;
                db[t] = new bool[n];
                yes[t] = new float[n];
                for (var x = 0; x < n; x++) yes[t][x] = 1f;
                foreach (var c in rel.GetApprovedNeighbors(w, floor))
                {
                    db[t][c] = true;
                    yes[t][c] = (float)rel.MaxYesScore(w, c);
                }
                var okW = fastText && d.HasFastText(w);
                sims[t] = okW ? FtSims(w) : null;
                for (var x = 0; x < n; x++) usable[x] &= db[t][x] || (fastText && okW && d.HasFastText(x));
            }
            var banned = CommittedBanned(p);
            var idx = new List<int>();
            for (var x = 0; x < n; x++)
                if (usable[x] && allowedMask[x] && !banned.Contains(x)) idx.Add(x);
            var summary = new FtSummary { Candidates = idx.Count };
            var list = new List<FtCandidate>(idx.Count);
            if (idx.Count == 0) return (list, summary);
            var g = Guidance();
            foreach (var x in idx)
            {
                var c = new FtCandidate { Word = x, Db = new bool[k], Sims = new float[k], Restricted = new bool[k], StoredNo = new bool[k] };
                var low = float.PositiveInfinity;
                var sum = 0f;
                var dbmin = 1f;
                for (var t = 0; t < k; t++)
                {
                    var D = db[t][x];
                    var S = sims[t] != null ? sims[t][x] : 0f;
                    c.Db[t] = D;
                    c.Sims[t] = S;
                    if (D)
                    {
                        if (yes[t][x] < dbmin) dbmin = yes[t][x];
                        continue;
                    }
                    var nw = nb[t].w;
                    c.Restricted[t] = restricted[x] || restricted[nw];
                    c.StoredNo[t] = Rejected(nw).Contains(x);
                    if (S < pref || c.Restricted[t] || c.StoredNo[t]) c.Weak++;
                    c.Count++;
                    if (S < low) low = S;
                    sum = (float)(sum + S);
                }
                c.Low = c.Count == 0 ? 1f : low;
                c.Mean = c.Count > 0 ? (double)sum / c.Count : 1.0;
                c.DbMin = dbmin;
                c.Guidance = g;
                c.Closeness = g != 0 ? Closeness(x) : 0.0;
                c.Adjusted = (double)c.Low + g * c.Closeness;
                if (c.Weak == 0) summary.Good++;
                list.Add(c);
            }
            list.Sort((a, b) =>
            {
                if (a.Weak != b.Weak) return a.Weak.CompareTo(b.Weak);
                if (a.Count != b.Count) return a.Count.CompareTo(b.Count);
                if (a.Adjusted != b.Adjusted) return b.Adjusted.CompareTo(a.Adjusted);
                if (a.Mean != b.Mean) return b.Mean.CompareTo(a.Mean);
                if (a.DbMin != b.DbMin) return b.DbMin.CompareTo(a.DbMin);
                return a.Word.CompareTo(b.Word);
            });
            var order = list.GetRange(0, Math.Min(FtTop, list.Count));
            if (g != 0 && !order.Any(c => c.Word == Map.Target))
            {
                var hit = list.Find(c => c.Word == Map.Target);
                if (hit != null) order.Add(hit);      // an eligible target is never cut off
            }
            var best = order[0];
            var tier = order.Where(c => c.Weak == best.Weak && c.Count == best.Count && c.Adjusted >= best.Adjusted - TierDelta)
                .Take(cfg.SelectionTop).ToList();
            if (cfg.Temperature > 0 && tier.Count > 1)
            {
                var recent = Map.Recent.Where(d.HasFastText).ToList();
                var div = (float)(0.1 * cfg.Diversity);
                var score = new double[tier.Count];
                for (var t = 0; t < tier.Count; t++)
                {
                    var novelty = 0f;
                    if (recent.Count > 0 && fastText)
                    {
                        var v = d.GetFastTextVector(tier[t].Word);
                        var m = float.NegativeInfinity;
                        foreach (var r in recent)
                        {
                            var s = EngineMath.Clip01(EngineMath.Dot32(v, d.GetFastTextVector(r)));
                            if (s > m) m = s;
                        }
                        novelty = (float)(1f - m);
                    }
                    score[t] = tier[t].Mean + (float)(div * novelty);
                }
                var top = score.Max();
                var weights = new double[tier.Count];
                var total = 0.0;
                for (var t = 0; t < tier.Count; t++)
                {
                    weights[t] = Math.Exp((score[t] - top) / cfg.Temperature);
                    total += weights[t];
                }
                for (var t = 0; t < tier.Count; t++) weights[t] /= total;
                var first = tier[PythonRandom.Choice(weights, PythonRandom.CoordinateUniform(cfg.Seed, p))];
                order.Remove(first);
                order.Insert(0, first);
            }
            return (order, summary);
        }

        /// <summary>Simulation.ft_links: link records for a chosen candidate; weak links used as a last resort say why.</summary>
        List<LinkEvidence> FtLinks(GridPos p, FtCandidate c, FtSummary summary, bool budgetHit)
        {
            var nb = NeighborsOf(p, new List<Assigned>());
            var floor = ladder[ladder.Length - 1];
            string reason = null;
            if (c.Weak > 0)
                reason = summary.Good == 0
                    ? "no candidate at this position meets the preferred fastText minimum without restricted surfaces"
                    : "better candidates conflicted with the other planned rooms" + (budgetHit ? " (search limit reached)" : "");
            var links = new List<LinkEvidence>();
            for (var t = 0; t < nb.Count; t++)
            {
                var (q, w) = nb[t];
                if (c.Db[t])
                {
                    var (i, j, y) = Evidence(c.Word, w, floor);
                    links.Add(new LinkEvidence
                    {
                        Room = p, Neighbor = q, Source = "relation_db", SenseId = d.GetSenseId(i), NeighborSenseId = d.GetSenseId(j),
                        YesScore = y, Threshold = floor, Relaxed = y < ladder[0] - 1e-9,
                    });
                }
                else
                {
                    var weak = (double)c.Sims[t] < cfg.FastTextPreferredMin || c.Restricted[t] || c.StoredNo[t];
                    links.Add(new LinkEvidence
                    {
                        Room = p, Neighbor = q, Source = "fasttext", Similarity = c.Sims[t], VectorVersion = d.Identity.fasttextVersion,
                        DbState = StateName(rel.GetWordPairState(c.Word, w, floor)), Weak = weak, Restricted = c.Restricted[t],
                        LastResort = weak, LastResortReason = weak ? reason : null,
                    });
                }
            }
            return links;
        }

        readonly struct PlacementKey
        {
            public readonly int Empty, Weak, FtLinks;
            public readonly double NegWeakest, NegMean;

            public PlacementKey(int empty, int weak, int ft, double negWeakest, double negMean)
            {
                Empty = empty;
                Weak = weak;
                FtLinks = ft;
                NegWeakest = negWeakest;
                NegMean = negMean;
            }

            public int Compare4(PlacementKey o)
            {
                if (Empty != o.Empty) return Empty.CompareTo(o.Empty);
                if (Weak != o.Weak) return Weak.CompareTo(o.Weak);
                if (FtLinks != o.FtLinks) return FtLinks.CompareTo(o.FtLinks);
                return NegWeakest.CompareTo(o.NegWeakest);
            }

            public int Compare(PlacementKey o)
            {
                var c = Compare4(o);
                return c != 0 ? c : NegMean.CompareTo(o.NegMean);
            }
        }

        sealed class Optimal : Exception
        {
        }

        /// <summary>
        /// Simulation.plan_fasttext: choose the whole placement -- fewest empty positions, then fewest weak fastText
        /// links, fewest fastText links, highest weakest value (after guidance), highest mean -- by branch and bound
        /// over the per-position lists within placement_budget nodes, allowing 0, 1, ... empty positions in turn.
        /// </summary>
        void PlanFasttext(List<GridPos> needed, bool fastText, FillReport report, List<PlacedRoom> placed)
        {
            var lists = new Dictionary<GridPos, List<FtCandidate>>();
            var summaries = new Dictionary<GridPos, FtSummary>();
            foreach (var p in needed)
            {
                CheckCancel();
                var (list, summary) = FtCandidates(p, fastText);
                lists[p] = list;
                summaries[p] = summary;
            }
            var radius = cfg.RepeatRadius;
            PlacementKey? bestKey = null;
            List<(GridPos p, FtCandidate c)> bestChosen = null;
            List<GridPos> bestEmpty = null;
            var nodes = 0;

            PlacementKey Key(List<(GridPos p, FtCandidate c)> chosen, List<GridPos> empty)
            {
                var weak = 0;
                var ftCount = 0;
                var ftSum = 0.0;
                var minAdj = double.PositiveInfinity;
                foreach (var (_, c) in chosen)
                {
                    weak += c.Weak;
                    for (var t = 0; t < c.Db.Length; t++)
                        if (!c.Db[t])
                        {
                            ftCount++;
                            ftSum += c.Sims[t];
                        }
                    if (c.Adjusted < minAdj) minAdj = c.Adjusted;
                }
                return new PlacementKey(empty.Count, weak, ftCount, -(chosen.Count > 0 ? minAdj : 1.0), -(ftCount > 0 ? ftSum / ftCount : 1.0));
            }

            void Dfs(int k, List<(GridPos p, FtCandidate c)> chosen, List<GridPos> empty, int left, int tops)
            {
                if (k == needed.Count)
                {
                    var kk = Key(chosen, empty);
                    if (bestKey == null || kk.Compare(bestKey.Value) < 0)
                    {
                        bestKey = kk;
                        bestChosen = new List<(GridPos, FtCandidate)>(chosen);
                        bestEmpty = new List<GridPos>(empty);
                    }
                    if (tops == needed.Count) throw new Optimal();
                    return;
                }
                var p = needed[k];
                var candidates = lists[p];
                for (var i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    var clash = false;
                    foreach (var (q, o) in chosen)
                        if (o.Word == c.Word && GridPos.Distance(p, q) <= radius)
                        {
                            clash = true;
                            break;
                        }
                    if (clash) continue;
                    chosen.Add((p, c));
                    if (bestKey != null && Key(chosen, empty).Compare4(bestKey.Value) >= 0)
                    {
                        chosen.RemoveAt(chosen.Count - 1);
                        continue;
                    }
                    nodes++;
                    if (nodes > cfg.PlacementBudget) throw new BudgetExhausted();
                    if (nodes % 256 == 0) CheckCancel();
                    Dfs(k + 1, chosen, empty, left, tops + (i == 0 ? 1 : 0));
                    chosen.RemoveAt(chosen.Count - 1);
                }
                if (left > 0) Dfs(k + 1, chosen, new List<GridPos>(empty) { p }, left - 1, tops);
            }

            var budgetHit = false;
            for (var skips = 0; skips < needed.Count; skips++)
            {
                nodes = 0;
                string result;
                try
                {
                    Dfs(0, new List<(GridPos, FtCandidate)>(), new List<GridPos>(), skips, 0);
                    result = "searched";
                }
                catch (Optimal)
                {
                    result = "tier_best";
                }
                catch (BudgetExhausted)
                {
                    budgetHit = true;
                    result = bestKey != null ? "best_within_budget" : "none_within_budget";
                }
                report.SupplementAttempts.Add($"빈 위치 허용 {skips} · 노드 {nodes} · {(bestKey != null || result == "none_within_budget" ? result : "none")}");
                if (bestKey != null) break;
            }
            report.SupplementBudgetHit = budgetHit;
            if (bestKey == null)
            {
                foreach (var p in needed)
                    report.Empty.Add(new EmptyPosition { Position = p, Reason = budgetHit ? "no placement within the search limit" : "no candidate" });
                return;
            }
            var stage = fastText ? "fasttext" : "relation_db_partial";
            foreach (var (p, c) in bestChosen)
            {
                var room = new PlacedRoom
                {
                    Position = p, Word = c.Word, Stage = stage, Candidates = summaries[p].Candidates, Score = c.Adjusted,
                    GuidanceStrength = c.Guidance, GuidanceBonus = c.Guidance * c.Closeness,
                };
                room.Links.AddRange(FtLinks(p, c, summaries[p], budgetHit));
                placed.Add(room);
            }
            foreach (var p in bestEmpty)
                report.Empty.Add(new EmptyPosition { Position = p, Reason = lists[p].Count == 0 ? "no candidate" : "conflict with the other planned rooms" });
        }

        // ---- filling around the current room --------------------------------------------------------------
        /// <summary>
        /// Simulation.fill: relation DB with the threshold ladder, then the fastText supplement. A failure leaves the
        /// move in place and is recorded; the same room is retried only after the map has changed (room count).
        /// </summary>
        FillReport Fill(GridPos center)
        {
            var report = new FillReport { Center = center };
            var needed = Directions.Around(center).Where(q => !Map.HasRoom(q)).ToList();
            if (needed.Count == 0)
            {
                report.Complete = true;
                return report;
            }
            if (Map.Unfilled.TryGetValue(center, out var previous) && previous.Rooms == Map.RoomCount)
            {
                report.Stage = "skipped_same_state";
                report.Empty.AddRange(previous.Empty);
                return report;
            }
            var watch = Stopwatch.StartNew();
            noveltyCache = new Dictionary<long, float>();
            var plan = Plan(center, needed, report);
            var placed = new List<PlacedRoom>();
            if (plan.Ok)
            {
                foreach (var a in plan.Assign) placed.Add(DbRoom(a, plan.Threshold));
                Commit(placed, report);
                Map.Unfilled.Remove(center);
                report.Stage = "relation_db";
                report.Threshold = plan.Threshold;
                report.Complete = true;
                report.Seconds = watch.Elapsed.TotalSeconds;
                return report;
            }
            report.DbMessage = plan.Message;
            var useFt = cfg.FastTextSupplement;   // the package always carries fastText vectors
            report.Stage = useFt ? "fasttext" : "relation_db_partial";
            PlanFasttext(needed, useFt, report, placed);
            Commit(placed, report);
            var missing = needed.Where(q => !Map.HasRoom(q)).ToList();
            report.Complete = missing.Count == 0;
            if (report.Complete) Map.Unfilled.Remove(center);
            else Map.Unfilled[center] = new UnfilledRecord { Rooms = Map.RoomCount, Missing = missing, Empty = new List<EmptyPosition>(report.Empty) };
            report.Seconds = watch.Elapsed.TotalSeconds;
            return report;
        }

        string Describe(FillReport f) =>
            f.Empty.Count == 0 ? "" : string.Join(" / ", f.Empty.Select(e => $"{e.Position}: {e.Reason}"));
    }
}
