namespace WordRoom.RoomDebug
{
    /// <summary>
    /// The fixed outcome of one experiment that reached its target by a real, confirmed entry (never by opening a
    /// door or preparing). Made once by RoomGenerationDebug and handed out read-only (RoomGenerationDebug.GoalReached);
    /// nothing in it changes afterwards.
    /// </summary>
    public sealed class GoalResult
    {
        /// <summary>
        /// Real seconds from the first moment the placed player could be controlled to the moment the player crossed
        /// into the target room (monotonic real time; menus and generation waits in between count, loading and the
        /// first placement do not; the display of the entry is not waited for).
        /// </summary>
        public double Seconds { get; }

        /// <summary>Distinct rooms entered, start and target room included (RoomMap.UniqueVisited, copied on the worker with that entry).</summary>
        public int VisitedRooms { get; }

        public GoalResult(double seconds, int visitedRooms)
        {
            Seconds = seconds < 0d ? 0d : seconds;
            VisitedRooms = visitedRooms;
        }
    }
}
