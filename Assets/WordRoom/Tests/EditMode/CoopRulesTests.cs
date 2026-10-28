using System.Linq;
using NUnit.Framework;
using WordRoom.Network;

namespace WordRoom.Data.Tests
{
    /// <summary>Lobby list changes, join availability, start condition and text rules of the coop lobby (no Photon connection).</summary>
    public sealed class CoopRulesTests
    {
        static LobbyRoom Room(string name, int count = 1, int max = 4, bool open = true, bool visible = true, bool removed = false) =>
            new LobbyRoom(name, $"{name} 방", count, max, open, visible, removed);

        [Test]
        public void EmptyFirstUpdateIsAReceivedEmptyList()
        {
            var cache = new RoomListCache();
            Assert.IsFalse(cache.Received);
            Assert.IsTrue(cache.Apply(new LobbyRoom[0]));
            Assert.IsTrue(cache.Received);
            Assert.AreEqual(0, cache.Count);
            Assert.IsFalse(cache.Apply(new LobbyRoom[0]));                     // nothing new after that
        }

        [Test]
        public void UpdatesAreChangeSetsKeyedByRoomName()
        {
            var cache = new RoomListCache();
            cache.Apply(new[] { Room("A"), Room("B"), Room("C") });
            var version = cache.Version;
            Assert.IsTrue(cache.Apply(new[] { Room("B", count: 3) }));          // only B changed; A and C stay
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, cache.Rooms.Select(r => r.Name).ToArray());
            Assert.IsTrue(cache.TryGet("B", out var b) && b.PlayerCount == 3);
            Assert.Greater(cache.Version, version);

            version = cache.Version;
            Assert.IsFalse(cache.Apply(new[] { Room("A") }));                  // same values: no change
            Assert.AreEqual(version, cache.Version);

            cache.Apply(new[] { Room("B", removed: true), Room("D") });
            CollectionAssert.AreEqual(new[] { "A", "C", "D" }, cache.Rooms.Select(r => r.Name).ToArray());
            Assert.IsTrue(cache.TryGet("D", out _));
            Assert.IsFalse(cache.TryGet("B", out _));

            cache.Apply(new[] { Room("C", visible: false), Room("Z", removed: true) });   // invisible = gone; unknown removal ignored
            CollectionAssert.AreEqual(new[] { "A", "D" }, cache.Rooms.Select(r => r.Name).ToArray());
            Assert.IsTrue(cache.TryGet("D", out var d) && d.Name == "D");       // index still right after removals

            cache.Clear();
            Assert.IsFalse(cache.Received);
            Assert.AreEqual(0, cache.Count);
        }

        [Test]
        public void AvailabilityFollowsCountOpenAndRemoval()
        {
            Assert.AreEqual(RoomAvailability.Open, Room("A", 3, 4).Availability);
            Assert.AreEqual(RoomAvailability.Full, Room("A", 4, 4).Availability);
            Assert.AreEqual(RoomAvailability.Closed, Room("A", 1, 4, open: false).Availability);
            Assert.AreEqual(RoomAvailability.Gone, Room("A", removed: true).Availability);
            Assert.AreEqual(RoomAvailability.Gone, Room("A", visible: false).Availability);
        }

        [Test]
        public void StartNeedsTheHostTwoPlayersAndEveryoneReady()
        {
            Assert.IsTrue(CoopRules.CanStart(true, 2, 2));
            Assert.IsTrue(CoopRules.CanStart(true, 4, 4));
            Assert.IsFalse(CoopRules.CanStart(false, 2, 2));                  // not the host
            Assert.IsFalse(CoopRules.CanStart(true, 1, 1));                   // alone
            Assert.IsFalse(CoopRules.CanStart(true, 3, 2));                   // someone not ready
        }

        [Test]
        public void CodesKeepTheirCharactersAndNamesAreCleaned()
        {
            Assert.AreEqual("AbC123", CoopRules.CleanCode("  AbC123 \t"));
            Assert.AreEqual("", CoopRules.CleanCode(null));
            Assert.AreEqual("A B", CoopRules.CleanDisplayName(" A\nB ", "X"));
            Assert.AreEqual("X", CoopRules.CleanDisplayName("   ", "X"));
            Assert.AreEqual(CoopRules.MaxDisplayNameLength, CoopRules.CleanDisplayName(new string('가', 40), "X").Length);
            Assert.AreEqual(2, CoopRules.ClampMaxPlayers(1));
            Assert.AreEqual(4, CoopRules.ClampMaxPlayers(9));
            var code = CoopRules.NewRoomCode();
            Assert.AreEqual(CoopRules.RoomCodeLength, code.Length);
            Assert.AreEqual(code, CoopRules.CleanCode(code));
            Assert.IsFalse(code.Any(c => "0O1IL".Contains(c)));
        }
    }
}
