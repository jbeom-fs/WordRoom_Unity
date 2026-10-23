using System.Collections.Generic;

namespace WordRoom.World
{
    public enum DoorState
    {
        Closed,
        Open,
    }

    /// <summary>
    /// Door state per connection for the current session, kept apart from the door GameObjects so a pooled door
    /// can be returned and later shown again with the same state. Sparse: connections never changed are Closed,
    /// and a stored state does not mean the rooms on either side exist. No persistent save; cleared per session.
    /// A door opened by the player is stored Open when the opening starts (the slide itself is view-only state of
    /// the door instance); open doors stay open, there is no closing.
    /// </summary>
    public sealed class DoorStateStore
    {
        readonly Dictionary<RoomConnection, DoorState> states = new Dictionary<RoomConnection, DoorState>();

        public int Count => states.Count;

        public DoorState Get(RoomConnection id) => states.TryGetValue(id, out var s) ? s : DoorState.Closed;

        public void Set(RoomConnection id, DoorState state)
        {
            if (state == DoorState.Closed) states.Remove(id);
            else states[id] = state;
        }

        public void Clear() => states.Clear();
    }
}
