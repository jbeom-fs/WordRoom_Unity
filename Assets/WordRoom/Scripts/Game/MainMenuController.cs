using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        [Tooltip("메뉴 글꼴(Pretendard-Black SDF)")]
        [SerializeField] TMP_FontAsset font;
        [Tooltip("시작할 게임 씬 이름(빌드 씬 목록에 있어야 함)")]
        [SerializeField] string gameplayScene = GameLaunch.GameplayScene;

        RectTransform mainPanel, difficultyPanel;
        SettingsPanel settings;
        Button startButton, startGameButton;
        readonly Button[] difficultyButtons = new Button[3];
        TextMeshProUGUI message;
        Difficulty chosen = Difficulty.Normal;
        bool loading;

        void Awake()
        {
            ArchiveUi.Font = font;
            ArchiveUi.EnsureEventSystem();
            var canvas = ArchiveUi.CreateCanvas(transform, "MainMenu Canvas", 100);
            BuildMain(canvas.transform);
            BuildDifficulty(canvas.transform);
            settings = SettingsPanel.Create(canvas.transform);
            settings.Closed += ShowMain;
        }

        void Start()
        {
            FirstPersonPlayer.ReleaseCursor();
            ShowMain();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (loading || kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            if (settings.IsOpen) settings.Close();
            else if (difficultyPanel.gameObject.activeSelf) ShowMain();
        }

        void BuildMain(Transform canvas)
        {
            mainPanel = ArchiveUi.Panel(canvas, "Main");
            Center(mainPanel, 520f);
            ArchiveUi.Vertical(mainPanel, 20f, 48, TextAnchor.UpperCenter);
            var title = ArchiveUi.AddText(mainPanel, "WordRoom", 84f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            ArchiveUi.Size(title.gameObject, 424f, 120f);
            startButton = ArchiveUi.AddButton(mainPanel, "시작", ShowDifficulty, 360f, 68f, 28f);
            ArchiveUi.AddButton(mainPanel, "설정", ShowSettings, 360f, 68f, 28f);
            ArchiveUi.AddButton(mainPanel, "종료", ArchiveUi.QuitGame, 360f, 68f, 28f);
        }

        void BuildDifficulty(Transform canvas)
        {
            difficultyPanel = ArchiveUi.Panel(canvas, "Difficulty");
            Center(difficultyPanel, 760f);
            ArchiveUi.Vertical(difficultyPanel, 18f, 44, TextAnchor.UpperCenter);
            var title = ArchiveUi.AddText(difficultyPanel, "난이도 선택", 44f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            ArchiveUi.Size(title.gameObject, 672f, 64f);
            foreach (var d in new[] { Difficulty.Easy, Difficulty.Normal, Difficulty.Hard })
            {
                var option = d;
                var label = $"{GameLaunch.Label(d)}\n<size=20><color=#D8D0BF>{GameLaunch.Description(d)}</color></size>";
                difficultyButtons[(int)d] = ArchiveUi.AddButton(difficultyPanel, label, () => Choose(option), 640f, 96f, 30f);
            }
            message = ArchiveUi.AddText(difficultyPanel, "", 20f, ArchiveUi.IvoryDim, TextAlignmentOptions.Center);
            ArchiveUi.Size(message.gameObject, 640f, 30f);
            var footer = ArchiveUi.NewRect("Footer", difficultyPanel);
            ArchiveUi.Horizontal(footer, 20f, TextAnchor.MiddleCenter);
            ArchiveUi.Size(footer.gameObject, 640f, 68f);
            ArchiveUi.AddButton(footer, "뒤로", ShowMain, 260f, 64f, 26f);
            startGameButton = ArchiveUi.AddButton(footer, "게임 시작", StartGame, 260f, 64f, 26f);
            Choose(Difficulty.Normal);
        }

        /// <summary>Centered panel of fixed width whose height follows its content.</summary>
        static void Center(RectTransform panel, float width)
        {
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0.5f, 0.5f);
            panel.sizeDelta = new Vector2(width, 0f);
            panel.anchoredPosition = Vector2.zero;
            panel.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        void Choose(Difficulty d)
        {
            chosen = d;
            for (var i = 0; i < difficultyButtons.Length; i++)
                if (difficultyButtons[i] != null) ArchiveUi.SetChosen(difficultyButtons[i], i == (int)d);
        }

        void ShowMain()
        {
            if (loading) return;
            mainPanel.gameObject.SetActive(true);
            difficultyPanel.gameObject.SetActive(false);
            ArchiveUi.Select(startButton.gameObject);
        }

        void ShowDifficulty()
        {
            mainPanel.gameObject.SetActive(false);
            difficultyPanel.gameObject.SetActive(true);
            message.text = "";
            ArchiveUi.Select(startGameButton.gameObject);
        }

        void ShowSettings()
        {
            mainPanel.gameObject.SetActive(false);
            settings.Open();
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
            foreach (var b in GetComponentsInChildren<Button>(true)) b.interactable = on;
        }
    }
}
