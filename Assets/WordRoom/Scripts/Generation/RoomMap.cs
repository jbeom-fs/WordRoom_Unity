using System.Collections.Generic;

namespace WordRoom.Generation
{
    /// <summary>
    /// Map state of one experiment: committed rooms (never changed once placed), visits, path, recent words,
    /// link evidence, unfilled rooms and the experiment status. Read-only to callers; the generator mutates it.
    /// Generated and visited rooms are distinct: a room exists once committed, and is visited only when entered.
    /// </summary>
    public sealed class RoomMap
    {
        readonly Dictionary<GridPos, int> rooms = new Dictionary<GridPos, int>();
        readonly List<GridPos> creationOrder = new List<GridPos>();
        readonly Dictionary<GridPos, int> visits = new Dictionary<GridPos, int>();
        readonly List<GridPos> path = new List<GridPos>();
        readonly List<int> recent = new List<int>();
        readonly int recentWindow;
        readonly Dictionary<GridPos, PlacedRoom> roomInfo = new Dictionary<GridPos, PlacedRoom>();
        readonly HashSet<GridPos> prepared = new HashSet<GridPos>();
        internal readonly Dictionary<GridPos, UnfilledRecord> Unfilled = new Dictionary<GridPos, UnfilledRecord>();

        public int Start { get; }
        public int Target { get; }
        public GridPos Position { get; private set; } = GridPos.Origin;
        public int Moves { get; private set; }
        public string Status { get; internal set; } = "running";
        public string FailureReason { get; internal set; }
        public int RoomCount => rooms.Count;
        /// <summary>Distinct rooms entered, including the start room (Python len(visits)).</summary>
        public int UniqueVisited => visits.Count;
        public IReadOnlyList<GridPos> Path => path;
        public IReadOnlyList<GridPos> CreationOrder => creationOrder;
        /// <summary>Recent words, oldest first (Python deque(maxlen=recent_window)).</summary>
        public IReadOnlyList<int> Recent => recent;

        internal RoomMap(int start, int target, int recentWindow)
        {
            Start = start;
            Target = target;
            this.recentWindow = recentWindow;
            rooms[GridPos.Origin] = start;
            creationOrder.Add(GridPos.Origin);
            visits[GridPos.Origin] = 1;
            path.Add(GridPos.Origin);
            PushRecent(start);
        }

        public bool TryGetWord(GridPos p, out int word) => rooms.TryGetValue(p, out word);
        public bool HasRoom(GridPos p) => rooms.ContainsKey(p);
        public int VisitCount(GridPos p) => visits.TryGetValue(p, out var n) ? n : 0;
        public bool IsVisited(GridPos p) => visits.ContainsKey(p);
        public PlacedRoom GetRoomInfo(GridPos p) => roomInfo.TryGetValue(p, out var r) ? r : null;
        public bool IsUnfilled(GridPos p) => Unfilled.ContainsKey(p);

        /// <summary>
        /// A fill request around p has been processed (start room, a room moved into, or a room prepared through a
        /// door). Processed is not complete: some neighbors may have stayed 미생성 (see IsUnfilled).
        /// </summary>
        public bool IsPrepared(GridPos p) => prepared.Contains(p);

        internal void MarkPrepared(GridPos p) => prepared.Add(p);

        /// <summary>Recorded reason why `position` around `center` was left empty, or null.</summary>
        public string EmptyReason(GridPos center, GridPos position)
        {
            if (!Unfilled.TryGetValue(center, out var u)) return null;
            foreach (var e in u.Empty)
                if (e.Position == position) return e.Reason;
            return null;
        }

        /// <summary>Committed rooms in creation order.</summary>
        public IEnumerable<KeyValuePair<GridPos, int>> Rooms()
        {
            foreach (var p in creationOrder) yield return new KeyValuePair<GridPos, int>(p, rooms[p]);
        }

        internal void PushRecent(int word)
        {
            recent.Add(word);
            if (recent.Count > recentWindow) recent.RemoveAt(0);
        }

        internal void Commit(PlacedRoom room)
        {
            rooms.Add(room.Position, room.Word);     // throws if the coordinate already holds a room
            creationOrder.Add(room.Position);
            roomInfo[room.Position] = room;
            PushRecent(room.Word);
        }

        /// <summary>Python step: pos, moves, visits, path. Returns whether the room was entered for the first time.</summary>
        internal bool Enter(GridPos dest)
        {
            var fresh = !visits.ContainsKey(dest);
            Position = dest;
            Moves++;
            visits[dest] = VisitCount(dest) + 1;
            path.Add(dest);
            return fresh;
        }
    }

    internal sealed class UnfilledRecord
    {
        public int Rooms;
        public List<GridPos> Missing;
        public List<EmptyPosition> Empty;
    }
}
