using System;
using System.Collections.Generic;
using System.Linq;

namespace WordRoom.Generation
{
    /// <summary>
    /// Read-only access to one session's state, made by SessionCoordinator for exactly one capture or Read callback
    /// and usable only during that callback and only on the thread running it (inside the coordinator's request
    /// lock, so every read of one callback belongs to the same confirmed revision). When the callback ends the reader
    /// is closed for good: a kept reader never becomes valid again, also not while a later callback runs. Reads after
    /// that or from another thread throw InvalidOperationException. Only values and copies come out: no shared
    /// collection, PlacedRoom or link object of the world is handed out.
    /// </summary>
    public sealed class SessionReader : ISessionView
    {
        readonly SessionState session;
        readonly RoomMap world;
        readonly RoomGenerator generator;
        readonly int ownerThread;
        volatile bool closed;

        /// <summary>Open from now on, for the calling thread only (the coordinator's callback thread).</summary>
        internal SessionReader(SessionState session, RoomMap world, RoomGenerator generator)
        {
            this.session = session;
            this.world = world;
            this.generator = generator;
            ownerThread = Environment.CurrentManagedThreadId;
        }

        /// <summary>End of its callback: never readable again.</summary>
        internal void Close() => closed = true;

        void Check()
        {
            if (closed) throw new InvalidOperationException("SessionReader는 자신의 캡처·Read 콜백이 끝난 뒤에는 읽을 수 없습니다.");
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException("SessionReader는 콜백을 실행하는 스레드에서만 읽을 수 있습니다.");
        }

        // ---- session ------------------------------------------------------------------------------------
        public string SessionId { get { Check(); return session.Id; } }
        public SessionMode Mode { get { Check(); return session.Mode; } }
        public long Revision { get { Check(); return session.Revision; } }
        public int StartWord { get { Check(); return session.Start; } }
        public int TargetWord { get { Check(); return session.Target; } }
        public bool Started { get { Check(); return session.Started; } }
        public string Status { get { Check(); return session.Status; } }
        public string FailureReason { get { Check(); return session.FailureReason; } }
        public SessionResult Result { get { Check(); return session.Result; } }

        // ---- shared world -------------------------------------------------------------------------------
        public int RoomCount { get { Check(); return world.RoomCount; } }

        public bool TryGetWord(GridPos p, out int word)
        {
            Check();
            return world.TryGetWord(p, out word);
        }

        public bool HasRoom(GridPos p)
        {
            Check();
            return world.HasRoom(p);
        }

        /// <summary>A fill request around p was processed (not: all four neighbors exist).</summary>
        public bool IsPrepared(GridPos p)
        {
            Check();
            return world.IsPrepared(p);
        }

        public bool IsUnfilled(GridPos p)
        {
            Check();
            return world.IsUnfilled(p);
        }

        public string EmptyReason(GridPos center, GridPos position)
        {
            Check();
            return world.EmptyReason(center, position);
        }

        /// <summary>Copy of the generation record of p (null for the start room or a missing room).</summary>
        public PlacedRoom RoomInfo(GridPos p)
        {
            Check();
            return world.GetRoomInfo(p)?.Copy();
        }

        public bool IsTeamVisited(GridPos p)
        {
            Check();
            return world.IsTeamVisited(p);
        }

        /// <summary>Distinct coordinates entered by anyone, start included.</summary>
        public int TeamVisitedCount { get { Check(); return world.TeamVisitedCount; } }

        public DoorState Door(RoomConnection id)
        {
            Check();
            return world.Doors.Get(id);
        }

        // ---- players --------------------------------------------------------------------------------------
        public bool HasPlayer(string playerId)
        {
            Check();
            return session.FindPlayer(playerId) != null;
        }

        /// <summary>Registered players in registration order (a copy).</summary>
        public string[] PlayerIds()
        {
            Check();
            return session.Players.Select(p => p.Id).ToArray();
        }

        public GridPos PlayerPosition(string playerId) => Player(playerId).Position;
        public int PlayerMoves(string playerId) => Player(playerId).Moves;
        public int PlayerUniqueVisited(string playerId) => Player(playerId).UniqueVisited;
        public int PlayerVisitCount(string playerId, GridPos p) => Player(playerId).VisitCount(p);

        /// <summary>Guidance strength new rooms would get now from a request of this player.</summary>
        public double Guidance(string playerId) => generator.Guidance(SessionCoordinator.GuidanceProgress(session, world, Player(playerId)));

        PlayerState Player(string playerId)
        {
            Check();
            return session.FindPlayer(playerId) ?? throw new ArgumentException($"등록되지 않은 플레이어: {playerId}");
        }

        /// <summary>The whole session at the current revision: world, every player and the session state together.</summary>
        public SessionCopy CopyAll()
        {
            Check();
            return new SessionCopy(session, world);
        }
    }

    /// <summary>One room of a SessionCopy, in creation order. Info is a copy (null for the start room).</summary>
    public readonly struct RoomRecord
    {
        public readonly GridPos Position;
        public readonly int Word;
        public readonly PlacedRoom Info;

        public RoomRecord(GridPos position, int word, PlacedRoom info)
        {
            Position = position;
            Word = word;
            Info = info;
        }
    }

    /// <summary>A room whose fill stayed incomplete: what was missing, why, and the room count it may be retried after.</summary>
    public sealed class UnfilledCopy
    {
        public GridPos Center { get; }
        /// <summary>Room count of the world when it failed (retried only once the count differs).</summary>
        public int RoomCount { get; }
        public IReadOnlyList<GridPos> Missing { get; }
        public IReadOnlyList<EmptyPosition> Empty { get; }

        internal UnfilledCopy(GridPos center, UnfilledRecord r)
        {
            Center = center;
            RoomCount = r.Rooms;
            Missing = Array.AsReadOnly(r.Missing.ToArray());
            Empty = Array.AsReadOnly(r.Empty.Select(e => e.Copy()).ToArray());
        }
    }

    /// <summary>One player's logical state in a SessionCopy.</summary>
    public sealed class PlayerCopy
    {
        public string Id { get; }
        public GridPos Position { get; }
        public int Moves { get; }
        public int UniqueVisited => Visits.Count;
        /// <summary>Visit count per coordinate, in coordinate order.</summary>
        public IReadOnlyList<KeyValuePair<GridPos, int>> Visits { get; }
        public IReadOnlyList<GridPos> Path { get; }

        internal PlayerCopy(PlayerState p)
        {
            Id = p.Id;
            Position = p.Position;
            Moves = p.Moves;
            Visits = Array.AsReadOnly(p.Visits.OrderBy(kv => kv.Key).ToArray());
            Path = Array.AsReadOnly(p.Path.ToArray());
        }

        internal PlayerCopy(SessionCopyParts.Player p)
        {
            Id = p.Id;
            Position = p.Position;
            Moves = p.Moves;
            Visits = Array.AsReadOnly(p.Visits.OrderBy(kv => kv.Key).ToArray());
            Path = Array.AsReadOnly(p.Path.ToArray());
        }
    }

    /// <summary>
    /// The values a SessionCopy is restored from (e.g. decoded from a host's message on a device without a
    /// coordinator). Only what a display needs: no generation records, recent words, unfilled records or results.
    /// </summary>
    public sealed class SessionCopyParts
    {
        public sealed class Player
        {
            public string Id;
            public GridPos Position;
            public int Moves;
            public List<KeyValuePair<GridPos, int>> Visits = new List<KeyValuePair<GridPos, int>>();
            public List<GridPos> Path = new List<GridPos>();
        }

        public string SessionId;
        public SessionMode Mode;
        public long Revision;
        public int StartWord;
        public int TargetWord;
        public long Seed;
        public string PackageId;
        public string Status;
        /// <summary>Rooms in creation order (the start room first, at the origin).</summary>
        public List<KeyValuePair<GridPos, int>> Rooms = new List<KeyValuePair<GridPos, int>>();
        public List<GridPos> Prepared = new List<GridPos>();
        public List<GridPos> TeamVisited = new List<GridPos>();
        public List<RoomConnection> OpenDoors = new List<RoomConnection>();
        public List<Player> Players = new List<Player>();
        /// <summary>The host's confirmed result (only with Status "success"); null otherwise.</summary>
        public SessionResult Result;
    }

    /// <summary>
    /// The whole session at one revision (world, players and session state of the same confirmed change number), for
    /// recovering a display that missed a change. Independent of the live state: nothing in it reaches the session.
    /// The dataset and the generator's derived caches are not part of it. Also a read-only ISessionView, so a display
    /// can be captured from it directly — including a copy restored from parts (Restore) on a device that only shows
    /// a host's confirmed state and never runs a generator.
    /// </summary>
    public sealed class SessionCopy : ISessionView
    {
        public string SessionId { get; private set; }
        public SessionMode Mode { get; private set; }
        public long Revision { get; private set; }
        public int StartWord { get; private set; }
        public int TargetWord { get; private set; }
        public long Seed { get; private set; }
        public string PackageId { get; private set; }
        public bool Started { get; private set; }
        public string Status { get; private set; }
        public string FailureReason { get; private set; }
        public double PlayStartedAt { get; private set; }
        public SessionResult Result { get; private set; }

        /// <summary>Every committed room in creation order.</summary>
        public IReadOnlyList<RoomRecord> Rooms { get; private set; }
        public IReadOnlyList<GridPos> Prepared { get; private set; }
        public IReadOnlyList<UnfilledCopy> Unfilled { get; private set; }
        /// <summary>Recent generated words, oldest first (the world's single history).</summary>
        public IReadOnlyList<int> Recent { get; private set; }
        public IReadOnlyList<GridPos> TeamVisited { get; private set; }
        public IReadOnlyList<RoomConnection> OpenDoors { get; private set; }
        /// <summary>Registered players in registration order.</summary>
        public IReadOnlyList<PlayerCopy> Players { get; private set; }

        readonly Dictionary<GridPos, int> wordAt = new Dictionary<GridPos, int>();
        readonly HashSet<GridPos> preparedSet = new HashSet<GridPos>();
        readonly HashSet<RoomConnection> openSet = new HashSet<RoomConnection>();
        readonly HashSet<GridPos> teamSet = new HashSet<GridPos>();

        SessionCopy()
        {
        }

        internal SessionCopy(SessionState s, RoomMap w)
        {
            SessionId = s.Id;
            Mode = s.Mode;
            Revision = s.Revision;
            StartWord = s.Start;
            TargetWord = s.Target;
            Seed = s.Config.Seed;
            PackageId = s.PackageId;
            Started = s.Started;
            Status = s.Status;
            FailureReason = s.FailureReason;
            PlayStartedAt = s.PlayStartedAt;
            Result = s.Result;
            var rooms = new List<RoomRecord>(w.RoomCount);
            foreach (var p in w.CreationOrder)
                if (w.TryGetWord(p, out var word)) rooms.Add(new RoomRecord(p, word, w.GetRoomInfo(p)?.Copy()));
            Rooms = rooms.AsReadOnly();
            Prepared = Array.AsReadOnly(w.Prepared.OrderBy(p => p).ToArray());
            Unfilled = Array.AsReadOnly(w.Unfilled.OrderBy(kv => kv.Key).Select(kv => new UnfilledCopy(kv.Key, kv.Value)).ToArray());
            Recent = Array.AsReadOnly(w.Recent.ToArray());
            TeamVisited = Array.AsReadOnly(w.TeamVisited.OrderBy(p => p).ToArray());
            OpenDoors = w.Doors.OpenConnections().AsReadOnly();
            Players = Array.AsReadOnly(s.Players.Select(p => new PlayerCopy(p)).ToArray());
            Index();
        }

        void Index()
        {
            foreach (var r in Rooms) wordAt[r.Position] = r.Word;
            foreach (var p in Prepared) preparedSet.Add(p);
            foreach (var d in OpenDoors) openSet.Add(d);
            foreach (var p in TeamVisited) teamSet.Add(p);
        }

        public PlayerCopy FindPlayer(string playerId) => Players.FirstOrDefault(p => p.Id == playerId);

        // ---- ISessionView -----------------------------------------------------------------------------------
        public GridPos PlayerPosition(string playerId) =>
            (FindPlayer(playerId) ?? throw new ArgumentException($"등록되지 않은 플레이어: {playerId}")).Position;

        public bool TryGetWord(GridPos p, out int word) => wordAt.TryGetValue(p, out word);
        public bool HasRoom(GridPos p) => wordAt.ContainsKey(p);
        public bool IsPrepared(GridPos p) => preparedSet.Contains(p);
        public DoorState Door(RoomConnection id) => openSet.Contains(id) ? DoorState.Open : DoorState.Closed;
        public bool IsTeamVisited(GridPos p) => teamSet.Contains(p);

        // ---- confirmed change ------------------------------------------------------------------------------
        /// <summary>
        /// A new copy one revision later: `c` (the host's confirmed change of revision Revision + 1) applied to this one,
        /// then checked as a whole like Restore. Checks the change itself against this state the way the coordinator
        /// confirmed it: same session, the next revision, a running session, a registered requester standing in From,
        /// To adjacent and existing; Prepare — rooms added only on empty coordinates next to To, the door stored Open
        /// exactly when it was closed, To prepared, nobody moved; Enter — no rooms added, the door Open and To prepared,
        /// the requester's moves one more and the team's first visit as reported. Throws ArgumentException with the
        /// reason (nothing applied); this copy never changes.
        /// </summary>
        public SessionCopy Apply(SessionChange c)
        {
            if (c == null) throw new ArgumentException("변경이 없습니다.");
            if (c.SessionId != SessionId) throw new ArgumentException($"다른 세션의 변경입니다({c.SessionId}).");
            if (c.Revision != Revision + 1) throw new ArgumentException($"변경 순번이 이어지지 않습니다(보유 {Revision}, 받은 {c.Revision}).");
            if (Status != "running") throw new ArgumentException($"끝난 세션의 변경입니다({Status}).");
            var player = FindPlayer(c.PlayerId) ?? throw new ArgumentException($"등록되지 않은 플레이어의 변경입니다({c.PlayerId}).");
            if (c.From != player.Position) throw new ArgumentException($"{c.PlayerId}의 확정 위치 {player.Position}가 변경의 출발 {c.From}와 다릅니다.");
            if (GridPos.Distance(c.From, c.To) != 1 || !HasRoom(c.To)) throw new ArgumentException($"{c.From}→{c.To}는 인접한 기존 방이 아닙니다.");
            if (string.IsNullOrEmpty(c.Status)) throw new ArgumentException("변경의 세션 상태가 비어 있습니다.");
            var id = RoomConnection.Between(c.From, c.To);
            var parts = ToParts();
            parts.Revision = c.Revision;
            parts.Status = c.Status;
            parts.Result = c.Result ?? Result;
            var p = parts.Players.First(x => x.Id == c.PlayerId);
            switch (c.Kind)
            {
                case RequestKind.Prepare:
                    if (c.Moves != player.Moves || c.Status != Status) throw new ArgumentException("문 열기 변경이 이동 수나 세션 상태를 바꿉니다.");
                    if (c.DoorOpened == (Door(id) == DoorState.Open)) throw new ArgumentException($"문 {id}의 열림 기록이 현재 상태와 맞지 않습니다.");
                    foreach (var kv in c.Added)
                    {
                        if (HasRoom(kv.Key) || GridPos.Distance(kv.Key, c.To) != 1) throw new ArgumentException($"추가된 방 {kv.Key}의 위치가 올바르지 않습니다.");
                        parts.Rooms.Add(kv);                               // Restore refuses a coordinate added twice
                    }
                    if (c.DoorOpened) parts.OpenDoors.Add(id);
                    if (!IsPrepared(c.To)) parts.Prepared.Add(c.To);
                    break;
                case RequestKind.Enter:
                    if (c.Added.Count > 0 || c.DoorOpened) throw new ArgumentException("입장 변경에 방 생성·문 열기가 있습니다.");
                    if (Door(id) != DoorState.Open || !IsPrepared(c.To)) throw new ArgumentException($"{c.To}로 가는 문이 열리지 않았거나 준비 전입니다.");
                    if (c.Moves != player.Moves + 1) throw new ArgumentException($"{c.PlayerId}의 이동 수가 이어지지 않습니다(보유 {player.Moves}, 받은 {c.Moves}).");
                    if (c.TeamFirstVisit == IsTeamVisited(c.To)) throw new ArgumentException($"{c.To}의 팀 첫 방문 여부가 현재 상태와 맞지 않습니다.");
                    p.Position = c.To;
                    p.Moves = c.Moves;
                    var at = p.Visits.FindIndex(v => v.Key == c.To);
                    if (at < 0) p.Visits.Add(new KeyValuePair<GridPos, int>(c.To, 1));
                    else p.Visits[at] = new KeyValuePair<GridPos, int>(c.To, p.Visits[at].Value + 1);
                    p.Path.Add(c.To);
                    if (c.TeamFirstVisit) parts.TeamVisited.Add(c.To);
                    break;
                default:
                    throw new ArgumentException($"협동 변경으로 적용할 수 없는 종류입니다({c.Kind}).");
            }
            return Restore(parts);
        }

        /// <summary>
        /// Whether this copy describes one consistent state by Restore's rules (e.g. the host's state after an error in a
        /// request, before it is sent to everyone). False with the reason otherwise.
        /// </summary>
        public bool Validate(out string why)
        {
            try
            {
                Restore(ToParts());
                why = null;
                return true;
            }
            catch (ArgumentException e)
            {
                why = e.Message;
                return false;
            }
        }

        /// <summary>The values of this copy as new, independent parts (Restore of them gives an equal copy).</summary>
        SessionCopyParts ToParts()
        {
            var parts = new SessionCopyParts
            {
                SessionId = SessionId, Mode = Mode, Revision = Revision, StartWord = StartWord, TargetWord = TargetWord, Seed = Seed,
                PackageId = PackageId, Status = Status, Result = Result,
            };
            foreach (var r in Rooms) parts.Rooms.Add(new KeyValuePair<GridPos, int>(r.Position, r.Word));
            parts.Prepared.AddRange(Prepared);
            parts.TeamVisited.AddRange(TeamVisited);
            parts.OpenDoors.AddRange(OpenDoors);
            foreach (var p in Players)
            {
                var copy = new SessionCopyParts.Player { Id = p.Id, Position = p.Position, Moves = p.Moves };
                copy.Visits.AddRange(p.Visits);
                copy.Path.AddRange(p.Path);
                parts.Players.Add(copy);
            }
            return parts;
        }

        // ---- restore ------------------------------------------------------------------------------------------
        /// <summary>
        /// A copy made from `parts` after checking that they describe one consistent state: the start room at the
        /// origin with the start word, no coordinate twice, prepared / team-visited / door coordinates on existing rooms
        /// (doors between adjacent existing rooms), unique players standing in existing rooms they visited, paths ending
        /// there, and every personal visit inside the team's. Throws ArgumentException with the reason otherwise.
        /// Recent words, unfilled records and generation records are not part of a restored copy; a result is kept only
        /// with the status "success" and a revision not after the copy's.
        /// </summary>
        public static SessionCopy Restore(SessionCopyParts parts)
        {
            if (parts == null) throw new ArgumentException("사본 정보가 없습니다.");
            if (string.IsNullOrEmpty(parts.SessionId)) throw new ArgumentException("세션 ID가 비어 있습니다.");
            if (parts.Revision < 1) throw new ArgumentException($"변경 순번이 올바르지 않습니다({parts.Revision}).");
            if (string.IsNullOrEmpty(parts.Status)) throw new ArgumentException("세션 상태가 비어 있습니다.");
            if (parts.Rooms == null || parts.Rooms.Count == 0) throw new ArgumentException("방이 없습니다.");
            if (parts.Rooms[0].Key != GridPos.Origin || parts.Rooms[0].Value != parts.StartWord) throw new ArgumentException("첫 방이 원점의 시작 단어가 아닙니다.");
            var rooms = new Dictionary<GridPos, int>();
            var records = new List<RoomRecord>(parts.Rooms.Count);
            foreach (var kv in parts.Rooms)
            {
                if (kv.Value < 0) throw new ArgumentException($"{kv.Key}의 단어 번호가 올바르지 않습니다.");
                if (rooms.ContainsKey(kv.Key)) throw new ArgumentException($"{kv.Key}에 방이 두 번 있습니다.");
                rooms.Add(kv.Key, kv.Value);
                records.Add(new RoomRecord(kv.Key, kv.Value, null));
            }
            var prepared = Distinct(parts.Prepared, rooms, "주변 준비");
            var team = Distinct(parts.TeamVisited, rooms, "팀 방문");
            var doors = new HashSet<RoomConnection>();
            foreach (var d in parts.OpenDoors ?? new List<RoomConnection>())
            {
                if (!rooms.ContainsKey(d.A) || !rooms.ContainsKey(d.B)) throw new ArgumentException($"열린 문 {d}의 방이 없습니다.");
                if (!doors.Add(d)) throw new ArgumentException($"열린 문 {d}이 두 번 있습니다.");
            }
            if (parts.Players == null || parts.Players.Count == 0) throw new ArgumentException("플레이어가 없습니다.");
            var ids = new HashSet<string>();
            var teamSet = new HashSet<GridPos>(team);
            var players = new List<PlayerCopy>(parts.Players.Count);
            foreach (var p in parts.Players)
            {
                if (p == null || string.IsNullOrEmpty(p.Id) || !ids.Add(p.Id)) throw new ArgumentException("플레이어 ID가 비어 있거나 중복됩니다.");
                if (!rooms.ContainsKey(p.Position)) throw new ArgumentException($"{p.Id}의 위치 {p.Position}에 방이 없습니다.");
                if (p.Moves < 0) throw new ArgumentException($"{p.Id}의 이동 수가 올바르지 않습니다.");
                var visited = new HashSet<GridPos>();
                foreach (var v in p.Visits ?? new List<KeyValuePair<GridPos, int>>())
                {
                    if (v.Value < 1 || !rooms.ContainsKey(v.Key) || !visited.Add(v.Key)) throw new ArgumentException($"{p.Id}의 방문 기록 {v.Key}가 올바르지 않습니다.");
                    if (!teamSet.Contains(v.Key)) throw new ArgumentException($"{p.Id}의 방문 {v.Key}가 팀 방문에 없습니다.");
                }
                if (!visited.Contains(p.Position)) throw new ArgumentException($"{p.Id}의 현재 방이 방문 기록에 없습니다.");
                if (p.Path == null || p.Path.Count == 0 || p.Path[p.Path.Count - 1] != p.Position || p.Path.Any(q => !rooms.ContainsKey(q)))
                    throw new ArgumentException($"{p.Id}의 경로가 올바르지 않습니다.");
                players.Add(new PlayerCopy(p));
            }
            if (parts.Result != null && (parts.Status != "success" || parts.Result.Revision > parts.Revision || !ids.Contains(parts.Result.ReachedBy)))
                throw new ArgumentException("세션 결과가 상태·순번·참가자와 맞지 않습니다.");
            var c = new SessionCopy
            {
                SessionId = parts.SessionId,
                Mode = parts.Mode,
                Revision = parts.Revision,
                StartWord = parts.StartWord,
                TargetWord = parts.TargetWord,
                Seed = parts.Seed,
                PackageId = parts.PackageId,
                Started = true,
                Status = parts.Status,
                PlayStartedAt = double.NaN,
                Result = parts.Result,
                Rooms = records.AsReadOnly(),
                Prepared = Array.AsReadOnly(prepared.OrderBy(p => p).ToArray()),
                Unfilled = Array.AsReadOnly(new UnfilledCopy[0]),
                Recent = Array.AsReadOnly(new int[0]),
                TeamVisited = Array.AsReadOnly(team.OrderBy(p => p).ToArray()),
                OpenDoors = Array.AsReadOnly(doors.OrderBy(d => d).ToArray()),
                Players = players.AsReadOnly(),
            };
            c.Index();
            return c;
        }

        static List<GridPos> Distinct(List<GridPos> list, Dictionary<GridPos, int> rooms, string what)
        {
            var seen = new HashSet<GridPos>();
            foreach (var p in list ?? new List<GridPos>())
                if (!rooms.ContainsKey(p) || !seen.Add(p)) throw new ArgumentException($"{what} 좌표 {p}가 올바르지 않습니다.");
            return seen.ToList();
        }
    }
}
