using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.World;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Escape menu of the shelf prototype, owning the Escape key while it is usable (the player's own Escape handling
    /// is turned off then, so one press is read once). Escape opens 재시작 · 설정 · 메인으로 · 게임종료; Escape again or
    /// 재시작 closes it and play goes on exactly where it was (room, open passage, path, moves and timer untouched —
    /// 재시작 is a resume, a new run only comes from the result screen's 다시 도전). 설정 and 메인으로 are shown but
    /// disabled in this prototype; 게임종료 uses the project's quit (ArchiveUi.QuitGame).
    /// While open the player's input is blocked (no look, walking, E, click-to-lock; gravity goes on) and the cursor is
    /// free; closing unblocks from the next frame and locks the cursor again only for a placed player. Time is not
    /// paused. The menu is the scene's Shelf Menu Canvas (prefab instance, inactive while closed), saved in the scene
    /// with its references. With incomplete references it reports once and leaves Escape to the player.
    /// The demo turns this component off while the result screen is shown (no menu over the result).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfPauseMenu : MonoBehaviour
    {
        [Tooltip("입력을 막고 Esc를 넘겨받을 1인칭 플레이어")]
        [SerializeField] FirstPersonPlayer player;

        [Header("화면 (씬에 배치된 Shelf Menu Canvas)")]
        [Tooltip("메뉴 전체(화면을 어둡게 덮고 클릭을 막는 배경 포함). 닫혀 있는 동안 비활성")]
        [SerializeField] GameObject menuRoot;
        [Tooltip("재시작 = 이어서 하기(Resume)")]
        [SerializeField] Button resumeButton;
        [Tooltip("설정(이번 시안에서는 비활성)")]
        [SerializeField] Button settingsButton;
        [Tooltip("메인으로(이번 시안에서는 비활성)")]
        [SerializeField] Button mainMenuButton;
        [SerializeField] Button quitButton;

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        bool ready;
        bool open;
        bool leaving;
        bool blocking;

        public bool IsOpen => open;

        void Awake()
        {
            string missing = null;
            ArchiveUi.Need(player, nameof(player), ref missing);
            ArchiveUi.Need(menuRoot, nameof(menuRoot), ref missing);
            ArchiveUi.Need(resumeButton, nameof(resumeButton), ref missing);
            ArchiveUi.Need(settingsButton, nameof(settingsButton), ref missing);
            ArchiveUi.Need(mainMenuButton, nameof(mainMenuButton), ref missing);
            ArchiveUi.Need(quitButton, nameof(quitButton), ref missing);
            ready = missing == null;
            if (!ready)
            {
                Debug.LogError($"[서가 메뉴] ShelfPauseMenu의 참조가 비어 있어 Esc 메뉴를 쓰지 않습니다(Esc는 플레이어의 커서 해제로 남김): {missing}. ShelfCorridorPrototype 씬의 Shelf Menu Canvas 참조를 Inspector에서 확인하세요.", this);
                return;
            }
            ArchiveUi.Bind(bindings, resumeButton, Close);
            ArchiveUi.Bind(bindings, quitButton, Quit);
            settingsButton.interactable = false;                             // shown, not usable in this prototype
            mainMenuButton.interactable = false;
            if (menuRoot.activeSelf) menuRoot.SetActive(false);              // closed until Escape
        }

        void OnEnable()
        {
            if (ready) player.HandlesEscape = false;                         // this menu reads Escape
        }

        void OnDisable()
        {
            Close();
            if (ready && player != null) player.HandlesEscape = true;        // nobody else reads it now
        }

        void OnDestroy() => ArchiveUi.Unbind(bindings);

        void Update()
        {
            if (!ready || leaving) return;
            player.HandlesEscape = false;
            var kb = Keyboard.current;
            if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            if (open) Close();
            else Open();
        }

        public void Open()
        {
            if (!ready || open || leaving || !isActiveAndEnabled) return;
            open = true;
            menuRoot.SetActive(true);
            player.SetInputBlocked(true);
            blocking = true;
            FirstPersonPlayer.ReleaseCursor();
            ArchiveUi.Select(resumeButton.gameObject);
        }

        /// <summary>Closes and gives input back (from the next frame); the run goes on unchanged.</summary>
        public void Close()
        {
            if (!open) return;
            open = false;
            if (menuRoot != null) menuRoot.SetActive(false);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);   // keys no longer reach a menu button
            if (blocking && player != null) player.SetInputBlocked(false);
            blocking = false;
        }

        void Quit()
        {
            if (leaving) return;
            leaving = true;
            resumeButton.interactable = false;
            quitButton.interactable = false;
            ArchiveUi.QuitGame();
        }
    }
}
