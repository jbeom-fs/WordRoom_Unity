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

    /// <summary>Generator rules on the synthetic fixture (12 surfaces).</summary>
    public sealed class RoomGeneratorFixtureTests
    {
        static WordRoomDataset data;

        static WordRoomDataset Data => data ?? (data = WordRoomDataLoader.Load(new DirectoryPackageSource(
            Path.Combine(Application.dataPath, "WordRoom", "Tests", "Fixtures~", "Synthetic", PackageFormat.DefaultFolderName))));

        static RoomGenerator NewGenerator(long seed = 42)
        {
            var cfg = GenerationConfig.FromSettings(Data.Settings);
            cfg.Seed = seed;
            Assert.IsTrue(Data.TryGetWordRank("경기", out var start));
            Assert.IsTrue(Data.TryGetWordRank("바다", out var target));
            return RoomGenerator.Create(Data, cfg, start, target);
        }

        static List<string> Walk(RoomGenerator g, int steps)
        {
            var log = new List<string>();
            var u = g.Start();
            log.Add(Snapshot(g, u));
            for (var k = 0; k < steps && g.Map.Status == "running"; k++)
            {
                var dir = Directions.GenerationOrder.FirstOrDefault(d => g.Map.HasRoom(g.Map.Position.Add(Directions.Offset(d))));
                var m = g.Move(dir, out var refusal);
                Assert.IsNotNull(m, refusal);
                log.Add(Snapshot(g, m));
            }
            return log;
        }

        static string Snapshot(RoomGenerator g, GenerationUpdate u) =>
            $"{g.Map.Position}|{u.Status}|" + string.Join(";", g.Map.Rooms().Select(kv => $"{kv.Key}={kv.Value}")) + "|" +
            string.Join(";", u.Added.Select(r => $"{r.Position}:{r.Stage}:{r.Threshold}:{string.Join(",", r.Links.Select(l => $"{l.Source}{l.YesScore}{l.Similarity}"))}"));

        [Test]
        public void SameSeedAndMovesGiveSameMap()
        {
            CollectionAssert.AreEqual(Walk(NewGenerator(), 4), Walk(NewGenerator(), 4));
        }

        [Test]
        public void RoomsNeverChangeAndRepeatsAreKeptApart()
        {
            var g = NewGenerator();
            g.Start();
            for (var k = 0; k < 4 && g.Map.Status == "running"; k++)
            {
                var before = g.Map.Rooms().ToList();
                var dir = Directions.GenerationOrder.FirstOrDefault(d => g.Map.HasRoom(g.Map.Position.Add(Directions.Offset(d))));
                g.Move(dir, out _);
                foreach (var kv in before)
                {
                    Assert.IsTrue(g.Map.TryGetWord(kv.Key, out var w));
                    Assert.AreEqual(kv.Value, w, $"{kv.Key}의 단어가 바뀜");
                }
            }
            var rooms = g.Map.Rooms().ToList();
            foreach (var a in rooms)
                foreach (var b in rooms)
                    if (a.Key != b.Key && GridPos.Distance(a.Key, b.Key) <= g.Config.RepeatRadius)
                        Assert.AreNotEqual(a.Value, b.Value, $"{a.Key}와 {b.Key}에 같은 표기");
        }

        [Test]
        public void RelationLinksAreApprovedAtTheirThreshold()
        {
            var g = NewGenerator();
            var u = g.Start();
            foreach (var room in u.Added)
                foreach (var l in room.Links.Where(l => l.Source == "relation_db"))
                    Assert.AreEqual(WordPairState.Approved, Data.Relations.GetWordPairState(room.Word, l.NeighborWord, l.Threshold));
        }

        [Test]
        public void UngeneratedDirectionIsRefused()
        {
            var g = NewGenerator();
            g.Start();
            var missing = Directions.GenerationOrder.Where(d => !g.Map.HasRoom(g.Map.Position.Add(Directions.Offset(d)))).ToList();
            if (missing.Count == 0) Assert.Ignore("모든 방향이 생성되어 미생성 이동을 시험할 수 없음");
            Assert.IsNull(g.Move(missing[0], out var refusal));
            StringAssert.Contains("미생성", refusal);
            Assert.AreEqual(GridPos.Origin, g.Map.Position);
        }

        [Test]
        public void InvalidInputIsRejectedWithoutSubstitution()
        {
            var cfg = GenerationConfig.FromSettings(Data.Settings);
            Assert.IsTrue(Data.TryGetWordRank("경기", out var w));
            Assert.Throws<ArgumentException>(() => RoomGenerator.Create(Data, cfg, w, w));
            cfg.VocabularyLimit = 99999;
            Assert.IsTrue(Data.TryGetWordRank("바다", out var t));
            Assert.Throws<ArgumentException>(() => RoomGenerator.Create(Data, cfg, w, t));
        }
    }
}
