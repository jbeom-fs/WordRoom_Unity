using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using WordRoom.RoomDebug;
using WordRoom.World;

namespace WordRoom.Game
{
    /// <summary>
    /// The single-player map of the Gameplay scene: every room generated in this session (RoomGenerationDebug's
    /// ExploredMap — also rooms the 3D view has let go), visited or not, the current room, the target room once it
    /// exists, the connections, and the player's position and heading. Tab opens and closes it (once per press), the 닫기 button and
    /// Escape close it, the wheel zooms at the pointer, a right drag on the map pans, the left button uses the drawing
    /// tool (연필·지우개, MapSketchView), 현재 위치 centers on the player.
    /// Input rules (explicit, not by Update order): Escape belongs to the game menu, which closes an open map first
    /// and then stops, so one press never closes the map and opens the menu; only without a usable game menu does
    /// this component read Escape itself. The map does not open while the game menu (or its settings) is open, before
    /// the player is placed, after a stop, or while the result screen has it disabled — the result screen closes it
    /// first and disables it before taking input. While open the player is in map mode (FirstPersonPlayer.SetMapMode):
    /// walking, gravity, door aiming and E go on (doors open and entries are handled as usual), mouse look and click-to-lock stop, the cursor is free for
    /// the map; this is not the menu block, so it never lifts a menu's or the result's block, and the generation's
    /// walking lock stays as it is. Closing pauses input for one frame, then mouse look resumes. A new experiment closes it and
    /// starts the view over. The map's buttons have no keyboard navigation, so WASD only walks.
    /// Drawing: this screen owns the session's MapSketchBoard (Sketch). In single play it is attached to the explored
    /// map's session here (this device decides); in coop CoopGameplayFlow attaches it to the host. A new game drops it;
    /// closing and opening the map keeps it. Without the drawing parts (MapView.Sketch) the map works as before, drawing
    /// off (reported once).
    /// In coop the player mark takes this participant's robot color, and the other participants are drawn in the view's
    /// prepared remote marks from RemotePlayers' latest received poses (only in rooms of the explored map; display only).
    /// The display is the scene's Map Canvas (prefab instance, inactive while closed); nothing is made at runtime.
    /// With incomplete parts it reports once and never takes input.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapScreen : MonoBehaviour
    {
        [Tooltip("지도 데이터와 플레이어를 가진 방 생성 제어. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] RoomGenerationDebug game;
        [Tooltip("Esc를 처리하는 게임 메뉴. 비우면 같은 오브젝트의 것을 씁니다. 게임 메뉴의 Map이 이 컴포넌트여야 합니다.")]
        [SerializeField] PauseMenu pauseMenu;
        [Tooltip("월드 위치를 지도 좌표로 바꿀 3D 방 표시. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] RoomWorldView worldView;

        [Header("화면 (씬에 배치된 Map Canvas)")]
        [Tooltip("지도 화면 전체. 닫혀 있는 동안 비활성")]
        [SerializeField] GameObject display;
        [SerializeField] Canvas canvas;
        [SerializeField] MapView view;

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        Action onStarted;
        FirstPersonPlayer mapPlayer;          // the player put in map mode by this map
        bool ready;
        bool open;
        bool unusableReported;
        string viewSession;                  // explored-map session the view's center and zoom belong to (null = none)
        string[] slotIds;                    // PlayerId drawn in each remote mark of the view (null = empty slot)
        int boundVersion = int.MinValue;              // RemotePlayers.Version the slots were bound at
        string boundSession;                 // explored-map session the slots were bound at
        bool boundCoop;
        bool slotsShortReported;
        readonly MapSketchBoard sketch = new MapSketchBoard();
        MapSketchView sketchView;            // null = drawing off (missing or incomplete parts)
        Func<string, Color32> sketchColor;

        /// <summary>This device's drawing of the current session (attached by this screen in single play, by CoopGameplayFlow in coop).</summary>
        public MapSketchBoard Sketch => sketch;

        public bool IsOpen => open;

        void Awake()
        {
            if (game == null) game = GetComponent<RoomGenerationDebug>();
            if (pauseMenu == null) pauseMenu = GetComponent<PauseMenu>();
            if (worldView == null) worldView = GetComponent<RoomWorldView>();
            var missing = Missing();
            ready = missing == null;
            if (!ready)
            {
                Debug.LogError($"[지도] MapScreen을 쓸 수 없어 지도를 열지 않습니다(게임 입력은 그대로): {missing}. WordRoom/지도 화면 구성 메뉴로 Gameplay 씬의 지도 UI를 배치·연결하세요.", this);
                return;
            }
            view.Initialize();
            InitializeSketch();
            slotIds = new string[view.RemoteSlots];
            ArchiveUi.Bind(bindings, view.CloseButton, Close);
            ArchiveUi.Bind(bindings, view.RecenterButton, Recenter);
            onStarted = OnExperimentStarted;
            game.TrackExploredMap = true;                                  // map changes are copied from now on
            if (display.activeSelf) display.SetActive(false);              // closed until Tab
        }

        string Missing()
        {
            string missing = null;
            ArchiveUi.Need(game, nameof(game), ref missing);
            ArchiveUi.Need(worldView, nameof(worldView), ref missing);
            ArchiveUi.Need(display, nameof(display), ref missing);
            ArchiveUi.Need(canvas, nameof(canvas), ref missing);
            ArchiveUi.Need(view, nameof(view), ref missing);
            if (view != null)
            {
                var parts = view.MissingParts();
                if (parts != null) missing = missing == null ? $"view({parts})" : $"{missing}, view({parts})";
            }
            if (pauseMenu != null && pauseMenu.Map != this)
                missing = missing == null ? "pauseMenu.map(게임 메뉴가 이 지도를 가리키지 않음)" : $"{missing}, pauseMenu.map(게임 메뉴가 이 지도를 가리키지 않음)";
            return missing;
        }

        void OnEnable()
        {
            if (ready) game.ExperimentStarted += onStarted;
        }

        void OnDisable()
        {
            if (!ready) return;
            if (game != null) game.ExperimentStarted -= onStarted;
            Close();                                                       // never leaves the map mode behind
        }

        void OnDestroy() => ArchiveUi.Unbind(bindings);

        /// <summary>The game menu reads Escape when it is there and working; it closes the map first (see PauseMenu).</summary>
        bool MenuOwnsEscape => pauseMenu != null && pauseMenu.OwnsEscape;

        void Update()
        {
            if (!ready) return;
            if (open && mapPlayer != game.Player) SetMapPlayer(game.Player);   // the player was replaced while open
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.tabKey.wasPressedThisFrame)                              // once per press: holding does not repeat
            {
                if (open) Close();
                else TryOpen();
            }
            else if (open && !MenuOwnsEscape && kb.escapeKey.wasPressedThisFrame) Close();
        }

        void LateUpdate()
        {
            if (!open) return;
            var hasPose = TryPlayerPose(out var pose, out var heading);
            view.SetPlayerColor(TryCoopColor(game.ExploredMap.LocalPlayer, out var own) ? own : (Color?)null);
            view.Tick(game.ExploredMap, hasPose, pose, heading);
            DrawRemotes();
            if (sketchView != null)
            {
                AttachLocalSketch();
                var rp = game.RemotePlayers;
                sketchView.Tick(sketch, sketchColor, game.IsCoop ? (rp != null ? rp.Version : -1) : int.MaxValue);   // colors change only with the participants or the mode
            }
        }

        /// <summary>The drawing parts, checked once: incomplete parts turn drawing off (its tools hidden), never the map.</summary>
        void InitializeSketch()
        {
            var s = view.Sketch;
            string missing = s == null ? "MapView.sketch(비어 있음)" : s.MissingParts();
            if (missing != null)
            {
                if (s != null) s.Hide();
                Debug.LogWarning($"[지도] 지도 그림 부품이 없거나 빠져 그림 기능을 끕니다(지도의 다른 기능은 그대로): {missing}. WordRoom/지도 화면 구성 메뉴로 보완하세요.", this);
                return;
            }
            sketchView = s;
            sketchView.Initialize(view, view.Input);
            sketchColor = author => TryCoopColor(author, out var c) ? c : view.SoloColor;   // the participants' robot and mark colors; the mark's own color in single play
        }

        /// <summary>Single play: this device decides the drawing of the explored map's session (a new session starts an empty one).</summary>
        void AttachLocalSketch()
        {
            var map = game.ExploredMap;
            if (!game.IsCoop && map.HasData && map.Session != null) sketch.AttachLocal(map.Session, map.LocalPlayer);
        }

        /// <summary>A coop participant's color (the same as its robot's accent); false in single play or without RemotePlayers.</summary>
        bool TryCoopColor(string playerId, out Color color)
        {
            color = default;
            var rp = game.RemotePlayers;
            return game.IsCoop && rp != null && rp.TryGetAccent(playerId, out color);
        }

        /// <summary>
        /// The other participants' marks: each bound one with a fresh received pose standing in a room of the explored map,
        /// at its real position and heading (display only — nothing here confirms an entry, a visit or the goal). Every
        /// other slot is hidden. Never this device's own player.
        /// </summary>
        void DrawRemotes()
        {
            BindSlots();
            var rp = game.RemotePlayers;
            var map = game.ExploredMap;
            for (var i = 0; i < slotIds.Length; i++)
            {
                var id = slotIds[i];
                var show = false;
                Vector2 mp = default;
                var h = 0f;
                var color = default(Color);
                if (id != null && rp != null && rp.TryGetPose(id, out var position, out var yaw)
                    && worldView.TryGetMapPose(position, yaw, out mp, out h)
                    && map.Has(RoomWorldView.MapCell(mp))                      // never a room this map has not revealed
                    && rp.TryGetAccent(id, out color))
                    show = true;
                view.DrawRemote(i, show, mp, h, color);
            }
        }

        /// <summary>
        /// Slots follow the fixed participants (fixed order, this device's own and those who left skipped); rebound only
        /// when the participants, the session or the play mode change, never per frame.
        /// </summary>
        void BindSlots()
        {
            var rp = game.RemotePlayers;
            var version = rp != null ? rp.Version : -1;
            var session = game.ExploredMap.Session;
            var coop = game.IsCoop;
            if (version == boundVersion && session == boundSession && coop == boundCoop) return;
            boundVersion = version;
            boundSession = session;
            boundCoop = coop;
            for (var i = 0; i < slotIds.Length; i++) slotIds[i] = null;
            if (!coop || rp == null) return;
            var local = game.ExploredMap.LocalPlayer;
            var next = 0;
            var others = 0;
            var list = rp.Participants;
            for (var i = 0; i < list.Count; i++)
            {
                var id = list[i];
                if (id == local || !rp.IsPresent(id)) continue;
                others++;
                if (next < slotIds.Length) slotIds[next++] = id;
            }
            if (others > slotIds.Length && !slotsShortReported)
            {
                slotsShortReported = true;
                Debug.LogWarning($"[지도] 다른 참가자 {others}명 중 {slotIds.Length}명만 지도에 표시합니다(원격 마커 슬롯 부족). WordRoom/지도 화면 구성 메뉴로 Map Canvas의 원격 마커를 보완하세요.", this);
            }
        }

        bool TryPlayerPose(out Vector2 pose, out float heading)
        {
            pose = default;
            heading = 0f;
            var p = game.Player;
            return p != null && p.Placed && worldView.TryGetMapPose(p.transform, out pose, out heading);
        }

        /// <summary>Opens only when the map can really be used; the screen is checked before input is taken.</summary>
        void TryOpen()
        {
            if (open || !isActiveAndEnabled) return;
            if (!game.CanShowMap || !game.ExploredMap.HasData) return;    // loading, not placed, stopped
            if (pauseMenu != null && pauseMenu.IsOpen) return;             // never together with the game menu
            var displayWasActive = display.activeSelf;
            var modeTaken = false;
            try
            {
                if (!displayWasActive) display.SetActive(true);
                string reason = null;
                if (!display.activeInHierarchy) reason = $"'{display.name}'의 상위 오브젝트가 비활성";
                else if (!canvas.transform.IsChildOf(display.transform) || !canvas.isActiveAndEnabled) reason = $"Canvas '{canvas.name}'가 꺼져 있거나 지도 화면 밖에 있음";
                else if (!view.isActiveAndEnabled) reason = "MapView가 꺼져 있음";
                if (reason != null)
                {
                    if (!displayWasActive) display.SetActive(false);
                    ReportUnusable(reason);
                    return;
                }
                modeTaken = true;
                SetMapPlayer(game.Player);
                FirstPersonPlayer.ReleaseCursor();
                if (game.ExploredMap.Session != viewSession)                // a new session (also one started while this was disabled)
                {
                    viewSession = game.ExploredMap.Session;
                    view.BeginSession();
                }
                var hasPose = TryPlayerPose(out var pose, out _);
                view.Open(game.ExploredMap, hasPose, pose);
                open = true;
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                open = false;
                if (modeTaken) SetMapPlayer(null);
                if (display != null && !displayWasActive) display.SetActive(false);
                ReportUnusable("여는 중 오류(Console 확인)");
            }
        }

        /// <summary>Closes the map (also from the game menu's Escape and the result screen). Only its own input block is lifted.</summary>
        public void Close()
        {
            if (!open) return;
            open = false;
            if (sketchView != null) sketchView.Close();                    // a press in progress is dropped; the arrow again
            if (display != null) display.SetActive(false);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            SetMapPlayer(null);                                            // mouse look again after a one-frame input pause
        }

        void Recenter()
        {
            if (!open) return;
            if (sketchView != null) sketchView.Interrupt();                 // a line in progress ends where it was before the view moves
            if (TryPlayerPose(out var pose, out _)) view.CenterOn(pose);
            else view.CenterOn(new Vector2(game.ExploredMap.Current.X, game.ExploredMap.Current.Y));
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);   // Space/Enter never press it again while walking
        }

        void OnExperimentStarted()
        {
            Close();
            sketch.Detach();                                                   // a new game: the old drawing goes (coop attaches the new one when play starts)
            viewSession = game.ExploredMap.Session;
            view.BeginSession();
            boundVersion = int.MinValue;                                       // slots and colors are bound again on the next open
            boundSession = null;
        }

        /// <summary>Only the map mode of this map's player changes; menu blocks and the generation lock are untouched.</summary>
        void SetMapPlayer(FirstPersonPlayer p)
        {
            if (mapPlayer != null && mapPlayer != p) mapPlayer.SetMapMode(false);
            mapPlayer = p;
            if (mapPlayer != null) mapPlayer.SetMapMode(true);
        }

        void ReportUnusable(string reason)
        {
            if (unusableReported) return;
            unusableReported = true;
            Debug.LogError($"[지도] 지도 화면을 사용할 수 없어 열지 않았습니다 — {reason}. 게임 입력은 그대로입니다. Map Canvas 설정을 확인하세요.", this);
        }
    }
}
