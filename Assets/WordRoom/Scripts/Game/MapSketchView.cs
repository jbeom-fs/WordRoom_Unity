using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WordRoom.Generation;

namespace WordRoom.Game
{
    public enum MapSketchTool
    {
        Pencil,
        Eraser,
    }

    /// <summary>
    /// The drawing part of the map screen (on the Map Canvas root next to MapView, parts made in the Editor; optional —
    /// without it, or with a part missing, the map works as before and only drawing is off, its tools hidden).
    /// Tools (outside the viewport, no keyboard navigation, so WASD only walks): 연필 (default), 지우개, 모두 지우기 and the
    /// current tool's name. With the left button on the viewport (MapViewportInput) the pencil draws a line from the press
    /// (a click leaves a dot), adding a point only after the pointer moved pointSpacing canvas units; a line reaching
    /// SketchRules.MaxStrokePoints is handed over and the next one continues from its last point. The eraser removes the
    /// parts of this device's own lines it passes over (shown at once; sent when released, or every MaxStrokePoints path
    /// points). 모두 지우기 removes all of this device's own lines. Everything goes to the session's MapSketchBoard (single
    /// play decides there; coop sends to the host). Points are map coordinates (MapView.TryScreenToMap), widths and the
    /// eraser radius map units, so zoom, pan, 현재 위치, UI size and resolution never move a line.
    /// Lines: the board's display split over the prepared meshes (each line stays in its mesh while it lives, so only
    /// meshes whose lines changed are rebuilt) plus one for the line being drawn; the layer they sit in follows the view.
    /// Cursor (hardware, hotspot at the tool's tip / center): pencil or eraser over the viewport while drawing is possible
    /// (the eraser also shows its reach as a ring sized with the zoom), the grabbing hand while panning, the arrow
    /// elsewhere and whenever the map closes, this is disabled or the application loses focus (the lock and visibility
    /// of the cursor stay FirstPersonPlayer's). A press in progress is ended — kept by a pan, the wheel, 현재 위치 or a tool
    /// change; dropped by closing, disabling, focus loss or the end of the session's drawing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapSketchView : MonoBehaviour
    {
        [Header("그림 표시 (지도 영역 안)")]
        [Tooltip("그림 선을 담는 지도 좌표 레이어(가운데 기준). 지도 이동·확대에 맞춰 위치와 배율만 바뀝니다.")]
        [SerializeField] RectTransform layerRoot;
        [Tooltip("확정·대기 중 그림을 나눠 그리는 메시(각각 MapGraphic.VertexBudget 정점 이하)")]
        [SerializeField] MapSketchGraphic[] layers = new MapSketchGraphic[0];
        [Tooltip("지금 그리는 선")]
        [SerializeField] MapSketchGraphic preview;
        [Tooltip("지우개 범위(지도 영역 안, 포인터 위치)")]
        [SerializeField] MapSketchRing eraserRing;

        [Header("도구 (지도 영역 밖)")]
        [SerializeField] GameObject toolBar;
        [SerializeField] Button pencilButton;
        [SerializeField] Button eraserButton;
        [SerializeField] Button clearButton;
        [SerializeField] TextMeshProUGUI toolText;
        [Tooltip("한도·전송 안내")]
        [SerializeField] TextMeshProUGUI noteText;

        [Header("커서 (텍스처 형식 Cursor, 핫스팟 = 텍스처 왼쪽 위 기준 픽셀)")]
        [SerializeField] Texture2D pencilCursor;
        [SerializeField] Vector2 pencilHotspot = new Vector2(1f, 30f);
        [SerializeField] Texture2D eraserCursor;
        [SerializeField] Vector2 eraserHotspot = new Vector2(16f, 16f);
        [SerializeField] Texture2D handCursor;
        [SerializeField] Vector2 handHotspot = new Vector2(16f, 16f);

        [Header("크기 (지도 좌표: 방 간격 = 1)")]
        [Tooltip("선 굵기(지도 단위, 기본 0.04 = 기본 칸 120에서 약 5). 확대·축소와 함께 변합니다.")]
        [SerializeField] float strokeWidth = SketchRules.StrokeWidth;
        [Tooltip("지우개 반지름(지도 단위, 기본 0.12 = 기본 칸 120에서 약 14). 선 굵기의 절반까지 닿으면 지웁니다.")]
        [SerializeField] float eraserRadius = SketchRules.EraserRadius;
        [Tooltip("새 점을 더하는 최소 포인터 이동(Canvas 단위). 가까운 점은 줄입니다.")]
        [SerializeField] float pointSpacing = 3f;
        [Tooltip("안내 문구 표시 시간(초)")]
        [SerializeField] float noteSeconds = 4f;

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        readonly List<float> line = new List<float>();
        readonly List<float> path = new List<float>();
        MapView view;
        MapViewportInput input;
        bool initialized;
        MapSketchTool tool = MapSketchTool.Pencil;
        MapSketchBoard board;
        Func<string, Color32> colorOf;
        bool editable;
        bool active;                          // a press of the current tool is going on
        Vector2 last;                         // last point taken (map units)
        Color pencilNormal, eraserNormal;
        bool cursorWarned, overflowWarned;
        int noteShown = -1;
        double noteUntil;

        // meshes: each line keeps the mesh it was put in while it lives
        List<SketchStroke>[] meshLines;
        int[] meshVertices;
        bool[] meshDirty;
        readonly Dictionary<SketchStroke, int> meshOf = new Dictionary<SketchStroke, int>();
        readonly Dictionary<SketchStroke, int> order = new Dictionary<SketchStroke, int>();
        MapSketchBoard shownBoard;
        int shownVersion = int.MinValue;
        int shownColors = int.MinValue;

        readonly List<RaycastResult> hits = new List<RaycastResult>();
        PointerEventData pointerData;
        EventSystem pointerSystem;

        static Texture2D cursorShown;
        static bool cursorCustom;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            cursorShown = null;
            cursorCustom = false;
        }

        float Width => Mathf.Clamp(strokeWidth, SketchRules.MinStrokeWidth, SketchRules.MaxStrokeWidth);
        float Radius => Mathf.Clamp(eraserRadius, SketchRules.MinEraserRadius, SketchRules.MaxEraserRadius);

        public MapSketchTool Tool => tool;

        /// <summary>Why drawing cannot be used (null = usable). The cursor images are optional (the arrow is used instead, reported once).</summary>
        public string MissingParts()
        {
            string missing = null;
            ArchiveUi.Need(layerRoot, nameof(layerRoot), ref missing);
            ArchiveUi.Need(preview, nameof(preview), ref missing);
            ArchiveUi.Need(eraserRing, nameof(eraserRing), ref missing);
            ArchiveUi.Need(toolBar, nameof(toolBar), ref missing);
            ArchiveUi.Need(pencilButton, nameof(pencilButton), ref missing);
            ArchiveUi.Need(eraserButton, nameof(eraserButton), ref missing);
            ArchiveUi.Need(clearButton, nameof(clearButton), ref missing);
            ArchiveUi.Need(toolText, nameof(toolText), ref missing);
            ArchiveUi.Need(noteText, nameof(noteText), ref missing);
            if (layers == null || layers.Length == 0) missing = missing == null ? "layers(비어 있음)" : $"{missing}, layers(비어 있음)";
            else
                for (var i = 0; i < layers.Length; i++) ArchiveUi.Need(layers[i], $"layers[{i}]", ref missing);
            return missing;
        }

        /// <summary>Drawing is off: its tools and marks are hidden (the rest of the map is unchanged).</summary>
        public void Hide()
        {
            if (toolBar != null && toolBar.activeSelf) toolBar.SetActive(false);
            if (noteText != null) noteText.enabled = false;
            if (eraserRing != null) eraserRing.enabled = false;
            if (preview != null) preview.enabled = false;
        }

        /// <summary>Once, when the map is ready: pointer events, buttons (no keyboard navigation), the marks never catching the pointer.</summary>
        public void Initialize(MapView mapView, MapViewportInput viewportInput)
        {
            if (initialized) return;
            initialized = true;
            view = mapView;
            input = viewportInput;
            input.ToolDown += OnToolDown;
            input.ToolMove += OnToolMove;
            input.ToolUp += OnToolUp;
            input.ToolStop += Finish;
            ArchiveUi.Bind(bindings, pencilButton, () => Choose(MapSketchTool.Pencil));
            ArchiveUi.Bind(bindings, eraserButton, () => Choose(MapSketchTool.Eraser));
            ArchiveUi.Bind(bindings, clearButton, ClearOwn);
            foreach (var b in new[] { pencilButton, eraserButton, clearButton }) b.navigation = new Navigation { mode = Navigation.Mode.None };
            pencilNormal = pencilButton.colors.normalColor;
            eraserNormal = eraserButton.colors.normalColor;
            layerRoot.sizeDelta = new Vector2(2f * SketchRules.MaxCoordinate, 2f * SketchRules.MaxCoordinate);   // never culled by the mask for its own rect
            meshLines = new List<SketchStroke>[layers.Length];
            meshVertices = new int[layers.Length];
            meshDirty = new bool[layers.Length];
            for (var i = 0; i < layers.Length; i++)
            {
                meshLines[i] = new List<SketchStroke>();
                layers[i].raycastTarget = false;
            }
            preview.raycastTarget = false;
            eraserRing.raycastTarget = false;
            eraserRing.enabled = false;
            noteText.raycastTarget = false;
            noteText.enabled = false;
            toolText.raycastTarget = false;
            if (!toolBar.activeSelf) toolBar.SetActive(true);
            if ((pencilCursor == null || eraserCursor == null || handCursor == null) && !cursorWarned)
            {
                cursorWarned = true;
                Debug.LogWarning("[지도] 그림 커서 이미지가 비어 있어 해당 커서 대신 기본 화살표를 씁니다(그림은 그대로). WordRoom/지도 화면 구성 메뉴로 커서를 만들고 연결하세요.", this);
            }
            ShowTool();
        }

        void OnDestroy()
        {
            ArchiveUi.Unbind(bindings);
            if (!initialized || input == null) return;
            input.ToolDown -= OnToolDown;
            input.ToolMove -= OnToolMove;
            input.ToolUp -= OnToolUp;
            input.ToolStop -= Finish;
        }

        void OnDisable() => Close();

        void OnApplicationFocus(bool focus)
        {
            if (!focus) Close();                                               // the input drops its press itself; ShowCursor keeps the arrow until focus is back
        }

        /// <summary>The map closed (or this is disabled, or focus left): the press is dropped, the arrow and no ring.</summary>
        public void Close()
        {
            Finish(false);
            SetCursor(null, Vector2.zero);
            if (eraserRing != null) eraserRing.enabled = false;
        }

        /// <summary>Ends the press keeping its line (현재 위치).</summary>
        public void Interrupt()
        {
            if (input != null) input.Interrupt();
            Finish(true);
        }

        /// <summary>
        /// While the map is open, after MapView.Tick: the session's board (null = none), each author's color and a key that
        /// changes when the colors may have; the layer follows the view, changed lines are drawn, cursor, ring and note.
        /// </summary>
        public void Tick(MapSketchBoard sketch, Func<string, Color32> colors, int colorKey)
        {
            if (!initialized) return;
            board = sketch;
            colorOf = colors;
            editable = board != null && board.Attached;
            if (!editable) Finish(false);
            var cell = view.Cell;
            layerRoot.anchoredPosition = -view.Center * cell;
            layerRoot.localScale = new Vector3(cell, cell, 1f);
            var version = board != null ? board.Version : int.MinValue;
            if (board != shownBoard || version != shownVersion || colorKey != shownColors)
            {
                var recolor = colorKey != shownColors;
                shownBoard = board;
                shownVersion = version;
                shownColors = colorKey;
                Draw(editable ? board.Display : null, recolor);
                if (recolor) DrawLine();
            }
            pencilButton.interactable = eraserButton.interactable = clearButton.interactable = editable;
            ShowNote();
            ShowCursor();
        }

        // ---- tools --------------------------------------------------------------------------------------------
        void Choose(MapSketchTool t)
        {
            Interrupt();                                                       // the press in progress ends first; the new tool takes the next one
            tool = t;
            ShowTool();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);   // Space/Enter never press it again while walking
        }

        void ClearOwn()
        {
            Interrupt();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            if (editable) board.Submit(SketchKind.Clear, 0f, new float[0]);   // only this device's own lines; never refused for the limits
        }

        void ShowTool()
        {
            toolText.text = tool == MapSketchTool.Pencil ? "도구: 연필" : "도구: 지우개";
            ArchiveUi.SetNormalColor(pencilButton, tool == MapSketchTool.Pencil ? (Color)ArchiveUi.Chosen : pencilNormal);
            ArchiveUi.SetNormalColor(eraserButton, tool == MapSketchTool.Eraser ? (Color)ArchiveUi.Chosen : eraserNormal);
        }

        void OnToolDown(Vector2 screen, Camera cam)
        {
            if (!editable || active || !view.TryScreenToMap(screen, cam, out var p)) return;
            if (tool == MapSketchTool.Pencil)
            {
                if (!board.CanAdd(1, out var why))
                {
                    board.SetNote(why, true);
                    return;
                }
                active = true;
                line.Clear();
                line.Add(p.x);
                line.Add(p.y);
                DrawLine();
            }
            else
            {
                active = true;
                path.Clear();
                path.Add(p.x);
                path.Add(p.y);
                board.PreviewErase(new[] { p.x, p.y }, Radius);
            }
            last = p;
        }

        void OnToolMove(Vector2 screen, Camera cam)
        {
            if (!active || !view.TryScreenToMap(screen, cam, out var p)) return;
            var spacing = Mathf.Max(0.5f, pointSpacing) / Mathf.Max(1f, view.Cell);
            if ((p - last).sqrMagnitude >= spacing * spacing) Take(p);
        }

        void OnToolUp(Vector2 screen, Camera cam)
        {
            if (!active) return;
            if (view.TryScreenToMap(screen, cam, out var p) && (p - last).sqrMagnitude > 0f) Take(p);   // the release point itself
            Finish(true);
        }

        void Take(Vector2 p)
        {
            if (tool == MapSketchTool.Pencil)
            {
                if (!board.CanAdd(line.Count / 2 + 1, out var why))           // the whole drawing's limit: the line ends here
                {
                    board.SetNote(why, true);
                    Finish(true);
                    return;
                }
                line.Add(p.x);
                line.Add(p.y);
                last = p;
                if (line.Count / 2 >= SketchRules.MaxStrokePoints)            // handed over; the next line goes on from the same point
                {
                    CommitLine();
                    if (!board.CanAdd(1, out why))                            // the 128th line, or the whole drawing is full: no next line
                    {
                        board.SetNote(why, true);
                        Finish(false);                                         // its line was handed over; only the preview goes
                        return;
                    }
                    line.Add(last.x);
                    line.Add(last.y);
                }
                DrawLine();
                return;
            }
            path.Add(p.x);
            path.Add(p.y);
            board.PreviewErase(new[] { last.x, last.y, p.x, p.y }, Radius);   // the segment, so a fast drag leaves no gap
            last = p;
            if (path.Count / 2 >= SketchRules.MaxStrokePoints)
            {
                CommitErase();
                path.Add(last.x);
                path.Add(last.y);
            }
        }

        /// <summary>
        /// The press ends: its line or erase is handed to the board (keep) or dropped. Whatever this press left locally —
        /// points, the preview mesh, the eraser's drag preview — is cleared also when no press is going on any more (closing,
        /// cancelling, 모두 지우기), never a pending or confirmed line.
        /// </summary>
        void Finish(bool keep)
        {
            var was = active;
            active = false;
            if (was && keep && editable)
            {
                if (tool == MapSketchTool.Pencil) CommitLine();
                else CommitErase();
            }
            DropProgress();
        }

        void DropProgress()
        {
            line.Clear();
            path.Clear();
            if (preview != null) preview.Clear();                              // the mesh itself, not only the list it was drawn from
            if (board != null) board.EndErasePreview();
        }

        void CommitLine()
        {
            if (line.Count >= 2) board.Submit(SketchKind.Add, Width, line.ToArray());
            line.Clear();
        }

        void CommitErase()
        {
            if (path.Count >= 2) board.Submit(SketchKind.Erase, Radius, path.ToArray());   // the pending erase shows what the preview showed
            path.Clear();
            board.EndErasePreview();
        }

        // ---- drawing ------------------------------------------------------------------------------------------
        Color32 OwnColor() => board != null && board.LocalPlayer != null && colorOf != null ? colorOf(board.LocalPlayer) : (Color32)Color.white;

        void DrawLine()
        {
            if (line.Count >= 2) preview.SetLoose(line, Width, OwnColor());
            else preview.Clear();
        }

        /// <summary>
        /// The display's lines over the meshes: a line gone leaves its mesh, a new one goes into the first mesh with room;
        /// only meshes whose lines changed (or all, on a color change) are rebuilt, in drawing order.
        /// </summary>
        void Draw(MapSketch display, bool recolor)
        {
            order.Clear();
            if (display != null)
                for (var i = 0; i < display.Strokes.Count; i++) order[display.Strokes[i]] = i;
            for (var k = 0; k < layers.Length; k++)
            {
                var list = meshLines[k];
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (order.ContainsKey(list[i])) continue;
                    meshVertices[k] -= MapSketchGraphic.Cost(list[i]);
                    meshOf.Remove(list[i]);
                    list.RemoveAt(i);
                    meshDirty[k] = true;
                }
            }
            if (display != null)
                foreach (var s in display.Strokes)
                {
                    if (meshOf.ContainsKey(s)) continue;
                    var cost = MapSketchGraphic.Cost(s);
                    var k = 0;
                    while (k < layers.Length && meshVertices[k] + cost > MapGraphic.VertexBudget) k++;
                    if (k == layers.Length)
                    {
                        if (!overflowWarned) Debug.LogWarning("[지도] 그림 메시가 부족해 일부 선을 표시하지 못했습니다(그림 메시 수 확인 필요).", this);
                        overflowWarned = true;
                        continue;
                    }
                    meshLines[k].Add(s);
                    meshVertices[k] += cost;
                    meshOf[s] = k;
                    meshDirty[k] = true;
                }
            for (var k = 0; k < layers.Length; k++)
            {
                if (!meshDirty[k] && !recolor) continue;
                meshDirty[k] = false;
                meshLines[k].Sort((a, b) => order[a].CompareTo(order[b]));
                layers[k].SetLines(meshLines[k], colorOf);
            }
        }

        void ShowNote()
        {
            var now = Time.realtimeSinceStartupAsDouble;
            if (board != null && board.NoteVersion != noteShown)
            {
                noteShown = board.NoteVersion;
                if (board.Note != null)
                {
                    noteText.text = board.Note;
                    noteText.color = board.NoteIsError ? (Color)ArchiveUi.Error : (Color)ArchiveUi.IvoryDim;
                    noteUntil = now + noteSeconds;
                }
                else noteUntil = 0;
            }
            var show = now < noteUntil;
            if (noteText.enabled != show) noteText.enabled = show;
        }

        // ---- cursor -------------------------------------------------------------------------------------------
        /// <summary>
        /// Every tick: without focus only the arrow and no ring (nothing turns them on again until focus is back). With
        /// focus the tool cursor and ring follow where the pointer really is — inside the viewport and with the viewport
        /// as the topmost UI under it (a raycast now, not the last enter/exit, which may be stale after a focus change).
        /// </summary>
        void ShowCursor()
        {
            var mouse = Mouse.current;
            if (!Application.isFocused || mouse == null)
            {
                SetCursor(null, Vector2.zero);
                if (eraserRing.enabled) eraserRing.enabled = false;
                return;
            }
            var pointer = mouse.position.ReadValue();
            var over = editable && !input.Panning && PointerOnViewport(pointer);
            if (input.Panning) SetCursor(handCursor, handHotspot);
            else if (over && tool == MapSketchTool.Pencil) SetCursor(pencilCursor, pencilHotspot);
            else if (over) SetCursor(eraserCursor, eraserHotspot);
            else SetCursor(null, Vector2.zero);
            var ring = over && tool == MapSketchTool.Eraser;
            if (ring && view.TryScreenToViewport(pointer, view.EventCamera, out var local))
            {
                var size = 2f * Radius * view.Cell;                            // the reach at this zoom, in the viewport's units (UI size included)
                var r = eraserRing.rectTransform;
                r.anchoredPosition = local;
                if (!Mathf.Approximately(r.sizeDelta.x, size)) r.sizeDelta = new Vector2(size, size);
                if (!eraserRing.enabled) eraserRing.enabled = true;
            }
            else if (eraserRing.enabled) eraserRing.enabled = false;
        }

        /// <summary>The pointer is inside the viewport's rect and the viewport is the topmost UI under it (a tool button, for one, is not).</summary>
        bool PointerOnViewport(Vector2 screen)
        {
            if (!RectTransformUtility.RectangleContainsScreenPoint((RectTransform)input.transform, screen, view.EventCamera)) return false;
            var es = EventSystem.current;
            if (es == null) return input.Hovered;
            if (pointerData == null || pointerSystem != es)
            {
                pointerSystem = es;
                pointerData = new PointerEventData(es);
            }
            pointerData.position = screen;
            hits.Clear();
            es.RaycastAll(pointerData, hits);
            return hits.Count > 0 && hits[0].gameObject == input.gameObject;
        }

        /// <summary>The hardware cursor image (null = the arrow); set only when it changes. Its lock and visibility are never touched here.</summary>
        static void SetCursor(Texture2D image, Vector2 hotspot)
        {
            if (image == cursorShown && (image != null || !cursorCustom)) return;
            cursorShown = image;
            cursorCustom = image != null;
            Cursor.SetCursor(image, image != null ? hotspot : Vector2.zero, CursorMode.Auto);
        }
    }
}
