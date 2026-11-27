using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WordRoom.Shelf;

namespace WordRoom.Data.Tests
{
    public sealed class ShelfLinkDatabaseTests
    {
        const string ScenePath = "Assets/WordRoom/Scenes/ShelfCorridorPrototype.unity";
        static WordRoomDataset fixture;

        static WordRoomDataset Fixture() => fixture ??= WordRoomDataLoader.Load(new DirectoryPackageSource(
            Path.Combine(Application.dataPath, "WordRoom", "Tests", "Fixtures~", "Synthetic", PackageFormat.DefaultFolderName)));

        sealed class LinkSource : IPackageFileSource
        {
            readonly byte[] bytes;

            public LinkSource(string json) => bytes = Encoding.UTF8.GetBytes(json);
            public string Description => "link test fixture";
            public bool Exists(string name) => name == ShelfLinkDatabase.FileName;
            public long GetLength(string name) => bytes.Length;
            public Stream OpenRead(string name) => new MemoryStream(bytes, false);
        }

        static string FixtureJson(string links, string packageId = null, int omitRow = -1)
        {
            var data = Fixture();
            var rows = Enumerable.Range(0, data.SenseCount).Where(row => row != omitRow).Select(row =>
            {
                var id = data.GetSenseId(row);
                return $"{{\"senseId\":\"{id}\",\"links\":[{(id == "512" ? links : "")}]}}";
            });
            return $"{{\"format\":\"WordRoomLinkDatabase\",\"schemaVersion\":1,\"packageId\":\"{packageId ?? data.PackageId}\"," +
                   $"\"offsetUnit\":\"utf16\",\"textBasis\":\"ShelfLinkText.DisplayDefinition\",\"senses\":[{string.Join(",", rows)}]}}";
        }

        [Test]
        public void MissingDatabaseIsAnError()
        {
            var absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var ex = Assert.Throws<WordRoomDataException>(() =>
                ShelfLinkDatabase.Load(new DirectoryPackageSource(absent), Fixture(), CancellationToken.None));
            Assert.AreEqual(DataErrorCode.MissingFile, ex.Code);
            Assert.AreEqual("links.json", ex.FileName);
        }

        [Test]
        public void PackageIdMismatchIsAnError()
        {
            var ex = Assert.Throws<WordRoomDataException>(() =>
                ShelfLinkDatabase.Load(new LinkSource(FixtureJson("", "different-package")), Fixture(), CancellationToken.None));
            Assert.AreEqual(DataErrorCode.PackageIdMismatch, ex.Code);
        }

        [TestCase("{\"length\":2,\"text\":\"지구\",\"target\":\"나\"}", DataErrorCode.InvalidFormat)]
        [TestCase("{\"start\":0,\"length\":2,\"text\":\"문자\",\"target\":\"나\"}", DataErrorCode.InvalidValue)]
        [TestCase("{\"start\":0,\"length\":2,\"text\":\"지구\",\"target\":\"미등재어\"}", DataErrorCode.InvalidReference)]
        [TestCase("{\"start\":2147483647,\"length\":2,\"text\":\"지구\",\"target\":\"나\"}", DataErrorCode.InvalidValue)]
        [TestCase("{\"start\":0,\"length\":2,\"text\":\"지구\",\"target\":\"나\"},{\"start\":1,\"length\":1,\"text\":\"구\",\"target\":\"나\"}", DataErrorCode.InvalidValue)]
        public void BadSpanOrTargetStopsLoading(string record, DataErrorCode expected)
        {
            var ex = Assert.Throws<WordRoomDataException>(() =>
                ShelfLinkDatabase.Load(new LinkSource(FixtureJson(record)), Fixture(), CancellationToken.None));
            Assert.AreEqual(expected, ex.Code, ex.Message);
        }

        [Test]
        public void MissingSenseListIsAnError()
        {
            var ex = Assert.Throws<WordRoomDataException>(() =>
                ShelfLinkDatabase.Load(new LinkSource(FixtureJson("", omitRow: 0)), Fixture(), CancellationToken.None));
            Assert.AreEqual(DataErrorCode.CountMismatch, ex.Code);
            StringAssert.Contains(Fixture().GetSenseId(0), ex.Message);
        }

        [Test]
        public void UsesUtf16PositionsAndSeparateDisplayTextAndTarget()
        {
            var data = Fixture();
            Assert.IsTrue(data.TryGetSenseRow("512", out var row));
            var definition = data.GetSense(row).Definition;
            var start = definition.IndexOf("문자", StringComparison.Ordinal);
            Assert.That(definition.IndexOf("𝄞", StringComparison.Ordinal), Is.InRange(0, start - 2));
            var record = $"{{\"start\":{start},\"length\":2,\"text\":\"문자\",\"target\":\"선박\"}}";
            var db = ShelfLinkDatabase.Load(new LinkSource(FixtureJson(record)), data, CancellationToken.None);
            var part = db.Get("512").Links.Single();
            Assert.AreEqual(start, part.Start);
            Assert.AreEqual("문자", part.Text);
            Assert.AreEqual("선박", part.Target);
            var graph = new ShelfLinkGraph(data, db, data.WordCount, 2, new List<ShelfLinkGraph.RoomRule>(), CancellationToken.None);
            Assert.IsTrue(graph.SetGoal("선박"));          // a goal can be entered even without any exit of its own
            Assert.IsTrue(graph.CanTravel("바다", "선박"));
            Assert.IsFalse(graph.CanTravel("경기", "선박"));
            Assert.AreEqual("선박", graph.Doors(row).Single().Target);
        }

        [Test]
        public void OneLetterHeadwordsAreReadButNeverRooms()
        {
            var data = Fixture();
            Assert.IsTrue(data.TryGetSenseRow("512", out var row));
            var definition = data.GetSense(row).Definition;
            var letters = definition.IndexOf("문자", StringComparison.Ordinal);
            var land = definition.IndexOf("육지", StringComparison.Ordinal);
            var records = $"{{\"start\":{letters},\"length\":2,\"text\":\"문자\",\"target\":\"배\"}}," +
                          $"{{\"start\":{land},\"length\":2,\"text\":\"육지\",\"target\":\"선박\"}}";
            var db = ShelfLinkDatabase.Load(new LinkSource(FixtureJson(records)), data, CancellationToken.None);
            CollectionAssert.AreEquivalent(new[] { "배", "선박" }, db.Get("512").Links.Select(p => p.Target));   // the DB keeps both

            // challengeMinLength 1: the draw is narrowed by it, never widened below RoomMinLength.
            var graph = new ShelfLinkGraph(data, db, data.WordCount, 1, new List<ShelfLinkGraph.RoomRule>(), CancellationToken.None);
            Assert.IsFalse(graph.IsCandidate("배"));
            Assert.AreEqual($"{ShelfLinkGraph.RoomMinLength}글자 미만 표제어", graph.WhyNotCandidate("배"));
            Assert.IsFalse(graph.SetGoal("배"));
            Assert.IsTrue(graph.SetGoal("선박"));
            Assert.IsFalse(graph.IsValidDoor("배"));
            Assert.IsFalse(graph.CanTravel("바다", "배"));
            Assert.IsTrue(graph.CanTravel("바다", "선박"));

            var doors = graph.Doors(row);
            CollectionAssert.AreEqual(new[] { "선박" }, doors.Select(p => p.Target));
            CollectionAssert.AreEquivalent(new[] { "선박" }, graph.ValidExits("바다"));
            var body = graph.ReadingDefinition(row);
            StringAssert.Contains("문자", body);                                     // 문자 stays in the text, unlinked
            var parts = ShelfLinkText.Split(body, doors);
            Assert.AreEqual(body, string.Concat(parts.Select(p => p.Text)));
            CollectionAssert.AreEqual(new[] { "육지" }, parts.Where(p => p.IsLink).Select(p => p.Text));

            Assert.IsTrue(graph.PickChallenge(new System.Random(1), 1, CancellationToken.None,
                out var goal, out var start, out var moves, out _));
            Assert.AreEqual("선박", goal);
            Assert.AreEqual("바다", start);
            Assert.AreEqual(1, moves);
        }

        [Test]
        public void RealSceneDatabase_DocumentsAndGraphShareExits()
        {
            var package = Path.Combine(Application.streamingAssetsPath, PackageFormat.DefaultFolderName);
            if (!File.Exists(Path.Combine(package, PackageFormat.Manifest))) Assert.Ignore($"실데이터 사전 패키지가 없습니다 ({package})");
            var source = new DirectoryPackageSource(package);
            var data = WordRoomDataLoader.Load(source);
            var db = ShelfLinkDatabase.Load(source, data, CancellationToken.None);
            var (limit, minLength, rules) = SceneSettings();
            var graph = new ShelfLinkGraph(data, db, limit, minLength, rules, CancellationToken.None);
            Assert.IsTrue(graph.SetGoal("지구"));

            List<ShelfTextPart> Links(string id)
            {
                Assert.IsTrue(data.TryGetSenseRow(id, out var row), id);
                return graph.Doors(row);
            }

            string Marked(string id)
            {
                Assert.IsTrue(data.TryGetSenseRow(id, out var row), id);
                return string.Concat(ShelfLinkText.Split(graph.ReadingDefinition(row), graph.Doors(row))
                    .Select(p => p.IsLink ? $"[{p.Text}]" : p.Text));
            }

            // One-letter headwords stay in the text, unlinked; their DB records are still read.
            Assert.AreEqual("해, 달, 별 들이 [운행]하는 도수.", Marked("338542"));
            CollectionAssert.AreEqual(new[] { "운행" }, Links("338542").Select(p => p.Target));
            CollectionAssert.IsSubsetOf(new[] { "해", "달", "별", "운행" }, graph.DatabaseLinks("338542").Select(p => p.Target).ToList());
            Assert.IsTrue(graph.CanTravel("경기", "운행"));
            foreach (var target in new[] { "해", "달", "별" })
                Assert.IsFalse(graph.CanTravel("경기", target), target);

            StringAssert.StartsWith("[말과]의 [포유류]. 말과 비슷한데", Marked("126851"));
            StringAssert.Contains("귀가 길다", Marked("126851"));
            var donkey = Links("126851").Select(p => p.Target).ToList();
            CollectionAssert.IsSubsetOf(new[] { "말과", "포유류" }, donkey);
            CollectionAssert.DoesNotContain(donkey, "말");
            CollectionAssert.DoesNotContain(donkey, "귀");
            CollectionAssert.IsSubsetOf(new[] { "말", "귀" }, graph.DatabaseLinks("126851").Select(p => p.Target).ToList());

            Assert.AreEqual("신경과", Links("438835").Single(p => p.Start == 12).Target);
            Assert.AreEqual(3, Links("438835").Single(p => p.Start == 12).Length);
            CollectionAssert.AreEqual(new[] { "바닷물" }, Links("316979").Select(p => p.Target));   // 판 (one letter) stays text
            CollectionAssert.AreEqual(new[] { "바다" }, Links("532114").Select(p => p.Target));
            CollectionAssert.Contains(Links("1423").Select(p => p.Target).ToList(), "공기");       // 대기 → 공기
            CollectionAssert.Contains(Links("17642").Select(p => p.Target).ToList(), "어머니");    // 엄마 → 어머니

            Assert.IsFalse(graph.IsCandidate("장"));
            Assert.IsFalse(graph.IsValidDoor("장"));

            Assert.IsTrue(graph.PickChallenge(new System.Random(20261126), 10, CancellationToken.None,
                out var goal, out var start, out var moves, out _));
            Assert.GreaterOrEqual(moves, 10);
            Assert.GreaterOrEqual(start.Length, Math.Max(minLength, ShelfLinkGraph.RoomMinLength));
            Assert.GreaterOrEqual(goal.Length, Math.Max(minLength, ShelfLinkGraph.RoomMinLength));
            Assert.AreEqual(moves, graph.DistanceToGoal(start));

            var checkedRooms = 0;
            var screenExits = new Dictionary<string, HashSet<string>>();
            for (var rank = 0; rank < data.WordCount; rank++)
            {
                var word = data.GetWord(rank);
                if (!graph.IsCandidate(word)) continue;
                Assert.GreaterOrEqual(data.GetWordLength(rank), ShelfLinkGraph.RoomMinLength, word);
                var exits = new HashSet<string>();
                foreach (var row in data.SenseRowsOf(rank))
                {
                    var body = graph.ReadingDefinition(row);
                    if (body == null) continue;
                    var parts = ShelfLinkText.Split(body, graph.Doors(row));
                    Assert.AreEqual(body, string.Concat(parts.Select(p => p.Text)));
                    var seen = new HashSet<string>();
                    foreach (var part in parts)
                    {
                        Assert.AreEqual(body.Substring(part.Start, part.Length), part.Text);
                        if (!part.IsLink) continue;
                        Assert.AreNotEqual(word, part.Target);
                        Assert.IsTrue(graph.IsCandidate(part.Target), $"{word} → {part.Target}: 방 후보가 아닌 목적지");
                        Assert.IsTrue(graph.CanTravel(word, part.Target), $"{word} → {part.Target}: 읽기 화면 링크와 이동 판정 불일치");
                        Assert.IsTrue(seen.Add(part.Target), "한 뜻풀이의 중복 목적지");
                        exits.Add(part.Target);
                    }
                }
                CollectionAssert.AreEquivalent(graph.ValidExits(word), exits, word);
                screenExits.Add(word, exits);
                checkedRooms++;
            }
            Assert.Greater(checkedRooms, 10000);
            Debug.Log($"[링크 DB] {data.SenseCount:N0}개 뜻풀이 로드, {checkedRooms:N0}개 방의 읽기 화면과 그래프 출구 일치");

            var distances = new Dictionary<string, int> { [start] = 0 };
            var queue = new Queue<string>();
            queue.Enqueue(start);
            while (queue.Count != 0)
            {
                var word = queue.Dequeue();
                if (word == goal) break;
                foreach (var target in screenExits[word])
                    if (distances.TryAdd(target, distances[word] + 1)) queue.Enqueue(target);
            }
            Assert.AreEqual(moves, distances[goal]);
            Debug.Log($"[링크 DB] 무작위 도전 {start} → {goal}, 최단 {moves}회");
        }

        static (int, int, List<ShelfLinkGraph.RoomRule>) SceneSettings()
        {
            var scene = EditorSceneManager.OpenPreviewScene(ScenePath);
            try
            {
                var demo = scene.GetRootGameObjects().SelectMany(o => o.GetComponentsInChildren<ShelfCorridorDemo>(true)).Single();
                using var settings = new SerializedObject(demo);
                var rooms = settings.FindProperty("rooms");
                var rules = new List<ShelfLinkGraph.RoomRule>();
                for (var i = 0; i < rooms.arraySize; i++)
                {
                    var room = rooms.GetArrayElementAtIndex(i);
                    var word = room.FindPropertyRelative("word").stringValue;
                    var senseId = room.FindPropertyRelative("senseId").stringValue;
                    var links = room.FindPropertyRelative("links");
                    var selection = Enumerable.Range(0, links.arraySize).Select(j => links.GetArrayElementAtIndex(j).stringValue)
                        .Where(link => !string.IsNullOrWhiteSpace(link)).Select(link => link.Trim()).ToHashSet();
                    if (!string.IsNullOrWhiteSpace(word))
                        rules.Add(new ShelfLinkGraph.RoomRule
                        {
                            Word = word.Trim(),
                            SenseId = string.IsNullOrWhiteSpace(senseId) ? null : senseId.Trim(),
                            Links = selection.Count == 0 ? null : selection,
                        });
                }
                return (settings.FindProperty("vocabularyLimit").intValue, settings.FindProperty("challengeMinLength").intValue, rules);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
