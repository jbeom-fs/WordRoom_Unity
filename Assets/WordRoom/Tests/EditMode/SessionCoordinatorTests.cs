using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using WordRoom.Generation;
using WordRoom.World;

namespace WordRoom.Data.Tests
{
    /// <summary>Shared world, per-player records and request rules of SessionCoordinator on the synthetic fixture.</summary>
    public sealed class SessionCoordinatorTests
    {
        long next;

        static SessionCoordinator Coop(params string[] ids)
        {
            var d = RoomGeneratorFixtureTests.Data;
            var cfg = GenerationConfig.FromSettings(d.Settings);
            Assert.IsTrue(d.TryGetWordRank("경기", out var start));
            Assert.IsTrue(d.TryGetWordRank("바다", out var target));
            return SessionCoordinator.Create(d, cfg, start, target, SessionMode.Coop, ids);
        }

        static T Read<T>(SessionCoordinator c, Func<SessionReader, T> f) => c.Read(f);

        static GridPos Pos(SessionCoordinator c, string id) => c.Read(r => r.PlayerPosition(id));

        RequestResult<bool> Send(SessionCoordinator c, string id, RequestKind kind, Direction d, GridPos? from = null, long? requestId = null, string session = null) =>
            c.Submit(new SessionRequest(session ?? c.SessionId, id, requestId ?? ++next, kind, from ?? Pos(c, id), d), (r, u) => true);

        /// <summary>A direction from the start room toward an existing room (the test is ignored when there is none).</summary>
        static Direction StartExit(SessionCoordinator c)
        {
            var found = c.Read(r => Directions.GenerationOrder.Where(d => r.HasRoom(Directions.Offset(d))).Select(d => (Direction?)d).FirstOrDefault());
            if (found == null) Assert.Ignore("시작 방 주변에 생성된 방이 없어 시험할 수 없음");
            return found.Value;
        }

        static Direction Opposite(Direction d) =>
            d == Direction.Up ? Direction.Down : d == Direction.Down ? Direction.Up : d == Direction.Left ? Direction.Right : Direction.Left;

        [Test]
        public void StartVisitsOncePerPlayerAndOnceForTheTeam()
        {
            var c = Coop("a", "b");
            Assert.IsTrue(c.Start<bool>(null).Accepted);
            Assert.IsFalse(c.Start<bool>(null).Accepted);
            Read(c, r =>
            {
                Assert.AreEqual(1, r.PlayerUniqueVisited("a"));
                Assert.AreEqual(1, r.PlayerUniqueVisited("b"));
                Assert.AreEqual(0, r.PlayerMoves("a"));
                Assert.AreEqual(1, r.TeamVisitedCount);
                Assert.AreEqual(1, r.Revision);
                return true;
            });
        }

        [Test]
        public void PrepareChangesNoPersonalRecord()
        {
            var c = Coop("a", "b");
            c.Start<bool>(null);
            var d = StartExit(c);
            var p = Send(c, "a", RequestKind.Prepare, d);
            Assert.IsTrue(p.Accepted, p.Refusal);
            Assert.AreEqual(RoomConnection.Of(GridPos.Origin, d), p.Update.OpenedDoor);
            Read(c, r =>
            {
                Assert.AreEqual(GridPos.Origin, r.PlayerPosition("a"));
                Assert.AreEqual(0, r.PlayerMoves("a"));
                Assert.AreEqual(1, r.PlayerUniqueVisited("a"));
                Assert.AreEqual(1, r.TeamVisitedCount);
                Assert.AreEqual("running", r.Status);
                Assert.IsTrue(r.IsPrepared(Directions.Offset(d)));
                Assert.AreEqual(DoorState.Open, r.Door(RoomConnection.Of(GridPos.Origin, d)));
                return true;
            });
        }

        [Test]
        public void PlayersKeepTheirOwnRecordsAndTheTeamCountsCoordinatesOnce()
        {
            var c = Coop("a", "b");
            c.Start<bool>(null);
            var d = StartExit(c);
            var dest = Directions.Offset(d);
            Send(c, "a", RequestKind.Prepare, d);
            var e = Send(c, "a", RequestKind.Enter, d);
            Assert.IsTrue(e.Accepted, e.Refusal);
            Assert.IsTrue(e.Update.FirstVisit);
            Assert.IsTrue(e.Update.TeamFirstVisit);
            Read(c, r =>
            {
                Assert.AreEqual(dest, r.PlayerPosition("a"));
                Assert.AreEqual(GridPos.Origin, r.PlayerPosition("b"));
                Assert.AreEqual(0, r.PlayerMoves("b"));
                Assert.AreEqual(1, r.PlayerUniqueVisited("b"));
                Assert.AreEqual(2, r.TeamVisitedCount);
                return true;
            });
            var b = Send(c, "b", RequestKind.Enter, d);                          // the door is already open and the room prepared
            Assert.IsTrue(b.Accepted, b.Refusal);
            Assert.IsTrue(b.Update.FirstVisit);
            Assert.IsFalse(b.Update.TeamFirstVisit);
            Read(c, r =>
            {
                Assert.AreEqual(2, r.TeamVisitedCount);
                Assert.AreEqual(2, r.PlayerUniqueVisited("b"));
                Assert.AreEqual(1, r.PlayerMoves("a"));
                Assert.AreEqual(1, r.PlayerVisitCount("a", dest));
                return true;
            });
        }

        [Test]
        public void DuplicateRequestIsNotAppliedAgain()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            Send(c, "a", RequestKind.Prepare, d);
            var first = Send(c, "a", RequestKind.Enter, d, GridPos.Origin, 100);
            var again = Send(c, "a", RequestKind.Enter, d, GridPos.Origin, 100);
            Assert.IsTrue(first.Accepted, first.Refusal);
            Assert.IsTrue(again.Duplicate);
            Assert.AreNotSame(first.Update, again.Update);                                      // each response is its own copy
            Assert.AreEqual(first.Update.Revision, again.Update.Revision);
            Assert.AreEqual(first.Update.To, again.Update.To);
            Assert.AreEqual(1, Read(c, r => r.PlayerMoves("a")));
            Assert.AreEqual(first.Update.Revision, Read(c, r => r.Revision));
        }

        [Test]
        public void DuplicateAfterAnotherChangeKeepsTheFirstResultWithoutCapture()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            var dest = Directions.Offset(d);
            var captures = 0;
            RequestResult<long> Req(RequestKind k, Direction dir, long id, GridPos from) =>
                c.Submit(new SessionRequest(c.SessionId, "a", id, k, from, dir), (r, u) =>
                {
                    captures++;
                    return r.Revision;
                });
            var a = Req(RequestKind.Prepare, d, 100, GridPos.Origin);
            var b = Req(RequestKind.Enter, d, 101, GridPos.Origin);
            Assert.IsTrue(a.Accepted && b.Accepted, a.Refusal ?? b.Refusal);
            Assert.AreEqual(a.Update.Revision, a.Captured);                                     // a capture belongs to its own change
            Assert.AreEqual(2, captures);
            var again = Req(RequestKind.Prepare, d, 100, GridPos.Origin);
            Assert.IsTrue(again.Duplicate);
            Assert.IsTrue(again.Accepted);
            Assert.IsFalse(again.HasCaptured);
            Assert.Throws<InvalidOperationException>(() => _ = again.Captured);
            Assert.AreEqual(2, captures);                                                       // no capture ran for the duplicate
            Assert.AreEqual(a.Update.Revision, again.Update.Revision);
            Assert.AreEqual("prepare", again.Update.Kind);
            Assert.AreEqual(GridPos.Origin, again.Update.From);
            Read(c, r =>
            {
                Assert.AreEqual(dest, r.PlayerPosition("a"));                                  // not back to the first request's room
                Assert.AreEqual(1, r.PlayerMoves("a"));
                Assert.AreEqual(b.Update.Revision, r.Revision);
                return true;
            });
            Assert.IsTrue(Req(RequestKind.Prepare, Opposite(d), 102, dest).Accepted);           // the next request starts from the current room

            var refused = Req(RequestKind.Enter, d, 103, new GridPos(7, 7));
            var refusedAgain = Req(RequestKind.Enter, d, 103, dest);                            // same id: the first refusal, not a new check
            Assert.IsFalse(refused.Accepted);
            Assert.IsTrue(refusedAgain.Duplicate);
            Assert.IsFalse(refusedAgain.Accepted);
            Assert.AreEqual(refused.Refusal, refusedAgain.Refusal);
            Assert.IsFalse(refusedAgain.HasCaptured);
        }

        [Test]
        public void ChangingAResponseChangesNeitherTheRecordNorTheWorld()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            var first = c.Submit(new SessionRequest(c.SessionId, "a", 200, RequestKind.Prepare, GridPos.Origin, d), (r, u) =>
            {
                u.Revision = -1;                                                                // the capture changes its own copy only
                return true;
            });
            Assert.IsTrue(first.Accepted, first.Refusal);
            var confirmedRevision = Read(c, r => r.Revision);
            var to = Directions.Offset(d);
            var added = first.Update.Added.Select(x => (x.Position, x.Word, x.Links.Count)).ToArray();
            var steps = first.Update.Fill.Steps.Count;
            var empty = first.Update.Fill.Empty.Count;

            void Scramble(GenerationUpdate u)
            {
                u.To = new GridPos(99, 99);
                u.Revision = -5;
                u.FirstVisit = !u.FirstVisit;
                foreach (var room in u.Fill.Added)
                {
                    room.Word = -1;
                    foreach (var l in room.Links) l.NeighborWord = -1;
                    room.Links.Clear();
                }
                foreach (var s in u.Fill.Steps)
                {
                    s.CandidateCounts.Clear();
                    foreach (var p in s.Positions) p.BlockedBy.Clear();
                    s.Positions.Clear();
                }
                u.Fill.Added.Clear();
                u.Fill.Steps.Clear();
                u.Fill.Empty.Add(new EmptyPosition { Position = new GridPos(50, 50), Reason = "x" });
            }

            void Same(GenerationUpdate u)
            {
                Assert.AreEqual(confirmedRevision, u.Revision);
                Assert.AreEqual(to, u.To);
                CollectionAssert.AreEqual(added, u.Added.Select(x => (x.Position, x.Word, x.Links.Count)).ToArray());
                Assert.AreEqual(steps, u.Fill.Steps.Count);
                Assert.AreEqual(empty, u.Fill.Empty.Count);
            }

            Scramble(first.Update);
            var dup1 = c.Submit<bool>(new SessionRequest(c.SessionId, "a", 200, RequestKind.Prepare, GridPos.Origin, d), null);
            Same(dup1.Update);
            Scramble(dup1.Update);
            var dup2 = c.Submit<bool>(new SessionRequest(c.SessionId, "a", 200, RequestKind.Prepare, GridPos.Origin, d), null);
            Same(dup2.Update);
            Read(c, r =>
            {
                foreach (var (p, w, links) in added)
                {
                    Assert.IsTrue(r.TryGetWord(p, out var stored));
                    Assert.AreEqual(w, stored);
                    Assert.AreEqual(links, r.RoomInfo(p).Links.Count);
                }
                return true;
            });
        }

        [Test]
        public void AReaderIsUsableOnlyInItsOwnCallbackAndThread()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            SessionReader kept = null;
            c.Read(r =>
            {
                kept = r;
                return r.Revision;
            });
            Assert.Throws<InvalidOperationException>(() => _ = kept.Revision);
            c.Read(r =>
            {
                Assert.Throws<InvalidOperationException>(() => _ = kept.Revision);             // never valid again, also during another callback
                return r.Revision;
            });
            var otherThreadRefused = c.Read(r => Task.Run(() =>
            {
                try
                {
                    _ = r.Revision;
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }).Result);
            Assert.IsTrue(otherThreadRefused);
            SessionReader fromCapture = null;
            var d = StartExit(c);
            c.Submit(new SessionRequest(c.SessionId, "a", 1, RequestKind.Prepare, GridPos.Origin, d), (r, u) =>
            {
                fromCapture = r;
                return true;
            });
            Assert.Throws<InvalidOperationException>(() => _ = fromCapture.Status);
        }

        [Test]
        public void ReenteringFromACallbackIsRefusedBeforeAnyChange()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            var id = RoomConnection.Of(GridPos.Origin, d);
            var revision = Read(c, r => r.Revision);
            var refused = c.Read(r =>
            {
                try
                {
                    c.Submit(new SessionRequest(c.SessionId, "a", 300, RequestKind.Prepare, GridPos.Origin, d), (x, u) => true);
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            });
            Assert.IsTrue(refused);
            Assert.AreEqual(revision, Read(c, r => r.Revision));
            Assert.AreEqual(DoorState.Closed, Read(c, r => r.Door(id)));
            Assert.Throws<InvalidOperationException>(() => c.Read(r => c.Read(x => x.Revision)));
            var outer = c.Submit(new SessionRequest(c.SessionId, "a", 301, RequestKind.Prepare, GridPos.Origin, d), (r, u) =>
            {
                Assert.Throws<InvalidOperationException>(() => c.Read(x => x.Revision));
                Assert.Throws<InvalidOperationException>(() => c.Start<bool>(null));
                Assert.Throws<InvalidOperationException>(() => c.Submit<bool>(new SessionRequest(c.SessionId, "a", 302, RequestKind.Enter, GridPos.Origin, d), null));
                return r.Revision;
            });
            Assert.IsTrue(outer.Accepted, outer.Refusal);
            Read(c, r =>
            {
                Assert.AreEqual(revision + 1, r.Revision);                                     // only the outer request changed anything
                Assert.AreEqual(GridPos.Origin, r.PlayerPosition("a"));
                Assert.AreEqual(0, r.PlayerMoves("a"));
                return true;
            });
            var notRecorded = c.Submit<bool>(new SessionRequest(c.SessionId, "a", 302, RequestKind.Enter, GridPos.Origin, d), null);
            Assert.IsFalse(notRecorded.Duplicate);                                              // the refused re-entry left no record
            Assert.IsTrue(notRecorded.Accepted, notRecorded.Refusal);
            var fromOtherThread = Task.Run(() => c.Submit<bool>(new SessionRequest(c.SessionId, "a", 303, RequestKind.Prepare, Directions.Offset(d), Opposite(d)), null)).Result;
            Assert.IsTrue(fromOtherThread.Accepted, fromOtherThread.Refusal);                   // a normal request of another thread is no re-entry
        }

        [Test]
        public void UndefinedKindOrDirectionChangesNothing()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            (long, int, int, GridPos, int, int) State() => Read(c, r =>
            {
                var all = r.CopyAll();
                return (r.Revision, r.RoomCount, all.OpenDoors.Count, r.PlayerPosition("a"), r.PlayerMoves("a"), all.Recent.Count);
            });
            var before = State();
            var kind = Send(c, "a", (RequestKind)99, d);
            Assert.IsFalse(kind.Accepted);
            StringAssert.Contains("요청 종류", kind.Refusal);
            foreach (var bad in new[] { (Direction)7, (Direction)(-1) })
                foreach (var k in new[] { RequestKind.Prepare, RequestKind.Enter, RequestKind.Move })
                {
                    var r = Send(c, "a", k, bad);
                    Assert.IsFalse(r.Accepted);
                    StringAssert.Contains("방향", r.Refusal);
                }
            Assert.AreEqual(before, State());
            Assert.IsTrue(Send(c, "a", RequestKind.Prepare, d).Accepted);                     // normal requests still work
        }

        [Test]
        public void InvalidRequestsAreRefusedWithoutChange()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            var revision = Read(c, r => r.Revision);
            Assert.IsFalse(Send(c, "a", RequestKind.Prepare, d, session: "older-session").Accepted);
            Assert.IsFalse(Send(c, "a", RequestKind.Prepare, d, new GridPos(5, 5)).Accepted);   // wrong expected room
            Assert.IsFalse(Send(c, "x", RequestKind.Prepare, d, GridPos.Origin).Accepted);      // not a participant
            Assert.IsFalse(Send(c, "a", RequestKind.Enter, d).Accepted);                         // door closed, not prepared
            Assert.AreEqual(revision, Read(c, r => r.Revision));
            Assert.AreEqual("running", Read(c, r => r.Status));                                  // a refusal is no session failure
        }

        [Test]
        public void BothSidesOfAConnectionShareOneDoor()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            Send(c, "a", RequestKind.Prepare, d);
            Send(c, "a", RequestKind.Enter, d);
            var back = Send(c, "a", RequestKind.Prepare, Opposite(d));
            Assert.IsTrue(back.Accepted, back.Refusal);
            Assert.IsNull(back.Update.OpenedDoor);                                              // already open from the other side
            Assert.AreEqual(DoorState.Open, Read(c, r => r.Door(RoomConnection.Of(Directions.Offset(d), Opposite(d)))));
            Assert.AreEqual(1, Read(c, r => r.CopyAll().OpenDoors.Count));
        }

        [Test]
        public void EnteringDoesNotAddRecentWords()
        {
            var c = Coop("a");
            c.Start<bool>(null);
            var d = StartExit(c);
            Send(c, "a", RequestKind.Prepare, d);
            var before = Read(c, r => r.CopyAll().Recent.ToArray());
            Send(c, "a", RequestKind.Enter, d);
            CollectionAssert.AreEqual(before, Read(c, r => r.CopyAll().Recent.ToArray()));
        }

        [Test]
        public void ExploredMapAppliesInOrderAndRecoversFromAFullCopy()
        {
            var c = Coop("a", "b");
            var words = RoomGeneratorFixtureTests.Data;
            var map = new ExploredMap();
            map.Reset(c.SessionId, "b");
            var start = c.Start((r, u) => MapUpdate.FromCopy(r.CopyAll(), words.GetWord));
            Assert.AreEqual(MapApplyResult.Applied, map.Apply(start.Captured, out _));
            var d = StartExit(c);
            var dest = Directions.Offset(d);
            RequestResult<MapUpdate> Change(RequestKind k) =>
                c.Submit(new SessionRequest(c.SessionId, "a", ++next, k, Pos(c, "a"), d), (r, u) => MapUpdate.FromChange(r, u, words.GetWord));
            var prepare = Change(RequestKind.Prepare).Captured;
            var enter = Change(RequestKind.Enter).Captured;
            Assert.AreEqual(MapApplyResult.Gap, map.Apply(enter, out _));                    // the prepare was missed
            Assert.AreEqual(MapApplyResult.Applied, map.Apply(prepare, out _));
            Assert.AreEqual(MapApplyResult.Ignored, map.Apply(prepare, out _));
            Assert.AreEqual(MapApplyResult.Applied, map.Apply(enter, out _));
            Assert.IsTrue(map.TryGet(dest, out var room) && room.Visited);                      // another player's visit shows
            Assert.AreEqual(GridPos.Origin, map.Current);                                       // the local player has not moved
            Assert.IsTrue(map.TryGetPlayer("a", out var a) && a == dest);
            Assert.AreEqual(MapApplyResult.Ignored, map.Apply(start.Captured, out _));         // older full copy
            var other = new ExploredMap();
            other.Reset(c.SessionId, "a");
            Assert.AreEqual(MapApplyResult.Gap, other.Apply(enter, out _));
            Assert.AreEqual(MapApplyResult.Applied, other.Apply(c.Read(r => MapUpdate.FromCopy(r.CopyAll(), words.GetWord)), out _));
            Assert.AreEqual(dest, other.Current);
            Assert.AreEqual(MapApplyResult.OtherSession, other.Apply(Coop("a").Start((r, u) => MapUpdate.FromCopy(r.CopyAll(), words.GetWord)).Captured, out _));
        }

        [Test]
        public void SoloNeedsExactlyOnePlayer()
        {
            var d = RoomGeneratorFixtureTests.Data;
            var cfg = GenerationConfig.FromSettings(d.Settings);
            Assert.IsTrue(d.TryGetWordRank("경기", out var start));
            Assert.IsTrue(d.TryGetWordRank("바다", out var target));
            Assert.Throws<ArgumentException>(() => SessionCoordinator.Create(d, cfg, start, target, SessionMode.Solo, new[] { "a", "b" }));
            Assert.Throws<ArgumentException>(() => SessionCoordinator.Create(d, cfg, start, target, SessionMode.Coop, new[] { "a", "a" }));
        }
    }
}
