namespace WordRoom.Generation
{
    /// <summary>
    /// What a display copy reads of one session state: the session's identity and status, the target, each player's
    /// confirmed room, the shared rooms, their preparation and the door states. Implemented by SessionReader (live
    /// state inside a coordinator callback) and SessionCopy (an independent copy of one revision — also one restored
    /// from a host's message on a device without a coordinator). Read only; nothing here can change a session.
    /// </summary>
    public interface ISessionView
    {
        string SessionId { get; }
        long Revision { get; }
        string Status { get; }
        int TargetWord { get; }

        /// <summary>Confirmed room of a registered player (throws for an unknown id).</summary>
        GridPos PlayerPosition(string playerId);

        bool TryGetWord(GridPos p, out int word);
        bool IsPrepared(GridPos p);
        DoorState Door(RoomConnection id);
    }
}
