using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.Generation;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// The map drawing parts (same building blocks and palette as the map): inside the viewport a "Sketch" layer in map
    /// units (SketchLayerCount line meshes and the line being drawn, under the participants' marks, above rooms and
    /// words) and the eraser's ring on top; over the viewport's top-left corner — but outside the viewport, so pressing
    /// a tool never reaches the map — the tool bar (current tool, 연필, 지우개, 모두 지우기; no keyboard navigation) and
    /// the note line under it. The three cursor images are drawn here once (32×32, imported as Cursor) when missing.
    /// </summary>
    public static partial class ArchiveUiBuilder
    {
        public const string MapHelpText = "Tab·Esc: 닫기    휠: 확대/축소    우클릭 드래그: 이동    좌클릭: 연필·지우개";
        /// <summary>The help line before drawing (replaced only when it still reads exactly this).</summary>
        public const string OldMapHelpText = "Tab 또는 Esc: 닫기    휠: 확대/축소    드래그: 이동";

        public const string SketchLayerName = "Sketch";
        public const string SketchRingName = "EraserRing";
        public const string SketchToolsName = "SketchTools";
        public const string SketchNoteName = "SketchNote";
        /// <summary>Line meshes: 3 × MapGraphic.VertexBudget covers the whole drawing (16,384 points ≈ 65,536 vertices plus dots) with room for previews.</summary>
        public const int SketchLayerCount = 3;

        public const string CursorFolder = PrefabFolder + "/Cursors";
        public const string PencilCursorPath = CursorFolder + "/MapPencil.png";
        public const string EraserCursorPath = CursorFolder + "/MapEraser.png";
        public const string HandCursorPath = CursorFolder + "/MapHand.png";
        const int CursorSize = 32;
        /// <summary>Hotspots (pixels from the image's top-left): the pencil's tip, the eraser's and the hand's centers.</summary>
        public static readonly Vector2 PencilHotspot = new Vector2(1f, 30f);
        public static readonly Vector2 EraserHotspot = new Vector2(16f, 16f);
        public static readonly Vector2 HandHotspot = new Vector2(16f, 16f);

        /// <summary>
        /// Adds the drawing parts to a map root (its panel and viewport given) and a connected MapSketchView; the layer
        /// goes to sibling `firstMarkSibling` of the viewport (under the marks). `cursors` = pencil, eraser, hand (each may be null).
        /// </summary>
        public static MapSketchView AddMapSketch(GameObject root, RectTransform panel, RectTransform viewport, int firstMarkSibling, Texture2D[] cursors)
        {
            var layer = NewRect(SketchLayerName, viewport);
            layer.anchorMin = layer.anchorMax = layer.pivot = new Vector2(0.5f, 0.5f);
            layer.sizeDelta = new Vector2(2f * SketchRules.MaxCoordinate, 2f * SketchRules.MaxCoordinate);   // never culled by the mask for its own rect
            layer.SetSiblingIndex(Mathf.Clamp(firstMarkSibling, 0, viewport.childCount - 1));
            var lines = new MapSketchGraphic[SketchLayerCount];
            for (var i = 0; i < lines.Length; i++) lines[i] = SketchGraphic(layer, $"Lines{i}");
            var drawing = SketchGraphic(layer, "Drawing");

            var ringRect = NewRect(SketchRingName, viewport);                 // on top of everything in the viewport
            ringRect.anchorMin = ringRect.anchorMax = ringRect.pivot = new Vector2(0.5f, 0.5f);
            ringRect.sizeDelta = new Vector2(28f, 28f);
            var ring = ringRect.gameObject.AddComponent<MapSketchRing>();
            ring.color = new Color32(0xF2, 0xEB, 0xDD, 230);
            ring.raycastTarget = false;
            ring.enabled = false;

            var stretch = viewport.anchorMin == Vector2.zero && viewport.anchorMax == Vector2.one;
            var corner = stretch ? new Vector2(viewport.offsetMin.x + 12f, viewport.offsetMax.y - 12f) : new Vector2(36f, -108f);
            var bar = NewRect(SketchToolsName, panel);
            bar.anchorMin = bar.anchorMax = bar.pivot = new Vector2(0f, 1f);
            bar.anchoredPosition = corner;
            bar.SetSiblingIndex(viewport.GetSiblingIndex() + 1);              // above the map, outside the viewport
            AddImage(bar.gameObject, ArchiveUi.PanelWalnut, true);            // catches clicks: nothing behind it reacts
            AddFrame(bar);
            var row = Horizontal(bar, 8f, TextAnchor.MiddleLeft);
            row.padding = new RectOffset(16, 12, 8, 8);
            var fit = bar.gameObject.AddComponent<ContentSizeFitter>();
            fit.horizontalFit = fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var tool = AddText(bar, "Tool", "도구: 연필", 20f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            tool.textWrappingMode = TextWrappingModes.NoWrap;
            Size(tool.gameObject, 130f, 48f);
            var pencil = AddButton(bar, "PencilButton", "연필", 96f, 48f, 20f);
            var eraser = AddButton(bar, "EraserButton", "지우개", 110f, 48f, 20f);
            var clear = AddButton(bar, "ClearButton", "모두 지우기", 150f, 48f, 20f);
            pencil.navigation = eraser.navigation = clear.navigation = new Navigation { mode = Navigation.Mode.None };   // WASD walks, never moves a selection here

            var note = AddText(panel, SketchNoteName, "", 20f, ArchiveUi.IvoryDim, TextAlignmentOptions.TopLeft);
            note.rectTransform.anchorMin = note.rectTransform.anchorMax = note.rectTransform.pivot = new Vector2(0f, 1f);
            note.rectTransform.anchoredPosition = corner + new Vector2(4f, -76f);
            note.rectTransform.sizeDelta = new Vector2(960f, 60f);
            note.rectTransform.SetSiblingIndex(bar.GetSiblingIndex() + 1);
            note.enabled = false;

            var sketch = root.AddComponent<MapSketchView>();
            var so = new SerializedObject(sketch);
            Set(so, "layerRoot", layer);
            var p = so.FindProperty("layers");
            p.arraySize = lines.Length;
            for (var i = 0; i < lines.Length; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = lines[i];
            Set(so, "preview", drawing);
            Set(so, "eraserRing", ring);
            Set(so, "toolBar", bar.gameObject);
            Set(so, "pencilButton", pencil);
            Set(so, "eraserButton", eraser);
            Set(so, "clearButton", clear);
            Set(so, "toolText", tool);
            Set(so, "noteText", note);
            SetCursors(so, cursors, false);
            so.ApplyModifiedPropertiesWithoutUndo();
            return sketch;
        }

        static MapSketchGraphic SketchGraphic(RectTransform layer, string name)
        {
            var r = NewRect(name, layer);
            Stretch(r);
            var g = r.gameObject.AddComponent<MapSketchGraphic>();
            g.raycastTarget = false;
            return g;
        }

        /// <summary>The cursor images and their hotspots into a MapSketchView; `onlyEmpty` keeps images already set. True when something changed.</summary>
        public static bool SetCursors(SerializedObject sketch, Texture2D[] cursors, bool onlyEmpty)
        {
            var changed = false;
            changed |= SetCursor(sketch, "pencilCursor", "pencilHotspot", cursors[0], PencilHotspot, onlyEmpty);
            changed |= SetCursor(sketch, "eraserCursor", "eraserHotspot", cursors[1], EraserHotspot, onlyEmpty);
            changed |= SetCursor(sketch, "handCursor", "handHotspot", cursors[2], HandHotspot, onlyEmpty);
            return changed;
        }

        static bool SetCursor(SerializedObject so, string field, string hotspotField, Texture2D image, Vector2 hotspot, bool onlyEmpty)
        {
            var p = so.FindProperty(field);
            if (image == null || p.objectReferenceValue == image || (onlyEmpty && p.objectReferenceValue != null)) return false;
            p.objectReferenceValue = image;
            so.FindProperty(hotspotField).vector2Value = hotspot;             // the hotspot belongs to this image
            return true;
        }

        // ---- cursor images ------------------------------------------------------------------------------------
        /// <summary>
        /// Pencil, eraser and hand cursor images: the existing assets as they are, or — when missing — drawn here (dark
        /// outline, archive colors), saved as PNG and imported as Cursor (readable, no mipmaps, uncompressed). `notes`
        /// and `saved` may be null.
        /// </summary>
        public static Texture2D[] EnsureMapCursors(List<string> notes, List<string> saved) => new[]
        {
            EnsureCursor(PencilCursorPath, PaintPencil, notes, saved),
            EnsureCursor(EraserCursorPath, PaintEraser, notes, saved),
            EnsureCursor(HandCursorPath, PaintHand, notes, saved),
        };

        static Texture2D EnsureCursor(string path, Func<float, float, Color?> shape, List<string> notes, List<string> saved)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;
            if (!AssetDatabase.IsValidFolder(CursorFolder)) AssetDatabase.CreateFolder(PrefabFolder, "Cursors");
            var tex = new Texture2D(CursorSize, CursorSize, TextureFormat.RGBA32, false);
            try
            {
                const int samples = 4;
                for (var y = 0; y < CursorSize; y++)
                    for (var x = 0; x < CursorSize; x++)
                    {
                        float r = 0f, g = 0f, b = 0f, a = 0f;
                        for (var sy = 0; sy < samples; sy++)
                            for (var sx = 0; sx < samples; sx++)
                            {
                                var c = shape(x + (sx + 0.5f) / samples, y + (sy + 0.5f) / samples);   // y from the top
                                if (c == null) continue;
                                r += c.Value.r;
                                g += c.Value.g;
                                b += c.Value.b;
                                a += 1f;
                            }
                        var color = a > 0f ? new Color(r / a, g / a, b / a, a / (samples * samples)) : Color.clear;
                        tex.SetPixel(x, CursorSize - 1 - y, color);
                    }
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Cursor;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.isReadable = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
            if (saved != null && !saved.Contains(path)) saved.Add(path);
            if (notes != null) notes.Add($"커서 이미지 생성: {path}");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static readonly Color CursorOutline = new Color32(0x1E, 0x16, 0x12, 255);

        /// <summary>The pencil, its tip at the hotspot (1, 30) pointing down-left: graphite, wood, brass body, rose eraser end.</summary>
        static Color? PaintPencil(float x, float y)
        {
            var tip = new Vector2(PencilHotspot.x, PencilHotspot.y);
            var axis = new Vector2(1f, -1f).normalized;
            var d = new Vector2(x, y) - tip;
            var t = Vector2.Dot(d, axis);                                      // along the pencil from the tip
            var s = Mathf.Abs(d.x * axis.y - d.y * axis.x);                    // across it
            const float cone = 7f, length = 36f, half = 3.6f;
            float Half(float at) => at < cone ? half * at / cone : half;
            if (t >= 0f && t <= length && s <= Half(t))
            {
                if (t < 3f) return new Color32(0x3A, 0x32, 0x2D, 255);
                if (t < cone) return new Color32(0xE8, 0xC9, 0x9B, 255);
                if (t > length - 6f) return new Color32(0xE3, 0xA0, 0x8C, 255);
                return new Color32(0xD7, 0xB5, 0x68, 255);
            }
            if (t >= -1.2f && t <= length + 1.2f && s <= Half(Mathf.Max(0f, t)) + 1.2f) return CursorOutline;
            return null;
        }

        /// <summary>A block eraser turned 30°, centered on the hotspot, with a dark band and a light center dot.</summary>
        static Color? PaintEraser(float x, float y)
        {
            var c = new Vector2(EraserHotspot.x, EraserHotspot.y);
            var d = new Vector2(x, y) - c;
            if (d.sqrMagnitude <= 1.1f * 1.1f) return new Color32(0xF2, 0xEB, 0xDD, 255);
            const float angle = 30f * Mathf.Deg2Rad;
            var u = d.x * Mathf.Cos(angle) + d.y * Mathf.Sin(angle);
            var v = -d.x * Mathf.Sin(angle) + d.y * Mathf.Cos(angle);
            var box = RoundBox(u, v, 10f, 6f, 2f);
            if (box <= 0f) return u > 3f ? new Color32(0x6E, 0x55, 0x37, 255) : new Color32(0xE3, 0xA0, 0x8C, 255);
            if (box <= 1.3f) return CursorOutline;
            return null;
        }

        /// <summary>A closed (grabbing) hand centered on the hotspot: palm, four knuckles, thumb.</summary>
        static Color? PaintHand(float x, float y)
        {
            var p = new Vector2(x, y);
            var palm = RoundBox(x - 16.5f, y - 19f, 8f, 6.5f, 3f);
            var shape = palm;
            for (var i = 0; i < 4; i++) shape = Mathf.Min(shape, Vector2.Distance(p, new Vector2(11f + 3.7f * i, 12.5f)) - 2.4f);
            shape = Mathf.Min(shape, Vector2.Distance(p, new Vector2(8f, 20f)) - 2.8f);
            if (shape <= 0f) return new Color32(0xF2, 0xEB, 0xDD, 255);
            if (shape <= 1.3f) return CursorOutline;
            return null;
        }

        /// <summary>Signed distance to a rounded box of half sizes (hx, hy) and corner radius r around the origin.</summary>
        static float RoundBox(float u, float v, float hx, float hy, float r)
        {
            var qx = Mathf.Abs(u) - (hx - r);
            var qy = Mathf.Abs(v) - (hy - r);
            var outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }
    }
}
