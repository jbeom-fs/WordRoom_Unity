using WordRoom.Generation;

namespace WordRoom.RoomDebug
{
    /// <summary>
    /// What the single-player result screen shows for a session that reached its target by a real, confirmed entry
    /// (never by opening a door or preparing). Read from the session's one result (SessionResult, confirmed by
    /// SessionCoordinator in the goal entry); RoomGenerationDebug hands it out read-only (GoalReached) once the entry
    /// is shown. Nothing in it changes afterwards and it keeps no time or count of its own.
    /// </summary>
    public sealed class GoalResult
    {
        /// <summary>The session's confirmed result: team and per-player counts.</summary>
        public SessionResult Session { get; }

        /// <summary>The player of this device.</summary>
        public string PlayerId { get; }

        /// <summary>
        /// Real seconds from the first moment the placed player could be controlled to the moment the player crossed
        /// into the target room (monotonic real time; menus, the map and generation waits in between count, loading
        /// and the first placement do not; the display of the entry is not waited for).
        /// </summary>
        public double Seconds => Session.Seconds;

        /// <summary>Distinct rooms this player entered, start and target room included (solo: the same as the team's).</summary>
        public int VisitedRooms { get; }

        public GoalResult(SessionResult session, string playerId)
        {
            Session = session;
            PlayerId = playerId;
            VisitedRooms = session.TryGetPlayer(playerId, out var p) ? p.VisitedRooms : session.TeamVisitedRooms;
        }
    }
}
