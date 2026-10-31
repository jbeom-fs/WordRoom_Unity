using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UI;
using WordRoom.RoomDebug;
using WordRoom.World;

namespace WordRoom.Game
{
    /// <summary>
    /// The game menu of the Gameplay scene, owning the Escape key (the player's own Escape handling is turned off):
    /// Escape opens 계속 · 설정 · 메인 메뉴 · 종료, and Escape again goes back (settings → list → closed). While open the
    /// player's input is blocked (no look, walking, E, click-to-lock; gravity goes on) and the cursor is free; this
    /// block is separate from the generation lock, so closing never releases a lock a running request holds. On
    /// closing, the player takes input again from the next frame and locks the cursor only if it is placed. Time is
    /// not paused; a request already running may finish. 메인 메뉴 leaves the scene, which ends the session (its map
    /// and doors are dropped; preferences stay).
    /// With a map: an Escape while the map is open only closes the map (this press opens nothing), and the menu never
    /// opens over the map; the map itself refuses to open while this menu is open.
    /// The menu is the scene's GameMenu Canvas (a prefab instance saved in the scene, inactive while closed); this
    /// component only switches it and handles its clicks, with listeners added once in Awake. When its references
    /// are incomplete it reports once and leaves Escape to the player (cursor release), so play is never stuck.
    /// 끼임방지 (optional button, "끼임방지 (1회)"): asks RoomGenerationDebug for one return to the start room. The button
    /// can be pressed only when the game allows it (CanRequestUnstuck: placed, running, nothing of this device waiting,
    /// its one use of this game not spent) and stays visible but disabled otherwise — also after the use. The menu closes
    /// by itself once the body stands in the start room (UnstuckFinished true); input then resumes as after 계속. A
    /// failure is logged (a coop refusal is also told on screen by the game). Without the button the rest of the menu is
    /// unchanged (reported once).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PauseMenu : MonoBehaviour
    {
        [Tooltip("플레이어를 찾을 방 생성 제어. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] RoomGenerationDebug game;
        [Tooltip("메인 메뉴 씬 이름(빌드 씬 목록에 있어야 함)")]
        [SerializeField] string mainMenuScene = GameLaunch.MainMenuScene;

        [Header("화면 (씬에 배치된 GameMenu Canvas)")]
        [Tooltip("메뉴 전체(화면을 어둡게 덮고 클릭을 막는 배경 포함). 닫혀 있는 동안 비활성")]
        [SerializeField] GameObject menuRoot;
        [Tooltip("계속·설정·메인 메뉴·종료 목록")]
        [SerializeField] GameObject list;
        [Tooltip("같은 Canvas 안의 설정 패널(처음에는 비활성)")]
        [SerializeField] SettingsPanel settings;
        [SerializeField] Button continueButton;
        [SerializeField] Button settingsButton;
        [SerializeField] Button mainMenuButton;
        [SerializeField] Button quitButton;
        [Tooltip("지도(선택). 열려 있으면 Esc가 지도만 닫고 메뉴는 열지 않습니다.")]
        [SerializeField] MapScreen map;

        [Header("끼임방지 (선택)")]
        [Tooltip("시작 방으로 돌아가는 끼임방지 버튼(같은 메뉴 목록 안, 게임당 1회)")]
        [SerializeField] Button unstuckButton;

#pragma warning disable CS0414, CS0649   // read and written only by the Editor setup (SerializedObject), never at runtime
        // Value saved by the former runtime-built menu (field "font"), moved once onto this scene's canvas instance.
        [SerializeField, HideInInspector, FormerlySerializedAs("font")] TMP_FontAsset legacyFont = null;
        [SerializeField, HideInInspector] bool legacyUiMigrated;
#pragma warning restore CS0414, CS0649

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        Button[] lockable;
        FirstPersonPlayer blocked;
        bool ready;
        bool open;
        bool leaving;
        bool unstuckUsable;
        System.Action<bool, string> onUnstuck;

        public bool IsOpen => open;

        /// <summary>The map whose Escape this menu handles first (null = no map in the scene).</summary>
        public MapScreen Map => map;

        /// <summary>This menu reads Escape now (references complete and the component enabled).</summary>
        public bool OwnsEscape => ready && isActiveAndEnabled;

        void Awake()
        {
            if (game == null) game = GetComponent<RoomGenerationDebug>();
            ready = Validate();
            if (!ready) return;
            lockable = new[] { continueButton, settingsButton, mainMenuButton, quitButton };
            ArchiveUi.Bind(bindings, continueButton, Close);
            ArchiveUi.Bind(bindings, settingsButton, ShowSettings);
            ArchiveUi.Bind(bindings, mainMenuButton, ToMainMenu);
            ArchiveUi.Bind(bindings, quitButton, ArchiveUi.QuitGame);
            settings.Closed += ShowList;
            InitializeUnstuck();
            if (menuRoot.activeSelf) menuRoot.SetActive(false);              // closed until Escape
            TakeEscape();
        }

        bool Validate()
        {
            string missing = null;
            ArchiveUi.Need(game, nameof(game), ref missing);
            ArchiveUi.Need(menuRoot, nameof(menuRoot), ref missing);
            ArchiveUi.Need(list, nameof(list), ref missing);
            ArchiveUi.Need(settings, nameof(settings), ref missing);
            ArchiveUi.Need(continueButton, nameof(continueButton), ref missing);
            ArchiveUi.Need(settingsButton, nameof(settingsButton), ref missing);
            ArchiveUi.Need(mainMenuButton, nameof(mainMenuButton), ref missing);
            ArchiveUi.Need(quitButton, nameof(quitButton), ref missing);
            if (missing == null) return true;
            Debug.LogError($"[게임 메뉴] PauseMenu의 참조가 비어 있어 Esc 게임 메뉴를 사용하지 않습니다(Esc는 플레이어의 커서 해제로 남김): {missing}. WordRoom/메인 메뉴와 게임 설정 구성 메뉴로 Gameplay 씬의 UI를 배치·연결하세요.", this);
            return false;
        }

        void OnDestroy()
        {
            ArchiveUi.Unbind(bindings);
            if (ready && settings != null) settings.Closed -= ShowList;
            if (onUnstuck != null && game != null) game.UnstuckFinished -= onUnstuck;
        }

        /// <summary>The optional 끼임방지 button (reported once when missing); listeners added once.</summary>
        void InitializeUnstuck()
        {
            unstuckUsable = unstuckButton != null;
            if (!unstuckUsable)
            {
                Debug.LogWarning("[게임 메뉴] 끼임방지 버튼이 연결되지 않아 끼임방지를 메뉴에 표시하지 않습니다(메뉴의 다른 기능은 그대로). WordRoom/끼임방지·협동 결과 구성 메뉴로 보완하세요.", this);
                return;
            }
            lockable = new[] { continueButton, settingsButton, mainMenuButton, quitButton, unstuckButton };
            ArchiveUi.Bind(bindings, unstuckButton, RequestUnstuck);
            onUnstuck = OnUnstuckFinished;
            game.UnstuckFinished += onUnstuck;
        }

        void RequestUnstuck()
        {
            if (!open || leaving) return;
            if (!game.RequestUnstuck(out var why)) Debug.Log($"[끼임방지] 요청하지 않았습니다: {why}");   // nothing was sent
            RefreshUnstuck();
        }

        /// <summary>Done: the menu closes and play resumes as after 계속. Failed: logged (play may also be stopped with its reason on screen).</summary>
        void OnUnstuckFinished(bool ok, string why)
        {
            if (ok)
            {
                if (open && !leaving) Close();
                return;
            }
            Debug.LogWarning($"[끼임방지] 시작 방으로 돌아가지 못했습니다: {(string.IsNullOrEmpty(why) ? "이유 없음" : why)}");
            if (open) RefreshUnstuck();
        }

        /// <summary>While open: the button can be pressed only while a request is possible now (waiting, used, stopped: disabled, still shown).</summary>
        void RefreshUnstuck()
        {
            if (!unstuckUsable) return;
            var interactable = game.CanRequestUnstuck(out _) && !leaving;
            if (unstuckButton.interactable != interactable) unstuckButton.interactable = interactable;
        }

        void Update()
        {
            if (!ready) return;
            TakeEscape();                                                    // also for a player connected later
            if (open && blocked != CurrentPlayer) Block(CurrentPlayer);      // the player was replaced while open
            if (open) RefreshUnstuck();
            var kb = Keyboard.current;
            if (leaving || kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            if (map != null && map.IsOpen)
            {
                map.Close();                                                 // this press only closes the map
                return;
            }
            if (!open) Open();
            else if (settings.IsOpen) settings.Close();
            else Close();
        }

        FirstPersonPlayer CurrentPlayer => game != null ? game.Player : null;

        void TakeEscape()
        {
            var p = CurrentPlayer;
            if (p != null) p.HandlesEscape = false;
        }

        public void Open()
        {
            if (!ready || open || leaving) return;
            if (map != null && map.IsOpen) return;                           // never together with the map
            open = true;
            menuRoot.SetActive(true);
            Block(CurrentPlayer);
            FirstPersonPlayer.ReleaseCursor();
            ShowList();
            RefreshUnstuck();
        }

        public void Close()
        {
            if (!open) return;
            if (settings.IsOpen) settings.Close();
            open = false;
            menuRoot.SetActive(false);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);   // WASD/Enter no longer reach a menu button
            Block(null);                                                     // input again from the next frame
        }

        void Block(FirstPersonPlayer p)
        {
            if (blocked != null && blocked != p) blocked.SetInputBlocked(false);
            blocked = p;
            if (blocked != null) blocked.SetInputBlocked(true);
        }

        void ShowList()
        {
            if (!open) return;
            list.SetActive(true);
            ArchiveUi.Select(continueButton.gameObject);
        }

        void ShowSettings()
        {
            if (!settings.Open()) return;                                    // incomplete panel (reported): stay on the list
            list.SetActive(false);
        }

        /// <summary>
        /// Ends this game: loading the main menu destroys the session (map, doors, running work is cancelled). In a coop
        /// game this is this player's own leave: the coop room is left (the connection ended only when the coop session
        /// owns it), and a host leaving before the common start stops the start for the others first.
        /// </summary>
        void ToMainMenu()
        {
            if (leaving) return;
            leaving = true;
            foreach (var b in lockable) b.interactable = false;
            if (settings.IsOpen) settings.Close();
            var coop = WordRoom.Network.CoopSession.Current;
            if (coop != null) coop.LeaveGame();
            SceneManager.LoadScene(mainMenuScene);
        }

        void OnDisable()
        {
            if (blocked != null) blocked.SetInputBlocked(false);
            blocked = null;
            if (open && !leaving && menuRoot != null) menuRoot.SetActive(false);   // a disabled menu never stays on screen
            open = false;
        }
    }
}
