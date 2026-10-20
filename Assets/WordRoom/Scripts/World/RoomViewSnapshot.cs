using System.Collections.Generic;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>
    /// What the 3D view needs from the logical map: the current room and those of its 상·하·좌·우 neighbors that
    /// exist in RoomMap. Captured right after a generator request finished (on the worker, before control returns
    /// to the main thread), so the main thread never walks RoomMap while a request runs. Read-only copy; never
    /// generates anything and is not used for neighbors of neighbors.
    /// </summary>
    public sealed class RoomViewSnapshot
    {
        public GridPos Center { get; private set; }
        public string Status { get; private set; }

        /// <summary>Existing rooms to show: the center first, then neighbors in 상·하·좌·우 order. At most five.</summary>
        public IReadOnlyList<KeyValuePair<GridPos, int>> Rooms => rooms;

        readonly List<KeyValuePair<GridPos, int>> rooms = new List<KeyValuePair<GridPos, int>>(5);

        public static RoomViewSnapshot Capture(RoomMap map)
        {
            var s = new RoomViewSnapshot { Center = map.Position, Status = map.Status };
            if (map.TryGetWord(map.Position, out var here)) s.rooms.Add(new KeyValuePair<GridPos, int>(map.Position, here));
            foreach (var d in Directions.DisplayOrder)
            {
                var q = map.Position.Add(Directions.Offset(d));
                if (map.TryGetWord(q, out var w)) s.rooms.Add(new KeyValuePair<GridPos, int>(q, w));   // 미생성 좌표는 표시하지 않음
            }
            return s;
        }
    }
}
