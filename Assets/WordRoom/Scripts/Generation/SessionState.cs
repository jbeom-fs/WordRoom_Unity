using System;
using System.Collections.Generic;

namespace WordRoom.Generation
{
    /// <summary>
    /// Solo: one registered player; the guidance progress follows the configured GuidanceBasis of that player (unique
    /// rooms or moves, as before). Coop: any number of players; the guidance progress is the team's distinct visited
    /// coordinates, start included (agreed 2026-10-27).
    /// </summary>
    public enum SessionMode
    {
        Solo,
        Coop,
    }

    /// <summary>One player's part of a confirmed session result.</summary>
    public readonly struct PlayerResult
    {
        public readonly string PlayerId;
        /// <summary>Distinct rooms this player entered, start included.</summary>
        public readonly int VisitedRooms;
        public readonly int Moves;

        public PlayerResult(string playerId, int visitedRooms, int moves)
        {
            PlayerId = playerId;
            VisitedRooms = visitedRooms;
            Moves = moves;
        }
    }

    /// <summary>
    /// The fixed outcome of a session that reached its target by a confirmed entry (never by opening a door or
    /// preparing), made once by SessionCoordinator inside that entry's request. Immutable; team and personal
    /// counts are kept apart.
    /// </summary>
    public sealed class SessionResult
    {
        /// <summary>Confirmed change number of the goal entry.</summary>
        public long Revision { get; }
        public string ReachedBy { get; }

        /// <summary>
        /// Real seconds from the play clock start to the boundary crossing reported with the goal entry; NaN when the
        /// clock had not started (no time can be given then).
        /// </summary>
        public double Seconds { get; }

        /// <summary>Distinct coordinates entered by the whole team, start and target room included.</summary>
        public int TeamVisitedRooms { get; }

        /// <summary>Every registered player, in registration order.</summary>
        public IReadOnlyList<PlayerResult> Players { get; }

        internal SessionResult(long revision, string reachedBy, double seconds, int teamVisitedRooms, PlayerResult[] players)
        {
            Revision = revision;
            ReachedBy = reachedBy;
            Seconds = double.IsNaN(seconds) ? double.NaN : Math.Max(0d, seconds);
            TeamVisitedRooms = teamVisitedRooms;
            Players = Array.AsReadOnly(players);
        }

        public bool TryGetPlayer(string playerId, out PlayerResult result)
        {
            foreach (var p in Players)
                if (p.PlayerId == playerId)
                {
                    result = p;
                    return true;
                }
            result = default;
            return false;
        }
    }

    /// <summary>
    /// Session-wide state owned by SessionCoordinator: identity, start and target, seed and the frozen generation
    /// settings, data package, progress and end status, the confirmed change number, the time base and the result.
    /// User preferences (radius, sensitivity, volume, UI size) are not session state.
    /// </summary>
    internal sealed class SessionState
    {
        readonly object timeGate = new object();   // small own lock: the main thread never waits for a generation request here
        double playStartedAt = double.NaN;

        public string Id { get; }
        public SessionMode Mode { get; }
        public int Start { get; }
        public int Target { get; }
        /// <summary>The generator's own copy, fixed when the session was created (seed included).</summary>
        public GenerationConfig Config { get; }
        public string PackageId { get; }
        public bool Started { get; set; }
        /// <summary>"running", "success", "move_limit" (test only) or "no_exit" (start room without any neighbor).</summary>
        public string Status { get; set; } = "running";
        public string FailureReason { get; set; }
        /// <summary>Confirmed change number: 0 when created, 1 after the start, one more per accepted request.</summary>
        public long Revision { get; set; }
        public SessionResult Result { get; set; }
        public readonly List<PlayerState> Players = new List<PlayerState>();

        public SessionState(string id, SessionMode mode, int start, int target, GenerationConfig config, string packageId)
        {
            Id = id;
            Mode = mode;
            Start = start;
            Target = target;
            Config = config;
            PackageId = packageId;
        }

        public PlayerState FindPlayer(string id)
        {
            foreach (var p in Players)
                if (p.Id == id) return p;
            return null;
        }

        /// <summary>Real time (the caller's clock) play first became possible; NaN until then.</summary>
        public double PlayStartedAt
        {
            get
            {
                lock (timeGate) return playStartedAt;
            }
        }

        /// <summary>Records the play start once; false when it was already recorded (the first one stays).</summary>
        public bool TryStartClock(double time)
        {
            lock (timeGate)
            {
                if (!double.IsNaN(playStartedAt)) return false;
                playStartedAt = time;
                return true;
            }
        }
    }
}
