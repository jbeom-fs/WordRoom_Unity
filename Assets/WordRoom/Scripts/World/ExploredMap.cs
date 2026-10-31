using System;
using System.Collections.Generic;
using WordRoom.Generation;

namespace WordRoom.World
{
    /// <summary>One room of the explored map: its coordinate, word, whether anyone of the team entered it and whether it is the target word.</summary>
    public struct MapRoom
    {
        public GridPos Position;
        public string Word;
        public bool Visited;
        public bool IsTarget;
    }

    /// <summary>
    /// One confirmed change of the session for the map, copied on the worker inside the coordinator's capture (the
    /// only place the session is read), with three separate parts: rooms added by the change, coordinates the team
    /// visited for the first time, and players' confirmed rooms. `Full` = the whole map of one revision (start or
    /// recovery from a SessionCopy): every room with its team-visited flag and every player. Read-only once made.
    /// </summary>
    public sealed class MapUpdate
    {
        public string Session { get; private set; }
        public long Revision { get; private set; }
        public bool Full { get; private set; }
        public IReadOnlyList<MapRoom> Rooms => rooms;
        /// <summary>Existing (or just added) coordinates that became team-visited with this change.</summary>
        public IReadOnlyList<GridPos> Visited => visited;
        /// <summary>Confirmed room per player changed by this change (all players when Full).</summary>
        public IReadOnlyList<KeyValuePair<string, GridPos>> Players => players;

        readonly List<MapRoom> rooms = new List<MapRoom>();
        readonly List<GridPos> visited = new List<GridPos>();
        readonly List<KeyValuePair<string, GridPos>> players = new List<KeyValuePair<string, GridPos>>();

        /// <summary>
        /// Worker only, inside the capture of the request that made `u`. The target mark uses the generator's rule
        /// (the room's word is the session's target word).
        /// </summary>
        public static MapUpdate FromChange(SessionReader session, GenerationUpdate u, Func<int, string> wordText)
        {
            var m = new MapUpdate { Session = u.SessionId, Revision = u.Revision };
            foreach (var r in u.Added)
                m.rooms.Add(new MapRoom { Position = r.Position, Word = wordText(r.Word), Visited = session.IsTeamVisited(r.Position), IsTarget = r.Word == session.TargetWord });
            if (u.Moved)
            {
                if (u.TeamFirstVisit) m.visited.Add(u.To);
                if (u.PlayerId != null) m.players.Add(new KeyValuePair<string, GridPos>(u.PlayerId, u.To));
            }
            else if (u.Kind == "return" && u.PlayerId != null) m.players.Add(new KeyValuePair<string, GridPos>(u.PlayerId, u.To));   // back in the start room: no visit
            return m;
        }

        /// <summary>
        /// A host's confirmed change as applied to this device's copy (`after` = the copy of the change's revision):
        /// the rooms its fill added, the team's first visit and the requester's room of an entry.
        /// </summary>
        public static MapUpdate FromSessionChange(SessionChange c, SessionCopy after, Func<int, string> wordText)
        {
            var m = new MapUpdate { Session = c.SessionId, Revision = c.Revision };
            foreach (var kv in c.Added)
                m.rooms.Add(new MapRoom { Position = kv.Key, Word = wordText(kv.Value), Visited = after.IsTeamVisited(kv.Key), IsTarget = kv.Value == after.TargetWord });
            if (c.Kind == RequestKind.Enter)
            {
                if (c.TeamFirstVisit) m.visited.Add(c.To);
                m.players.Add(new KeyValuePair<string, GridPos>(c.PlayerId, c.To));
            }
            else if (c.Kind == RequestKind.Return) m.players.Add(new KeyValuePair<string, GridPos>(c.PlayerId, c.To));   // back in the start room: no visit
            return m;
        }

        /// <summary>The whole map of one revision from a full session copy (any thread; the copy is independent).</summary>
        public static MapUpdate FromCopy(SessionCopy c, Func<int, string> wordText)
        {
            var m = new MapUpdate { Session = c.SessionId, Revision = c.Revision, Full = true };
            var team = new HashSet<GridPos>(c.TeamVisited);
            foreach (var r in c.Rooms)
                m.rooms.Add(new MapRoom { Position = r.Position, Word = wordText(r.Word), Visited = team.Contains(r.Position), IsTarget = r.Word == c.TargetWord });
            foreach (var p in c.Players) m.players.Add(new KeyValuePair<string, GridPos>(p.Id, p.Position));
            return m;
        }
    }

    public enum MapApplyResult
    {
        Applied,
        /// <summary>A duplicate or older change (or an older full copy): nothing to do.</summary>
        Ignored,
        /// <summary>Another session's change: dropped.</summary>
        OtherSession,
        /// <summary>A change was missed: nothing applied; a full copy of one revision is needed.</summary>
        Gap,
    }

    /// <summary>
    /// The explored map of the current session as one device's map UI sees it: every room generated so far (also those
    /// the 3D view has returned to its pool), the team's visited flag, the target mark and each player's confirmed
    /// room; Current is the local player's. Main thread only; it never reads the session — RoomGenerationDebug applies
    /// the MapUpdates of finished requests. Changes are applied strictly in revision order: duplicates and older
    /// ones are ignored, a gap is refused until a full copy arrives (never judged by the room count). Version grows
    /// with every applied change, so the UI redraws only then. Nothing here generates rooms.
    /// </summary>
    public sealed class ExploredMap
    {
        readonly Dictionary<GridPos, int> index = new Dictionary<GridPos, int>();
        readonly List<MapRoom> rooms = new List<MapRoom>();
        readonly Dictionary<string, GridPos> players = new Dictionary<string, GridPos>();

        /// <summary>Session this data belongs to; updates of another session are refused.</summary>
        public string Session { get; private set; }

        /// <summary>The player of this device (current room highlight).</summary>
        public string LocalPlayer { get; private set; }

        /// <summary>Confirmed change number of the applied state (0 = nothing yet).</summary>
        public long Revision { get; private set; }

        /// <summary>Grows with every applied change (0 = nothing yet in this session).</summary>
        public int Version { get; private set; }

        public int Count => rooms.Count;

        /// <summary>Generated rooms holding the target word (one word may stand at several coordinates).</summary>
        public int TargetCount { get; private set; }

        /// <summary>The local player's confirmed room (after the last applied start, entry, move or full copy).</summary>
        public GridPos Current => LocalPlayer != null && players.TryGetValue(LocalPlayer, out var p) ? p : GridPos.Origin;

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

        /// <summary>A player's confirmed room as last applied.</summary>
        public bool TryGetPlayer(string playerId, out GridPos room) => players.TryGetValue(playerId, out room);

        /// <summary>A new session: everything of the previous one is dropped.</summary>
        public void Reset(string session, string localPlayer)
        {
            index.Clear();
            rooms.Clear();
            players.Clear();
            TargetCount = 0;
            Session = session;
            LocalPlayer = localPlayer;
            Revision = 0;
            Version = 0;
        }

        /// <summary>Applies one update (see MapApplyResult); `failure` says why for a gap or another session.</summary>
        public MapApplyResult Apply(MapUpdate u, out string failure)
        {
            failure = null;
            if (u.Session != Session)
            {
                failure = $"다른 세션의 지도 갱신({u.Session}, 현재 {Session})";
                return MapApplyResult.OtherSession;
            }
            if (u.Full)
            {
                if (HasData && u.Revision <= Revision) return MapApplyResult.Ignored;
                index.Clear();
                rooms.Clear();
                players.Clear();
                TargetCount = 0;
            }
            else
            {
                if (u.Revision <= Revision) return MapApplyResult.Ignored;
                if (!HasData || u.Revision != Revision + 1)
                {
                    failure = $"지도 변경 순번 누락(보유 {Revision}, 받은 {u.Revision})";
                    return MapApplyResult.Gap;
                }
            }
            foreach (var r in u.Rooms)
            {
                if (index.ContainsKey(r.Position)) continue;               // committed rooms never change
                index[r.Position] = rooms.Count;
                rooms.Add(r);
                if (r.IsTarget) TargetCount++;
            }
            foreach (var p in u.Visited)                                   // visits come only from confirmed entries, by anyone
                if (index.TryGetValue(p, out var i) && !rooms[i].Visited)
                {
                    var room = rooms[i];
                    room.Visited = true;
                    rooms[i] = room;
                }
            foreach (var kv in u.Players) players[kv.Key] = kv.Value;
            Revision = u.Revision;
            Version++;
            return MapApplyResult.Applied;
        }
    }
}
