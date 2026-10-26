using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WordRoom.RoomDebug;
using WordRoom.World;

namespace WordRoom.Game
{
    /// <summary>
    /// The goal result of the Gameplay scene: 목표 달성, the play time, the visited room count and 메인 메뉴. It listens
    /// to RoomGenerationDebug.GoalReached (raised once per experiment for a confirmed entry into the target room) and
    /// shows the fixed values, formatted once (time cut to whole seconds, MM:SS below an hour, else HH:MM:SS). While
    /// shown it alone takes input: the game menu is closed and disabled (Escape neither opens it nor returns to
    /// play), the player's input is blocked (no look, walking, E, click-to-lock; the world and HUD stay behind),
    /// the cursor is free, a full-screen overlay catches clicks, and the "목표 도달" hint is left out. It stays until
    /// 메인 메뉴 is pressed (once: the scene change ends the session; preferences stay). A new experiment hides it and
    /// gives input and the menu back as they were.
    /// The display is the scene's Result Canvas (prefab instance, inactive while hidden); this component only
    /// controls it. Nothing is taken over before the screen is known to be usable: at the goal the references are
    /// checked again, the display is switched on, and only when it is active in the hierarchy, its Canvas is enabled
    /// and the 메인 메뉴 button is active and interactable are the menu, the hint and the input taken. Otherwise — or
    /// on an exception on the way — every step done is undone, the error is reported once, and the "목표 도달" hint
    /// and the game menu's way back to the main menu stay. A broken Canvas or button is never switched on by force.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ResultScreen : MonoBehaviour
    {
        [Tooltip("목표 도달을 알려 줄 방 생성 제어. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] RoomGenerationDebug game;
        [Tooltip("결과 화면 동안 닫고 끌 게임 메뉴. 비우면 같은 오브젝트의 것을 씁니다(없으면 끄지 않음).")]
        [SerializeField] PauseMenu pauseMenu;
        [Tooltip("결과 화면 동안 닫고 끌 지도. 비우면 같은 오브젝트의 것을 씁니다(없으면 끄지 않음).")]
        [SerializeField] MapScreen map;
        [Tooltip("메인 메뉴 씬 이름(빌드 씬 목록에 있어야 함)")]
        [SerializeField] string mainMenuScene = GameLaunch.MainMenuScene;

        [Header("화면 (씬에 배치된 Result Canvas)")]
        [Tooltip("결과 화면 전체(클릭을 막는 배경 포함). 숨겨진 동안 비활성")]
        [SerializeField] GameObject display;
        [Tooltip("결과 화면의 Canvas(display 또는 그 아래). 꺼져 있으면 결과 화면을 쓰지 않습니다.")]
        [SerializeField] Canvas canvas;
        [SerializeField] TextMeshProUGUI timeText;
        [SerializeField] TextMeshProUGUI visitedText;
        [SerializeField] Button mainMenuButton;

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        Action<GoalResult> onGoal;
        Action onStarted;
        FirstPersonPlayer blocked;
        bool ready;
        bool shown;
        bool leaving;
        bool unusableReported;

        // State before the result took over, given back by Hide.
        bool pauseWasEnabled;
        bool mapWasEnabled;
        bool hintWasSuppressed;

        public bool IsShown => shown;

        void Awake()
        {
            if (game == null) game = GetComponent<RoomGenerationDebug>();
            if (pauseMenu == null) pauseMenu = GetComponent<PauseMenu>();
            if (map == null) map = GetComponent<MapScreen>();
            var missing = MissingReferences();
            ready = missing == null;
            if (!ready)
            {
                Debug.LogError($"[결과] ResultScreen의 참조가 비어 있어 결과 화면을 쓰지 않습니다(목표 도달 안내와 Esc 메뉴의 메인 메뉴 복귀는 그대로): {missing}. WordRoom/결과 화면 구성 메뉴로 Gameplay 씬의 결과 UI를 배치·연결하세요.", this);
                return;
            }
            ArchiveUi.Bind(bindings, mainMenuButton, ToMainMenu);
            onGoal = Show;
            onStarted = Hide;
            if (display.activeSelf) display.SetActive(false);              // hidden until the goal
        }

        /// <summary>Names of the references that are not set or destroyed now; null when all are there.</summary>
        string MissingReferences()
        {
            string missing = null;
            ArchiveUi.Need(game, nameof(game), ref missing);
            ArchiveUi.Need(display, nameof(display), ref missing);
            ArchiveUi.Need(canvas, nameof(canvas), ref missing);
            ArchiveUi.Need(timeText, nameof(timeText), ref missing);
            ArchiveUi.Need(visitedText, nameof(visitedText), ref missing);
            ArchiveUi.Need(mainMenuButton, nameof(mainMenuButton), ref missing);
            return missing;
        }

        /// <summary>Why the switched-on display cannot be used (null = usable): checked on the Canvas, the hierarchy and the button only.</summary>
        string UnusableReason()
        {
            if (!display.activeInHierarchy) return $"'{display.name}'의 상위 오브젝트가 비활성";
            if (!canvas.transform.IsChildOf(display.transform)) return $"Canvas '{canvas.name}'가 '{display.name}' 아래에 없음";
            if (!canvas.isActiveAndEnabled) return $"Canvas '{canvas.name}'가 꺼져 있음";
            if (!mainMenuButton.transform.IsChildOf(display.transform)) return $"메인 메뉴 버튼 '{mainMenuButton.name}'이 '{display.name}' 아래에 없음";
            if (!mainMenuButton.IsActive()) return $"메인 메뉴 버튼 '{mainMenuButton.name}'이 비활성";
            if (!mainMenuButton.IsInteractable()) return $"메인 메뉴 버튼 '{mainMenuButton.name}'을 누를 수 없음(Interactable 또는 CanvasGroup)";
            return null;
        }

        void OnEnable()
        {
            if (!ready) return;
            game.GoalReached += onGoal;
            game.ExperimentStarted += onStarted;
        }

        void OnDisable()
        {
            if (!ready) return;
            if (game != null)
            {
                game.GoalReached -= onGoal;
                game.ExperimentStarted -= onStarted;
            }
            if (shown && !leaving) Hide();                                   // never leaves input blocked behind
        }

        void OnDestroy() => ArchiveUi.Unbind(bindings);

        void Update()
        {
            if (shown && !leaving && blocked != game.Player) Block(game.Player);   // the player was replaced while shown
        }

        /// <summary>
        /// Once per experiment. The screen is prepared and checked first; only a usable screen takes the game menu,
        /// the hint and the player's input (the menu is closed before the input block, so its unblock comes first).
        /// </summary>
        void Show(GoalResult result)
        {
            if (!ready || shown || leaving || !isActiveAndEnabled) return;
            var missing = MissingReferences();                             // objects may be gone since Awake
            if (missing != null)
            {
                ReportUnusable($"참조가 비어 있음: {missing}");
                return;
            }
            var displayWasActive = display.activeSelf;
            var pauseTaken = false;
            var mapTaken = false;
            var inputTaken = false;
            pauseWasEnabled = pauseMenu != null && pauseMenu.enabled;
            mapWasEnabled = map != null && map.enabled;
            hintWasSuppressed = game.SuppressGoalHint;
            try
            {
                timeText.text = FormatTime(result.Seconds);
                visitedText.text = $"{result.VisitedRooms}개";
                if (!displayWasActive) display.SetActive(true);
                var reason = UnusableReason();
                if (reason != null)
                {
                    if (!displayWasActive) display.SetActive(false);
                    ReportUnusable(reason);
                    return;
                }
                if (pauseMenu != null)
                {
                    pauseTaken = true;
                    pauseMenu.Close();                                     // its unblock happens before ours
                    pauseMenu.enabled = false;                             // no Escape menu over the result
                }
                if (map != null)
                {
                    mapTaken = true;
                    map.Close();                                           // its unblock also comes before ours
                    map.enabled = false;                                   // Tab does not open it over the result
                }
                inputTaken = true;
                Block(game.Player);
                FirstPersonPlayer.ReleaseCursor();
                game.SuppressGoalHint = true;
                shown = true;
                ArchiveUi.Select(mainMenuButton.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                shown = false;
                if (inputTaken) Block(null);                               // only the block taken here
                if (pauseTaken && pauseMenu != null) pauseMenu.enabled = pauseWasEnabled;
                if (mapTaken && map != null) map.enabled = mapWasEnabled;
                game.SuppressGoalHint = hintWasSuppressed;
                if (display != null && !displayWasActive) display.SetActive(false);
                ReportUnusable("표시 중 오류(Console 확인)");
            }
        }

        void ReportUnusable(string reason)
        {
            if (unusableReported) return;
            unusableReported = true;
            Debug.LogError($"[결과] 결과 화면을 사용할 수 없어 표시하지 않았습니다 — {reason}. 목표 도달 안내와 Esc 메뉴의 메인 메뉴 복귀는 그대로입니다. Result Canvas·버튼 설정을 확인하세요.", this);
        }

        /// <summary>Hidden again (new experiment or disabled): input, the game menu and the hint come back as they were.</summary>
        void Hide()
        {
            if (!shown) return;
            shown = false;
            if (display != null) display.SetActive(false);
            Block(null);
            if (game != null) game.SuppressGoalHint = hintWasSuppressed;
            if (pauseMenu != null) pauseMenu.enabled = pauseWasEnabled;
            if (map != null) map.enabled = mapWasEnabled;
        }

        void Block(FirstPersonPlayer p)
        {
            if (blocked != null && blocked != p) blocked.SetInputBlocked(false);
            blocked = p;
            if (blocked != null) blocked.SetInputBlocked(true);
        }

        /// <summary>Once: loading the main menu destroys the session (map, doors, running work); preferences stay.</summary>
        void ToMainMenu()
        {
            if (leaving) return;
            leaving = true;
            mainMenuButton.interactable = false;
            SceneManager.LoadScene(mainMenuScene);
        }

        /// <summary>Whole seconds (cut, never rounded up): MM:SS below an hour, else HH:MM:SS.</summary>
        public static string FormatTime(double seconds)
        {
            var total = seconds > 0d ? (long)Math.Floor(seconds) : 0L;
            var h = total / 3600;
            var m = total / 60 % 60;
            var s = total % 60;
            return h > 0 ? $"{h:00}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        }
    }
}
