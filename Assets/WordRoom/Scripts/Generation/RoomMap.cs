using System.Collections.Generic;

namespace WordRoom.Generation
{
    /// <summary>
    /// The shared world of one session (one per session, whatever the number of players): committed rooms (never
    /// changed once placed), creation order, link evidence, preparation records, unfilled rooms with their retry
    /// condition, the recent generated words, the team's visited coordinates and the door state per connection.
    /// It has no current position: where each player stands is PlayerState's. Internal to the generation layer;
    /// SessionCoordinator mutates it (rooms only through RoomGenerator's fill) and hands out copies via SessionReader.
    /// Generated and visited rooms are distinct: a room exists once committed, and is visited only when entered.
    /// </summary>
    internal sealed class RoomMap
    {
        readonly Dictionary<GridPos, int> rooms = new Dictionary<GridPos, int>();
        readonly List<GridPos> creationOrder = new List<GridPos>();
        readonly List<int> recent = new List<int>();
        readonly int recentWindow;
        readonly Dictionary<GridPos, PlacedRoom> roomInfo = new Dictionary<GridPos, PlacedRoom>();
        readonly HashSet<GridPos> prepared = new HashSet<GridPos>();
        readonly HashSet<GridPos> teamVisited = new HashSet<GridPos>();
        public readonly Dictionary<GridPos, UnfilledRecord> Unfilled = new Dictionary<GridPos, UnfilledRecord>();
        public readonly DoorStateStore Doors = new DoorStateStore();

        public int RoomCount => rooms.Count;
        public IReadOnlyList<GridPos> CreationOrder => creationOrder;

        /// <summary>Recent generated words, oldest first (Python deque(maxlen=recent_window)); one history for the whole world.</summary>
        public IReadOnlyList<int> Recent => recent;

        /// <summary>Coordinates entered by anyone, start included: the team's distinct visits (no count per player here).</summary>
        public int TeamVisitedCount => teamVisited.Count;

        public RoomMap(int start, int recentWindow)
        {
            this.recentWindow = recentWindow;
            rooms[GridPos.Origin] = start;
            creationOrder.Add(GridPos.Origin);
            PushRecent(start);
        }

        public bool TryGetWord(GridPos p, out int word) => rooms.TryGetValue(p, out word);
        public bool HasRoom(GridPos p) => rooms.ContainsKey(p);
        public PlacedRoom GetRoomInfo(GridPos p) => roomInfo.TryGetValue(p, out var r) ? r : null;
        public bool IsUnfilled(GridPos p) => Unfilled.ContainsKey(p);
        public bool IsTeamVisited(GridPos p) => teamVisited.Contains(p);
        public IEnumerable<GridPos> TeamVisited => teamVisited;
        public IEnumerable<GridPos> Prepared => prepared;

        /// <summary>
        /// A fill request around p has been processed (start room, a room moved into, or a room prepared through a
        /// door). Processed is not complete: some neighbors may have stayed 미생성 (see IsUnfilled).
        /// </summary>
        public bool IsPrepared(GridPos p) => prepared.Contains(p);

        public void MarkPrepared(GridPos p) => prepared.Add(p);

        /// <summary>Records a team visit; true when nobody had entered p before.</summary>
        public bool MarkTeamVisited(GridPos p) => teamVisited.Add(p);

        /// <summary>Recorded reason why `position` around `center` was left empty, or null.</summary>
        public string EmptyReason(GridPos center, GridPos position)
        {
            if (!Unfilled.TryGetValue(center, out var u)) return null;
            foreach (var e in u.Empty)
                if (e.Position == position) return e.Reason;
            return null;
        }

        public bool HasAnyNeighbor(GridPos p)
        {
            foreach (var q in Directions.Around(p))
                if (HasRoom(q)) return true;
            return false;
        }

        void PushRecent(int word)
        {
            recent.Add(word);
            if (recent.Count > recentWindow) recent.RemoveAt(0);
        }

        /// <summary>Generation only: a committed room joins the world and the recent generated words (never a visit).</summary>
        public void Commit(PlacedRoom room)
        {
            rooms.Add(room.Position, room.Word);     // throws if the coordinate already holds a room
            creationOrder.Add(room.Position);
            roomInfo[room.Position] = room;
            PushRecent(room.Word);
        }
    }

    internal sealed class UnfilledRecord
    {
        public int Rooms;
        public List<GridPos> Missing;
        public List<EmptyPosition> Empty;
    }
}
