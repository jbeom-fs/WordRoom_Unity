using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Generation;
using WordRoom.World;

namespace WordRoom.Game
{
    /// <summary>
    /// The drawing part of the map screen (on the Map Canvas prefab, with its parts connected in the prefab): north-up
    /// view of an ExploredMap with a center (map units) and a cell size (canvas units per room), wheel zoom that keeps
    /// the map point under the pointer, left drag to pan, and the player's mark at the player's real position and
    /// heading.
    /// Resources are fixed and reused: one MapGraphic mesh (rebuilt only when the data version or the view changed),
    /// one MapMarker moved by its RectTransform, and a pool of word texts made in the Editor. Only rooms inside the
    /// viewport get words; a room keeps its text while it stays visible, so a string is written only for a room that
    /// comes into view.
    /// Capacity (recomputed when the viewport size or the number of target rooms changes): the smallest cell is the
    /// first size at which MapGraphic.MaxVertices — every looked-up cell a room with tile and lines, the current
    /// border, every target room's border and star — stays within MapGraphic.VertexBudget. When that is above the
    /// configured largest cell, the largest cell is raised to it (safety first; reported once): nothing is ever cut
    /// from the mesh. Words show only from the cell size at which every visible cell fits the word pool.
    /// Word placement: every word sits at one fixed place of its own tile — the lower band, within the tile width so
    /// neighbors never overlap — whatever the player does. The player's mark is drawn at its real position and
    /// heading on top; neither is moved for the other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapView : MonoBehaviour
    {
        [Header("부품 (프리팹 안)")]
        [SerializeField] Canvas canvas;
        [Tooltip("지도 영역(RectMask2D·배경·드래그 입력). 방·단어·플레이어는 이 영역 가운데를 기준으로 그립니다.")]
        [SerializeField] RectTransform viewport;
        [SerializeField] MapViewportInput input;
        [SerializeField] MapGraphic graphic;
        [SerializeField] MapMarker playerMarker;
        [Tooltip("편집 단계에서 미리 만든 단어 텍스트 풀")]
        [SerializeField] TextMeshProUGUI[] words = new TextMeshProUGUI[0];
        [SerializeField] Button closeButton;
        [SerializeField] Button recenterButton;
        [Tooltip("범례의 플레이어 견본(선택). 실행 중 플레이어 삼각형 색을 씁니다.")]
        [SerializeField] Graphic playerSwatch;

        [Header("배율 (칸 크기 = 방 하나의 Canvas 단위 크기)")]
        [SerializeField] float defaultCell = 120f;
        [Tooltip("가장 축소했을 때의 칸 크기. 보이는 칸이 메시 한도를 넘지 않도록 실제 최솟값은 지도 영역 크기·목표 방 수에 맞춰 올라갈 수 있습니다.")]
        [SerializeField] float minCell = 28f;
        [Tooltip("가장 확대했을 때의 칸 크기. 안전한 최솟값이 이보다 크면 이 값이 그만큼 올라갑니다.")]
        [SerializeField] float maxCell = 260f;
        [Tooltip("휠 한 칸당 확대·축소 배율")]
        [SerializeField] float zoomStep = 1.15f;
        [Tooltip("이 칸 크기 이상에서만 단어를 표시합니다. 보이는 칸 수가 단어 풀보다 많으면 실제 값은 올라갑니다.")]
        [SerializeField] float wordMinCell = 100f;

        [Header("단어·플레이어 표시")]
        [Tooltip("단어 글자 크기 / 칸 크기")]
        [SerializeField] float wordFraction = 0.2f;
        [SerializeField] float wordMinFont = 10f;
        [SerializeField] float wordMaxFont = 28f;
        [Tooltip("단어 띠 중심이 방 가운데에서 아래로 떨어진 거리(칸 크기 대비). 모든 방에서 같은 자리")]
        [SerializeField] float wordBand = 0.27f;
        [Tooltip("단어 띠 높이 / 칸 크기")]
        [SerializeField] float wordBandHeight = 0.2f;
        [Tooltip("플레이어 삼각형 크기 / 칸 크기")]
        [SerializeField] float markerFraction = 0.22f;
        [SerializeField] float markerMinSize = 16f;

        public Button CloseButton => closeButton;
        public Button RecenterButton => recenterButton;

        Vector2 center;
        float cell;
        bool viewSet;                        // this session's first open placed the view
        bool dirty = true;
        int drawnVersion = -1;
        Vector2 limitsFor = new Vector2(-1f, -1f);
        int limitsTargets = -1;
        float effectiveMinCell, effectiveMaxCell, effectiveWordCell;
        bool wordLimitLogged, maxRaisedLogged, missingSlotLogged;
        bool initialized;

        // Word pool: slot i shows room slotPos[i] while slotUsed[i]; stamp marks the slots kept in this pass.
        readonly Dictionary<GridPos, int> slotOf = new Dictionary<GridPos, int>();
        readonly Stack<int> free = new Stack<int>();
        readonly List<GridPos> waiting = new List<GridPos>();
        GridPos[] slotPos;
        bool[] slotUsed;
        int[] slotStamp;
        float[] slotCell;
        int pass;
        float markerSize = -1f;

        /// <summary>Why the parts cannot be used (null = usable).</summary>
        public string MissingParts()
        {
            string missing = null;
            ArchiveUi.Need(canvas, nameof(canvas), ref missing);
            ArchiveUi.Need(viewport, nameof(viewport), ref missing);
            ArchiveUi.Need(input, nameof(input), ref missing);
            ArchiveUi.Need(graphic, nameof(graphic), ref missing);
            ArchiveUi.Need(playerMarker, nameof(playerMarker), ref missing);
            ArchiveUi.Need(closeButton, nameof(closeButton), ref missing);
            ArchiveUi.Need(recenterButton, nameof(recenterButton), ref missing);
            if (words == null || words.Length == 0) missing = missing == null ? "words(풀 비어 있음)" : $"{missing}, words(풀 비어 있음)";
            else
                for (var i = 0; i < words.Length; i++) ArchiveUi.Need(words[i], $"words[{i}]", ref missing);
            return missing;
        }

        /// <summary>Once, when its controller is ready: pointer events, the word pool and the player swatch.</summary>
        public void Initialize()
        {
            if (initialized) return;
            initialized = true;
            input.Zoomed += OnZoom;
            input.Dragged += OnDrag;
            var n = words.Length;
            slotPos = new GridPos[n];
            slotUsed = new bool[n];
            slotStamp = new int[n];
            slotCell = new float[n];
            for (var i = n - 1; i >= 0; i--)
            {
                free.Push(i);
                if (words[i].enabled) words[i].enabled = false;
            }
            if (playerMarker.enabled) playerMarker.enabled = false;
            if (playerSwatch != null && playerSwatch.color != playerMarker.color) playerSwatch.color = playerMarker.color;
        }

        void OnDestroy()
        {
            if (!initialized || input == null) return;
            input.Zoomed -= OnZoom;
            input.Dragged -= OnDrag;
        }

        /// <summary>New session: the next open centers on the player again at the default size; words of the old map go.</summary>
        public void BeginSession()
        {
            viewSet = false;
            drawnVersion = -1;
            dirty = true;
            if (initialized) ReleaseAllWords();
        }

        /// <summary>Called when the map opens: the first time in a session at the player (or the current room), otherwise as left.</summary>
        public void Open(ExploredMap map, bool hasPose, Vector2 pose)
        {
            UpdateLimits(map.TargetCount);
            if (!viewSet)
            {
                viewSet = true;
                center = hasPose ? pose : new Vector2(map.Current.X, map.Current.Y);
                cell = Mathf.Clamp(defaultCell, effectiveMinCell, effectiveMaxCell);
            }
            dirty = true;
        }

        public void CenterOn(Vector2 p)
        {
            center = p;
            dirty = true;
        }

        /// <summary>While open, once per frame after the player moved: redraw only on change; the mark follows the player.</summary>
        public void Tick(ExploredMap map, bool hasPose, Vector2 pose, float heading)
        {
            if (viewport.rect.size != limitsFor || map.TargetCount != limitsTargets)
            {
                UpdateLimits(map.TargetCount);
                dirty = true;
            }
            var redraw = dirty || map.Version != drawnVersion;
            if (redraw)
            {
                dirty = false;
                drawnVersion = map.Version;
                graphic.SetView(map, center, cell);
            }
            DrawMarker(hasPose, pose, heading);
            if (redraw) DrawWords(map);
        }

        // ---- capacity -----------------------------------------------------------------------------------------
        /// <summary>Safe smallest and largest cells for the mesh, and the smallest cell with words, at the current viewport size.</summary>
        void UpdateLimits(int targetRooms)
        {
            var size = viewport.rect.size;
            limitsFor = size;
            limitsTargets = targetRooms;
            var meshMin = CellFor(s => MapGraphic.MaxVertices(MapGraphic.MaxCellsInRange(size, s, MapGraphic.LookupMargin), targetRooms) <= MapGraphic.VertexBudget, size);
            effectiveMinCell = Mathf.Max(minCell, meshMin);
            effectiveMaxCell = Mathf.Max(maxCell, effectiveMinCell);          // the safe minimum is never lowered for the maximum
            if (effectiveMaxCell > maxCell + 0.5f && !maxRaisedLogged)
            {
                maxRaisedLogged = true;
                Debug.LogWarning($"[지도] 지도 영역 {size.x:0}×{size.y:0}에서 메시 한도({MapGraphic.VertexBudget}정점)를 지키려면 칸 크기가 {effectiveMinCell:0} 이상이어야 해 최대 칸 크기 {maxCell:0}을 {effectiveMaxCell:0}으로 올립니다(목표 방 {targetRooms}개).", this);
            }
            effectiveWordCell = Mathf.Max(wordMinCell, CellFor(s => MapGraphic.MaxCellsInRange(size, s, 0) <= words.Length, size));
            if (effectiveWordCell > wordMinCell + 0.5f && !wordLimitLogged)
            {
                wordLimitLogged = true;
                Debug.LogWarning($"[지도] 단어 풀 {words.Length}개로는 칸 {wordMinCell:0}부터 보이는 모든 방의 단어를 표시할 수 없어 단어 표시 시작 칸 크기를 {effectiveWordCell:0}으로 올립니다(지도 영역 {size.x:0}×{size.y:0}). 풀을 늘리려면 지도 프리팹을 다시 구성하세요.", this);
            }
            if (effectiveWordCell > effectiveMaxCell && !wordLimitLogged)
            {
                wordLimitLogged = true;
                Debug.LogWarning($"[지도] 단어 표시 칸 크기 {effectiveWordCell:0}이 최대 칸 크기 {effectiveMaxCell:0}보다 커서 단어를 표시할 수 없습니다. 단어 풀이나 최대 칸 크기를 늘리세요.", this);
            }
            if (cell > 0f) cell = Mathf.Clamp(cell, effectiveMinCell, effectiveMaxCell);
        }

        /// <summary>
        /// Smallest whole cell size for which `fits` holds (binary search; the counts only fall as cells grow). At a cell
        /// as large as the viewport only a few cells are looked up, so a size that fits always exists.
        /// </summary>
        static float CellFor(Func<float, bool> fits, Vector2 size)
        {
            float lo = 1f, hi = Mathf.Max(1f, Mathf.Max(size.x, size.y)) + 1f;
            while (!fits(hi)) hi *= 2f;
            for (var i = 0; i < 24; i++)
            {
                var mid = (lo + hi) * 0.5f;
                if (fits(mid)) hi = mid;
                else lo = mid;
            }
            return Mathf.Ceil(hi);
        }

        // ---- pointer ------------------------------------------------------------------------------------------
        void OnZoom(Vector2 screen, int direction, Camera eventCamera)
        {
            if (cell <= 0f || !RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, screen, eventCamera, out var local)) return;
            local -= viewport.rect.center;
            var under = center + local / cell;                                 // the map point under the pointer stays there
            var next = Mathf.Clamp(direction > 0 ? cell * zoomStep : cell / zoomStep, effectiveMinCell, effectiveMaxCell);
            if (Mathf.Approximately(next, cell)) return;
            cell = next;
            center = under - local / cell;
            dirty = true;
        }

        void OnDrag(Vector2 screenDelta, Camera eventCamera)
        {
            if (cell <= 0f) return;
            var scale = canvas.rootCanvas.scaleFactor;
            if (scale <= 0f) scale = 1f;
            center -= screenDelta / scale / cell;
            dirty = true;
        }

        // ---- words --------------------------------------------------------------------------------------------
        void DrawWords(ExploredMap map)
        {
            pass++;
            waiting.Clear();
            if (cell >= effectiveWordCell)
            {
                MapGraphic.VisibleRange(viewport.rect, center, cell, 0, out var minX, out var maxX, out var minY, out var maxY);
                for (var y = minY; y <= maxY; y++)
                    for (var x = minX; x <= maxX; x++)
                    {
                        var p = new GridPos(x, y);
                        if (!map.Has(p)) continue;
                        if (slotOf.TryGetValue(p, out var i)) slotStamp[i] = pass;
                        else waiting.Add(p);
                    }
            }
            for (var i = 0; i < words.Length; i++)                              // rooms gone from view give their texts back
                if (slotUsed[i] && slotStamp[i] != pass) Release(i);
            foreach (var p in waiting)
            {
                if (free.Count == 0)
                {
                    if (!missingSlotLogged) Debug.LogWarning("[지도] 단어 풀이 부족해 일부 단어를 표시하지 못했습니다(용량 계산 확인 필요).", this);
                    missingSlotLogged = true;
                    break;
                }
                var i = free.Pop();
                map.TryGet(p, out var room);
                slotUsed[i] = true;
                slotStamp[i] = pass;
                slotPos[i] = p;
                slotCell[i] = -1f;
                slotOf[p] = i;
                if (words[i].text != room.Word) words[i].text = room.Word;     // only a room that came into view is rewritten
                words[i].enabled = true;
            }
            for (var i = 0; i < words.Length; i++)
                if (slotUsed[i]) Layout(i);
        }

        /// <summary>Slot i's word at its room's fixed lower band (inside the tile width): position with the view, size with the zoom.</summary>
        void Layout(int i)
        {
            var p = slotPos[i];
            var t = words[i];
            var width = Mathf.Max(1f, graphic.TileHalf(cell) * 2f * 0.95f);
            t.rectTransform.anchoredPosition = new Vector2((p.X - center.x) * cell, (p.Y - center.y - wordBand) * cell);
            if (Mathf.Approximately(slotCell[i], cell)) return;               // size changes only with the zoom
            slotCell[i] = cell;
            var length = Mathf.Max(1, t.text.Length);
            var fit = width / length;                                          // Hangul ≈ 1 em wide: never wider than its space
            var size = Mathf.Min(Mathf.Min(cell * wordFraction, wordBandHeight * cell), wordMaxFont);
            t.fontSize = Mathf.Min(fit, Mathf.Max(size, wordMinFont));       // the minimum never makes it wider than its space
            t.rectTransform.sizeDelta = new Vector2(width, wordBandHeight * cell);
        }

        void Release(int i)
        {
            slotUsed[i] = false;
            slotOf.Remove(slotPos[i]);
            if (words[i].enabled) words[i].enabled = false;
            free.Push(i);
        }

        void ReleaseAllWords()
        {
            for (var i = 0; i < words.Length; i++)
                if (slotUsed[i]) Release(i);
        }

        // ---- player -------------------------------------------------------------------------------------------
        float MarkerSize() => Mathf.Max(markerMinSize, cell * markerFraction);

        void DrawMarker(bool hasPose, Vector2 pose, float heading)
        {
            if (!hasPose)
            {
                if (playerMarker.enabled) playerMarker.enabled = false;
                return;
            }
            var size = MarkerSize();
            var r = playerMarker.rectTransform;
            if (!Mathf.Approximately(size, markerSize))
            {
                markerSize = size;
                r.sizeDelta = new Vector2(size, size);
            }
            r.anchoredPosition = (pose - center) * cell;                       // the real position, never shifted for the words
            r.localRotation = Quaternion.Euler(0f, 0f, -heading);              // clockwise from 상, the map itself never turns
            if (!playerMarker.enabled) playerMarker.enabled = true;
        }
    }
}
