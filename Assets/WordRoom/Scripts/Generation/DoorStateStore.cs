using System.Collections.Generic;

namespace WordRoom.Generation
{
    public enum DoorState
    {
        Closed,
        Open,
    }

    /// <summary>
    /// Door state per connection of one session, part of the shared world (RoomMap) and changed only by
    /// SessionCoordinator requests. Kept apart from any door GameObject, so a view can return a pooled door and later
    /// show it again with the same state. Sparse: connections never changed are Closed, and a stored state does not
    /// mean the rooms on either side exist. A door is stored Open when its opening starts (the Prepare request that
    /// opens it records Open before its fill; the slide itself is view-only state of each device's door instance);
    /// open doors stay open, there is no closing. No persistent save.
    /// </summary>
    internal sealed class DoorStateStore
    {
        readonly Dictionary<RoomConnection, DoorState> states = new Dictionary<RoomConnection, DoorState>();

        public int Count => states.Count;

        public DoorState Get(RoomConnection id) => states.TryGetValue(id, out var s) ? s : DoorState.Closed;

        public void Set(RoomConnection id, DoorState state)
        {
            if (state == DoorState.Closed) states.Remove(id);
            else states[id] = state;
        }

        /// <summary>Connections stored Open, in connection order (a copy).</summary>
        public List<RoomConnection> OpenConnections()
        {
            var list = new List<RoomConnection>();
            foreach (var kv in states)
                if (kv.Value == DoorState.Open) list.Add(kv.Key);
            list.Sort();
            return list;
        }
    }
}
