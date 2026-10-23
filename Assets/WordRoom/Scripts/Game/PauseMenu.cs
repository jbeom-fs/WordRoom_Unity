using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
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
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PauseMenu : MonoBehaviour
    {
        [Tooltip("메뉴 글꼴(Pretendard-Black SDF)")]
        [SerializeField] TMP_FontAsset font;
        [Tooltip("플레이어를 찾을 방 생성 제어. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] RoomGenerationDebug game;
        [Tooltip("메인 메뉴 씬 이름(빌드 씬 목록에 있어야 함)")]
        [SerializeField] string mainMenuScene = GameLaunch.MainMenuScene;

        Canvas canvas;
        RectTransform list;
        Button continueButton;
        SettingsPanel settings;
        FirstPersonPlayer blocked;
        bool open;
        bool leaving;

        public bool IsOpen => open;

        void Awake()
        {
            if (game == null) game = GetComponent<RoomGenerationDebug>();
            ArchiveUi.Font = font;
            ArchiveUi.EnsureEventSystem();
            canvas = ArchiveUi.CreateCanvas(transform, "GameMenu Canvas", 200);   // above the HUD (50) and PlayHint (100)
            var overlay = ArchiveUi.NewRect("Overlay", canvas.transform);
            ArchiveUi.Stretch(overlay);
            ArchiveUi.AddImage(overlay.gameObject, ArchiveUi.Dim, true);       // clicks never reach the game

            list = ArchiveUi.Panel(canvas.transform, "Menu");
            list.anchorMin = list.anchorMax = list.pivot = new Vector2(0.5f, 0.5f);
            list.sizeDelta = new Vector2(480f, 0f);
            list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ArchiveUi.Vertical(list, 18f, 44, TextAnchor.UpperCenter);
            var title = ArchiveUi.AddText(list, "메뉴", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            ArchiveUi.Size(title.gameObject, 392f, 60f);
            continueButton = ArchiveUi.AddButton(list, "계속", Close, 360f, 64f, 26f);
            ArchiveUi.AddButton(list, "설정", ShowSettings, 360f, 64f, 26f);
            ArchiveUi.AddButton(list, "메인 메뉴", ToMainMenu, 360f, 64f, 26f);
            ArchiveUi.AddButton(list, "종료", ArchiveUi.QuitGame, 360f, 64f, 26f);

            settings = SettingsPanel.Create(canvas.transform);
            settings.Closed += ShowList;
            canvas.gameObject.SetActive(false);
            TakeEscape();
        }

        void Update()
        {
            TakeEscape();                                                    // also for a player connected later
            if (open && blocked != CurrentPlayer) Block(CurrentPlayer);      // the player was replaced while open
            var kb = Keyboard.current;
            if (leaving || kb == null || !kb.escapeKey.wasPressedThisFrame) return;
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
            if (open || leaving) return;
            open = true;
            canvas.gameObject.SetActive(true);
            Block(CurrentPlayer);
            FirstPersonPlayer.ReleaseCursor();
            ShowList();
        }

        public void Close()
        {
            if (!open) return;
            if (settings.IsOpen) settings.Close();
            open = false;
            canvas.gameObject.SetActive(false);
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
            list.gameObject.SetActive(true);
            ArchiveUi.Select(continueButton.gameObject);
        }

        void ShowSettings()
        {
            list.gameObject.SetActive(false);
            settings.Open();
        }

        /// <summary>Ends this game: loading the main menu destroys the session (map, doors, running work is cancelled).</summary>
        void ToMainMenu()
        {
            if (leaving) return;
            leaving = true;
            foreach (var b in canvas.GetComponentsInChildren<Button>(true)) b.interactable = false;
            if (settings.IsOpen) settings.Close();
            SceneManager.LoadScene(mainMenuScene);
        }

        void OnDisable()
        {
            if (blocked != null) blocked.SetInputBlocked(false);
            blocked = null;
            open = false;
        }
    }
}
