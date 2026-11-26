using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// The one physical reading room of the scene (OctagonalReadingRoom.prefab): parquet floor, eight carved bookcases
    /// with their books, lamps and ceiling, all fixed. Room-local frame: center of the floor at the origin (floor top
    /// at Y 0), side 0 toward +Z (faced on arrival), sides clockwise seen from above (side 1 toward +X+Z).
    /// Furnish shows one logical room (ShelfWordRoom) in it: the headword on the title plates and the desks and
    /// lecterns of ShelfFurniturePlan — one desk at `deskRadius` in front of each used side, its lecterns side by side
    /// on the top (ShelfDeskFit) — and one open book on each lectern's panel, the variants in turn by document order
    /// (ShelfFurniturePlan.BookVariant). Desks, lecterns and books (per variant) are pooled separately under
    /// `furniture`: instances made earlier are used first, only the missing ones are created, and the rest are unbound
    /// and turned off. The same room data always gives the same placement and the same books. The room object itself
    /// is never created or destroyed at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfRoomView : MonoBehaviour
    {
        [Tooltip("방 중앙의 바닥(이 오브젝트나 자식에 바닥 Collider). 플레이어를 이 위치 아래 바닥에 세웁니다.")]
        [SerializeField] Transform floor;
        [Tooltip("책상·독서대 인스턴스를 담는 곳")]
        [SerializeField] Transform furniture;
        [Tooltip("방 표제어를 쓰는 글자(책장 윗부분 장식띠)")]
        [SerializeField] TMP_Text[] titles = new TMP_Text[0];
        [Tooltip("방 중앙에서 책상 중심까지의 거리(m, 원본 모델 기준)")]
        [Min(1f)] [SerializeField] float deskRadius = 3.76f;
        [Tooltip("방 중앙에서 책장 안쪽 면까지의 거리(m, 원본 모델 기준)")]
        [Min(1f)] [SerializeField] float innerApothem = 4.915f;

        readonly List<ShelfDesk> desks = new List<ShelfDesk>();          // pool: [0, activeDesks) in use
        readonly List<ShelfLectern> lecterns = new List<ShelfLectern>(); // pool: [0, activeLecterns) in use
        readonly List<ShelfLectern> shown = new List<ShelfLectern>();    // in use, by document index
        readonly List<List<ShelfBook>> books = new List<List<ShelfBook>>();   // pool per variant
        int activeDesks;
        int activeLecterns;
        bool collected;

        /// <summary>The logical room shown now (null before the first Furnish).</summary>
        public ShelfWordRoom Room { get; private set; }
        public Transform Floor => floor;
        public float DeskRadius => deskRadius;
        public float InnerApothem => innerApothem;
        /// <summary>Lecterns in use, in document order (index i holds Documents[i]).</summary>
        public IReadOnlyList<ShelfLectern> Lecterns => shown;
        public int ActiveDeskCount => activeDesks;
        /// <summary>Desk and lectern instances made so far (in use or put away).</summary>
        public int DeskPoolSize => desks.Count;
        public int LecternPoolSize => lecterns.Count;
        /// <summary>Book instances made so far of `variant` (in use or put away).</summary>
        public int BookPoolSize(int variant) => variant < books.Count ? books[variant].Count : 0;
        /// <summary>Desk `i` in use (0 ≤ i &lt; ActiveDeskCount), in plan order.</summary>
        public ShelfDesk ActiveDesk(int i) => desks[i];

        public bool IsComplete => floor != null && furniture != null;

        /// <summary>
        /// Shows `room` with `deskPrefab`, `lecternPrefab` and `bookPrefabs` (one per variant) instances placed by its
        /// plan. False (and the room as it was before the call) when a reference is missing.
        /// </summary>
        public bool Furnish(ShelfWordRoom room, ShelfDesk deskPrefab, ShelfLectern lecternPrefab, IReadOnlyList<ShelfBook> bookPrefabs, ShelfLayout layout, out string failure)
        {
            if (!IsComplete)
            {
                failure = "ShelfRoomView의 floor·furniture 참조가 비어 있습니다";
                return false;
            }
            if (room == null)
            {
                failure = "방 데이터가 없습니다";
                return false;
            }
            var plan = ShelfFurniturePlan.Create(room.Documents.Count);
            if (plan.Count > 0 && (deskPrefab == null || lecternPrefab == null || !HasBooks(bookPrefabs)))
            {
                failure = "책상·독서대·책 프리팹 참조가 비어 있습니다";
                return false;
            }
            CollectExisting();
            var variants = bookPrefabs?.Count ?? 0;
            while (books.Count < variants) books.Add(new List<ShelfBook>());
            var usedBooks = new int[variants];

            var needDesks = plan.Count;
            var needLecterns = room.Documents.Count;
            while (desks.Count < needDesks) desks.Add(Create(deskPrefab, $"Desk_{desks.Count:00}"));
            while (lecterns.Count < needLecterns) lecterns.Add(Create(lecternPrefab, $"Lectern_{lecterns.Count:00}"));

            shown.Clear();
            var maxTop = 0f;
            for (var d = 0; d < plan.Count; d++)
            {
                var p = plan[d];
                var desk = desks[d];
                if (maxTop <= 0f) maxTop = layout.MaxTopWidth(deskRadius, desk.TopDepth);
                var dir = SideDirection(p.Side);
                var right = new Vector3(dir.z, 0f, -dir.x);                  // seen from the center facing the side
                desk.transform.SetLocalPositionAndRotation(dir * deskRadius, Quaternion.LookRotation(dir));
                var lectern = lecterns[p.First];
                var fit = ShelfDeskFit.Compute(p.Count, desk.BaseTopWidth, maxTop, lectern.BaseWidth, layout.edgeMargin, layout.minLecternGap);
                desk.SetTopWidth(fit.TopWidth);
                if (!desk.gameObject.activeSelf) desk.gameObject.SetActive(true);

                var top = transform.InverseTransformPoint(desk.Top.position);
                for (var j = 0; j < p.Count; j++)
                {
                    var i = p.First + j;
                    var l = lecterns[i];
                    l.transform.SetLocalPositionAndRotation(top + right * fit.Offset(j), Quaternion.LookRotation(dir));
                    l.transform.localScale = Vector3.one * fit.LecternScale;
                    var v = ShelfFurniturePlan.BookVariant(i, variants);
                    var pool = books[v];
                    if (usedBooks[v] == pool.Count) pool.Add(Create(bookPrefabs[v], $"Book_{v + 1:00}_{pool.Count:00}"));
                    var book = pool[usedBooks[v]++];
                    l.Bind(room.Documents[i], i, book);
                    book.AttachTo(l);
                    if (!l.gameObject.activeSelf) l.gameObject.SetActive(true);
                    shown.Add(l);
                }
            }
            for (var d = plan.Count; d < desks.Count; d++)
                if (desks[d].gameObject.activeSelf) desks[d].gameObject.SetActive(false);
            for (var i = needLecterns; i < lecterns.Count; i++)
            {
                lecterns[i].Unbind();
                if (lecterns[i].gameObject.activeSelf) lecterns[i].gameObject.SetActive(false);
            }
            for (var v = 0; v < books.Count; v++)
                for (var k = v < usedBooks.Length ? usedBooks[v] : 0; k < books[v].Count; k++) books[v][k].PutAway(furniture);
            activeDesks = needDesks;
            activeLecterns = needLecterns;

            foreach (var t in titles)
                if (t != null) t.text = room.Word;
            Room = room;
            Physics.SyncTransforms();                // moved and re-enabled colliders count from now on
            failure = null;
            return true;
        }

        static bool HasBooks(IReadOnlyList<ShelfBook> prefabs)
        {
            if (prefabs == null || prefabs.Count == 0) return false;
            foreach (var b in prefabs)
                if (b == null) return false;
            return true;
        }

        /// <summary>Unit vector (room-local) from the center toward side `s`.</summary>
        public static Vector3 SideDirection(int s) => Quaternion.Euler(0f, s * 45f, 0f) * Vector3.forward;

        /// <summary>Instances already under `furniture` (e.g. placed in the editor) join the pools once, put away.</summary>
        void CollectExisting()
        {
            if (collected) return;
            collected = true;
            foreach (Transform child in furniture)
            {
                if (child.TryGetComponent(out ShelfDesk desk))
                {
                    desks.Add(desk);
                    desk.gameObject.SetActive(false);
                }
                else if (child.TryGetComponent(out ShelfLectern lectern))
                {
                    lectern.Unbind();
                    lecterns.Add(lectern);
                    lectern.gameObject.SetActive(false);
                }
                else if (child.TryGetComponent(out ShelfBook book))
                {
                    while (books.Count <= book.Variant) books.Add(new List<ShelfBook>());
                    books[book.Variant].Add(book);
                    book.gameObject.SetActive(false);
                }
            }
        }

        T Create<T>(T prefab, string name) where T : Component
        {
            var instance = Instantiate(prefab, furniture, false);
            instance.name = name;
            return instance;
        }

#if UNITY_EDITOR
        /// <summary>Editor setup only: wires the prefab's parts and measured radii.</summary>
        public void EditorSetup(Transform floor, Transform furniture, TMP_Text[] titles, float deskRadius, float innerApothem)
        {
            this.floor = floor;
            this.furniture = furniture;
            this.titles = titles;
            this.deskRadius = deskRadius;
            this.innerApothem = innerApothem;
        }
#endif
    }
}
