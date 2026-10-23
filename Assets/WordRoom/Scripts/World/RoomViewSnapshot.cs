using System;
using System.Collections.Generic;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>
    /// What the 3D view needs from the logical map: the current room and those of its 상·하·좌·우 neighbors that
    /// exist in RoomMap, plus, for each of these rooms, the words of its four neighbors for the wall signs.
    /// Captured right after a generator request finished (on the worker, before control returns to the main
    /// thread), so the main thread never walks RoomMap while a request runs. Read-only copy; never generates
    /// anything. Neighbors of neighbors are only read for their stored words, never shown as rooms.
    /// </summary>
    public sealed class RoomViewSnapshot
    {
        public GridPos Center { get; private set; }
        public string Status { get; private set; }

        /// <summary>Existing rooms to show: the center first, then neighbors in 상·하·좌·우 order. At most five.</summary>
        public IReadOnlyList<KeyValuePair<GridPos, int>> Rooms => rooms;

        /// <summary>For each room in Rooms: the stored words of its neighbors, indexed by Direction; null = 미생성.</summary>
        public IReadOnlyDictionary<GridPos, string[]> Signs => signs;

        /// <summary>A room in Rooms whose neighbors' fill request was processed (RoomMap.IsPrepared) when captured.</summary>
        public bool IsPrepared(GridPos p) => prepared.Contains(p);

        readonly List<KeyValuePair<GridPos, int>> rooms = new List<KeyValuePair<GridPos, int>>(5);
        readonly Dictionary<GridPos, string[]> signs = new Dictionary<GridPos, string[]>(5);
        readonly HashSet<GridPos> prepared = new HashSet<GridPos>();

        /// <param name="wordText">Surface of a word id (the dataset's existing lookup); called here, on the worker.</param>
        public static RoomViewSnapshot Capture(RoomMap map, Func<int, string> wordText)
        {
            var s = new RoomViewSnapshot { Center = map.Position, Status = map.Status };
            if (map.TryGetWord(map.Position, out var here)) s.Add(map, map.Position, here, wordText);
            foreach (var d in Directions.DisplayOrder)
            {
                var q = map.Position.Add(Directions.Offset(d));
                if (map.TryGetWord(q, out var w)) s.Add(map, q, w, wordText);   // 미생성 좌표는 표시하지 않음
            }
            return s;
        }

        void Add(RoomMap map, GridPos p, int word, Func<int, string> wordText)
        {
            rooms.Add(new KeyValuePair<GridPos, int>(p, word));
            if (map.IsPrepared(p)) prepared.Add(p);
            var n = new string[4];
            foreach (var d in Directions.DisplayOrder)
                n[(int)d] = map.TryGetWord(p.Add(Directions.Offset(d)), out var w) ? wordText(w) : null;
            signs[p] = n;
        }
    }
}
