using System;
using System.Collections.Generic;
using System.Text;

namespace WordRoom.Network
{
    /// <summary>
    /// Fixed rules of the coop lobby and waiting room, without any Photon type (testable on its own): room size,
    /// display name, RoomCode, the property keys published to the lobby or set per participant, and the start
    /// condition. The RoomCode is the room's real Photon Room.Name — the value shown, copied, typed and joined is the
    /// same string; nothing maps codes to rooms.
    /// </summary>
    public static class CoopRules
    {
        public const int MinPlayers = 2;
        public const int MaxPlayers = 4;
        public const int DefaultMaxPlayers = 4;
        public const int MaxDisplayNameLength = 24;
        public const int RoomCodeLength = 8;

        /// <summary>Room property published to the lobby (CustomRoomPropertiesForLobby): the display name. Nothing else is published.</summary>
        public const string DisplayNameKey = "wn";

        /// <summary>Participant property: ready in the waiting room (bool). Absent = not ready.</summary>
        public const string ReadyKey = "wr";

        /// <summary>Letters and digits without the easily confused 0/O and 1/I/L.</summary>
        const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        static readonly Random random = new Random(Guid.NewGuid().GetHashCode());

        /// <summary>A new RoomCode (used as Room.Name when creating). A clash with an existing room is refused by the server (no retry here).</summary>
        public static string NewRoomCode()
        {
            var sb = new StringBuilder(RoomCodeLength);
            lock (random)
                for (var i = 0; i < RoomCodeLength; i++) sb.Append(CodeAlphabet[random.Next(CodeAlphabet.Length)]);
            return sb.ToString();
        }

        /// <summary>A typed or pasted code: surrounding white space removed, nothing else changed (case and characters stay).</summary>
        public static string CleanCode(string input) => input == null ? "" : input.Trim();

        /// <summary>Display name for the lobby: trimmed, line breaks and control characters as spaces, at most MaxDisplayNameLength; empty → `fallback`.</summary>
        public static string CleanDisplayName(string input, string fallback)
        {
            var sb = new StringBuilder();
            foreach (var c in input ?? "")
                sb.Append(char.IsControl(c) ? ' ' : c);
            var s = sb.ToString().Trim();
            if (s.Length > MaxDisplayNameLength) s = s.Substring(0, MaxDisplayNameLength).TrimEnd();
            return s.Length > 0 ? s : fallback;
        }

        public static int ClampMaxPlayers(int n) => Math.Max(MinPlayers, Math.Min(MaxPlayers, n));

        /// <summary>Shown when a participant has no NickName: distinct per participant of one room.</summary>
        public static string DefaultParticipantName(int actorNumber) => $"참가자 {actorNumber}";

        /// <summary>
        /// The waiting-room identity of a Photon participant (its ActorNumber in this room), the boundary to the
        /// session's PlayerId. Valid for this room only: it is not a persistent id for reconnecting.
        /// </summary>
        public static string PlayerIdOf(int actorNumber) => $"actor-{actorNumber}";

        /// <summary>Start condition of the waiting room: the host, at least MinPlayers participants, everyone ready.</summary>
        public static bool CanStart(bool isHost, int participants, int ready) => isHost && participants >= MinPlayers && ready == participants;
    }

    public enum RoomAvailability
    {
        Open,
        Full,
        Closed,
        Gone,
    }

    /// <summary>One room as the lobby lists it (copied from Photon's RoomInfo; the key is the real Room.Name).</summary>
    public readonly struct LobbyRoom : IEquatable<LobbyRoom>
    {
        public readonly string Name;
        public readonly string DisplayName;
        public readonly int PlayerCount;
        public readonly int MaxPlayers;
        public readonly bool IsOpen;
        public readonly bool IsVisible;
        public readonly bool Removed;

        public LobbyRoom(string name, string displayName, int playerCount, int maxPlayers, bool isOpen, bool isVisible, bool removed)
        {
            Name = name;
            DisplayName = displayName;
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
            IsOpen = isOpen;
            IsVisible = isVisible;
            Removed = removed;
        }

        /// <summary>From the list alone; the server decides when joining (it may have changed since).</summary>
        public RoomAvailability Availability =>
            Removed || !IsVisible ? RoomAvailability.Gone :
            !IsOpen ? RoomAvailability.Closed :
            MaxPlayers > 0 && PlayerCount >= MaxPlayers ? RoomAvailability.Full : RoomAvailability.Open;

        public bool Equals(LobbyRoom o) =>
            Name == o.Name && DisplayName == o.DisplayName && PlayerCount == o.PlayerCount && MaxPlayers == o.MaxPlayers &&
            IsOpen == o.IsOpen && IsVisible == o.IsVisible && Removed == o.Removed;

        public override bool Equals(object obj) => obj is LobbyRoom o && Equals(o);
        public override int GetHashCode() => Name != null ? Name.GetHashCode() : 0;
    }

    /// <summary>
    /// The lobby's room list kept from Photon's room list updates. Each update is a change set (not the whole list):
    /// rooms are added or replaced by Room.Name, rooms marked removed (or no longer visible) are dropped. Order is the
    /// order rooms first appeared, so rows do not jump when a room changes. Received tells "no list yet" apart from
    /// "an empty list". Cleared when the lobby is left, a room is entered or the connection ends.
    /// </summary>
    public sealed class RoomListCache
    {
        readonly List<LobbyRoom> rooms = new List<LobbyRoom>();
        readonly Dictionary<string, int> index = new Dictionary<string, int>();

        /// <summary>At least one update arrived since the last Clear.</summary>
        public bool Received { get; private set; }

        /// <summary>Grows with every change of the list (also Clear), so a view redraws only then.</summary>
        public int Version { get; private set; }

        public int Count => rooms.Count;
        public IReadOnlyList<LobbyRoom> Rooms => rooms;

        public bool TryGet(string name, out LobbyRoom room)
        {
            if (name != null && index.TryGetValue(name, out var i))
            {
                room = rooms[i];
                return true;
            }
            room = default;
            return false;
        }

        public void Clear()
        {
            rooms.Clear();
            index.Clear();
            Received = false;
            Version++;
        }

        /// <summary>Applies one change set; returns whether anything changed (the first update always counts: the list is now known).</summary>
        public bool Apply(IEnumerable<LobbyRoom> changes)
        {
            var changed = !Received;
            Received = true;
            foreach (var r in changes)
            {
                if (string.IsNullOrEmpty(r.Name)) continue;
                var known = index.TryGetValue(r.Name, out var i);
                if (r.Removed || !r.IsVisible)
                {
                    if (!known) continue;
                    rooms.RemoveAt(i);
                    index.Remove(r.Name);
                    for (var k = i; k < rooms.Count; k++) index[rooms[k].Name] = k;
                    changed = true;
                }
                else if (known)
                {
                    if (rooms[i].Equals(r)) continue;
                    rooms[i] = r;
                    changed = true;
                }
                else
                {
                    index[r.Name] = rooms.Count;
                    rooms.Add(r);
                    changed = true;
                }
            }
            if (changed) Version++;
            return changed;
        }
    }
}
