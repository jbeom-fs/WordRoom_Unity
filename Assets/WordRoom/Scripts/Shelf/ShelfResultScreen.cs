using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.World;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Goal result of the shelf prototype: 소요 시간, 이동 횟수, the words passed in order, this challenge's personal
    /// 최단 시간 and 최소 이동 (each marked when this run set it), and 다시 도전. The values are fixed by the demo once,
    /// at the goal entry. While shown it alone takes input: the player's input is blocked, the cursor is free and a
    /// full-screen overlay catches clicks (the demo turns the Escape menu off first). 다시 도전 is taken once per showing
    /// (the button is disabled at the press) and raises RetryRequested; the demo then hides this and starts the same
    /// challenge again. The display is the scene's Shelf Result Canvas (prefab instance, inactive while hidden), placed
    /// and connected by WordRoom/서가 시안 메뉴·결과 구성. A screen that is not usable is never forced on: Show returns
    /// false (reported once) and the demo keeps its on-screen success hint instead.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfResultScreen : MonoBehaviour
    {
        [Tooltip("결과 화면 동안 입력을 막을 1인칭 플레이어")]
        [SerializeField] FirstPersonPlayer player;

        [Header("화면 (씬에 배치된 Shelf Result Canvas)")]
        [Tooltip("결과 화면 전체(클릭을 막는 배경 포함). 숨겨진 동안 비활성")]
        [SerializeField] GameObject display;
        [SerializeField] Canvas canvas;
        [SerializeField] TextMeshProUGUI timeText;
        [SerializeField] TextMeshProUGUI movesText;
        [SerializeField] TextMeshProUGUI pathText;
        [SerializeField] TextMeshProUGUI bestTimeText;
        [SerializeField] TextMeshProUGUI bestMovesText;
        [SerializeField] Button retryButton;

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        bool ready;
        bool shown;
        bool blocking;
        bool unusableReported;

        /// <summary>다시 도전 was pressed (once per showing).</summary>
        public event Action RetryRequested;

        public bool IsShown => shown;

        void Awake()
        {
            var missing = Missing();
            ready = missing == null;
            if (!ready)
            {
                Debug.LogError($"[서가 결과] ShelfResultScreen의 참조가 비어 있어 결과 화면을 쓰지 않습니다(목표 도달 안내는 그대로): {missing}. WordRoom/서가 시안 메뉴·결과 구성을 실행하세요.", this);
                return;
            }
            ArchiveUi.Bind(bindings, retryButton, OnRetry);
            if (display.activeSelf) display.SetActive(false);                // hidden until the goal
        }

        string Missing()
        {
            string missing = null;
            ArchiveUi.Need(player, nameof(player), ref missing);
            ArchiveUi.Need(display, nameof(display), ref missing);
            ArchiveUi.Need(canvas, nameof(canvas), ref missing);
            ArchiveUi.Need(timeText, nameof(timeText), ref missing);
            ArchiveUi.Need(movesText, nameof(movesText), ref missing);
            ArchiveUi.Need(pathText, nameof(pathText), ref missing);
            ArchiveUi.Need(bestTimeText, nameof(bestTimeText), ref missing);
            ArchiveUi.Need(bestMovesText, nameof(bestMovesText), ref missing);
            ArchiveUi.Need(retryButton, nameof(retryButton), ref missing);
            return missing;
        }

        void OnDisable() => Hide();

        void OnDestroy() => ArchiveUi.Unbind(bindings);

        /// <summary>Shows one fixed result; false (nothing taken over) when the screen cannot be used.</summary>
        public bool Show(double seconds, int moves, IReadOnlyList<string> words, ShelfRecords.Best best, bool newTime, bool newMoves)
        {
            if (!ready || shown || !isActiveAndEnabled) return false;
            var missing = Missing();
            if (missing != null) return Unusable($"참조가 비어 있음: {missing}");
            timeText.text = ResultScreen.FormatTime(seconds);
            movesText.text = $"{moves}회";
            pathText.text = string.Join(" → ", words);
            bestTimeText.text = best.HasTime ? ResultScreen.FormatTime(best.Seconds) + (newTime ? "  (새 기록)" : "") : "--:--";
            bestMovesText.text = best.HasMoves ? $"{best.Moves}회" + (newMoves ? "  (새 기록)" : "") : "--";
            var wasActive = display.activeSelf;
            if (!wasActive) display.SetActive(true);
            if (!display.activeInHierarchy || !canvas.isActiveAndEnabled || !retryButton.IsActive())
            {
                if (!wasActive) display.SetActive(false);
                return Unusable("결과 화면·Canvas·다시 도전 버튼 중 꺼진 것이 있음");
            }
            retryButton.interactable = true;
            player.SetInputBlocked(true);
            blocking = true;
            FirstPersonPlayer.ReleaseCursor();
            shown = true;
            ArchiveUi.Select(retryButton.gameObject);
            return true;
        }

        bool Unusable(string reason)
        {
            if (!unusableReported) Debug.LogError($"[서가 결과] 결과 화면을 표시하지 않았습니다 — {reason}. 목표 도달 안내는 화면 아래에 남습니다.", this);
            unusableReported = true;
            return false;
        }

        /// <summary>Hidden again; the player's input comes back (from the next frame).</summary>
        public void Hide()
        {
            if (!shown) return;
            shown = false;
            if (display != null) display.SetActive(false);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            if (blocking && player != null) player.SetInputBlocked(false);
            blocking = false;
        }

        void OnRetry()
        {
            if (!shown || !retryButton.interactable) return;
            retryButton.interactable = false;                                // a second press in the same showing does nothing
            RetryRequested?.Invoke();
        }
    }
}
