using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WordRoom.Data;

namespace WordRoom.Generation
{
    public enum RequestKind
    {
        /// <summary>Door opening: store the connection Open, then fill around the room behind it (no move, no visit).</summary>
        Prepare,
        /// <summary>Walking through: enter the prepared room behind an open door (no fill).</summary>
        Enter,
        /// <summary>Development arrow keys: enter an existing neighbor, then fill around it (no door).</summary>
        Move,
    }

    /// <summary>
    /// One player's request. SessionId, PlayerId and RequestId identify it (the same RequestId of the same player is
    /// never applied twice); ExpectedFrom is the room the requester believes it stands in, checked against its
    /// confirmed position. CrossedAt is the requester's real time of the boundary crossing (Enter only; NaN
    /// otherwise), the end of the play time if this entry reaches the goal — in solo; a coop coordinator with an end
    /// clock (UseEndClock) reads the end at the confirmation instead and ignores it.
    /// </summary>
    public readonly struct SessionRequest
    {
        public readonly string SessionId;
        public readonly string PlayerId;
        public readonly long RequestId;
        public readonly RequestKind Kind;
        public readonly GridPos ExpectedFrom;
        public readonly Direction Direction;
        public readonly double CrossedAt;

        public SessionRequest(string sessionId, string playerId, long requestId, RequestKind kind, GridPos expectedFrom, Direction direction, double crossedAt = double.NaN)
        {
            SessionId = sessionId;
            PlayerId = playerId;
            RequestId = requestId;
            Kind = kind;
            ExpectedFrom = expectedFrom;
            Direction = direction;
            CrossedAt = crossedAt;
        }
    }

    /// <summary>
    /// What a request returned: the confirmed change (Update) or the refusal reason — a refused request changed
    /// nothing and is not a session failure (an ended session is told by Update.Status, an error by the exception).
    /// Update is always the caller's own deep copy of the confirmed change; changing it changes nothing kept.
    /// Duplicate: the same request (player and RequestId) was handled before. Its first outcome is returned as it was
    /// confirmed then — the first Update (a new copy, same Revision, To, Fill …), the first refusal or the first
    /// failure — and nothing is applied again. No capture runs for a duplicate: its Update may be older than the current
    /// state, so a caller needing the current state reads it separately (SessionCoordinator.Read), never from this result.
    /// Failure (only for a duplicate of a request whose first handling threw): that error, with FailedRevision the
    /// session's revision right after it — the state may have changed partly, so it is neither accepted nor refused.
    /// Captured exists only when HasCaptured: a first, accepted request with a capture callback; it was made inside
    /// the same lock right after that change, so it belongs to Update.Revision. Reading Captured otherwise throws.
    /// </summary>
    public sealed class RequestResult<T>
    {
        readonly T captured;

        public GenerationUpdate Update { get; }
        public string Refusal { get; }
        public string Failure { get; }
        public long FailedRevision { get; }
        public bool Duplicate { get; }
        public bool Accepted => Update != null;
        public bool HasCaptured { get; }

        public T Captured
        {
            get
            {
                if (!HasCaptured)
                    throw new InvalidOperationException(Duplicate ? "중복 요청 응답에는 캡처가 없습니다. 현재 상태는 Read로 조회하세요." : "이 요청 결과에는 캡처가 없습니다(거절 또는 캡처 콜백 없음).");
                return captured;
            }
        }

        RequestResult(GenerationUpdate update, string refusal, bool duplicate, bool hasCaptured, T captured, string failure = null, long failedRevision = 0)
        {
            Update = update;
            Refusal = refusal;
            Duplicate = duplicate;
            HasCaptured = hasCaptured;
            this.captured = captured;
            Failure = failure;
            FailedRevision = failedRevision;
        }

        internal static RequestResult<T> Refused(string refusal, bool duplicate = false) => new RequestResult<T>(null, refusal, duplicate, false, default);
        internal static RequestResult<T> Confirmed(GenerationUpdate update, bool duplicate) => new RequestResult<T>(update, null, duplicate, false, default);
        internal static RequestResult<T> WithCapture(GenerationUpdate update, T captured) => new RequestResult<T>(update, null, false, true, captured);
        internal static RequestResult<T> FailedBefore(string failure, long revision) => new RequestResult<T>(null, null, true, false, default, failure, revision);
    }

    /// <summary>
    /// The one authority of a session (pure C#, no Unity objects): it owns the session state, the shared world
    /// (RoomMap: rooms, preparation, recent words, team visits, doors), every player's state and the generator, and is
    /// the only path that changes them. Each request runs entirely inside one lock: checks (session, participant,
    /// expected position, adjacency, room, door, preparation, progress) → state change → confirmed revision →
    /// the caller's capture (snapshots read through SessionReader). Nothing of another request can mix into a
    /// capture, also for reads that change nothing (Read). Requests from other threads wait for the lock and run in
    /// turn. A callback must not call Start, Submit or Read of the same coordinator: such a re-entry (same thread,
    /// inside the lock) throws InvalidOperationException before anything is checked or changed. Each callback gets
    /// its own SessionReader, valid only during that callback and on its thread. Requests of the same player and
    /// RequestId are applied once (see RequestResult for duplicates); requests of another session are refused. The
    /// confirmed updates kept for duplicates are never handed out themselves, only copies. Solo play registers one
    /// player; coop registers several, each with an independent position, moves, visits and path.
    /// Not covered here: network transport, arrival-time priority between hosts and guests, reconnection; the
    /// handled-request record is kept for the whole session (its limit is a later decision). Cancellation is checked
    /// where the generator always checked it (see Known Issue KI-001, kept as is).
    /// </summary>
    public sealed class SessionCoordinator
    {
        readonly object gate = new object();
        readonly SessionState session;
        readonly RoomMap world;
        readonly RoomGenerator generator;
        readonly Dictionary<(string, long), Handled> handled = new Dictionary<(string, long), Handled>();
        readonly string[] playerIds;
        bool inCallback;                     // a capture or Read callback runs (only ever seen true by its own thread, inside the lock)

        /// <summary>First outcome of a request; Update is the kept confirmed change, never handed out itself.</summary>
        sealed class Handled
        {
            public GenerationUpdate Update;
            public string Refusal;
            public string Failure;               // its handling threw; FailedRevision = the revision right after
            public long FailedRevision;
        }

        Func<double> endClock;               // coop: the clock read for the goal entry when it is confirmed; null = the request's CrossedAt

        /// <summary>
        /// Coop: the end of play of the goal entry is read from `clock` inside the lock, at the moment that entry is
        /// confirmed (not the requester's CrossedAt) — on the same time axis as the play clock start. The clock is called
        /// on the worker that runs the request, so it must be thread-safe and use no engine API. Set once, before requests.
        /// </summary>
        public void UseEndClock(Func<double> clock)
        {
            lock (gate)
            {
                ThrowIfReentered();
                endClock = clock;
            }
        }

        public string SessionId => session.Id;
        public SessionMode Mode => session.Mode;
        public int StartWord => session.Start;
        public int TargetWord => session.Target;
        public WordRoomDataset Data => generator.Data;
        /// <summary>Copy of the session's fixed generation settings (changing it changes nothing).</summary>
        public GenerationConfig Config => generator.Config.Clone();
        public IReadOnlyList<string> PlayerIds => playerIds;

        SessionCoordinator(SessionMode mode, RoomGenerator generator, int start, int target, string[] players)
        {
            this.generator = generator;
            session = new SessionState(Guid.NewGuid().ToString("N"), mode, start, target, generator.Config, generator.Data.PackageId);
            world = new RoomMap(start, generator.Config.RecentWindow);
            foreach (var id in players) session.Players.Add(new PlayerState(id));
            playerIds = players;
        }

        /// <summary>
        /// A new session (not started): validates the settings and words (RoomGenerator.Create; ArgumentException with
        /// the reason) and registers the players, all in the start room once Start runs. Solo needs exactly one.
        /// </summary>
        public static SessionCoordinator Create(WordRoomDataset data, GenerationConfig config, int start, int target, SessionMode mode, IEnumerable<string> players, CancellationToken cancel = default)
        {
            var ids = players?.ToArray() ?? Array.Empty<string>();
            if (ids.Length == 0) throw new ArgumentException("참가자가 없습니다.");
            if (mode == SessionMode.Solo && ids.Length != 1) throw new ArgumentException($"단독 세션은 참가자 한 명이어야 합니다({ids.Length}명).");
            if (ids.Any(string.IsNullOrEmpty)) throw new ArgumentException("비어 있는 플레이어 ID가 있습니다.");
            if (ids.Distinct().Count() != ids.Length) throw new ArgumentException("중복된 플레이어 ID가 있습니다.");
            var gen = RoomGenerator.Create(data, config, start, target, cancel);
            return new SessionCoordinator(mode, gen, start, target, ids);
        }

        public static SessionCoordinator CreateSolo(WordRoomDataset data, GenerationConfig config, int start, int target, string playerId, CancellationToken cancel = default) =>
            Create(data, config, start, target, SessionMode.Solo, new[] { playerId }, cancel);

        // ---- requests ------------------------------------------------------------------------------------
        /// <summary>
        /// Simulation.reserve, once per session: every registered player stands in the start room with one personal
        /// visit, the team visit set holds the start coordinate once, and its neighbors are filled. A start room
        /// without any neighbor ends the session ("no_exit": nobody can leave). Refused when already started.
        /// `capture` (may be null) runs on the confirmed state in the same lock.
        /// </summary>
        public RequestResult<T> Start<T>(Func<SessionReader, GenerationUpdate, T> capture, CancellationToken token = default)
        {
            lock (gate)
            {
                ThrowIfReentered();
                if (session.Started) return RequestResult<T>.Refused("이미 시작한 실험입니다.");
                session.Started = true;
                var lead = session.Mode == SessionMode.Solo ? session.Players[0] : null;
                var u = new GenerationUpdate { Kind = "start", From = GridPos.Origin, To = GridPos.Origin, Moved = false, FirstVisit = true, TeamFirstVisit = true };
                try
                {
                    foreach (var p in session.Players) p.BeginAt(GridPos.Origin);
                    world.MarkTeamVisited(GridPos.Origin);
                    u.Fill = generator.Fill(world, new GenerationContext(GridPos.Origin, GuidanceProgress(session, world, lead), token));
                    if (!world.HasAnyNeighbor(GridPos.Origin))
                    {
                        session.Status = "no_exit";
                        session.FailureReason = "시작 방 주변에 생성된 방이 하나도 없어 이동할 수 없습니다. " + RoomGenerator.Describe(u.Fill);
                    }
                }
                finally
                {
                    session.Revision++;                    // also after an error: whatever changed is a new state
                }
                Finish(u, lead, 0, double.NaN);
                return Respond(u, capture);                // the start is never kept for duplicates: refused once started
            }
        }

        /// <summary>
        /// Checks and applies one request, then runs `capture` (may be null) on the confirmed state in the same lock.
        /// Prepare stores the connection Open (at most once per connection, from either side) and fills around the
        /// room behind it; the requester's position, moves, visits and the session status do not change. Enter
        /// updates only the requester's record and the team visit set and confirms the goal once; it never fills.
        /// Move (debug) enters and then fills, as before. Refusals change nothing. A duplicate returns its first
        /// outcome without a capture (see RequestResult). An error while applying is thrown as before, and recorded:
        /// the same request is never applied again (its duplicate returns that failure with the revision after it —
        /// whatever had changed stays; nothing is rolled back here, see KI-001).
        /// </summary>
        public RequestResult<T> Submit<T>(SessionRequest r, Func<SessionReader, GenerationUpdate, T> capture, CancellationToken token = default)
        {
            lock (gate)
            {
                ThrowIfReentered();
                if (r.SessionId != session.Id) return RequestResult<T>.Refused($"다른 세션의 요청입니다({r.SessionId}).");
                var key = (r.PlayerId ?? "", r.RequestId);
                if (handled.TryGetValue(key, out var before))
                {
                    if (before.Failure != null) return RequestResult<T>.FailedBefore(before.Failure, before.FailedRevision);
                    return before.Update != null ? RequestResult<T>.Confirmed(before.Update.Copy(), true) : RequestResult<T>.Refused(before.Refusal, true);
                }
                Handled h;
                try
                {
                    h = Apply(r, token);
                }
                catch (Exception e)
                {
                    handled[key] = new Handled { Failure = e.Message, FailedRevision = session.Revision };   // never applied a second time
                    throw;
                }
                handled[key] = h;                          // kept as confirmed; only copies leave
                return h.Update != null ? Respond(h.Update, capture) : RequestResult<T>.Refused(h.Refusal);
            }
        }

        /// <summary>
        /// A capture without any change (e.g. a display radius change or the current state after a duplicate
        /// response), consistent with the last confirmed revision.
        /// </summary>
        public T Read<T>(Func<SessionReader, T> read)
        {
            lock (gate)
            {
                ThrowIfReentered();
                return RunCallback(read);
            }
        }

        // ---- time ----------------------------------------------------------------------------------------
        /// <summary>
        /// The device reports the first moment play could really be controlled (its real-time clock). Recorded once;
        /// later calls change nothing. Uses the session's small time lock only, so it never waits for a generation.
        /// </summary>
        public bool StartPlayClock(double realTime) => session.TryStartClock(realTime);

        public bool PlayClockStarted => !double.IsNaN(session.PlayStartedAt);

        // ---- inside the lock -------------------------------------------------------------------------------
        /// <summary>
        /// The lock is re-entrant for its own thread: true inCallback here can only be a callback calling back into
        /// this coordinator (another thread waits for the lock until the callback has ended).
        /// </summary>
        void ThrowIfReentered()
        {
            if (inCallback) throw new InvalidOperationException("캡처·Read 콜백 안에서 같은 SessionCoordinator의 Start·Submit·Read를 호출할 수 없습니다.");
        }

        /// <summary>A fresh copy for the caller; the capture (if any) sees that same copy, never the kept update.</summary>
        RequestResult<T> Respond<T>(GenerationUpdate confirmed, Func<SessionReader, GenerationUpdate, T> capture)
        {
            var given = confirmed.Copy();
            if (capture == null) return RequestResult<T>.Confirmed(given, false);
            return RequestResult<T>.WithCapture(given, RunCallback(r => capture(r, given)));
        }

        /// <summary>One callback with its own reader: valid on this thread until the callback ends, then closed for good.</summary>
        T RunCallback<T>(Func<SessionReader, T> callback)
        {
            var reader = new SessionReader(session, world, generator);
            inCallback = true;
            try
            {
                return callback(reader);
            }
            finally
            {
                reader.Close();
                inCallback = false;
            }
        }

        Handled Apply(SessionRequest r, CancellationToken token)
        {
            var player = session.FindPlayer(r.PlayerId);
            var refusal = Check(r, player, out var dest, out var word);
            if (refusal != null) return new Handled { Refusal = refusal };
            var from = player.Position;
            GenerationUpdate u = null;
            var changed = false;
            try
            {
                switch (r.Kind)
                {
                    case RequestKind.Prepare:
                    {
                        var id = RoomConnection.Of(from, r.Direction);
                        var opened = world.Doors.Get(id) == DoorState.Closed;
                        changed = true;
                        if (opened) world.Doors.Set(id, DoorState.Open);     // recorded when the opening starts, before the fill
                        u = new GenerationUpdate
                        {
                            Kind = "prepare", From = from, To = dest, Moved = false, FirstVisit = !player.IsVisited(dest),
                            TeamFirstVisit = !world.IsTeamVisited(dest), OpenedDoor = opened ? id : (RoomConnection?)null,
                        };
                        u.Fill = generator.Fill(world, new GenerationContext(dest, GuidanceProgress(session, world, player), token));
                        break;
                    }
                    case RequestKind.Enter:
                        changed = true;
                        u = EnterRoom(player, dest, word, "enter");
                        break;
                    case RequestKind.Move:
                        changed = true;
                        u = EnterRoom(player, dest, word, "move");
                        u.Fill = generator.Fill(world, new GenerationContext(dest, GuidanceProgress(session, world, player), token));   // also for the target room (1.1.0)
                        break;
                }
            }
            finally
            {
                if (changed) session.Revision++;           // also after an error: whatever changed is a new state
            }
            if (u == null) throw new InvalidOperationException($"처리할 수 없는 요청 종류: {r.Kind}");   // Check refuses these first; nothing was changed
            Finish(u, player, r.RequestId, r.Kind == RequestKind.Enter ? (endClock != null ? endClock() : r.CrossedAt) : double.NaN);   // read at the confirmation
            return new Handled { Update = u };
        }

        static bool IsDefined(RequestKind k) => k == RequestKind.Prepare || k == RequestKind.Enter || k == RequestKind.Move;

        static bool IsDefined(Direction d) => d == Direction.Up || d == Direction.Down || d == Direction.Left || d == Direction.Right;

        /// <summary>Why the request cannot be applied now, or null (then `dest` exists and `word` is its word).</summary>
        string Check(SessionRequest r, PlayerState player, out GridPos dest, out int word)
        {
            dest = default;
            word = -1;
            if (!IsDefined(r.Kind)) return $"알 수 없는 요청 종류입니다({(int)r.Kind}).";
            if (!IsDefined(r.Direction)) return $"알 수 없는 방향입니다({(int)r.Direction}).";   // before any Directions.Offset (its default is 우)
            if (!session.Started) return "실험이 아직 시작되지 않았습니다.";
            if (session.Status != "running") return $"실험이 끝났습니다 ({StatusText.Korean(session.Status)}).";
            if (player == null) return $"이 세션에 등록되지 않은 플레이어입니다({r.PlayerId}).";
            var from = player.Position;
            if (r.ExpectedFrom != from) return $"요청의 출발 좌표 {r.ExpectedFrom}이 확정 위치 {from}와 다릅니다.";
            if (r.Kind == RequestKind.Move && !world.HasAnyNeighbor(from)) return "현재 방에 생성된 인접 방이 없습니다.";   // this player only; the session goes on
            dest = from.Add(Directions.Offset(r.Direction));
            var side = Directions.Korean(r.Direction);
            if (!world.TryGetWord(dest, out word))
            {
                var reason = world.EmptyReason(from, dest);
                return $"{side} 방향 {dest}은 미생성 좌표라 이동할 수 없습니다." +
                       (world.IsUnfilled(from) ? " (이 방의 주변 생성이 완성되지 못한 위치" + (reason != null ? $": {reason}" : "") + ")" : "");
            }
            if (r.Kind != RequestKind.Enter) return null;
            if (world.Doors.Get(RoomConnection.Between(from, dest)) != DoorState.Open) return $"{side} 방향 {dest}으로 가는 문이 열리지 않았습니다.";
            if (!world.IsPrepared(dest)) return $"{side} 방향 {dest}은 주변 준비 전이라 입장할 수 없습니다.";
            return null;
        }

        /// <summary>Simulation.step without the fill for one player: its position, moves, visits, path; the team visit set; target arrival and the test move limit.</summary>
        GenerationUpdate EnterRoom(PlayerState player, GridPos dest, int word, string kind)
        {
            var from = player.Position;
            var fresh = player.Enter(dest);
            var teamFresh = world.MarkTeamVisited(dest);
            if (word == session.Target) session.Status = "success";
            else if (session.Config.UseMoveLimit && player.Moves >= session.Config.MaxMoves) session.Status = "move_limit";   // test-only; normal play has no limit
            return new GenerationUpdate { Kind = kind, From = from, To = dest, Moved = true, FirstVisit = fresh, TeamFirstVisit = teamFresh };
        }

        /// <summary>After the revision: identity, the requester's counters, status and — for the goal entry — the one result.</summary>
        void Finish(GenerationUpdate u, PlayerState player, long requestId, double crossedAt)
        {
            u.SessionId = session.Id;
            u.PlayerId = player?.Id;
            u.RequestId = requestId;
            u.Revision = session.Revision;
            u.Moves = player?.Moves ?? 0;
            u.Status = session.Status;
            u.Arrived = u.Moved && session.Status == "success";
            u.GuidanceStrength = generator.Guidance(GuidanceProgress(session, world, player));
            if (!u.Arrived || session.Result != null) return;
            var players = session.Players.Select(p => new PlayerResult(p.Id, p.UniqueVisited, p.Moves)).ToArray();
            session.Result = new SessionResult(session.Revision, player.Id, crossedAt - session.PlayStartedAt, world.TeamVisitedCount, players);
            u.Result = session.Result;
        }

        /// <summary>
        /// Guidance progress of a request: coop = the team's distinct visited coordinates (start included; revisits,
        /// overlaps between players, door openings and generation add nothing); solo = the configured GuidanceBasis of
        /// the one player (distinct rooms, or moves), as before.
        /// </summary>
        internal static int GuidanceProgress(SessionState s, RoomMap w, PlayerState p)
        {
            if (s.Mode == SessionMode.Coop || p == null) return w.TeamVisitedCount;
            return s.Config.GuidanceBasis == "unique" ? p.UniqueVisited : p.Moves;
        }
    }
}
