using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Builds one octagonal reading room from primitives (frame: see ShelfLayout). One lectern per document of the
    /// headword, in document order: with one document it stands in front of side 0 (straight ahead on arrival); with
    /// more, document i goes to side floor(i·8/n), so up to eight documents spread over the sides and more share
    /// sides as evenly as possible. Lecterns of one side stand side by side, left to right seen from the center, and
    /// the room widens (ShelfLayout.ApothemFor) so a side holds them with walking room at its corners. Each lectern
    /// carries a parchment on its slanted top; its short title is shown only in the aim hint and the reading screen.
    /// The headword is written on the upper part of the front and back walls. A room with no document (a goal room
    /// without links) is the octagon with its titles only. The same room always gives the same
    /// layout, so a revisited room looks the same. One solid box collider per lectern serves aiming and blocking.
    /// </summary>
    internal sealed class ShelfRoomBuilder
    {
        const int Sides = 8;
        static readonly Color WallColor = new Color32(0xD9, 0xCF, 0xBC, 0xFF);
        static readonly Color FloorColor = new Color32(0x6B, 0x63, 0x5A, 0xFF);
        static readonly Color WoodColor = new Color32(0x4A, 0x35, 0x26, 0xFF);
        static readonly Color TrimColor = new Color32(0xB8, 0x8A, 0x3E, 0xFF);
        static readonly Color ParchmentColor = new Color32(0xE8, 0xDA, 0xB8, 0xFF);

        const float TextLift = 0.005f;           // text just in front of its surface
        const float DeskTilt = -20f;             // reader-side edge lower

        readonly ShelfLayout layout;
        readonly ShelfMaterials materials;
        readonly TMP_FontAsset font;
        readonly Dictionary<Color, Material> tinted = new Dictionary<Color, Material>();

        public ShelfRoomBuilder(ShelfLayout layout, ShelfMaterials materials, TMP_FontAsset font)
        {
            this.layout = layout;
            this.materials = materials;
            this.font = font;
        }

        /// <summary>Side of document `i` of `n` (0 = front, clockwise seen from above).</summary>
        public static int SideOf(int i, int n) => n <= 1 ? 0 : i * Sides / n;

        /// <summary>Largest number of lecterns on one side for `n` documents.</summary>
        public static int PerSide(int n)
        {
            if (n <= Sides) return 1;
            var counts = new int[Sides];
            var max = 0;
            for (var i = 0; i < n; i++) max = Mathf.Max(max, ++counts[SideOf(i, n)]);
            return max;
        }

        /// <summary>Unit vector from the center toward side `s`.</summary>
        static Vector3 SideDirection(int s) => Quaternion.Euler(0f, s * 45f, 0f) * Vector3.forward;

        /// <summary>Apothem of the room for `n` documents.</summary>
        public float ApothemOf(int n) => layout.ApothemFor(PerSide(n));

        public ShelfRoomView Build(ShelfWordRoom room, Vector3 position)
        {
            var L = layout;
            var n = room.Documents.Count;
            var a = ApothemOf(n);
            var t = L.wallThickness;

            var root = new GameObject($"ShelfRoom_{room.Word}").transform;
            root.SetPositionAndRotation(position, Quaternion.identity);
            var view = root.gameObject.AddComponent<ShelfRoomView>();

            var outer = a + t;
            var floor = Box(root, "Floor", new Vector3(0f, -0.1f, 0f), new Vector3(2f * outer, 0.2f, 2f * outer), materials.floor, FloorColor, true);
            var wallLength = 2f * outer * Mathf.Tan(ShelfLayout.HalfStep * Mathf.Deg2Rad) + 0.02f;   // corners closed
            for (var s = 0; s < Sides; s++)
            {
                var dir = SideDirection(s);
                var wall = Box(root, $"Wall_{s}", dir * (a + t * 0.5f) + Vector3.up * (L.wallHeight * 0.5f),
                    new Vector3(wallLength, L.wallHeight, t), materials.wall, WallColor, true);
                wall.localRotation = Quaternion.LookRotation(dir);
            }
            BuildTitle(root, room.Word, 0, a);
            BuildTitle(root, room.Word, 4, a);

            // Lecterns per side, in document order.
            var bySide = new List<int>[Sides];
            for (var i = 0; i < n; i++) (bySide[SideOf(i, n)] ??= new List<int>()).Add(i);
            var lecterns = new ShelfLectern[n];
            for (var s = 0; s < Sides; s++)
            {
                if (bySide[s] == null) continue;
                var dir = SideDirection(s);
                var right = new Vector3(dir.z, 0f, -dir.x);                  // seen from the center facing the side
                var k = bySide[s].Count;
                for (var j = 0; j < k; j++)
                {
                    var i = bySide[s][j];
                    var offset = (j - (k - 1) * 0.5f) * L.lecternSpacing;
                    var at = dir * (a - L.lecternInset) + right * offset;
                    lecterns[i] = BuildLectern(root, room.Documents[i], i, at, dir);
                }
            }

            view.Init(room, a, floor, lecterns);
            Physics.SyncTransforms();                 // the new colliders were moved after creation
            return view;
        }

        // ---- parts -----------------------------------------------------------------------------------------------

        /// <summary>
        /// A lectern at `at` (floor), its local +Z pointing from the reader toward the wall: base, post, slanted desk with
        /// a brass lip and the parchment on it. One solid box collider on the root covers it all.
        /// </summary>
        ShelfLectern BuildLectern(Transform root, ShelfDocument document, int index, Vector3 at, Vector3 outward)
        {
            var L = layout;
            var holder = new GameObject($"Lectern_{index}").transform;
            holder.SetParent(root, false);
            holder.localPosition = at;
            holder.localRotation = Quaternion.LookRotation(outward);

            var w = L.lecternWidth;
            var h = L.lecternHeight;
            Box(holder, "Base", new Vector3(0f, 0.03f, 0f), new Vector3(w * 0.65f, 0.06f, 0.45f), materials.lectern, WoodColor, false);
            Box(holder, "Post", new Vector3(0f, h * 0.5f, 0.04f), new Vector3(0.12f, h, 0.12f), materials.lectern, WoodColor, false);
            var desk = new GameObject("Desk").transform;
            desk.SetParent(holder, false);
            desk.localPosition = new Vector3(0f, h, 0f);
            desk.localRotation = Quaternion.Euler(DeskTilt, 0f, 0f);
            Box(desk, "Top", Vector3.zero, new Vector3(w, 0.05f, 0.56f), materials.lectern, WoodColor, false);
            Box(desk, "Lip", new Vector3(0f, 0.04f, -0.27f), new Vector3(w, 0.04f, 0.025f), materials.trim, TrimColor, false);
            Box(desk, "Parchment", new Vector3(0f, 0.03f, 0.02f), new Vector3(w * 0.78f, 0.008f, 0.44f), materials.parchment, ParchmentColor, false);

            var aim = holder.gameObject.AddComponent<BoxCollider>();
            aim.center = new Vector3(0f, (h + 0.2f) * 0.5f, 0f);
            aim.size = new Vector3(w + 0.04f, h + 0.2f, 0.62f);
            var lectern = holder.gameObject.AddComponent<ShelfLectern>();
            lectern.Init(document, index);
            return lectern;
        }

        /// <summary>The headword on the upper part of side `side`'s wall, facing the center.</summary>
        void BuildTitle(Transform root, string word, int side, float a)
        {
            var L = layout;
            var dir = SideDirection(side);
            var holder = new GameObject($"Title_{side}").transform;
            holder.SetParent(root, false);
            holder.localPosition = dir * (a - TextLift);
            holder.localRotation = Quaternion.LookRotation(dir);
            var chord = 2f * a * Mathf.Tan(ShelfLayout.HalfStep * Mathf.Deg2Rad);
            var title = CreateText(holder, "Word", new Vector3(0f, L.titleHeight, 0f), new Vector2(chord - 0.4f, 0.55f), L.titleSize, 1f, materials.textColor);
            title.text = word;
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        /// <summary>Plain (no rich text) one-line centered 3D text; auto size down to `minSize` when smaller.</summary>
        TextMeshPro CreateText(Transform parent, string name, Vector3 position, Vector2 size, float fontSize, float minSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshPro>();
            text.rectTransform.sizeDelta = size;
            text.rectTransform.localPosition = position;
            text.font = font;
            text.color = color;
            text.richText = false;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = minSize < fontSize;
            text.fontSizeMax = fontSize;
            text.fontSizeMin = Mathf.Min(minSize, fontSize);
            text.fontSize = fontSize;
            return text;
        }

        Transform Box(Transform parent, string name, Vector3 center, Vector3 size, Material material, Color fallback, bool solid)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (!solid) Object.DestroyImmediate(go.GetComponent<Collider>());   // decoration never blocks walking or aiming
            var tr = go.transform;
            tr.SetParent(parent, false);
            tr.localPosition = center;
            tr.localRotation = Quaternion.identity;
            tr.localScale = size;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material != null ? material : Tinted(renderer.sharedMaterial, fallback);
            return tr;
        }

        /// <summary>One shared tinted copy of the primitive's default material per fallback color.</summary>
        Material Tinted(Material source, Color color)
        {
            if (tinted.TryGetValue(color, out var m) && m != null) return m;
            m = new Material(source) { color = color };
            tinted[color] = m;
            return m;
        }
    }
}
