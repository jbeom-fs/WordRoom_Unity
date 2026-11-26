using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WordRoom.Shelf;

namespace WordRoom.Data.Tests
{
    /// <summary>
    /// Reading room furniture: the logical plan (sides per document count, book per lectern), the desk-top fit, and the
    /// real prefabs furnished in one fixed room instance — pooling of desks, lecterns and books, one desk per side, no
    /// overlap, walkways, books cycling through the eight variants on the stand panels with no text anywhere on the
    /// furniture, and that aiming at each book from its reading area finds that book holding its own document while the
    /// gap between two books on one desk finds none.
    /// </summary>
    public sealed class ShelfReadingRoomTests
    {
        const string RoomPrefab = "Assets/Prefabs/Shelf/OctagonalReadingRoom.prefab";
        const string DeskPrefab = "Assets/Prefabs/Shelf/ReadingDesk.prefab";
        const string StandPrefab = "Assets/Prefabs/Shelf/ReadingStand.prefab";
        const string BookPrefabFormat = "Assets/Prefabs/Shelf/Books/OpenBook_{0:00}.prefab";
        const int BookVariants = 8;

        static ShelfWordRoom FakeRoom(string word, int count)
        {
            var docs = new List<ShelfDocument>();
            for (var i = 0; i < count; i++)
            {
                var text = $"{word} 뜻 {i + 1}의 풀이 링크.";
                var parts = new List<ShelfTextPart> { new ShelfTextPart(text.Substring(0, text.Length - 3), 0, null), new ShelfTextPart("링크", text.Length - 3, "링크") };
                docs.Add(new ShelfDocument(word, $"E{i / 3}", "명사", count > 1 ? i + 1 : 0, $"뜻{i + 1}", new ShelfSense($"{word}-{i}", text, parts)));
            }
            return new ShelfWordRoom(word, docs);
        }

        // ---- plan ------------------------------------------------------------------------------------------------

        [Test]
        public void Plan_FollowsDocumentCount()
        {
            Assert.AreEqual(0, ShelfFurniturePlan.Create(0).Count);
            CollectionAssert.AreEqual(new[] { 0 }, ShelfFurniturePlan.Create(1).Select(d => d.Side));
            CollectionAssert.AreEqual(new[] { 0, 2, 5 }, ShelfFurniturePlan.Create(3).Select(d => d.Side));
            Assert.IsTrue(ShelfFurniturePlan.Create(3).All(d => d.Count == 1));
            CollectionAssert.AreEqual(Enumerable.Range(0, 8), ShelfFurniturePlan.Create(8).Select(d => d.Side));
            Assert.IsTrue(ShelfFurniturePlan.Create(8).All(d => d.Count == 1));

            var ten = ShelfFurniturePlan.Create(10);
            Assert.AreEqual(8, ten.Count);
            Assert.AreEqual(2, ten.Count(d => d.Count == 2));
            Assert.AreEqual(10, ten.Sum(d => d.Count));
        }

        [Test]
        public void Plan_OneDeskPerSide_ContiguousAndDeterministic()
        {
            for (var n = 0; n <= 60; n++)
            {
                var a = ShelfFurniturePlan.Create(n);
                var b = ShelfFurniturePlan.Create(n);
                Assert.AreEqual(Mathf.Min(n, 8), a.Count, $"n={n}");
                Assert.AreEqual(a.Select(d => d.Side).Distinct().Count(), a.Count, $"n={n}: two desks on one side");
                var next = 0;
                for (var i = 0; i < a.Count; i++)
                {
                    Assert.AreEqual(next, a[i].First, $"n={n}");
                    Assert.Greater(a[i].Count, 0);
                    next += a[i].Count;
                    Assert.AreEqual(a[i].Side, b[i].Side);
                    Assert.AreEqual(a[i].Count, b[i].Count);
                }
                Assert.AreEqual(n, next);
                if (n > 0) Assert.LessOrEqual(ShelfFurniturePlan.MaxPerDesk(n) - a.Min(d => d.Count), 1, $"n={n}: uneven sharing");
            }
        }

        [Test]
        public void Fit_NeverOverlapsOrLeavesTheTop()
        {
            const float baseTop = 1.64f, maxTop = 1.99f, width = 0.692f, edge = 0.06f, gap = 0.06f;
            for (var k = 1; k <= 7; k++)
            {
                var fit = ShelfDeskFit.Compute(k, baseTop, maxTop, width, edge, gap);
                Assert.GreaterOrEqual(fit.TopWidth, baseTop - 1e-4f);
                Assert.LessOrEqual(fit.TopWidth, maxTop + 1e-4f);
                Assert.Greater(fit.LecternScale, 0f);
                Assert.LessOrEqual(fit.LecternScale, 1f);
                var w = width * fit.LecternScale;
                Assert.LessOrEqual(Mathf.Abs(fit.Offset(0)) + w * 0.5f, fit.TopWidth * 0.5f - edge + 1e-4f, $"k={k}: off the top");
                Assert.AreEqual(-fit.Offset(0), fit.Offset(k - 1), 1e-4f, $"k={k}: not centered");
                if (k > 1) Assert.GreaterOrEqual(fit.Gap(width), gap - 1e-4f, $"k={k}: overlap");
            }
            Assert.AreEqual(1f, ShelfDeskFit.Compute(2, baseTop, maxTop, width, edge, gap).LecternScale);   // two fit the normal top
            Assert.AreEqual(baseTop, ShelfDeskFit.Compute(2, baseTop, maxTop, width, edge, gap).TopWidth, 1e-4f);
        }

        // ---- prefabs in one room ---------------------------------------------------------------------------------------

        [Test]
        public void Room_FurnishesRepeatedly_WithOneRoomAndPooledFurniture()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);   // before loading: it unloads unused assets
            var roomAsset = AssetDatabase.LoadAssetAtPath<ShelfRoomView>(RoomPrefab);
            var desk = AssetDatabase.LoadAssetAtPath<ShelfDesk>(DeskPrefab);
            var stand = AssetDatabase.LoadAssetAtPath<ShelfLectern>(StandPrefab);
            var bookPrefabs = new ShelfBook[BookVariants];
            for (var v = 0; v < BookVariants; v++) bookPrefabs[v] = AssetDatabase.LoadAssetAtPath<ShelfBook>(string.Format(BookPrefabFormat, v + 1));
            if (roomAsset == null || desk == null || stand == null || bookPrefabs.Any(b => b == null))
                Assert.Inconclusive("독서실·책 프리팹이 아직 없습니다(WordRoom/Shelf/독서실 프리팹·씬 구성 실행 필요)");
            for (var v = 0; v < BookVariants; v++) Assert.AreEqual(v, bookPrefabs[v].Variant, $"책 프리팹 {v + 1}의 종류 번호");

            var room = ((GameObject)PrefabUtility.InstantiatePrefab(roomAsset.gameObject, scene)).GetComponent<ShelfRoomView>();
            var layout = new ShelfLayout();
            var maxTop = layout.MaxTopWidth(room.DeskRadius, desk.TopDepth);
            Assert.Greater(maxTop, desk.BaseTopWidth, "책상 폭을 넓힐 여유가 없습니다");

            var counts = new[] { 3, 10, 0, 8, 46, 10, 1, 3 };
            var maxSeen = 0;
            var firstPlacement = new Dictionary<int, List<Vector3>>();
            foreach (var n in counts)
            {
                var data = FakeRoom($"방{n}", n);
                Assert.IsTrue(room.Furnish(data, desk, stand, bookPrefabs, layout, out var why), why);
                maxSeen = Mathf.Max(maxSeen, n);

                Assert.AreEqual(1, Object.FindObjectsByType<ShelfRoomView>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, "방 인스턴스가 하나가 아닙니다");
                var plan = ShelfFurniturePlan.Create(n);
                Assert.AreEqual(plan.Count, room.ActiveDeskCount, $"n={n}");
                Assert.LessOrEqual(room.DeskPoolSize, 8);
                Assert.AreEqual(Mathf.Max(maxSeen > 8 ? 8 : maxSeen, 0), room.DeskPoolSize, $"n={n}: 책상을 필요 이상 만들었습니다");
                Assert.AreEqual(maxSeen, room.LecternPoolSize, $"n={n}: 독서대를 필요 이상 만들었습니다");
                Assert.AreEqual(n, room.Lecterns.Count);
                Assert.AreEqual(data.Word, room.Room.Word);

                var active = Object.FindObjectsByType<ShelfLectern>(FindObjectsSortMode.None);
                Assert.AreEqual(n, active.Length, $"n={n}: 켜진 독서대 수");
                foreach (var l in Object.FindObjectsByType<ShelfLectern>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!l.gameObject.activeSelf)
                    {
                        Assert.IsNull(l.Document, "치운 독서대에 이전 뜻풀이가 남았습니다");
                        Assert.IsNull(l.Book, "치운 독서대에 책이 남았습니다");
                    }

                // Books: one per lectern, pooled per variant, nothing extra made; put-away books hold nothing.
                Assert.AreEqual(n, Object.FindObjectsByType<ShelfBook>(FindObjectsSortMode.None).Length, $"n={n}: 켜진 책 수");
                for (var v = 0; v < BookVariants; v++)
                    Assert.AreEqual(maxSeen > v ? (maxSeen - 1 - v) / BookVariants + 1 : 0, room.BookPoolSize(v), $"n={n}: 책 {v + 1}을 필요 이상 만들었습니다");
                foreach (var b in Object.FindObjectsByType<ShelfBook>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!b.gameObject.activeSelf) Assert.IsNull(b.Holder, "치운 책이 독서대를 가리킵니다");
                // No title or preview anywhere on stands and books: the only texts are the room titles.
                foreach (var t in room.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    Assert.IsNull(t.GetComponentInParent<ShelfLectern>(true), $"독서대에 글자 '{t.name}'가 있습니다");
                    Assert.IsNull(t.GetComponentInParent<ShelfBook>(true), $"책에 글자 '{t.name}'가 있습니다");
                    Assert.AreEqual(data.Word, t.text, "방 제목 외의 글자가 있습니다");
                }

                // One desk per used side, at its side.
                var sides = new HashSet<int>();
                for (var d = 0; d < room.ActiveDeskCount; d++)
                {
                    var dk = room.ActiveDesk(d);
                    var local = room.transform.InverseTransformPoint(dk.transform.position);
                    var side = Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, 360f) / 45f) % 8;
                    Assert.IsTrue(sides.Add(side), $"n={n}: 한 면에 책상 둘");
                    Assert.AreEqual(plan[d].Side, side);
                    Assert.LessOrEqual(dk.TopWidth, maxTop + 1e-3f, "책상이 보행 폭을 침범합니다");
                }

                var positions = new List<Vector3>();
                for (var i = 0; i < n; i++)
                {
                    var l = room.Lecterns[i];
                    Assert.AreSame(data.Documents[i], l.Document, $"n={n} 독서대 {i}");
                    Assert.AreEqual(i, l.Index);
                    var book = l.Book;
                    Assert.IsNotNull(book, $"n={n} 독서대 {i}: 책 없음");
                    Assert.IsTrue(book.gameObject.activeInHierarchy);
                    Assert.AreEqual(ShelfFurniturePlan.BookVariant(i, BookVariants), book.Variant, $"n={n} 독서대 {i}: 책 종류");
                    Assert.AreSame(l, book.Holder);
                    Assert.AreSame(l.BookAnchor, book.transform.parent, "책이 독서대 판 위에 있지 않습니다");
                    Assert.Less(book.transform.localPosition.magnitude, 1e-5f);
                    Assert.Less(Quaternion.Angle(Quaternion.identity, book.transform.localRotation), 1e-3f);
                    Assert.AreEqual(l.transform.lossyScale.x, book.transform.lossyScale.x, 1e-4f, "책이 독서대와 같은 비율로 줄지 않았습니다");
                    positions.Add(l.transform.position);
                }
                if (firstPlacement.TryGetValue(n, out var before))
                    for (var i = 0; i < n; i++) Assert.Less((before[i] - positions[i]).magnitude, 1e-4f, $"n={n}: 같은 방 데이터의 배치가 다릅니다");
                else firstPlacement[n] = positions;

                AssertTops(room, plan, layout);
                AssertAim(room, layout, n);
                AssertCenterFree(room);
            }
            Object.DestroyImmediate(room.gameObject);
        }

        /// <summary>Lecterns of one desk lie on its top, inside the edge margins, without overlapping.</summary>
        static void AssertTops(ShelfRoomView room, List<ShelfDeskPlan> plan, ShelfLayout layout)
        {
            for (var d = 0; d < plan.Count; d++)
            {
                var desk = room.ActiveDesk(d);
                var xs = new List<(float min, float max)>();
                for (var j = 0; j < plan[d].Count; j++)
                {
                    var l = room.Lecterns[plan[d].First + j];
                    var p = desk.transform.InverseTransformPoint(l.transform.position);
                    var half = l.BaseWidth * l.transform.localScale.x * 0.5f;
                    Assert.AreEqual(desk.Top.localPosition.y, p.y, 1e-3f, "독서대가 상판 높이에 있지 않습니다");
                    Assert.LessOrEqual(Mathf.Abs(p.x) + half, desk.TopWidth * 0.5f - layout.edgeMargin + 1e-3f, "독서대가 상판을 벗어났습니다");
                    xs.Add((p.x - half, p.x + half));
                }
                for (var j = 1; j < xs.Count; j++) Assert.GreaterOrEqual(xs[j].min - xs[j - 1].max, layout.minLecternGap - 1e-3f, "독서대가 겹칩니다");
            }
        }

        /// <summary>
        /// From each lectern's reading area the center ray at its book finds that book on that lectern (E opens its
        /// document); a ray at the gap between two neighboring books of one desk finds no book.
        /// </summary>
        static void AssertAim(ShelfRoomView room, ShelfLayout layout, int n)
        {
            for (var i = 0; i < n; i++)
            {
                var l = room.Lecterns[i];
                var forward = l.transform.forward;
                forward.y = 0f;
                forward.Normalize();
                var feet = new Vector3(l.transform.position.x, 0f, l.transform.position.z) - forward * 0.95f;
                Assert.IsTrue(l.IsInReach(feet, layout.readRange), $"독서대 {i}: 읽기 범위 밖");
                var eye = feet + Vector3.up * 1.6f;
                var target = l.Book.Aim.bounds.center;
                var dist = (target - eye).magnitude;
                Assert.LessOrEqual(dist, layout.interactDistance, $"독서대 {i}: 조준 거리 초과");
                Assert.IsTrue(Physics.Raycast(eye, (target - eye).normalized, out var hit, layout.interactDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore), $"독서대 {i}: 조준선에 아무것도 없습니다");
                var aimed = hit.collider.GetComponent<ShelfBook>();
                Assert.AreSame(l.Book, aimed, $"독서대 {i}: 책을 조준하면 '{hit.collider.name}'가 잡힙니다");
                Assert.AreSame(l, aimed.Holder);
                Assert.AreSame(room.Room.Documents[i], aimed.Holder.Document);

                if (i + 1 < n && room.Lecterns[i + 1].transform.position != l.transform.position &&
                    Vector3.Dot(room.Lecterns[i + 1].transform.forward, l.transform.forward) > 0.999f)       // same desk
                {
                    var gap = (target + room.Lecterns[i + 1].Book.Aim.bounds.center) * 0.5f;
                    if (Physics.Raycast(eye, (gap - eye).normalized, out var gapHit, layout.interactDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                        Assert.IsNull(gapHit.collider.GetComponent<ShelfBook>(), $"독서대 {i}와 {i + 1} 사이를 조준했는데 책 '{gapHit.collider.name}'가 잡힙니다");
                }
            }
        }

        static void AssertCenterFree(ShelfRoomView room)
        {
            var c = room.Floor.position;
            foreach (var col in Physics.OverlapCapsule(c + Vector3.up * 0.4f, c + Vector3.up * 1.5f, 0.3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                Assert.IsTrue(col.transform.IsChildOf(room.Floor), $"방 중앙에 '{col.name}'가 있습니다");
        }
    }
}
