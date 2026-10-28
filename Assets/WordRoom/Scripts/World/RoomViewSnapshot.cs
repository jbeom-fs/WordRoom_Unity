using System;
using System.Collections.Generic;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>
    /// What one device's 3D view needs from the session: the rooms of the shared world within the display radius of
    /// the given (local) player's confirmed room (Manhattan distance |dx|+|dy| ≤ radius: at most 5, 13 or 25 rooms
    /// for 1, 2 or 3), plus, for each of these rooms, the words of its four neighbors for the wall signs, the
    /// confirmed state of every connection of these rooms, and the current and target words for the HUD.
    /// Captured inside a SessionCoordinator request or Read (on the worker), so it always belongs to one confirmed
    /// revision and the main thread never reads the session. Read-only copy; never generates anything: missing
    /// coordinates are simply left out, and rooms just outside the radius are only read for their stored words.
    /// </summary>
    public sealed class RoomViewSnapshot
    {
        public const int MaxRadius = 3;

        /// <summary>Largest room count a snapshot can hold (radius 3: 2·3² + 2·3 + 1 = 25).</summary>
        public const int MaxRooms = 2 * MaxRadius * MaxRadius + 2 * MaxRadius + 1;

        public string SessionId { get; private set; }
        public long Revision { get; private set; }

        /// <summary>The player this view follows (the local player of the device).</summary>
        public string PlayerId { get; private set; }

        /// <summary>That player's confirmed room.</summary>
        public GridPos Center { get; private set; }
        public string Status { get; private set; }

        /// <summary>Display radius this snapshot was captured with (1–3).</summary>
        public int Radius { get; private set; }

        /// <summary>Surface of the current room (Center), copied on the worker for the HUD.</summary>
        public string CurrentWord { get; private set; }

        /// <summary>Surface of the session's target (fixed at its start), copied on the worker.</summary>
        public string TargetWord { get; private set; }

        /// <summary>Existing rooms to show: the center first, then by distance, then from 상 to 하 and 좌 to 우 (fixed order).</summary>
        public IReadOnlyList<KeyValuePair<GridPos, int>> Rooms => rooms;

        /// <summary>For each room in Rooms: the stored words of its neighbors, indexed by Direction; null = 미생성.</summary>
        public IReadOnlyDictionary<GridPos, string[]> Signs => signs;

        /// <summary>A room in Rooms whose neighbors' fill request was processed when captured (not: all four exist).</summary>
        public bool IsPrepared(GridPos p) => prepared.Contains(p);

        /// <summary>Confirmed state of a connection of a room in Rooms (Closed for any other connection).</summary>
        public DoorState Door(RoomConnection id) => openDoors.Contains(id) ? DoorState.Open : DoorState.Closed;

        readonly List<KeyValuePair<GridPos, int>> rooms = new List<KeyValuePair<GridPos, int>>(MaxRooms);
        readonly Dictionary<GridPos, string[]> signs = new Dictionary<GridPos, string[]>(MaxRooms);
        readonly HashSet<GridPos> prepared = new HashSet<GridPos>();
        readonly HashSet<RoomConnection> openDoors = new HashSet<RoomConnection>();

        /// <summary>Offsets within each radius, center first, then by distance, then y descending (상 first), x ascending.</summary>
        static readonly GridPos[][] OffsetsByRadius = BuildOffsets();

        static GridPos[][] BuildOffsets()
        {
            var result = new GridPos[MaxRadius + 1][];
            for (var r = 0; r <= MaxRadius; r++)
            {
                var list = new List<GridPos>();
                for (var dy = -r; dy <= r; dy++)
                    for (var dx = -r; dx <= r; dx++)
                        if (Math.Abs(dx) + Math.Abs(dy) <= r) list.Add(new GridPos(dx, dy));
                list.Sort((a, b) =>
                {
                    var da = Math.Abs(a.X) + Math.Abs(a.Y);
                    var db = Math.Abs(b.X) + Math.Abs(b.Y);
                    if (da != db) return da.CompareTo(db);
                    return a.Y != b.Y ? b.Y.CompareTo(a.Y) : a.X.CompareTo(b.X);
                });
                result[r] = list.ToArray();
            }
            return result;
        }

        /// <param name="session">A SessionReader (only inside the coordinator's capture or Read callback) or an independent SessionCopy.</param>
        /// <param name="playerId">The player the view is centered on (the local player).</param>
        /// <param name="wordText">Surface of a word id (the dataset's existing lookup); called here, on the worker.</param>
        /// <param name="radius">Display radius (clamped to 1–3).</param>
        public static RoomViewSnapshot Capture(ISessionView session, string playerId, Func<int, string> wordText, int radius = 1)
        {
            radius = Math.Max(1, Math.Min(MaxRadius, radius));
            var center = session.PlayerPosition(playerId);
            var s = new RoomViewSnapshot
            {
                SessionId = session.SessionId, Revision = session.Revision, PlayerId = playerId, Center = center, Status = session.Status,
                Radius = radius, TargetWord = wordText(session.TargetWord),
            };
            if (session.TryGetWord(center, out var here)) s.CurrentWord = wordText(here);
            foreach (var offset in OffsetsByRadius[radius])                    // the center comes first
            {
                var q = center.Add(offset);
                if (session.TryGetWord(q, out var w)) s.Add(session, q, w, wordText);   // 미생성 좌표는 표시하지 않음
            }
            return s;
        }

        void Add(ISessionView session, GridPos p, int word, Func<int, string> wordText)
        {
            rooms.Add(new KeyValuePair<GridPos, int>(p, word));
            if (session.IsPrepared(p)) prepared.Add(p);
            var n = new string[4];
            foreach (var d in Directions.DisplayOrder)
            {
                n[(int)d] = session.TryGetWord(p.Add(Directions.Offset(d)), out var w) ? wordText(w) : null;
                var id = RoomConnection.Of(p, d);
                if (session.Door(id) == DoorState.Open) openDoors.Add(id);
            }
            signs[p] = n;
        }
    }
}
