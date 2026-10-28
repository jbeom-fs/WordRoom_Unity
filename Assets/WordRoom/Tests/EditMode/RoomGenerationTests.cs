using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WordRoom.Generation;

namespace WordRoom.Data.Tests
{
    /// <summary>Random-source ports against published NumPy / RFC 7693 values.</summary>
    public sealed class PythonRandomTests
    {
        [Test]
        public void DefaultRngFirstDoubleMatchesNumpy()
        {
            // np.random.default_rng(42).random() and np.random.default_rng(0).random()
            Assert.AreEqual(0.7739560485559633, Pcg64.FromSeedSequence(SeedSequence.FromInt(42)).NextDouble());
            Assert.AreEqual(0.6369616873214543, Pcg64.FromSeedSequence(SeedSequence.FromInt(0)).NextDouble());
        }

        [Test]
        public void Blake2bMatchesRfc7693()
        {
            var hash = Blake2b.Hash(System.Text.Encoding.ASCII.GetBytes("abc"), 64);
            Assert.AreEqual("ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
                string.Concat(hash.Select(b => b.ToString("x2"))));
        }

        [Test]
        public void ChoiceIsSearchsortedRight()
        {
            Assert.AreEqual(0, PythonRandom.Choice(new[] { 0.5, 0.5 }, 0.49));
            Assert.AreEqual(1, PythonRandom.Choice(new[] { 0.5, 0.5 }, 0.5));
            Assert.AreEqual(2, PythonRandom.Choice(new[] { 0.2, 0.3, 0.5 }, 0.99));
        }

        [Test]
        public void CoordinateDrawIsStable()
        {
            var a = PythonRandom.CoordinateUniform(42, new GridPos(1, -2));
            Assert.AreEqual(a, PythonRandom.CoordinateUniform(42, new GridPos(1, -2)));
            Assert.AreNotEqual(a, PythonRandom.CoordinateUniform(43, new GridPos(1, -2)));
            Assert.That(a, Is.InRange(0.0, 1.0));
        }
    }

    /// <summary>Generator rules on the synthetic fixture (12 surfaces), through a solo session.</summary>
    public sealed class RoomGeneratorFixtureTests
    {
        const string Me = "p";
        static WordRoomDataset data;
        long nextRequest;

        internal static WordRoomDataset Data => data ?? (data = WordRoomDataLoader.Load(new DirectoryPackageSource(
            Path.Combine(Application.dataPath, "WordRoom", "Tests", "Fixtures~", "Synthetic", PackageFormat.DefaultFolderName))));

        static SessionCoordinator NewSession(long seed = 42)
        {
            var cfg = GenerationConfig.FromSettings(Data.Settings);
            cfg.Seed = seed;
            Assert.IsTrue(Data.TryGetWordRank("경기", out var start));
            Assert.IsTrue(Data.TryGetWordRank("바다", out var target));
            return SessionCoordinator.CreateSolo(Data, cfg, start, target, Me);
        }

        /// <summary>Position, status, every room and the rooms added by `u` (read inside the request's capture).</summary>
        static string Snapshot(SessionReader r, GenerationUpdate u) =>
            $"{r.PlayerPosition(Me)}|{u.Status}|" + string.Join(";", r.CopyAll().Rooms.Select(kv => $"{kv.Position}={kv.Word}")) + "|" +
            string.Join(";", u.Added.Select(x => $"{x.Position}:{x.Stage}:{x.Threshold}:{string.Join(",", x.Links.Select(l => $"{l.Source}{l.YesScore}{l.Similarity}"))}"));

        static Direction? FirstExisting(SessionCoordinator c) =>
            c.Read(r =>
            {
                var here = r.PlayerPosition(Me);
                foreach (var d in Directions.GenerationOrder)
                    if (r.HasRoom(here.Add(Directions.Offset(d)))) return (Direction?)d;
                return null;
            });

        RequestResult<string> Move(SessionCoordinator c, Direction d) =>
            c.Submit(new SessionRequest(c.SessionId, Me, ++nextRequest, RequestKind.Move, c.Read(r => r.PlayerPosition(Me)), d), Snapshot);

        List<string> Walk(SessionCoordinator c, int steps)
        {
            var log = new List<string>();
            var s = c.Start(Snapshot);
            log.Add(s.Captured);
            for (var k = 0; k < steps && c.Read(r => r.Status) == "running"; k++)
            {
                var dir = FirstExisting(c);
                Assert.IsNotNull(dir);
                var m = Move(c, dir.Value);
                Assert.IsTrue(m.Accepted, m.Refusal);
                log.Add(m.Captured);
            }
            return log;
        }

        [Test]
        public void SameSeedAndMovesGiveSameMap()
        {
            CollectionAssert.AreEqual(Walk(NewSession(), 4), Walk(NewSession(), 4));
        }

        [Test]
        public void RoomsNeverChangeAndRepeatsAreKeptApart()
        {
            var c = NewSession();
            c.Start<bool>(null);
            for (var k = 0; k < 4 && c.Read(r => r.Status) == "running"; k++)
            {
                var before = c.Read(r => r.CopyAll().Rooms);
                var dir = FirstExisting(c);
                Assert.IsNotNull(dir);
                Move(c, dir.Value);
                c.Read(r =>
                {
                    foreach (var kv in before)
                    {
                        Assert.IsTrue(r.TryGetWord(kv.Position, out var w));
                        Assert.AreEqual(kv.Word, w, $"{kv.Position}의 단어가 바뀜");
                    }
                    return true;
                });
            }
            var rooms = c.Read(r => r.CopyAll().Rooms);
            var radius = c.Config.RepeatRadius;
            foreach (var a in rooms)
                foreach (var b in rooms)
                    if (a.Position != b.Position && GridPos.Distance(a.Position, b.Position) <= radius)
                        Assert.AreNotEqual(a.Word, b.Word, $"{a.Position}와 {b.Position}에 같은 표기");
        }

        [Test]
        public void RelationLinksAreApprovedAtTheirThreshold()
        {
            var c = NewSession();
            var u = c.Start<bool>(null).Update;
            foreach (var room in u.Added)
                foreach (var l in room.Links.Where(l => l.Source == "relation_db"))
                    Assert.AreEqual(WordPairState.Approved, Data.Relations.GetWordPairState(room.Word, l.NeighborWord, l.Threshold));
        }

        [Test]
        public void UngeneratedDirectionIsRefused()
        {
            var c = NewSession();
            c.Start<bool>(null);
            var missing = c.Read(r => Directions.GenerationOrder.Where(d => !r.HasRoom(Directions.Offset(d))).ToList());
            if (missing.Count == 0) Assert.Ignore("모든 방향이 생성되어 미생성 이동을 시험할 수 없음");
            var m = Move(c, missing[0]);
            Assert.IsFalse(m.Accepted);
            StringAssert.Contains("미생성", m.Refusal);
            Assert.AreEqual(GridPos.Origin, c.Read(r => r.PlayerPosition(Me)));
        }

        [Test]
        public void InvalidInputIsRejectedWithoutSubstitution()
        {
            var cfg = GenerationConfig.FromSettings(Data.Settings);
            Assert.IsTrue(Data.TryGetWordRank("경기", out var w));
            Assert.Throws<ArgumentException>(() => SessionCoordinator.CreateSolo(Data, cfg, w, w, Me));
            cfg.VocabularyLimit = 99999;
            Assert.IsTrue(Data.TryGetWordRank("바다", out var t));
            Assert.Throws<ArgumentException>(() => SessionCoordinator.CreateSolo(Data, cfg, w, t, Me));
        }
    }
}
