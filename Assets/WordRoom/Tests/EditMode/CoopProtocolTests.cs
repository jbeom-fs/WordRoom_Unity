using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using WordRoom.Generation;
using WordRoom.Network;
using WordRoom.World;

namespace WordRoom.Data.Tests
{
    /// <summary>Coop start messages, the host's report counting and the restored initial copy (no Photon connection).</summary>
    public sealed class CoopProtocolTests
    {
        static readonly int[] Actors = { 1, 2 };

        /// <summary>A coop session of the fixture started once, with its initial copy taken inside the start.</summary>
        static (SessionCoordinator c, SessionCopy copy) StartedCoop()
        {
            var d = RoomGeneratorFixtureTests.Data;
            var cfg = GenerationConfig.FromSettings(d.Settings);
            cfg.VocabularyLimit = d.WordCount;                                                  // within the fixture (the settings must fit the receiver's vocabulary)
            Assert.IsTrue(d.TryGetWordRank("경기", out var start));
            Assert.IsTrue(d.TryGetWordRank("바다", out var target));
            var c = SessionCoordinator.Create(d, cfg, start, target, SessionMode.Coop, Actors.Select(CoopRules.PlayerIdOf));
            var r = c.Start((reader, u) => reader.CopyAll());
            Assert.IsTrue(r.Accepted, r.Refusal);
            if (string.IsNullOrEmpty(r.Captured.PackageId)) Assert.Ignore("fixture 패키지 ID가 없어 초기 상태 메시지를 시험할 수 없음");
            return (c, r.Captured);
        }

        /// <summary>The initial world message as the host sends it: the coordinator's own fixed settings.</summary>
        static object[] World(SessionCoordinator c, SessionCopy copy) =>
            CoopProtocol.EncodeWorld("attempt1", 1, Actors, 1, copy.PackageId, c.Config, copy);

        /// <summary>Settings differing from the code defaults in every sent field.</summary>
        static GenerationConfig Unusual() => new GenerationConfig
        {
            Seed = 9876543210L, VocabularyLimit = 7, MinWordLength = 2, MaxWordLength = 5,
            AvoidAdjacentRepeat = false, RepeatScope = "global", RepeatRadius = 5,
            SelectionTop = 9, Temperature = 0.33, RecentWindow = 17, Diversity = 0.25, NeighborWeight = 0.75,
            RelationThreshold = 0.7, RelationStep = 0.05, RelationFloor = 0.2, PlacementBudget = 1234,
            FastTextSupplement = false, FastTextPreferredMin = 0.45,
            TargetGuidance = false, GuidanceStrength = 0.01, GuidanceStart = 3, GuidanceRamp = 0, GuidanceMax = 0.5, GuidanceBasis = "moves",
            UseMoveLimit = true, MaxMoves = 77, Source = "테스트",
        };

        [Test]
        public void BeginRoundTripsAndRejectsBadValues()
        {
            var ok = CoopProtocol.Encode(new CoopProtocol.Begin { Attempt = "a1", Host = 1, Actors = new[] { 1, 3 }, Difficulty = 2, SettingsRevision = 4 });
            Assert.IsTrue(CoopProtocol.TryDecode(ok, out CoopProtocol.Begin b, out var error), error);
            Assert.AreEqual("a1", b.Attempt);
            CollectionAssert.AreEqual(new[] { 1, 3 }, b.Actors);
            Assert.AreEqual(4, b.SettingsRevision);

            object[] With(int index, object value)
            {
                var copy = (object[])ok.Clone();
                copy[index] = value;
                return copy;
            }

            Assert.IsFalse(CoopProtocol.TryDecode(With(0, 99), out CoopProtocol.Begin _, out _));               // version
            Assert.IsFalse(CoopProtocol.TryDecode(With(2, 7), out CoopProtocol.Begin _, out _));                // host not a participant
            Assert.IsFalse(CoopProtocol.TryDecode(With(3, new[] { 3, 1 }), out CoopProtocol.Begin _, out _));   // order
            Assert.IsFalse(CoopProtocol.TryDecode(With(3, new[] { 1 }), out CoopProtocol.Begin _, out _));      // alone
            Assert.IsFalse(CoopProtocol.TryDecode(With(3, new[] { 1, 2, 3, 4, 5 }), out CoopProtocol.Begin _, out _));
            Assert.IsFalse(CoopProtocol.TryDecode(With(4, 3), out CoopProtocol.Begin _, out _));                // difficulty
            Assert.IsFalse(CoopProtocol.TryDecode(With(1, ""), out CoopProtocol.Begin _, out _));               // empty id
            Assert.IsFalse(CoopProtocol.TryDecode(new object[] { 1, "a" }, out CoopProtocol.Begin _, out _));   // length
            Assert.IsFalse(CoopProtocol.TryDecode("text", out CoopProtocol.Begin _, out _));
        }

        [Test]
        public void DataReportNeedsAPackageWhenReady()
        {
            var ready = CoopProtocol.Encode(new CoopProtocol.DataReport { Attempt = "a1", Ok = true, PackageId = "p" });
            Assert.IsTrue(CoopProtocol.TryDecode(ready, out CoopProtocol.DataReport m, out _) && m.Ok && m.PackageId == "p");
            var noPackage = CoopProtocol.Encode(new CoopProtocol.DataReport { Attempt = "a1", Ok = true, PackageId = "" });
            Assert.IsFalse(CoopProtocol.TryDecode(noPackage, out CoopProtocol.DataReport _, out _));
            var failed = CoopProtocol.Encode(new CoopProtocol.DataReport { Attempt = "a1", Ok = false, Error = new string('x', 500) });
            Assert.IsTrue(CoopProtocol.TryDecode(failed, out CoopProtocol.DataReport f, out _) && !f.Ok && f.Error.Length <= CoopProtocol.MaxTextLength);
        }

        [Test]
        public void InitialWorldRoundTripsIntoTheSameState()
        {
            var (c, copy) = StartedCoop();
            Assert.IsTrue(CoopProtocol.TryDecodeWorld(World(c, copy), RoomGeneratorFixtureTests.Data.WordCount, out var w, out var error), error);
            var r = w.Copy;
            Assert.AreEqual(copy.SessionId, r.SessionId);
            Assert.AreEqual(copy.Revision, r.Revision);
            Assert.AreEqual(1, r.Revision);
            Assert.AreEqual(copy.Seed, r.Seed);
            CollectionAssert.AreEqual(copy.Rooms.Select(x => (x.Position, x.Word)).ToArray(), r.Rooms.Select(x => (x.Position, x.Word)).ToArray());
            CollectionAssert.AreEqual(copy.Prepared.ToArray(), r.Prepared.ToArray());
            CollectionAssert.AreEqual(copy.OpenDoors.ToArray(), r.OpenDoors.ToArray());
            Assert.AreEqual(1, r.TeamVisited.Count);                                            // one team start coordinate
            Assert.AreEqual(GridPos.Origin, r.TeamVisited[0]);
            CollectionAssert.AreEqual(Actors.Select(CoopRules.PlayerIdOf).ToArray(), r.Players.Select(p => p.Id).ToArray());
            foreach (var p in r.Players)
            {
                Assert.AreEqual(GridPos.Origin, p.Position);
                Assert.AreEqual(0, p.Moves);
                Assert.AreEqual(1, p.UniqueVisited);                                            // one personal start visit each
                Assert.AreEqual(1, p.Visits[0].Value);
            }
            Assert.AreEqual(0, r.Recent.Count);                                                 // the host keeps the generation history
            CollectionAssert.AreEqual(CoopProtocol.EncodeConfig(c.Config), CoopProtocol.EncodeConfig(w.Config));   // the coordinator's own settings
        }

        [Test]
        public void GuestViewMatchesTheHostsView()
        {
            var (c, copy) = StartedCoop();
            var words = RoomGeneratorFixtureTests.Data;
            Assert.IsTrue(CoopProtocol.TryDecodeWorld(World(c, copy), words.WordCount, out var w, out var error), error);
            var id = CoopRules.PlayerIdOf(2);
            var host = c.Read(r => RoomViewSnapshot.Capture(r, id, words.GetWord, 3));
            var guest = RoomViewSnapshot.Capture(w.Copy, id, words.GetWord, 3);
            Assert.AreEqual(host.Center, guest.Center);
            Assert.AreEqual(host.SessionId, guest.SessionId);
            Assert.AreEqual(host.Revision, guest.Revision);
            Assert.AreEqual(host.CurrentWord, guest.CurrentWord);
            Assert.AreEqual(host.TargetWord, guest.TargetWord);
            CollectionAssert.AreEqual(host.Rooms.ToArray(), guest.Rooms.ToArray());
            foreach (var kv in host.Rooms)
            {
                CollectionAssert.AreEqual(host.Signs[kv.Key], guest.Signs[kv.Key]);
                Assert.AreEqual(host.IsPrepared(kv.Key), guest.IsPrepared(kv.Key));
            }
        }

        [Test]
        public void InitialWorldWithBadValuesIsRefused()
        {
            var (c, copy) = StartedCoop();
            var count = RoomGeneratorFixtureTests.Data.WordCount;
            var good = World(c, copy);
            const int I = 0;

            object[] With(int index, object value)
            {
                var a = (object[])good.Clone();
                a[index] = value;
                return a;
            }

            Assert.AreEqual(CoopProtocol.WorldIndex.Length, good.Length);
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(good, 1, out _, out _));                                      // words beyond the receiver's vocabulary
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(I, 1), count, out _, out _));                             // the former protocol version
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.PlayerIds, new[] { "actor-1", "actor-9" }), count, out _, out _));
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.RoomX, new int[0]), count, out _, out _));   // arrays not matching
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.RoomWord,
                ((int[])good[CoopProtocol.WorldIndex.RoomWord]).Select(_ => count + 5).ToArray()), count, out _, out _));
            var rx = (int[])good[CoopProtocol.WorldIndex.RoomX];
            if (rx.Length > 1)
            {
                var duplicate = (int[])rx.Clone();
                duplicate[1] = duplicate[0];
                var ry = (int[])((int[])good[CoopProtocol.WorldIndex.RoomY]).Clone();
                ry[1] = ry[0];
                var a = With(CoopProtocol.WorldIndex.RoomX, duplicate);
                a[CoopProtocol.WorldIndex.RoomY] = ry;
                Assert.IsFalse(CoopProtocol.TryDecodeWorld(a, count, out _, out _));                                    // one coordinate twice
            }
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.Revision, 0L), count, out _, out _));
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.PlayerX, new[] { 50, 0 }), count, out _, out _));   // a player outside the rooms
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.Revision, 1), count, out _, out _));                // int, not long
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.Config, null), count, out _, out _));               // settings missing
            var badConfig = CoopProtocol.EncodeConfig(c.Config);
            badConfig[8] = 2.0;                                                                                      // temperature out of 0~1
            Assert.IsFalse(CoopProtocol.TryDecodeWorld(With(CoopProtocol.WorldIndex.Config, badConfig), count, out _, out _));
        }

        [Test]
        public void EverySentSettingRoundTripsIntoANewInstance()
        {
            var original = Unusual();
            var sent = CoopProtocol.EncodeConfig(original);
            Assert.AreEqual(CoopProtocol.ConfigLength, sent.Length);
            Assert.IsTrue(CoopProtocol.TryDecodeConfig(sent, 100, out var back, out var error), error);
            Assert.AreNotSame(original, back);
            CollectionAssert.AreEqual(sent, CoopProtocol.EncodeConfig(back));
            Assert.AreEqual(original.Seed, back.Seed);
            Assert.AreEqual(original.RepeatScope, back.RepeatScope);
            Assert.AreEqual(original.Temperature, back.Temperature);
            Assert.AreEqual(original.RelationStep, back.RelationStep);
            Assert.AreEqual(original.PlacementBudget, back.PlacementBudget);
            Assert.AreEqual(original.FastTextPreferredMin, back.FastTextPreferredMin);
            Assert.AreEqual(original.GuidanceBasis, back.GuidanceBasis);
            Assert.AreEqual(original.UseMoveLimit, back.UseMoveLimit);
            Assert.AreEqual(original.MaxMoves, back.MaxMoves);
            Assert.AreNotEqual(original.Source, back.Source);                                   // Source is a log text, not sent
            back.Seed = 1;
            Assert.IsTrue(CoopProtocol.TryDecodeConfig(sent, 100, out var again, out _));
            Assert.AreEqual(original.Seed, again.Seed);                                         // a decoded copy never changes the message
        }

        [Test]
        public void BadSettingsAreRefused()
        {
            object[] With(int index, object value)
            {
                var a = CoopProtocol.EncodeConfig(Unusual());
                a[index] = value;
                return a;
            }

            Assert.IsFalse(CoopProtocol.TryDecodeConfig(new object[3], 100, out _, out _));      // length
            Assert.IsFalse(CoopProtocol.TryDecodeConfig("x", 100, out _, out _));
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(CoopProtocol.EncodeConfig(Unusual()), 5, out _, out _));   // vocabulary beyond the receiver's
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(0, 5), 100, out _, out _));        // seed as int, not long
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(8, double.NaN), 100, out _, out _));
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(10, double.PositiveInfinity), 100, out _, out _));
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(8, 1), 100, out _, out _));        // int where a double is expected
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(5, "everywhere"), 100, out _, out _));   // repeat scope (Validate)
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(23, "steps"), 100, out _, out _));       // guidance basis (Validate)
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(14, 0.9), 100, out _, out _));     // floor above the start threshold (Validate)
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(15, 0), 100, out _, out _));       // placement budget
            Assert.IsFalse(CoopProtocol.TryDecodeConfig(With(3, 1), 100, out _, out _));        // max length below min length
        }

        [Test]
        public void RestoreRefusesAnInconsistentState()
        {
            SessionCopyParts Parts()
            {
                var p = new SessionCopyParts { SessionId = "s", Revision = 1, StartWord = 3, TargetWord = 4, Status = "running" };
                p.Rooms.Add(new KeyValuePair<GridPos, int>(GridPos.Origin, 3));
                p.Rooms.Add(new KeyValuePair<GridPos, int>(new GridPos(1, 0), 5));
                p.TeamVisited.Add(GridPos.Origin);
                var player = new SessionCopyParts.Player { Id = "actor-1", Position = GridPos.Origin };
                player.Visits.Add(new KeyValuePair<GridPos, int>(GridPos.Origin, 1));
                player.Path.Add(GridPos.Origin);
                p.Players.Add(player);
                return p;
            }

            Assert.IsNotNull(SessionCopy.Restore(Parts()));
            var startElsewhere = Parts();
            startElsewhere.Rooms[0] = new KeyValuePair<GridPos, int>(new GridPos(2, 2), 3);
            Assert.Throws<ArgumentException>(() => SessionCopy.Restore(startElsewhere));
            var twice = Parts();
            twice.Rooms.Add(new KeyValuePair<GridPos, int>(new GridPos(1, 0), 6));
            Assert.Throws<ArgumentException>(() => SessionCopy.Restore(twice));
            var notVisitedByTeam = Parts();
            notVisitedByTeam.TeamVisited.Clear();
            Assert.Throws<ArgumentException>(() => SessionCopy.Restore(notVisitedByTeam));
            var doorToNothing = Parts();
            doorToNothing.OpenDoors.Add(RoomConnection.Between(GridPos.Origin, new GridPos(0, 1)));
            Assert.Throws<ArgumentException>(() => SessionCopy.Restore(doorToNothing));
            var samePlayers = Parts();
            samePlayers.Players.Add(samePlayers.Players[0]);
            Assert.Throws<ArgumentException>(() => SessionCopy.Restore(samePlayers));
        }

        [Test]
        public void ReportsCountOncePerRealParticipant()
        {
            var a = new CoopStartAttempt("a1", 1, new[] { 2, 1 }, 1, 3);
            CollectionAssert.AreEqual(new[] { 1, 2 }, a.Actors.ToArray());
            Assert.AreEqual(ReportResult.Accepted, a.RecordData(1, true, "p", null));
            Assert.AreEqual(ReportResult.Duplicate, a.RecordData(1, true, "p", null));
            Assert.AreEqual(ReportResult.NotMember, a.RecordData(5, true, "p", null));          // a late joiner is never added
            Assert.IsFalse(a.TryAgreedPackage(out _));                                          // participant 2 still loading
            Assert.AreEqual(ReportResult.Accepted, a.RecordData(2, true, "p", null));
            Assert.IsTrue(a.TryAgreedPackage(out var package) && package == "p");

            a.SetWorld("session", 1);
            Assert.AreEqual(ReportResult.Invalid, a.RecordPlaced(1, "actor-1", "other", 1, true, null));   // another session
            Assert.AreEqual(ReportResult.Invalid, a.RecordPlaced(1, "actor-1", "session", 2, true, null)); // another revision
            Assert.AreEqual(ReportResult.Invalid, a.RecordPlaced(1, "actor-2", "session", 1, true, null)); // not its own PlayerId
            Assert.AreEqual(ReportResult.Accepted, a.RecordPlaced(1, "actor-1", "session", 1, true, null));
            Assert.AreEqual(ReportResult.Duplicate, a.RecordPlaced(1, "actor-1", "session", 1, true, null));
            Assert.IsFalse(a.AllPlaced);                                                        // one participant is still placing
            Assert.AreEqual(ReportResult.Accepted, a.RecordPlaced(2, "actor-2", "session", 1, true, null));
            Assert.IsTrue(a.AllPlaced);
        }

        [Test]
        public void MismatchOrFailureStopsTheStart()
        {
            var mismatch = new CoopStartAttempt("a1", 1, new[] { 1, 2 }, 1, 1);
            mismatch.RecordData(1, true, "p1", null);
            mismatch.RecordData(2, true, "p2", null);
            Assert.IsNotNull(mismatch.Failure);
            Assert.IsFalse(mismatch.TryAgreedPackage(out _));

            var failed = new CoopStartAttempt("a2", 1, new[] { 1, 2 }, 1, 1);
            failed.RecordData(2, false, null, "로딩 실패");
            Assert.IsNotNull(failed.Failure);                                                    // a failed load is never "ready"

            var placement = new CoopStartAttempt("a3", 1, new[] { 1, 2 }, 1, 1);
            placement.SetWorld("s", 1);
            Assert.AreEqual(ReportResult.Accepted, placement.RecordPlaced(2, "actor-2", "none", 1, false, "표시 실패"));
            Assert.IsNotNull(placement.Failure);
            Assert.IsFalse(placement.AllPlaced);
        }

        [Test]
        public void ReadyCountsOnlyForTheCurrentSettingsAndServerTimeWraps()
        {
            Assert.IsTrue(CoopRules.IsReadyFor(3, 3));
            Assert.IsFalse(CoopRules.IsReadyFor(2, 3));                                         // ready of older settings
            Assert.IsFalse(CoopRules.IsReadyFor(CoopRules.NotReady, CoopRules.NotReady));
            Assert.IsFalse(CoopRules.IsReadyFor(true, 1));                                      // the old bool value does not count
            Assert.IsFalse(CoopRules.IsReadyFor(null, 1));

            Assert.IsTrue(CoopRules.ServerTimeReached(1000, 1000));
            Assert.IsFalse(CoopRules.ServerTimeReached(999, 1000));
            var target = int.MaxValue - 5;
            var after = unchecked(target + 11);                                                 // wrapped past int.MaxValue
            Assert.IsTrue(CoopRules.ServerTimeReached(after, target));
            Assert.AreEqual(0.011, CoopRules.ServerSecondsSince(after, target), 1e-9);
            Assert.IsFalse(CoopRules.ServerTimeReached(target, after));
        }

        // ---- difficulty change: waiting, unknown, recovered --------------------------------------------------------
        /// <summary>The host's start condition with `ready` participants ready for the current revision (two in the room).</summary>
        static bool CanStart(SettingsChangeGate gate, int ready) =>
            CoopStartRules.HostCanStart(true, true, 2, ready, gate.State, CoopStage.Waiting);

        [Test]
        public void TimeoutKeepsTheStartBlockedUntilTheSettingsAreKnown()
        {
            var gate = new SettingsChangeGate(5);
            Assert.IsTrue(CanStart(gate, 2));                                                    // all ready for revision 3 (보통)
            Assert.IsTrue(gate.Begin(2, 4, "R", 1, 0));                                          // 어려움, revision 4 requested
            Assert.AreEqual(SettingsState.Waiting, gate.State);
            Assert.IsFalse(CanStart(gate, 2));                                                   // the old settings never start meanwhile
            Assert.AreEqual(SettingsChangeResult.BecameUnknown, gate.CheckTimeout(6));
            Assert.AreEqual(SettingsState.Unknown, gate.State);
            Assert.IsTrue(gate.InFlight);                                                        // a timeout does not cancel the request
            Assert.IsFalse(CanStart(gate, 2));                                                   // still no start with the old readies
            Assert.IsFalse(gate.Begin(0, 4, "R", 1, 7));                                         // no new change on top of an unknown one

            Assert.AreEqual(SettingsChangeResult.None, gate.Observe("R", 1, 1, 3));              // an update with the old values settles nothing
            Assert.AreEqual(SettingsState.Unknown, gate.State);
            Assert.AreEqual(SettingsChangeResult.Confirmed, gate.Observe("R", 1, 2, 4));         // the late success arrives
            Assert.IsTrue(gate.Settled);
            Assert.IsFalse(CanStart(gate, 0));                                                   // revision 4: the readies of 3 do not count
            Assert.IsTrue(CanStart(gate, 2));                                                    // everyone ready again for 4
        }

        [Test]
        public void RefusalLeavesTheSettingsUnknownAndAnImmediateFailureDoesNot()
        {
            var gate = new SettingsChangeGate(5);
            Assert.IsTrue(gate.Begin(0, 4, "R", 1, 0));
            gate.NotSent();                                                                      // nothing went out: the known settings stand
            Assert.IsTrue(gate.Settled);
            Assert.IsTrue(CanStart(gate, 2));

            Assert.IsTrue(gate.Begin(0, 4, "R", 1, 0));
            Assert.AreEqual(SettingsChangeResult.BecameUnknown, gate.Refused("R", 1));           // server refused (revision check)
            Assert.AreEqual(SettingsState.Unknown, gate.State);
            Assert.IsFalse(gate.InFlight);
            Assert.IsFalse(CanStart(gate, 2));                                                   // a refusal does not tell the current settings
            gate.NotSent();                                                                      // (no effect once it was sent)
            Assert.AreEqual(SettingsState.Unknown, gate.State);
            Assert.AreEqual(SettingsChangeResult.Superseded, gate.Observe("R", 1, 1, 5));        // the room's confirmed newer values arrive
            Assert.IsTrue(gate.Settled);
            Assert.IsFalse(CanStart(gate, 0));                                                   // revision 5: ready again
        }

        [Test]
        public void OnlyReallyLeavingEndsTheQuestionAndOldReportsNeverReachANewEntry()
        {
            var gate = new SettingsChangeGate(5);
            Assert.IsTrue(gate.Begin(2, 4, "R", 1, 0));
            gate.CheckTimeout(10);
            // closing and reopening the panel, or a leave request that could not be sent, call nothing here: still unknown
            Assert.AreEqual(SettingsState.Unknown, gate.State);
            Assert.IsFalse(CanStart(gate, 2));

            gate.Reset();                                                                        // the room was really left (다시 입장)
            Assert.IsTrue(gate.Settled);                                                         // the new entry's settings are those it received
            Assert.AreEqual(SettingsChangeResult.None, gate.Observe("R", 1, 2, 4));              // a late confirmation of the old entry: ignored
            Assert.AreEqual(SettingsChangeResult.None, gate.Refused("R", 1));

            Assert.IsTrue(gate.Begin(0, 6, "R", 2, 20));                                         // a change in the new entry
            Assert.AreEqual(SettingsChangeResult.None, gate.Refused("R", 1));                    // an old entry's refusal never touches it
            Assert.AreEqual(SettingsChangeResult.None, gate.Observe("Q", 2, 0, 6));              // nor another room's update
            Assert.AreEqual(SettingsState.Waiting, gate.State);
            Assert.AreEqual(SettingsChangeResult.Confirmed, gate.Observe("R", 2, 0, 6));
        }

        [Test]
        public void OnlyAnIntendedEndWithTheUserBackReconnects()
        {
            var e = new DisconnectExpectation();
            e.Request();                                                                         // the session leaves its owned connection
            Assert.IsTrue(e.Consume());                                                          // known also after the session object is gone
            Assert.IsFalse(e.Consume());                                                         // used once
            e.Request();
            e.Forget();                                                                          // a new connection started: older record is stale
            Assert.IsFalse(e.Consume());

            Assert.IsTrue(DisconnectExpectation.ShouldReconnect(true, true, true));              // intended end, user entered meanwhile
            Assert.IsFalse(DisconnectExpectation.ShouldReconnect(true, true, false));            // entered but no wish (connection was reused)
            Assert.IsFalse(DisconnectExpectation.ShouldReconnect(true, false, true));            // screen closed again
            Assert.IsFalse(DisconnectExpectation.ShouldReconnect(false, true, true));            // a real failure never reconnects by itself
        }

        [Test]
        public void StopAtTheStartBoundaryAppliesToEveryParticipant()
        {
            // three participants; the start time has just passed: the host is still Counting, guest A already Playing,
            // guest B leaves — the host stops, and both the host and guest A apply it.
            var stoppedAtHost = new HashSet<string>();
            var stoppedAtA = new HashSet<string>();
            Assert.IsTrue(CoopStartRules.HostMayStop(CoopStage.Counting));
            Assert.IsTrue(CoopStartRules.AppliesStop(CoopStage.Counting, "s1", "s1", stoppedAtHost));
            Assert.IsTrue(CoopStartRules.AppliesStop(CoopStage.Playing, "s1", "s1", stoppedAtA));   // Playing a frame earlier does not matter
            stoppedAtHost.Add("s1");
            stoppedAtA.Add("s1");
            Assert.IsFalse(CoopStartRules.AppliesStop(CoopStage.Aborted, "s1", "s1", stoppedAtA));  // a repeated stop does nothing
            Assert.IsFalse(CoopStartRules.AcceptsStart(CoopStage.Placed, "s1", "s1", stoppedAtA));  // a late start time never revives it
            Assert.IsFalse(CoopStartRules.AppliesStop(CoopStage.Loading, "s2", "s1", stoppedAtA));  // an older attempt's stop never hits a new one
            Assert.IsFalse(CoopStartRules.AppliesStop(CoopStage.Waiting, null, "s1", stoppedAtA));  // nor the waiting room or a single game

            Assert.IsFalse(CoopStartRules.HostMayStop(CoopStage.Playing));                      // once the host plays, nobody is sent back
            Assert.IsTrue(CoopStartRules.AcceptsStart(CoopStage.Placed, "s3", "s3", stoppedAtA));
            Assert.IsFalse(CoopStartRules.AcceptsStart(CoopStage.Placing, "s3", "s3", stoppedAtA));  // only after this device's placement
        }

        // ---- host change: everyone stops, the master cleans the room ---------------------------------------------
        sealed class SimRoom
        {
            public bool Open;
            public int Revision = 3;
        }

        /// <summary>One device as CoopSession applies the rules (stop once per id, reopen by the master).</summary>
        sealed class Device
        {
            public CoopStage Stage = CoopStage.Waiting;
            public string Attempt;
            public bool Master;
            public bool StartTimeKnown;
            public int SceneLoads;
            public readonly HashSet<string> Stopped = new HashSet<string>();

            void Stop()
            {
                if (Attempt == null || !Stopped.Add(Attempt)) return;
                Stage = CoopStage.Aborted;
                SceneLoads++;
            }

            public void HostAbort(string id, SimRoom room)
            {
                if (!CoopStartRules.AppliesStop(Stage, Attempt, id, Stopped)) return;
                Stop();
                Reopen(room);
            }

            public void HostChanged(bool nowMaster, SimRoom room)
            {
                Master = nowMaster;
                if (Attempt != null && CoopStartRules.StopsOnHostChange(Stage)) Stop();
                Reopen(room);
            }

            public void Go(string id)
            {
                if (!CoopStartRules.AcceptsStart(Stage, Attempt, id, Stopped)) return;
                StartTimeKnown = true;
                Stage = CoopStage.Counting;
            }

            void Reopen(SimRoom room)
            {
                if (!CoopStartRules.ReopensRoom(Master, room.Open, Stage)) return;
                room.Open = true;
                room.Revision++;
            }

            public void BackInWaitingRoom()
            {
                Stage = CoopStage.Waiting;
                Attempt = null;
                StartTimeKnown = false;
            }
        }

        [Test]
        public void HostLeavingRightAfterTheStartTimeStopsEveryoneAlike()
        {
            // three participants; the host sent the start time and left: A got it (and is already Playing), B did not.
            var room = new SimRoom { Open = false };
            var a = new Device { Attempt = "s1", Stage = CoopStage.Placed };
            var b = new Device { Attempt = "s1", Stage = CoopStage.Placed };
            a.Go("s1");
            a.Stage = CoopStage.Playing;                                                         // its frame passed the start time first
            Assert.IsTrue(a.StartTimeKnown);
            Assert.IsFalse(b.StartTimeKnown);

            b.HostChanged(false, room);                                                          // Photon's master switch; A becomes the master
            a.HostChanged(true, room);
            Assert.AreEqual(CoopStage.Aborted, a.Stage);                                        // same result whatever the start time or frame
            Assert.AreEqual(CoopStage.Aborted, b.Stage);
            Assert.AreEqual(1, a.SceneLoads);
            Assert.AreEqual(1, b.SceneLoads);
            Assert.IsTrue(room.Open);                                                            // the new master reopened the room
            Assert.AreEqual(4, room.Revision);                                                  // and voided every ready, once

            b.Go("s1");                                                                          // the start time arrives late at B
            Assert.AreEqual(CoopStage.Aborted, b.Stage);
            a.HostChanged(true, room);                                                           // a repeated switch / stop changes nothing
            a.HostAbort("s1", room);
            Assert.AreEqual(1, a.SceneLoads);
            Assert.AreEqual(4, room.Revision);
        }

        [Test]
        public void HostLeavingWhileCountingOrPlayingGivesTheSameResult()
        {
            foreach (var order in new[] { true, false })
            {
                var room = new SimRoom { Open = false };
                var a = new Device { Attempt = "s1", Stage = CoopStage.Playing, StartTimeKnown = true };
                var b = new Device { Attempt = "s1", Stage = CoopStage.Counting, StartTimeKnown = true };
                if (order)
                {
                    a.HostChanged(false, room);
                    b.HostChanged(true, room);
                }
                else
                {
                    b.HostChanged(true, room);
                    a.HostChanged(false, room);
                }
                Assert.AreEqual(CoopStage.Aborted, a.Stage);
                Assert.AreEqual(CoopStage.Aborted, b.Stage);
                Assert.IsTrue(room.Open);
                Assert.AreEqual(4, room.Revision);
            }
        }

        [Test]
        public void AParticipantThatStoppedBeforeBecomingTheMasterStillCleansTheRoom()
        {
            var room = new SimRoom { Open = false };
            var a = new Device { Attempt = "s1", Stage = CoopStage.Counting };
            a.HostAbort("s1", room);                                                             // stopped by the host (not the master: no reopening)
            Assert.AreEqual(CoopStage.Aborted, a.Stage);
            Assert.IsFalse(room.Open);
            a.BackInWaitingRoom();                                                               // back in the waiting room, then the host leaves
            a.HostChanged(true, room);
            Assert.IsTrue(room.Open);                                                            // the new master completes the cleanup
            Assert.AreEqual(4, room.Revision);
            Assert.AreEqual(1, a.SceneLoads);                                                    // no second scene load
        }

        [Test]
        public void AHostChangeInThePlainWaitingRoomStopsNothingAndANewAttemptStarts()
        {
            var room = new SimRoom { Open = true };
            var a = new Device();
            a.HostChanged(true, room);
            Assert.AreEqual(CoopStage.Waiting, a.Stage);
            Assert.AreEqual(0, a.SceneLoads);
            Assert.AreEqual(3, room.Revision);                                                  // an open room is left as it is

            Assert.IsFalse(CoopStartRules.StopsOnHostChange(CoopStage.Waiting));
            foreach (var s in new[] { CoopStage.Loading, CoopStage.Placing, CoopStage.Placed, CoopStage.Counting, CoopStage.Playing })
                Assert.IsTrue(CoopStartRules.StopsOnHostChange(s));

            var stopped = new HashSet<string> { "s1" };                                          // back after a stopped s1
            Assert.IsTrue(CoopStartRules.HostCanStart(true, true, 2, 2, SettingsState.Settled, CoopStage.Waiting));   // ready again: a new start
            Assert.IsTrue(CoopStartRules.AcceptsStart(CoopStage.Placed, "s2", "s2", stopped));   // with a new id
            Assert.IsFalse(CoopStartRules.AppliesStop(CoopStage.Loading, "s2", "s1", stopped));  // s1's late stop never touches s2
            Assert.IsFalse(CoopStartRules.HostCanStart(true, true, 2, 2, SettingsState.Settled, CoopStage.Loading));  // never twice
        }
    }
}
