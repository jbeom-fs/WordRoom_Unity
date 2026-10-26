using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UI;
using WordRoom.World;

namespace WordRoom.Game
{
    /// <summary>
    /// Main menu of the MainMenu scene: title and 시작 · 설정 · 종료. 시작 opens the difficulty choice (쉬움 · 보통 ·
    /// 어려움, 보통 preselected) — the game draws both words itself, nothing is typed. 게임 시작 hands the difficulty
    /// to Gameplay (GameLaunch) and loads it by scene name; all buttons lock at the first press, so repeated
    /// clicks cannot start twice. Escape goes back one screen. The cursor is free here. Nothing of the word data
    /// or the generator runs in this scene; the room behind the menu is a static prefab instance.
    /// The panels, buttons and the settings panel are the scene's MainMenu Canvas (a prefab instance saved in the
    /// scene); this component only switches them and handles their clicks. Listeners are added once in Awake.
    /// Missing references are reported once and the menu then does nothing (the cursor is still released).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        [Tooltip("시작할 게임 씬 이름(빌드 씬 목록에 있어야 함)")]
        [SerializeField] string gameplayScene = GameLaunch.GameplayScene;

        [Header("화면 (씬에 배치된 MainMenu Canvas)")]
        [SerializeField] GameObject mainPanel;
        [SerializeField] GameObject difficultyPanel;
        [Tooltip("같은 Canvas 안의 설정 패널(처음에는 비활성)")]
        [SerializeField] SettingsPanel settings;

        [Header("메인")]
        [SerializeField] Button startButton;
        [SerializeField] Button settingsButton;
        [SerializeField] Button quitButton;

        [Header("난이도")]
        [Tooltip("쉬움·보통·어려움 순서")]
        [SerializeField] Button[] difficultyButtons = new Button[3];
        [SerializeField] Button backButton;
        [SerializeField] Button startGameButton;
        [Tooltip("씬을 열 수 없을 때의 안내")]
        [SerializeField] TextMeshProUGUI message;
        [Tooltip("선택된 난이도 버튼의 Normal 색. 선택되지 않은 버튼은 Inspector에 지정된 Normal 색으로 돌아갑니다.")]
        [SerializeField] Color chosenColor = ArchiveUi.Chosen;

#pragma warning disable CS0414, CS0649   // read and written only by the Editor setup (SerializedObject), never at runtime
        // Value saved by the former runtime-built menu (field "font"), moved once onto this scene's canvas instance.
        [SerializeField, HideInInspector, FormerlySerializedAs("font")] TMP_FontAsset legacyFont = null;
        [SerializeField, HideInInspector] bool legacyUiMigrated;
#pragma warning restore CS0414, CS0649

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        Color[] normalColors;
        Button[] lockable;
        Difficulty chosen = Difficulty.Normal;
        bool ready;
        bool loading;

        void Awake()
        {
            ready = Validate();
            if (!ready) return;
            normalColors = new Color[difficultyButtons.Length];
            for (var i = 0; i < difficultyButtons.Length; i++) normalColors[i] = difficultyButtons[i].colors.normalColor;
            lockable = new[] { startButton, settingsButton, quitButton, difficultyButtons[0], difficultyButtons[1], difficultyButtons[2], backButton, startGameButton };

            ArchiveUi.Bind(bindings, startButton, ShowDifficulty);
            ArchiveUi.Bind(bindings, settingsButton, ShowSettings);
            ArchiveUi.Bind(bindings, quitButton, ArchiveUi.QuitGame);
            ArchiveUi.Bind(bindings, difficultyButtons[(int)Difficulty.Easy], () => Choose(Difficulty.Easy));
            ArchiveUi.Bind(bindings, difficultyButtons[(int)Difficulty.Normal], () => Choose(Difficulty.Normal));
            ArchiveUi.Bind(bindings, difficultyButtons[(int)Difficulty.Hard], () => Choose(Difficulty.Hard));
            ArchiveUi.Bind(bindings, backButton, ShowMain);
            ArchiveUi.Bind(bindings, startGameButton, StartGame);
            settings.Closed += ShowMain;
            Choose(Difficulty.Normal);
        }

        bool Validate()
        {
            string missing = null;
            ArchiveUi.Need(mainPanel, nameof(mainPanel), ref missing);
            ArchiveUi.Need(difficultyPanel, nameof(difficultyPanel), ref missing);
            ArchiveUi.Need(settings, nameof(settings), ref missing);
            ArchiveUi.Need(startButton, nameof(startButton), ref missing);
            ArchiveUi.Need(settingsButton, nameof(settingsButton), ref missing);
            ArchiveUi.Need(quitButton, nameof(quitButton), ref missing);
            if (difficultyButtons == null || difficultyButtons.Length != 3) missing = missing == null ? "difficultyButtons(3개)" : $"{missing}, difficultyButtons(3개)";
            else
                for (var i = 0; i < difficultyButtons.Length; i++) ArchiveUi.Need(difficultyButtons[i], $"difficultyButtons[{i}]", ref missing);
            ArchiveUi.Need(backButton, nameof(backButton), ref missing);
            ArchiveUi.Need(startGameButton, nameof(startGameButton), ref missing);
            ArchiveUi.Need(message, nameof(message), ref missing);
            if (missing == null) return true;
            Debug.LogError($"[메뉴] MainMenuController의 화면 참조가 비어 있어 메인 메뉴를 사용할 수 없습니다: {missing}. WordRoom/메인 메뉴와 게임 설정 구성 메뉴로 MainMenu 씬의 UI를 배치·연결하세요.", this);
            return false;
        }

        void OnDestroy()
        {
            ArchiveUi.Unbind(bindings);
            if (ready && settings != null) settings.Closed -= ShowMain;
        }

        void Start()
        {
            FirstPersonPlayer.ReleaseCursor();
            if (!ready) return;
            if (settings.IsOpen) settings.gameObject.SetActive(false);   // saved open in the scene: start on the main screen
            ShowMain();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (!ready || loading || kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            if (settings.IsOpen) settings.Close();
            else if (difficultyPanel.activeSelf) ShowMain();
        }

        void Choose(Difficulty d)
        {
            chosen = d;
            for (var i = 0; i < difficultyButtons.Length; i++)
                ArchiveUi.SetNormalColor(difficultyButtons[i], i == (int)d ? chosenColor : normalColors[i]);
        }

        void ShowMain()
        {
            if (loading) return;
            mainPanel.SetActive(true);
            difficultyPanel.SetActive(false);
            ArchiveUi.Select(startButton.gameObject);
        }

        void ShowDifficulty()
        {
            mainPanel.SetActive(false);
            difficultyPanel.SetActive(true);
            if (message.text.Length > 0) message.text = "";
            ArchiveUi.Select(startGameButton.gameObject);
        }

        void ShowSettings()
        {
            if (!settings.Open()) return;                                // incomplete panel (reported): stay on the main screen
            mainPanel.SetActive(false);
        }

        /// <summary>Once: hands the difficulty to Gameplay and loads it; every button locks meanwhile.</summary>
        void StartGame()
        {
            if (loading) return;
            loading = true;
            SetInteractable(false);
            GameLaunch.Request(chosen);
            var op = SceneManager.LoadSceneAsync(gameplayScene);
            if (op != null) return;
            GameLaunch.TryConsume(out _);                               // not started: drop the request again
            loading = false;
            SetInteractable(true);
            message.text = $"'{gameplayScene}' 씬을 열 수 없습니다. 메뉴 설치 도구로 빌드 씬 목록을 확인하세요.";
            Debug.LogError($"[메뉴] 게임 씬 '{gameplayScene}'을 불러올 수 없습니다(빌드 씬 목록 확인).");
        }

        void SetInteractable(bool on)
        {
            foreach (var b in lockable) b.interactable = on;
        }
    }
}
