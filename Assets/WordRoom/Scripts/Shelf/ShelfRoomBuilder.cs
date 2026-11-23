using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Builds one corridor room from primitives. Room-local frame: origin at the inner face of the entrance wall,
    /// corridor toward +Z, interior X within ±corridorWidth/2 and Z within 0..length, floor top at Y 0. Walls lie
    /// outside the interior.
    /// The definition reads as one line along the walls: plain text drawn straight on the wall (letters only, the
    /// wall material between them) and, for each link, a door with only its word on its face at the same height and
    /// one handle below. Placement starts on the left wall at the entrance and goes inward; the first item that does
    /// not fit continues on the right wall from the corridor end toward the entrance (a text part may be cut at a
    /// space there). Facing either wall the text reads left to right. Each door stores into the wall in reading
    /// direction when opened, so the next door's opening is kept beyond that pocket (text may stand in front of it).
    /// When both walls are not enough the wall length grows and the placement is redone. The same entry always gives
    /// the same layout, so a revisited room looks the same.
    /// A room reached through a passage also gets the connecting passage behind its entrance (its far-end seal off)
    /// and an open entrance; the start room's entrance wall is closed.
    /// </summary>
    internal sealed class ShelfRoomBuilder
    {
        static readonly Color WallColor = new Color32(0xD9, 0xCF, 0xBC, 0xFF);
        static readonly Color FloorColor = new Color32(0x6B, 0x63, 0x5A, 0xFF);
        static readonly Color PanelColor = new Color32(0x3A, 0x2A, 0x1E, 0xFF);
        static readonly Color HandleColor = new Color32(0xB8, 0x8A, 0x3E, 0xFF);
        static readonly Color DoorColor = new Color32(0x4A, 0x35, 0x26, 0xFF);

        const float GrowStep = 0.5f;             // wall length added per retry when the definition does not fit
        const int MaxGrow = 400;
        const float SlabFront = 0.15f;           // door face at 0.15 wall thickness inside the opening
        const float TextLift = 0.005f;           // wall text just in front of the wall surface

        /// <summary>A definition part ready to place: trimmed text, and whether the original had a space before it.</summary>
        struct Segment
        {
            public bool Door;
            public string Text;
            public string Target;
            public bool SpaceBefore;
        }

        struct Item
        {
            public bool Door;
            public string Text;
            public string Target;
            public float Width;                  // along the wall
            public int Side;                     // -1 left, +1 right
            public float Center;                 // room-local Z
        }

        readonly ShelfLayout layout;
        readonly ShelfMaterials materials;
        readonly TMP_FontAsset font;
        readonly Transform owner;
        readonly Dictionary<Color, Material> tinted = new Dictionary<Color, Material>();
        TextMeshPro measure;
        float spaceWidth;

        /// <param name="owner">Parent of the hidden text measuring object (destroyed with it).</param>
        public ShelfRoomBuilder(ShelfLayout layout, ShelfMaterials materials, TMP_FontAsset font, Transform owner)
        {
            this.layout = layout;
            this.materials = materials;
            this.font = font;
            this.owner = owner;
        }

        public ShelfRoomView Build(ShelfEntry entry, Vector3 position, Quaternion rotation, bool viaPassage)
        {
            var L = layout;
            var halfWidth = L.corridorWidth * 0.5f;
            var t = L.wallThickness;
            var width = L.DoorWidth;

            Measure().fontSize = L.textSize;
            spaceWidth = Mathf.Max(0f, TextWidth("가 가") - TextWidth("가가"));
            var lineHeight = Measure().GetPreferredValues("가").y;
            var segments = Segments(entry);
            var items = new List<Item>();
            var wallLength = L.wallLength;
            for (var grow = 0; !Place(segments, wallLength, items); grow++)
            {
                if (grow >= MaxGrow)
                {
                    Debug.LogWarning($"[서가 시안] '{entry.Word}' 뜻풀이를 벽 {wallLength:0.#}m에도 다 배치하지 못해 남은 글을 생략합니다.");
                    break;
                }
                wallLength += GrowStep;
            }
            var length = L.entryLength + wallLength + L.endLength;

            var root = new GameObject($"ShelfRoom_{entry.Word}").transform;
            root.SetPositionAndRotation(position, rotation);
            var view = root.gameObject.AddComponent<ShelfRoomView>();

            var floor = Box(root, "Floor", new Vector3(0f, -0.1f, length * 0.5f), new Vector3(L.corridorWidth + 2f * t, 0.2f, length + 2f * t), materials.floor, FloorColor, true);

            var left = new List<float>();
            var right = new List<float>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.Door)
                {
                    view.Add(BuildDoor(root, view, item, i, width, halfWidth, lineHeight));
                    (item.Side < 0 ? left : right).Add(item.Center + t);   // along the side wall frame
                }
                else BuildWallText(root, item, i, halfWidth, lineHeight);
            }

            var sideLength = length + 2f * t;
            Wall(root, "Wall_Left", new Vector3(-(halfWidth + t * 0.5f), 0f, -t), Quaternion.LookRotation(Vector3.left), sideLength, left, width);
            Wall(root, "Wall_Right", new Vector3(halfWidth + t * 0.5f, 0f, -t), Quaternion.LookRotation(Vector3.left), sideLength, right, width);
            Wall(root, "Wall_Entrance", new Vector3(-halfWidth, 0f, -t * 0.5f), Quaternion.identity, L.corridorWidth, viaPassage ? new List<float> { halfWidth } : null, width);
            Wall(root, "Wall_End", new Vector3(-halfWidth, 0f, length + t * 0.5f), Quaternion.identity, L.corridorWidth, null, width);

            BuildTitle(root, entry, length);
            var seal = viaPassage ? BuildConnectingPassage(root, width) : null;

            view.Init(entry, length, halfWidth, floor, seal);
            Physics.SyncTransforms();                 // the new colliders were moved after creation
            return view;
        }

        /// <summary>
        /// A homonym fork behind a door, from the door's outer pose (+Z away from the room): a stem passage, then a
        /// hall whose far wall has one opening per branch, left to right, each with its label on the wall above it.
        /// Returns the fork root and fills `roomPoses` with each branch room's origin: the branch room's own
        /// connecting passage starts at that opening, and the rooms run side by side without overlapping.
        /// </summary>
        public GameObject BuildFork(Vector3 position, Quaternion rotation, IReadOnlyList<string> labels, List<Pose> roomPoses)
        {
            var L = layout;
            var t = L.wallThickness;
            var w = L.DoorWidth;
            var h = L.passageHeight;
            var n = labels.Count;
            var spacing = L.corridorWidth + 2f * t + L.branchGap;     // one room footprint plus the gap
            var hx = Mathf.Max(w * 0.5f + 0.5f, (n - 1) * spacing * 0.5f + spacing * 0.5f);   // hall half width
            var stem = L.tunnelLength;
            var nearZ = stem + t * 0.5f;
            var innerFar = stem + t + L.hallDepth;
            var outerFar = innerFar + t;

            var root = new GameObject("ShelfFork").transform;
            root.SetPositionAndRotation(position, rotation);

            var stemCenter = stem * 0.5f;
            Box(root, "Stem_Floor", new Vector3(0f, -0.1f, stemCenter), new Vector3(w + 2f * t, 0.2f, stem), materials.floor, FloorColor, true);
            Box(root, "Stem_Wall_L", new Vector3(-(w + t) * 0.5f, (h + t) * 0.5f, stemCenter), new Vector3(t, h + t, stem), materials.wall, WallColor, true);
            Box(root, "Stem_Wall_R", new Vector3((w + t) * 0.5f, (h + t) * 0.5f, stemCenter), new Vector3(t, h + t, stem), materials.wall, WallColor, true);
            Box(root, "Stem_Ceiling", new Vector3(0f, h + t * 0.5f, stemCenter), new Vector3(w + 2f * t, t, stem), materials.wall, WallColor, true);

            var hallLength = L.hallDepth + 2f * t;
            Box(root, "Hall_Floor", new Vector3(0f, -0.1f, stem + hallLength * 0.5f), new Vector3(2f * (hx + t), 0.2f, hallLength), materials.floor, FloorColor, true);
            Wall(root, "Hall_Wall_Near", new Vector3(-(hx + t), 0f, nearZ), Quaternion.identity, 2f * (hx + t), new List<float> { hx + t }, w);
            var openings = new List<float>();
            for (var i = 0; i < n; i++) openings.Add(BranchX(i, n, spacing) + hx + t);
            Wall(root, "Hall_Wall_Far", new Vector3(-(hx + t), 0f, outerFar - t * 0.5f), Quaternion.identity, 2f * (hx + t), openings, w);
            Wall(root, "Hall_Wall_L", new Vector3(-(hx + t * 0.5f), 0f, stem), Quaternion.LookRotation(Vector3.left), hallLength, null, w);
            Wall(root, "Hall_Wall_R", new Vector3(hx + t * 0.5f, 0f, stem), Quaternion.LookRotation(Vector3.left), hallLength, null, w);

            roomPoses.Clear();
            for (var i = 0; i < n; i++)
            {
                var x = BranchX(i, n, spacing);
                var label = CreateText(root, $"Branch_{i}_Label", new Vector3(x, L.forkLabelHeight, innerFar - TextLift),
                    new Vector2(spacing - 0.3f, 0.5f), L.forkLabelSize, 0.6f, TextAlignmentOptions.Center, materials.textColor);
                label.text = labels[i];
                roomPoses.Add(new Pose(root.TransformPoint(new Vector3(x, 0f, outerFar + L.tunnelLength + t)), rotation));
            }
            Physics.SyncTransforms();
            return root.gameObject;
        }

        /// <summary>Branch i of n, left (−X) to right, spaced so the branch rooms sit side by side.</summary>
        static float BranchX(int i, int n, float spacing) => (i - (n - 1) * 0.5f) * spacing;

        // ---- placement -------------------------------------------------------------------------------------------

        /// <summary>Links as doors, plain parts trimmed (blank parts only add a space before the next item).</summary>
        static List<Segment> Segments(ShelfEntry entry)
        {
            var list = new List<Segment>();
            var pendingSpace = false;
            foreach (var part in entry.Parts)
            {
                if (part.IsLink)
                {
                    list.Add(new Segment { Door = true, Text = part.Text, Target = part.Target, SpaceBefore = pendingSpace });
                    pendingSpace = false;
                    continue;
                }
                var raw = part.Text;
                var text = raw.Trim();
                if (text.Length == 0)
                {
                    pendingSpace |= raw.Length > 0;
                    continue;
                }
                list.Add(new Segment { Text = text, SpaceBefore = pendingSpace || char.IsWhiteSpace(raw[0]) });
                pendingSpace = char.IsWhiteSpace(raw[raw.Length - 1]);
            }
            return list;
        }

        /// <summary>
        /// Places the segments in reading order along the left wall (from the entrance inward), then the right wall
        /// (from the corridor end toward the entrance), each wall `wallLength` long in its reading coordinate s.
        /// A door needs its width plus its pocket before the wall's end, and starts after the previous door's pocket.
        /// A text that does not fit is cut at the last space that fits (the rest goes on the right wall), or moves
        /// whole when no cut fits. False when the right wall is full too.
        /// </summary>
        bool Place(List<Segment> segments, float wallLength, List<Item> items)
        {
            var L = layout;
            var doorWidth = L.DoorWidth;
            items.Clear();
            var wall = 0;
            var s = 0f;                              // end of the last item on this wall
            var doorFree = 0f;                       // first s where the next door's opening may start
            var empty = true;                        // nothing on this wall yet (no gap before the first item)

            bool NextWall()
            {
                if (wall == 1) return false;
                wall = 1;
                s = 0f;
                doorFree = 0f;
                empty = true;
                return true;
            }

            foreach (var seg in segments)
            {
                var spaceBefore = seg.SpaceBefore;
                if (seg.Door)
                {
                    while (true)
                    {
                        var start = Mathf.Max(empty ? 0f : s + Gap(spaceBefore), doorFree);
                        if (start + doorWidth + L.PocketLength <= wallLength)
                        {
                            items.Add(MakeItem(true, seg.Text, seg.Target, wall, start, doorWidth, wallLength));
                            s = start + doorWidth;
                            doorFree = s + L.PocketLength + L.itemGap;
                            empty = false;
                            break;
                        }
                        if (!NextWall()) return false;
                    }
                    continue;
                }

                var text = seg.Text;
                while (true)
                {
                    var start = empty ? 0f : s + Gap(spaceBefore);
                    var width = TextWidth(text);
                    if (start + width <= wallLength)
                    {
                        items.Add(MakeItem(false, text, null, wall, start, width, wallLength));
                        s = start + width;
                        empty = false;
                        break;
                    }
                    var cut = -1;
                    for (var i = text.LastIndexOf(' '); i > 0; i = text.LastIndexOf(' ', i - 1))
                    {
                        if (start + TextWidth(text.Substring(0, i).TrimEnd()) > wallLength) continue;
                        cut = i;
                        break;
                    }
                    if (cut > 0)
                    {
                        var head = text.Substring(0, cut).TrimEnd();
                        items.Add(MakeItem(false, head, null, wall, start, TextWidth(head), wallLength));
                        text = text.Substring(cut).Trim();
                    }
                    else if (empty) return false;        // one word longer than an empty wall
                    if (!NextWall()) return false;
                    spaceBefore = false;
                }
            }
            return true;
        }

        float Gap(bool spaceBefore) => layout.itemGap + (spaceBefore ? spaceWidth : 0f);

        /// <summary>Reading coordinate s on a wall → room-local Z: left wall inward from the entrance, right wall back from the end.</summary>
        Item MakeItem(bool door, string text, string target, int wall, float start, float width, float wallLength)
        {
            var mid = start + width * 0.5f;
            return new Item
            {
                Door = door,
                Text = text,
                Target = target,
                Width = width,
                Side = wall == 0 ? -1 : 1,
                Center = layout.entryLength + (wall == 0 ? mid : wallLength - mid),
            };
        }

        // ---- parts -----------------------------------------------------------------------------------------------

        /// <summary>
        /// A word door: slab in the wall's opening with the word on its face at text height and the handle right below.
        /// Its frame: +X along the wall in reading direction, +Y up, +Z into the wall (inner face at 0). It opens by
        /// sliding into the wall in reading direction (left wall +Z, right wall −Z).
        /// </summary>
        ShelfPassage BuildDoor(Transform root, ShelfRoomView view, Item item, int index, float width, float halfWidth, float lineHeight)
        {
            var L = layout;
            var t = L.wallThickness;
            var door = new GameObject($"Door_{index}_{item.Text}").transform;
            door.SetParent(root, false);
            door.localPosition = new Vector3(item.Side * halfWidth, 0f, item.Center);
            door.localRotation = Quaternion.LookRotation(new Vector3(item.Side, 0f, 0f));

            var face = t * SlabFront;
            Box(door, "Slab", new Vector3(0f, L.passageHeight * 0.5f, t * 0.5f),
                new Vector3(width - 0.02f, L.passageHeight - 0.01f, t * (1f - 2f * SlabFront)), materials.door, DoorColor, true);

            var center = Mathf.Min(L.textCenter, L.passageHeight - 0.05f - lineHeight * 0.7f);
            var word = CreateText(door, "Word", new Vector3(0f, center, face - TextLift),
                new Vector2(width - 0.1f, lineHeight * 1.4f), L.textSize, 1f, TextAlignmentOptions.Center, materials.linkColor);
            word.text = item.Text;

            var passage = door.gameObject.AddComponent<ShelfPassage>();
            var slide = (width + ShelfLayout.SlideClearance) * (item.Side < 0 ? 1f : -1f);
            passage.Init(view, item.Side, item.Center, width, new Vector3(0f, 0f, slide), L.openSeconds);
            var handleY = Mathf.Min(L.handleHeight, center - lineHeight * 0.5f - 0.22f);
            passage.Add(BuildHandle(door, passage, item.Target, new Vector3(0f, handleY, face)));
            return passage;
        }

        /// <summary>Plain text letters straight on a side wall's inner face (no panel), one line at text height.</summary>
        void BuildWallText(Transform root, Item item, int index, float halfWidth, float lineHeight)
        {
            var L = layout;
            var holder = new GameObject($"Text_{index}").transform;
            holder.SetParent(root, false);
            holder.localPosition = new Vector3(item.Side * halfWidth, 0f, item.Center);
            holder.localRotation = Quaternion.LookRotation(new Vector3(item.Side, 0f, 0f));
            var text = CreateText(holder, "Text", new Vector3(0f, L.textCenter, -TextLift),
                new Vector2(item.Width + 0.02f, lineHeight * 1.4f), L.textSize, L.textSize, TextAlignmentOptions.Center, materials.textColor);
            text.text = item.Text;
        }

        ShelfHandle BuildHandle(Transform door, ShelfPassage passage, string target, Vector3 position)
        {
            var handle = new GameObject($"Handle_{target}").transform;
            handle.SetParent(door, false);
            handle.localPosition = position;
            Box(handle, "Plate", new Vector3(0f, 0f, -0.015f), new Vector3(0.16f, 0.1f, 0.03f), materials.handle, HandleColor, false);
            var lever = new GameObject("Lever").transform;
            lever.SetParent(handle, false);
            lever.localPosition = new Vector3(0f, 0f, -0.03f);
            Box(lever, "Grip", new Vector3(0f, 0.075f, 0f), new Vector3(0.035f, 0.15f, 0.035f), materials.handle, HandleColor, false);
            Primitive(PrimitiveType.Sphere, lever, "Knob", new Vector3(0f, 0.16f, 0f), Vector3.one * 0.065f, materials.handle, HandleColor, false);
            var aim = handle.gameObject.AddComponent<BoxCollider>();   // one aim target for rest and pulled poses
            aim.center = new Vector3(0f, 0.03f, -0.07f);
            aim.size = new Vector3(0.28f, 0.36f, 0.14f);
            var h = handle.gameObject.AddComponent<ShelfHandle>();
            h.Init(passage, target, lever, aim);
            return h;
        }

        void BuildTitle(Transform root, ShelfEntry entry, float length)
        {
            var L = layout;
            var holder = new GameObject("Title").transform;
            holder.SetParent(root, false);
            holder.localPosition = new Vector3(0f, 0f, length);          // +Z into the end wall, +X to the right
            var width = Mathf.Min(L.corridorWidth - 0.4f, 2.6f);
            const float center = 1.7f;
            Box(holder, "Board", new Vector3(0f, center, -L.panelDepth * 0.5f), new Vector3(width, 1f, L.panelDepth), materials.panel, PanelColor, false);
            var title = CreateText(holder, "Word", new Vector3(0f, center + 0.08f, -L.panelDepth - TextLift), new Vector2(width - 0.2f, 0.7f), L.titleSize, 1f, TextAlignmentOptions.Center, materials.titleColor);
            title.text = entry.Word;
            if (string.IsNullOrEmpty(entry.Pos)) return;
            var pos = CreateText(holder, "Pos", new Vector3(0f, center - 0.36f, -L.panelDepth - TextLift), new Vector2(width - 0.2f, 0.2f), 1.6f, 0.8f, TextAlignmentOptions.Center, materials.titleColor);
            pos.text = $"「{entry.Pos}」";
        }

        /// <summary>The passage from the previous room's wall to this entrance; returns its far-end seal (off).</summary>
        GameObject BuildConnectingPassage(Transform root, float w)
        {
            var L = layout;
            var t = L.wallThickness;
            var h = L.passageHeight;
            var z0 = -t - L.tunnelLength;
            var zc = (z0 - t) * 0.5f;
            var parent = new GameObject("ConnectingPassage").transform;
            parent.SetParent(root, false);
            Box(parent, "Floor", new Vector3(0f, -0.1f, zc), new Vector3(w + 2f * t, 0.2f, L.tunnelLength), materials.floor, FloorColor, true);
            Box(parent, "Wall_L", new Vector3(-(w + t) * 0.5f, (h + t) * 0.5f, zc), new Vector3(t, h + t, L.tunnelLength), materials.wall, WallColor, true);
            Box(parent, "Wall_R", new Vector3((w + t) * 0.5f, (h + t) * 0.5f, zc), new Vector3(t, h + t, L.tunnelLength), materials.wall, WallColor, true);
            Box(parent, "Ceiling", new Vector3(0f, h + t * 0.5f, zc), new Vector3(w + 2f * t, t, L.tunnelLength), materials.wall, WallColor, true);
            var seal = Box(parent, "Seal", new Vector3(0f, h * 0.5f, z0 + t * 0.5f), new Vector3(w, h, t), materials.wall, WallColor, true).gameObject;
            seal.SetActive(false);
            return seal;
        }

        /// <summary>
        /// A straight wall along the frame's +X from 0 to `length` (thickness centered on the frame's Z), cut by
        /// openings of `openingWidth` × passage height at the given centers, with a lintel above each opening.
        /// </summary>
        void Wall(Transform root, string name, Vector3 origin, Quaternion rotation, float length, List<float> openings, float openingWidth)
        {
            var L = layout;
            var frame = new GameObject(name).transform;
            frame.SetParent(root, false);
            frame.localPosition = origin;
            frame.localRotation = rotation;
            var half = openingWidth * 0.5f;
            var x = 0f;
            if (openings != null)
            {
                openings.Sort();
                foreach (var c in openings)
                {
                    var a = Mathf.Max(x, c - half);
                    var b = Mathf.Min(length, c + half);
                    if (a > x) Piece(frame, x, a, 0f, L.wallHeight);
                    if (b > a) Piece(frame, a, b, L.passageHeight, L.wallHeight);
                    x = Mathf.Max(x, b);
                }
            }
            if (x < length) Piece(frame, x, length, 0f, L.wallHeight);
        }

        void Piece(Transform frame, float x0, float x1, float y0, float y1)
        {
            if (x1 - x0 < 0.001f || y1 - y0 < 0.001f) return;
            Box(frame, "Wall", new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, 0f), new Vector3(x1 - x0, y1 - y0, layout.wallThickness), materials.wall, WallColor, true);
        }

        // ---- helpers ---------------------------------------------------------------------------------------------

        float TextWidth(string s) => Measure().GetPreferredValues(s).x;

        /// <summary>Hidden one-line TextMeshPro with the text settings, used only for GetPreferredValues.</summary>
        TextMeshPro Measure()
        {
            if (measure != null) return measure;
            var go = new GameObject("ShelfTextMeasure", typeof(RectTransform));
            go.transform.SetParent(owner, false);
            measure = go.AddComponent<TextMeshPro>();
            measure.font = font;
            measure.richText = false;
            measure.enableAutoSizing = false;
            measure.textWrappingMode = TextWrappingModes.NoWrap;
            measure.text = string.Empty;
            measure.GetComponent<MeshRenderer>().enabled = false;
            return measure;
        }

        /// <summary>Plain (no rich text) one-line 3D text with no background; auto size down to `minSize` when smaller.</summary>
        TextMeshPro CreateText(Transform parent, string name, Vector3 position, Vector2 size, float fontSize, float minSize,
            TextAlignmentOptions alignment, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshPro>();
            text.rectTransform.sizeDelta = size;
            text.rectTransform.localPosition = position;
            text.font = font;
            text.color = color;
            text.richText = false;
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = minSize < fontSize;
            text.fontSizeMax = fontSize;
            text.fontSizeMin = Mathf.Min(minSize, fontSize);
            text.fontSize = fontSize;
            return text;
        }

        Transform Box(Transform parent, string name, Vector3 center, Vector3 size, Material material, Color fallback, bool solid) =>
            Primitive(PrimitiveType.Cube, parent, name, center, size, material, fallback, solid);

        Transform Primitive(PrimitiveType type, Transform parent, string name, Vector3 position, Vector3 scale, Material material, Color fallback, bool solid)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (!solid) Object.DestroyImmediate(go.GetComponent<Collider>());   // decoration never blocks walking or aiming
            var tr = go.transform;
            tr.SetParent(parent, false);
            tr.localPosition = position;
            tr.localRotation = Quaternion.identity;
            tr.localScale = scale;
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
