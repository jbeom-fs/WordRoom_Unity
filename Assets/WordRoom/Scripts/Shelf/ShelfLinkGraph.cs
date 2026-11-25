using System;
using System.Collections.Generic;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Which headword rooms can be left again, decided once for the whole dictionary package, so the links shown in
    /// the reading screen and the rooms they lead to use the same result.
    /// A link candidate is a package word within the top `vocabularyLimit`, at least `minLength` long, not restricted,
    /// with a definition. A sense's link words are the candidates in its definition minus its own headword, limited to
    /// its link setting's selection, one per headword (first place). A headword room is one room for all of its senses:
    /// its exits are the link words of every sense together. A room is valid when it is the goal headword or at least
    /// one of its exits is a valid room. Computed as the largest such set: every room starts valid and rooms with no
    /// valid exit are removed until nothing changes (a removal can leave the rooms linking only to it without an exit).
    /// Rooms that only link among themselves in a loop stay valid, since each of them has an exit. Each room is removed
    /// at most once, so it always ends. A link word is shown as a link only when its room is valid.
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
        readonly string goalWord;                              // its room is valid without an exit
        readonly Dictionary<string, int> candidates = new Dictionary<string, int>();  // surface → rank, or -1
        readonly Dictionary<string, bool> entries = new Dictionary<string, bool>();   // surface → registered headword
        readonly bool[] valid;                                 // by word rank

        public int RoomCount { get; private set; }
        public int RemovedCount { get; private set; }

        /// <param name="cancel">Checked while walking the room candidates and while removing rooms; throws OperationCanceledException.</param>
        public ShelfLinkGraph(WordRoomDataset dataset, int vocabularyLimit, int minLength, List<RoomRule> roomRules,
            string goalWord, CancellationToken cancel)
        {
            d = dataset;
            limit = vocabularyLimit;
            this.minLength = minLength;
            this.roomRules = roomRules;
            this.goalWord = goalWord;
            valid = new bool[d.WordCount];
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

        void Build(CancellationToken cancel)
        {
            var exits = new int[d.WordCount];                 // distinct valid exit words of each room
            var linkedFrom = new List<int>[d.WordCount];     // exit word rank → rooms that have it as an exit (each once)
            var end = Math.Min(limit, d.WordCount);
            var goalRank = GoalRank();
            var targets = new HashSet<int>();
            for (var rank = 0; rank < d.WordCount; rank++)
            {
                cancel.ThrowIfCancellationRequested();
                if (rank >= end && rank != goalRank) continue;
                if (rank != goalRank && !IsCandidateRank(rank)) continue;
                targets.Clear();
                foreach (var row in d.GetSenseRows(rank))
                    foreach (var c in CandidateLinks(d.GetSense(row)))
                        targets.Add(CandidateRank(c.Target));
                foreach (var t in targets) (linkedFrom[t] ??= new List<int>()).Add(rank);
                exits[rank] = targets.Count;
                valid[rank] = true;
                RoomCount++;
            }
            // Every link word is a candidate, so it is in the room set above: all exits start valid.

            var queue = new Queue<int>();
            for (var rank = 0; rank < valid.Length; rank++)
            {
                if ((rank & 4095) == 0) cancel.ThrowIfCancellationRequested();
                if (valid[rank] && exits[rank] <= 0 && rank != goalRank) Remove(rank, queue);
            }
            while (queue.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var word = queue.Dequeue();
                if (linkedFrom[word] == null) continue;
                foreach (var room in linkedFrom[word])        // the word's room is gone: its links are no exits
                    if (valid[room] && --exits[room] <= 0 && room != goalRank) Remove(room, queue);
            }
        }

        void Remove(int rank, Queue<int> queue)
        {
            valid[rank] = false;
            RemovedCount++;
            queue.Enqueue(rank);
        }

        int GoalRank() => goalWord != null && d.TryGetWordRank(goalWord, out var g) && d.GetWord(g) == goalWord ? g : -1;

        /// <summary>A link word whose headword room can be entered (it has an exit, or it is the goal).</summary>
        public bool IsValidDoor(string surface)
        {
            var rank = CandidateRank(surface);
            return rank >= 0 && valid[rank];
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
