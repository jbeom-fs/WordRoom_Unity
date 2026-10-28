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
    public sealed class SessionReader
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
    }

    /// <summary>
    /// The whole session at one revision (world, players and session state of the same confirmed change number), for
    /// recovering a display that missed a change. Independent of the live state: nothing in it reaches the session.
    /// The dataset and the generator's derived caches are not part of it.
    /// </summary>
    public sealed class SessionCopy
    {
        public string SessionId { get; }
        public SessionMode Mode { get; }
        public long Revision { get; }
        public int StartWord { get; }
        public int TargetWord { get; }
        public long Seed { get; }
        public string PackageId { get; }
        public bool Started { get; }
        public string Status { get; }
        public string FailureReason { get; }
        public double PlayStartedAt { get; }
        public SessionResult Result { get; }

        /// <summary>Every committed room in creation order.</summary>
        public IReadOnlyList<RoomRecord> Rooms { get; }
        public IReadOnlyList<GridPos> Prepared { get; }
        public IReadOnlyList<UnfilledCopy> Unfilled { get; }
        /// <summary>Recent generated words, oldest first (the world's single history).</summary>
        public IReadOnlyList<int> Recent { get; }
        public IReadOnlyList<GridPos> TeamVisited { get; }
        public IReadOnlyList<RoomConnection> OpenDoors { get; }
        /// <summary>Registered players in registration order.</summary>
        public IReadOnlyList<PlayerCopy> Players { get; }

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
        }

        public PlayerCopy FindPlayer(string playerId) => Players.FirstOrDefault(p => p.Id == playerId);
    }
}
