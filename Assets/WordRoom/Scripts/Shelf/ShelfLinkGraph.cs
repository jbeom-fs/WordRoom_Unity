using System;
using System.Collections.Generic;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Which headword rooms can be left again, decided for the whole dictionary package, so the links shown in the
    /// reading screen and the rooms they lead to use the same result.
    /// A link candidate is a package word within the top `vocabularyLimit`, at least `minLength` long, not restricted,
    /// with a definition. A sense's link words are the candidates in its definition minus its own headword, limited to
    /// its link setting's selection, one per headword (first place). A headword room is one room for all of its senses:
    /// its exits are the link words of every sense together. A room is valid when it is the goal headword or at least
    /// one of its exits is a valid room. Computed as the largest such set: every room starts valid and rooms with no
    /// valid exit are removed until nothing changes (a removal can leave the rooms linking only to it without an exit).
    /// Rooms that only link among themselves in a loop stay valid, since each of them has an exit. Each room is removed
    /// at most once, so it always ends. A link word is shown as a link only when its room is valid.
    /// The link words of every room (the costly part) are extracted once, in the constructor. SetGoal then decides the
    /// valid rooms for one goal and, following the final links backwards from the goal (only links between valid rooms,
    /// exactly the links the parchments show), which rooms really lead to it: a room with exits that only loop among
    /// rooms never reaching the goal is no start. PickChallenge draws a goal with such rooms at a minimum shortest
    /// distance and a start among them.
    /// Built on a worker thread (the dataset is read only) and cancellable; use it from one thread at a time afterwards.
    /// </summary>
    internal sealed class ShelfLinkGraph
    {
        /// <summary>Link setting snapshot: a senseId (null: every sense of the word without its own setting) and its selection (null: all).</summary>
        public sealed class RoomRule
        {
            public string Word;
            public string SenseId;
            public HashSet<string> Links;
        }

        readonly WordRoomDataset d;
        readonly int limit;
        readonly int minLength;
        readonly List<RoomRule> roomRules;
        readonly Dictionary<string, int> candidates = new Dictionary<string, int>();  // surface → rank, or -1
        readonly Dictionary<string, bool> entries = new Dictionary<string, bool>();   // surface → registered headword
        readonly HashSet<(string, string)> parallelUses = new HashSet<(string, string)>();
        readonly List<int> rooms = new List<int>();            // candidate room ranks, in rank order
        readonly List<int>[] exitsOf;                          // room rank → its distinct link word ranks (before validity)
        readonly List<int>[] linkedFrom;                       // link word rank → rooms having it as a link word (each once)
        readonly bool[] valid;                                 // by word rank, for the current goal
        readonly int[] distance;                               // links from a room to the current goal (-1: never reaches it)
        readonly List<int> reaching = new List<int>();         // rooms (not the goal) that reach the current goal
        int goalRank = -1;

        /// <summary>Headword rooms that are link candidates (the rooms the graph is decided on).</summary>
        public int RoomCount => rooms.Count;
        /// <summary>Rooms dropped for the current goal (no exit to a valid room).</summary>
        public int RemovedCount { get; private set; }
        /// <summary>The current goal headword (SetGoal), or null.</summary>
        public string Goal => goalRank >= 0 ? d.GetWord(goalRank) : null;
        /// <summary>Rooms other than the goal from which links lead to the current goal: the start candidates.</summary>
        public int ReachingCount => reaching.Count;

        /// <param name="cancel">Checked while walking the room candidates; throws OperationCanceledException.</param>
        public ShelfLinkGraph(WordRoomDataset dataset, int vocabularyLimit, int minLength, List<RoomRule> roomRules, CancellationToken cancel)
        {
            d = dataset;
            limit = vocabularyLimit;
            this.minLength = minLength;
            this.roomRules = roomRules;
            valid = new bool[d.WordCount];
            distance = new int[d.WordCount];
            exitsOf = new List<int>[d.WordCount];
            linkedFrom = new List<int>[d.WordCount];
            for (var row = 0; row < d.SenseCount; row++)
            {
                cancel.ThrowIfCancellationRequested();
                var definition = d.GetSense(row).Definition;
                if (!string.IsNullOrEmpty(definition)) ShelfLinkText.AddParallelUses(definition, IsEntry, parallelUses);
            }
            Build(cancel);
        }

        // ---- candidates ----------------------------------------------------------------------------------------------

        /// <summary>A link candidate surface (exact package surface meeting the vocabulary rules).</summary>
        public bool IsCandidate(string surface) => CandidateRank(surface) >= 0;

        /// <summary>
        /// A registered headword of the package, whatever its rank, length or restriction — for deciding where an
        /// eojeol splits. The lookup normalizes, so the found surface must equal the spelling exactly.
        /// </summary>
        public bool IsEntry(string surface)
        {
            if (entries.TryGetValue(surface, out var ok)) return ok;
            ok = d.TryGetWordRank(surface, out var rank) && d.GetWord(rank) == surface;
            entries[surface] = ok;
            return ok;
        }

        /// <summary>
        /// The one extraction used everywhere (graph, reading text, link log): split on headwords with the split
        /// exceptions of the sense `senseId`, then keep link candidates.
        /// </summary>
        public List<ShelfTextPart> Extract(string displayDefinition, string senseId) =>
            ShelfLinkText.FindCandidates(displayDefinition, senseId, IsEntry, IsCandidate, HasParallelUse);

        bool HasParallelUse(string first, string second) =>
            parallelUses.Contains((first, second)) || parallelUses.Contains((second, first));

        /// <summary>Split exceptions that are ignored because their headword is not the eojeol's start or not registered.</summary>
        public List<string> ExceptionProblems() => ShelfLinkText.ExceptionProblems(IsEntry);

        int CandidateRank(string surface)
        {
            if (candidates.TryGetValue(surface, out var r)) return r;
            r = d.TryGetWordRank(surface, out var rank) && d.GetWord(rank) == surface && IsCandidateRank(rank) ? rank : -1;
            candidates[surface] = r;
            return r;
        }

        bool IsCandidateRank(int rank) =>
            rank < limit && d.GetWordLength(rank) >= minLength && !d.IsRestricted(rank) && HasDefinition(rank);

        bool HasDefinition(int rank)
        {
            foreach (var row in d.SenseRowsOf(rank))
                if (!string.IsNullOrEmpty(d.GetSense(row).Definition)) return true;
            return false;
        }

        /// <summary>Why a surface is no link candidate, or null when it is one.</summary>
        public string WhyNotCandidate(string surface)
        {
            if (!d.TryGetWordRank(surface, out var rank) || d.GetWord(rank) != surface) return "사전 패키지에 없는 표기";
            if (rank >= limit) return $"빈도 상위 {limit:N0}개 밖";
            if (d.GetWordLength(rank) < minLength) return $"{minLength}글자 미만";
            if (d.IsRestricted(rank)) return "제한어";
            return HasDefinition(rank) ? null : "뜻풀이 없음";
        }

        /// <summary>The link setting of a sense: the one with its senseId, else one of the word without a senseId.</summary>
        public RoomRule RuleOf(string word, string senseId)
        {
            RoomRule fallback = null;
            foreach (var r in roomRules)
            {
                if (r.Word != word) continue;
                if (r.SenseId == senseId) return r;
                if (r.SenseId == null && fallback == null) fallback = r;
            }
            return fallback;
        }

        /// <summary>
        /// Link words of a sense before validity: candidates of its display definition (Han parentheses removed, see
        /// ShelfLinkText.DisplayDefinition; positions are offsets in it) minus its own headword, selection applied,
        /// first place each.
        /// </summary>
        List<ShelfTextPart> CandidateLinks(SenseInfo sense)
        {
            var list = new List<ShelfTextPart>();
            if (string.IsNullOrEmpty(sense.Definition)) return list;
            var selection = RuleOf(sense.Word, sense.SenseId)?.Links;
            var seen = new HashSet<string>();
            foreach (var c in Extract(ShelfLinkText.DisplayDefinition(sense.Definition), sense.SenseId))
            {
                if (c.Target == sense.Word) continue;
                if (selection != null && !selection.Contains(c.Target)) continue;
                if (seen.Add(c.Target)) list.Add(c);
            }
            return list;
        }

        // ---- graph ---------------------------------------------------------------------------------------------------

        /// <summary>The link words of every candidate room, once (they do not depend on the goal).</summary>
        void Build(CancellationToken cancel)
        {
            var end = Math.Min(limit, d.WordCount);
            var targets = new HashSet<int>();
            for (var rank = 0; rank < end; rank++)
            {
                cancel.ThrowIfCancellationRequested();
                if (!IsCandidateRank(rank)) continue;
                targets.Clear();
                foreach (var row in d.GetSenseRows(rank))
                    foreach (var c in CandidateLinks(d.GetSense(row)))
                        targets.Add(CandidateRank(c.Target));
                exitsOf[rank] = new List<int>(targets);
                foreach (var t in targets) (linkedFrom[t] ??= new List<int>()).Add(rank);
                rooms.Add(rank);
            }
            // Every link word is a candidate, so it is one of the rooms above.
        }

        /// <summary>
        /// Makes `goal` (a link candidate headword) the goal: decides the valid rooms for it (its room is valid without
        /// an exit; the others need an exit to a valid room, removed until nothing changes) and the rooms that reach it.
        /// Following every final link backwards from the goal — a link of a valid room to a valid room, as the parchment
        /// shows it — gives each room's distance in links; a valid room never reached (a loop away from the goal)
        /// is no start. False, with nothing changed, when `goal` is no candidate.
        /// </summary>
        public bool SetGoal(string goal)
        {
            var g = goal != null ? CandidateRank(goal) : -1;
            if (g < 0) return false;
            SetGoal(g);
            return true;
        }

        void SetGoal(int g)
        {
            goalRank = g;
            Array.Clear(valid, 0, valid.Length);
            var exits = new int[d.WordCount];
            foreach (var r in rooms)
            {
                valid[r] = true;
                exits[r] = exitsOf[r].Count;
            }
            RemovedCount = 0;
            var queue = new Queue<int>();
            foreach (var r in rooms)
                if (exits[r] <= 0 && r != g) Remove(r, queue);
            while (queue.Count > 0)
            {
                var word = queue.Dequeue();
                if (linkedFrom[word] == null) continue;
                foreach (var room in linkedFrom[word])        // the word's room is gone: its links are no exits
                    if (valid[room] && --exits[room] <= 0 && room != g) Remove(room, queue);
            }

            // Backwards from the goal over final links only (both ends valid).
            for (var i = 0; i < distance.Length; i++) distance[i] = -1;
            reaching.Clear();
            distance[g] = 0;
            queue.Enqueue(g);
            while (queue.Count > 0)
            {
                var word = queue.Dequeue();
                if (linkedFrom[word] == null) continue;
                foreach (var room in linkedFrom[word])
                {
                    if (!valid[room] || distance[room] >= 0) continue;
                    distance[room] = distance[word] + 1;
                    reaching.Add(room);
                    queue.Enqueue(room);
                }
            }
        }

        void Remove(int rank, Queue<int> queue)
        {
            valid[rank] = false;
            RemovedCount++;
            queue.Enqueue(rank);
        }

        /// <summary>Links from `surface`'s room to the current goal along final links, or -1 when it never gets there.</summary>
        public int DistanceToGoal(string surface)
        {
            var rank = CandidateRank(surface);
            return rank >= 0 && goalRank >= 0 ? distance[rank] : -1;
        }

        /// <summary>
        /// Draws a random challenge whose shortest way is at least `minMoves` links: goals are tried in a random order
        /// among the candidate rooms; for each, SetGoal's backward search gives every room's shortest distance to it, and
        /// only rooms at `minMoves` or more are kept (exactly `minMoves` included). The first goal with such a room is
        /// taken and the start drawn uniformly from them; a goal without one (none reaches it, or all are nearer) is
        /// skipped. The graph is left set to that goal, `moves` is the start's shortest distance. False when no goal has
        /// one (the graph's goal is then undefined). `tried` counts the goals looked at.
        /// </summary>
        public bool PickChallenge(Random random, int minMoves, CancellationToken cancel, out string goal, out string start, out int moves, out int tried)
        {
            goal = start = null;
            moves = -1;
            tried = 0;
            var order = new List<int>(rooms);
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            foreach (var g in order)
            {
                cancel.ThrowIfCancellationRequested();
                tried++;
                if (linkedFrom[g] == null) continue;          // no room links it at all
                SetGoal(g);
                // `reaching` is in breadth-first order, so distances never decrease: the far rooms are its tail.
                var first = reaching.Count;
                while (first > 0 && distance[reaching[first - 1]] >= minMoves) first--;
                if (first == reaching.Count) continue;
                var pick = reaching[first + random.Next(reaching.Count - first)];
                goal = d.GetWord(g);
                start = d.GetWord(pick);
                moves = distance[pick];
                return true;
            }
            goalRank = -1;
            return false;
        }

        /// <summary>A link word whose headword room can be entered (it has an exit, or it is the goal).</summary>
        public bool IsValidDoor(string surface)
        {
            var rank = CandidateRank(surface);
            return rank >= 0 && valid[rank];
        }

        /// <summary>
        /// Headwords of the room's exits that lead to a valid room for the current goal: the connections the graph's
        /// distances use. Equal to the link targets of Doors over the room's senses (the reading screen's links).
        /// </summary>
        internal HashSet<string> ValidExits(string surface)
        {
            var set = new HashSet<string>();
            var rank = CandidateRank(surface);
            if (rank < 0 || exitsOf[rank] == null) return set;
            foreach (var t in exitsOf[rank])
                if (valid[t]) set.Add(d.GetWord(t));
            return set;
        }

        /// <summary>The links of a sense (offsets in its display definition): its candidate links that lead to a valid room, first place each.</summary>
        public List<ShelfTextPart> Doors(int row)
        {
            var links = CandidateLinks(d.GetSense(row));
            links.RemoveAll(c => !IsValidDoor(c.Target));
            return links;
        }

        // ---- lecterns ------------------------------------------------------------------------------------------------

        /// <summary>The word's senses with a definition grouped by EntryId, in dictionary order (numbered per entry; one lectern per sense with a link).</summary>
        public List<List<int>> EntryGroups(int rank)
        {
            var groups = new List<List<int>>();
            var index = new Dictionary<string, int>();
            foreach (var row in d.GetSenseRows(rank))
            {
                var sense = d.GetSense(row);
                if (string.IsNullOrEmpty(sense.Definition)) continue;
                var id = sense.EntryId ?? "";
                if (!index.TryGetValue(id, out var g))
                {
                    g = groups.Count;
                    index[id] = g;
                    groups.Add(new List<int>());
                }
                groups[g].Add(row);
            }
            return groups;
        }
    }
}
