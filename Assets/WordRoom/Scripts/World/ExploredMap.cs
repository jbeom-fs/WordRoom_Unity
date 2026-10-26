using System;
using System.Collections.Generic;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>One room of the explored map: its coordinate, word, whether it was entered and whether it is the target word.</summary>
    public struct MapRoom
    {
        public GridPos Position;
        public string Word;
        public bool Visited;
        public bool IsTarget;
    }

    /// <summary>
    /// What one generator request changed for the map, copied on the worker right after the request (the only place
    /// RoomMap is read): the rooms committed since `RoomsBefore` (RoomMap.CreationOrder only grows) or, when `Full`,
    /// every room with its visited flag; and the confirmed current room. Read-only once made.
    /// </summary>
    public sealed class MapUpdate
    {
        public int Session { get; private set; }
        public bool Full { get; private set; }
        public int RoomsBefore { get; private set; }
        public GridPos Current { get; private set; }
        public IReadOnlyList<MapRoom> Rooms => rooms;

        readonly List<MapRoom> rooms = new List<MapRoom>();

        /// <summary>
        /// Worker only, inside the request's task after the request finished: no other request runs on `map` then.
        /// `known` = rooms the main-thread map already holds; `full` = copy every room (start or resynchronization).
        /// The target mark uses the generator's own rule (the room's word is the map's target word).
        /// </summary>
        public static MapUpdate Capture(RoomMap map, Func<int, string> wordText, int session, int known, bool full)
        {
            var u = new MapUpdate { Session = session, Full = full, RoomsBefore = full ? 0 : known, Current = map.Position };
            var order = map.CreationOrder;
            for (var i = u.RoomsBefore; i < order.Count; i++)
            {
                var p = order[i];
                if (!map.TryGetWord(p, out var word)) continue;
                u.rooms.Add(new MapRoom { Position = p, Word = wordText(word), Visited = map.IsVisited(p), IsTarget = word == map.Target });
            }
            return u;
        }
    }

    /// <summary>
    /// The explored map of the current session as the map UI sees it: every room generated so far (also those the 3D
    /// view has returned to its pool), visited or not, the target mark and the confirmed current room. Main thread
    /// only; it never reads RoomMap — RoomGenerationDebug applies the MapUpdates of finished requests in order. Version
    /// grows with every applied change, so the UI redraws only then. Nothing here generates rooms; coordinates without
    /// a room simply have no entry.
    /// </summary>
    public sealed class ExploredMap
    {
        readonly Dictionary<GridPos, int> index = new Dictionary<GridPos, int>();
        readonly List<MapRoom> rooms = new List<MapRoom>();

        /// <summary>Session this data belongs to (one per experiment); updates of another session are refused.</summary>
        public int Session { get; private set; }

        /// <summary>Grows with every applied change (0 = nothing yet in this session).</summary>
        public int Version { get; private set; }

        public int Count => rooms.Count;

        /// <summary>Generated rooms holding the target word (one word may stand at several coordinates).</summary>
        public int TargetCount { get; private set; }

        /// <summary>The confirmed current room (after the last applied start, entry or move).</summary>
        public GridPos Current { get; private set; }

        /// <summary>Data of the session's start has arrived.</summary>
        public bool HasData => Version > 0;

        public bool TryGet(GridPos p, out MapRoom room)
        {
            if (index.TryGetValue(p, out var i))
            {
                room = rooms[i];
                return true;
            }
            room = default;
            return false;
        }

        public bool Has(GridPos p) => index.ContainsKey(p);

        /// <summary>A new session: everything of the previous one is dropped.</summary>
        public void Reset(int session)
        {
            index.Clear();
            rooms.Clear();
            TargetCount = 0;
            Session = session;
            Version = 0;
            Current = GridPos.Origin;
        }

        /// <summary>
        /// Applies one request's update. False (nothing changed) for another session, or for a partial update that
        /// does not continue exactly from the rooms held here — the caller then asks for a full copy next time.
        /// </summary>
        public bool Apply(MapUpdate u, out string failure)
        {
            failure = null;
            if (u.Session != Session)
            {
                failure = $"다른 세션의 지도 갱신({u.Session}, 현재 {Session})";
                return false;
            }
            if (u.Full)
            {
                index.Clear();
                rooms.Clear();
                TargetCount = 0;
            }
            else if (u.RoomsBefore != rooms.Count)
            {
                failure = $"지도 방 개수 불일치(갱신 기준 {u.RoomsBefore}, 보유 {rooms.Count})";
                return false;
            }
            foreach (var r in u.Rooms)
            {
                if (index.ContainsKey(r.Position)) continue;               // committed rooms never change
                index[r.Position] = rooms.Count;
                rooms.Add(r);
                if (r.IsTarget) TargetCount++;
            }
            Current = u.Current;
            if (index.TryGetValue(Current, out var c) && !rooms[c].Visited)   // visits only come from entering: the current room
            {
                var room = rooms[c];
                room.Visited = true;
                rooms[c] = room;
            }
            Version++;
            return true;
        }
    }
}
