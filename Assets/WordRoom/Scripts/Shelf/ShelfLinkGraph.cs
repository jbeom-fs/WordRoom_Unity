using System;
using System.Collections.Generic;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Which headword rooms can be left again, decided for the whole dictionary package, so the links shown in the
    /// reading screen and the rooms they lead to use the same result.
    /// A room candidate is a package word within the top `vocabularyLimit`, at least RoomMinLength letters long
    /// (GetWordLength), unrestricted and with a definition. Only room candidates are link targets, exits, travel
    /// destinations and challenge starts or goals; a shorter headword stays plain text in the definition (its database
    /// record is read but never used for a room). challengeMinLength only narrows the random draw on top of this.
    /// Links come from the validated database. Each sense keeps eligible targets other than its own headword, limited
    /// to its link setting's selection, one per target (first place). One room combines all senses of a headword:
    /// its exits are the link words of every sense together. A room is valid when it is the goal headword or at least
    /// one of its exits is a valid room. Computed as the largest such set: every room starts valid and rooms with no
    /// valid exit are removed until nothing changes (a removal can leave the rooms linking only to it without an exit).
    /// Rooms that only link among themselves in a loop stay valid, since each of them has an exit. Each room is removed
    /// at most once, so it always ends. A link word is shown as a link only when its room is valid.
    /// Each sense's clickable parts are prepared once in the constructor. SetGoal then decides the
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

        /// <summary>Fewest letters (GetWordLength) of a headword that can be a room, apart from challengeMinLength.</summary>
        public const int RoomMinLength = 2;

        readonly WordRoomDataset d;
        readonly int limit;
        readonly int challengeMinLength;
        readonly List<RoomRule> roomRules;
        readonly ShelfLinkDatabase database;
        readonly bool[] candidates;
        readonly string[] readingDefinitions;
        readonly List<ShelfTextPart>[] linksBySense;           // room selection, resolved references and deduplication
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
        public ShelfLinkGraph(WordRoomDataset dataset, ShelfLinkDatabase database, int vocabularyLimit, int challengeMinLength,
            List<RoomRule> roomRules, CancellationToken cancel)
        {
            d = dataset;
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            limit = vocabularyLimit;
            this.challengeMinLength = challengeMinLength;
            this.roomRules = roomRules;
            candidates = new bool[d.WordCount];
            readingDefinitions = new string[d.SenseCount];
            linksBySense = new List<ShelfTextPart>[d.SenseCount];
            valid = new bool[d.WordCount];
            distance = new int[d.WordCount];
            exitsOf = new List<int>[d.WordCount];
            linkedFrom = new List<int>[d.WordCount];
            for (var rank = 0; rank < Math.Min(limit, d.WordCount); rank++)
            {
                cancel.ThrowIfCancellationRequested();
                candidates[rank] = d.GetWordLength(rank) >= RoomMinLength && !d.IsRestricted(rank) && HasDefinition(rank);
                if (candidates[rank]) rooms.Add(rank);
            }
            Build(cancel);
        }

        // ---- candidates ----------------------------------------------------------------------------------------------

        /// <summary>A room candidate surface (exact package surface meeting the vocabulary and length rules).</summary>
        public bool IsCandidate(string surface) => CandidateRank(surface) >= 0;

        public IReadOnlyList<ShelfTextPart> DatabaseLinks(string senseId) => database.Get(senseId).Links;

        int CandidateRank(string surface) =>
            d.TryGetWordRank(surface, out var rank) && d.GetWord(rank) == surface && candidates[rank] ? rank : -1;

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
            if (d.GetWordLength(rank) < RoomMinLength) return $"{RoomMinLength}글자 미만 표제어";
            if (d.IsRestricted(rank)) return "제한어";
            return candidates[rank] ? null : "뜻풀이 없음";
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
        /// Prepares the reading definition and the links used by both the reader and the graph. Resolve references
        /// before choosing the first link to each target, since a rewrite can cover a DB span.
        /// </summary>
        List<ShelfTextPart> PrepareLinks(SenseInfo sense, int number, int count)
        {
            var entry = database.Get(sense.SenseId);
            readingDefinitions[sense.Row] = ShelfLinkText.ResolveSelfReferences(
                entry.Definition, sense.Word, number, count, entry.Links, out var moved);
            if (moved.Count == 0) return null;
            var selection = RuleOf(sense.Word, sense.SenseId)?.Links;
            var list = new List<ShelfTextPart>();
            var seen = new HashSet<string>();
            foreach (var c in moved)
            {
                if (c.Target == sense.Word || !IsCandidate(c.Target)) continue;
                if (selection != null && !selection.Contains(c.Target)) continue;
                if (seen.Add(c.Target)) list.Add(c);
            }
            return list;
        }

        // ---- graph ---------------------------------------------------------------------------------------------------

        /// <summary>The link words of every candidate room, once (they do not depend on the goal).</summary>
        void Build(CancellationToken cancel)
        {
            var targets = new HashSet<int>();
            foreach (var rank in rooms)
            {
                cancel.ThrowIfCancellationRequested();
                targets.Clear();
                foreach (var group in EntryGroups(rank))
                    for (var i = 0; i < group.Count; i++)
                    {
                        var row = group[i];
                        var number = group.Count > 1 ? i + 1 : 0;
                        var links = linksBySense[row] = PrepareLinks(d.GetSense(row), number, group.Count);
                        if (links == null) continue;
                        foreach (var link in links) targets.Add(CandidateRank(link.Target));
                    }
                exitsOf[rank] = new List<int>(targets);
                foreach (var t in targets) (linkedFrom[t] ??= new List<int>()).Add(rank);
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
            var order = rooms.FindAll(rank => d.GetWordLength(rank) >= challengeMinLength);
            var starts = new List<int>();
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
                starts.Clear();
                for (var i = reaching.Count - 1; i >= 0 && distance[reaching[i]] >= minMoves; i--)
                    if (d.GetWordLength(reaching[i]) >= challengeMinLength) starts.Add(reaching[i]);
                if (starts.Count == 0) continue;
                var pick = starts[random.Next(starts.Count)];
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

        public bool CanTravel(string from, string to)
        {
            var source = CandidateRank(from);
            var target = CandidateRank(to);
            return source >= 0 && valid[source] && target >= 0 && valid[target] && exitsOf[source].Contains(target);
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

        public string ReadingDefinition(int row) => readingDefinitions[row];

        /// <summary>The final links of a sense, at offsets in ReadingDefinition.</summary>
        public List<ShelfTextPart> Doors(int row)
        {
            var links = new List<ShelfTextPart>();
            var prepared = linksBySense[row];
            if (prepared != null)
                foreach (var link in prepared)
                    if (IsValidDoor(link.Target)) links.Add(link);
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
