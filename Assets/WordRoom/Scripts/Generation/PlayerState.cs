using System.Collections.Generic;

namespace WordRoom.Generation
{
    /// <summary>
    /// One participant's logical exploration in a session: the last confirmed room, entries, visits per coordinate
    /// and the path. Owned by SessionCoordinator; another player's request never changes it. No Transform, camera,
    /// input or menu state here — those stay on each device.
    /// </summary>
    internal sealed class PlayerState
    {
        readonly Dictionary<GridPos, int> visits = new Dictionary<GridPos, int>();
        readonly List<GridPos> path = new List<GridPos>();

        public string Id { get; }
        public GridPos Position { get; private set; } = GridPos.Origin;
        public int Moves { get; private set; }

        /// <summary>Distinct rooms this player entered, start room included (Python len(visits)).</summary>
        public int UniqueVisited => visits.Count;

        public IReadOnlyList<GridPos> Path => path;
        public IEnumerable<KeyValuePair<GridPos, int>> Visits => visits;

        public PlayerState(string id) => Id = id;

        public int VisitCount(GridPos p) => visits.TryGetValue(p, out var n) ? n : 0;
        public bool IsVisited(GridPos p) => visits.ContainsKey(p);

        /// <summary>Session start: in the start room, visited once, no move (once per player).</summary>
        public void BeginAt(GridPos start)
        {
            Position = start;
            visits[start] = 1;
            path.Add(start);
        }

        /// <summary>Python step for this player: pos, moves, visits, path. Returns whether the room was entered for the first time.</summary>
        public bool Enter(GridPos dest)
        {
            var fresh = !visits.ContainsKey(dest);
            Position = dest;
            Moves++;
            visits[dest] = VisitCount(dest) + 1;
            path.Add(dest);
            return fresh;
        }
    }
}
