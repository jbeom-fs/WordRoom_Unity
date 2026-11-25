using System;
using System.Collections.Generic;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Which rooms (headword + sense) can be left again, decided once for the whole dictionary package, so door
    /// placement and destination choice use the same result.
    /// A door candidate is a package word within the top `vocabularyLimit`, at least `minLength` long, not restricted,
    /// with a definition. A room's door words are the candidates in its definition minus its own headword, limited to
    /// its room setting's selection, one per headword. A room is valid when it is a goal room (any sense of the goal
    /// headword) or at least one of its door
    /// words is valid; a door word is valid when at least one of its senses is a valid room. Computed as the largest
    /// such set: every room starts valid and rooms with no valid door word are removed until nothing changes (a removal
    /// can invalidate a word and then the rooms linking only to it). Rooms that only link among themselves in a loop
    /// stay valid, since each of them has an exit. Each room is removed at most once, so it always ends.
    /// Behind a valid door word, each dictionary entry (EntryId) with a valid sense is one destination: its
    /// representative is the first valid sense among the room settings' senses of that entry, then the fork settings'
    /// senses, then the entry's senses in dictionary order. Entries without a valid sense are left out.
    /// Built on a worker thread (the dataset is read only) and cancellable; use it from one thread at a time afterwards.
    /// </summary>
    internal sealed class ShelfLinkGraph
    {
        /// <summary>Room setting snapshot: a senseId (null: every sense of the word without its own setting) and its selection (null: all).</summary>
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
        readonly Dictionary<string, List<string>> forkSenses;   // word → senseIds named in the fork settings
        readonly string goalWord;                              // every sense of it is a goal room
        readonly Dictionary<string, int> candidates = new Dictionary<string, int>();  // surface → rank, or -1
        readonly Dictionary<string, bool> entries = new Dictionary<string, bool>();   // surface → registered headword
        readonly bool[] valid;
        readonly int[] exits;
        readonly int[] validSenses;

        public int RoomCount { get; private set; }
        public int RemovedCount { get; private set; }

        /// <param name="cancel">Checked while walking the room candidates and while removing rooms; throws OperationCanceledException.</param>
        public ShelfLinkGraph(WordRoomDataset dataset, int vocabularyLimit, int minLength, List<RoomRule> roomRules,
            Dictionary<string, List<string>> forkSenses, string goalWord, CancellationToken cancel)
        {
            d = dataset;
            limit = vocabularyLimit;
            this.minLength = minLength;
            this.roomRules = roomRules;
            this.forkSenses = forkSenses;
            this.goalWord = goalWord;
            valid = new bool[d.SenseCount];
            exits = new int[d.SenseCount];
            validSenses = new int[d.WordCount];
            Build(cancel);
        }

        // ---- candidates ----------------------------------------------------------------------------------------------

        /// <summary>A door candidate surface (exact package surface meeting the vocabulary rules).</summary>
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
        /// The one extraction used everywhere (graph, room layout, link log): split on headwords with the split exceptions
        /// of the room `senseId`, then keep door candidates.
        /// </summary>
        public List<ShelfTextPart> Extract(string displayDefinition, string senseId) =>
            ShelfLinkText.FindCandidates(displayDefinition, senseId, IsEntry, IsCandidate);

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

        /// <summary>Why a surface is no door candidate, or null when it is one.</summary>
        public string WhyNotCandidate(string surface)
        {
            if (!d.TryGetWordRank(surface, out var rank) || d.GetWord(rank) != surface) return "사전 패키지에 없는 표기";
            if (rank >= limit) return $"빈도 상위 {limit:N0}개 밖";
            if (d.GetWordLength(rank) < minLength) return $"{minLength}글자 미만";
            if (d.IsRestricted(rank)) return "제한어";
            return HasDefinition(rank) ? null : "뜻풀이 없음";
        }

        /// <summary>The room setting of a sense: the one with its senseId, else one of the word without a senseId.</summary>
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
        /// Door words of a sense before validity: candidates of its display definition (Han parentheses removed, see
        /// ShelfLinkText.DisplayDefinition; positions are offsets in it) minus its own headword, selection applied,
        /// first place each.
        /// </summary>
        List<ShelfTextPart> CandidateDoors(SenseInfo sense)
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

        void Build(CancellationToken cancel)
        {
            var doorsOf = new int[d.SenseCount][];
            var linkedFrom = new List<int>[d.WordCount];     // door word rank → rooms that have it as a door word
            var end = Math.Min(limit, d.WordCount);
            var goalRank = goalWord != null && d.TryGetWordRank(goalWord, out var g) ? g : -1;
            for (var rank = 0; rank < d.WordCount; rank++)
            {
                cancel.ThrowIfCancellationRequested();
                if (rank >= end && rank != goalRank) continue;
                if (rank != goalRank && !IsCandidateRank(rank)) continue;
                foreach (var row in d.GetSenseRows(rank))
                {
                    var sense = d.GetSense(row);
                    if (string.IsNullOrEmpty(sense.Definition)) continue;
                    var doors = CandidateDoors(sense);
                    var ranks = new int[doors.Count];
                    for (var i = 0; i < doors.Count; i++)
                    {
                        ranks[i] = CandidateRank(doors[i].Target);
                        (linkedFrom[ranks[i]] ??= new List<int>()).Add(row);
                    }
                    doorsOf[row] = ranks;
                    valid[row] = true;
                    exits[row] = ranks.Length;
                    validSenses[rank]++;
                    RoomCount++;
                }
            }

            var queue = new Queue<int>();
            for (var row = 0; row < valid.Length; row++)
            {
                if ((row & 4095) == 0) cancel.ThrowIfCancellationRequested();
                if (valid[row] && exits[row] == 0 && !IsGoal(row)) Remove(row, queue);
            }
            while (queue.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var word = d.WordOfSense(queue.Dequeue());
                if (--validSenses[word] > 0 || linkedFrom[word] == null) continue;
                foreach (var room in linkedFrom[word])        // the word lost its last valid sense: its door is gone
                    if (valid[room] && --exits[room] == 0 && !IsGoal(room)) Remove(room, queue);
            }
        }

        void Remove(int row, Queue<int> queue)
        {
            valid[row] = false;
            RemovedCount++;
            queue.Enqueue(row);
        }

        /// <summary>A sense of the goal headword (any SenseId): kept as a destination even without an exit.</summary>
        public bool IsGoal(int row) => goalWord != null && d.GetSense(row).Word == goalWord;

        /// <summary>A room that can be entered as a destination (has an exit, or is a goal room).</summary>
        public bool IsValid(int row) => row >= 0 && row < valid.Length && valid[row];

        /// <summary>Number of valid door words of a sense (also for rooms outside the graph, such as a start room).</summary>
        public int ExitCount(int row) => Doors(row).Count;

        /// <summary>A door word with at least one valid sense behind it.</summary>
        public bool IsValidDoor(string surface)
        {
            var rank = CandidateRank(surface);
            return rank >= 0 && validSenses[rank] > 0;
        }

        /// <summary>The doors of a sense's room (offsets in its display definition): its candidate door words that are valid doors, first place each.</summary>
        public List<ShelfTextPart> Doors(int row)
        {
            var doors = CandidateDoors(d.GetSense(row));
            doors.RemoveAll(c => !IsValidDoor(c.Target));
            return doors;
        }

        // ---- destinations --------------------------------------------------------------------------------------------

        /// <summary>The word's senses with a definition grouped by EntryId, in dictionary order.</summary>
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

        /// <summary>
        /// The entry's representative among its valid senses: room settings' senses first, then fork settings',
        /// then dictionary order. -1 when no sense of the entry is valid.
        /// </summary>
        public int Representative(string surface, List<int> group)
        {
            foreach (var r in roomRules)
                if (r.Word == surface && Pick(group, r.SenseId, out var row)) return row;
            if (forkSenses.TryGetValue(surface, out var named))
                foreach (var id in named)
                    if (Pick(group, id, out var row)) return row;
            foreach (var row in group)
                if (valid[row]) return row;
            return -1;
        }

        bool Pick(List<int> group, string senseId, out int row)
        {
            row = -1;
            if (senseId == null || !d.TryGetSenseRow(senseId, out var r) || !group.Contains(r) || !valid[r]) return false;
            row = r;
            return true;
        }

        /// <summary>Representative sense rows behind a door of `surface`, one per entry that has a valid sense.</summary>
        public List<int> Destinations(string surface)
        {
            var list = new List<int>();
            if (!d.TryGetWordRank(surface, out var rank)) return list;
            foreach (var group in EntryGroups(rank))
            {
                var row = Representative(d.GetWord(rank), group);
                if (row >= 0) list.Add(row);
            }
            return list;
        }
    }
}
