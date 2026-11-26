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
    /// Goal result of the shelf prototype: 목표 도달, 소요 시간, 이동 횟수, this challenge's personal 최단 시간 and
    /// 최소 이동 (each marked when this run set it; times with hundredths), and 다시 도전. The values are fixed by the demo once,
    /// at the goal entry. While shown it alone takes input: the player's input is blocked, the cursor is free and a
    /// full-screen overlay catches clicks (the demo turns the Escape menu off first). 다시 도전 is taken once per showing
    /// (the button is disabled at the press) and raises RetryRequested; the demo then hides this and starts the same
    /// challenge again. When the new run cannot be placed, the demo keeps this result shown (ShowRetryFailure): the
    /// reason appears under the button, 다시 도전 is usable again, input stays blocked and the cursor free. The display is the scene's Shelf Result Canvas (prefab instance, inactive while hidden), saved
    /// in the scene with its references. A screen that is not usable is never forced on: Show returns
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
        [SerializeField] TextMeshProUGUI bestTimeText;
        [SerializeField] TextMeshProUGUI bestMovesText;
        [SerializeField] Button retryButton;
        [Tooltip("다시 도전 실패 사유(평소 숨김, 다시 도전 버튼 아래)")]
        [SerializeField] TextMeshProUGUI noticeText;

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
                Debug.LogError($"[서가 결과] ShelfResultScreen의 참조가 비어 있어 결과 화면을 쓰지 않습니다(목표 도달 안내는 그대로): {missing}. ShelfCorridorPrototype 씬의 Shelf Result Canvas 참조를 Inspector에서 확인하세요.", this);
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
            ArchiveUi.Need(bestTimeText, nameof(bestTimeText), ref missing);
            ArchiveUi.Need(bestMovesText, nameof(bestMovesText), ref missing);
            ArchiveUi.Need(retryButton, nameof(retryButton), ref missing);
            return missing;
        }

        void OnDisable() => Hide();

        void OnDestroy() => ArchiveUi.Unbind(bindings);

        /// <summary>Shows one fixed result; false (nothing taken over) when the screen cannot be used.</summary>
        public bool Show(double seconds, int moves, ShelfRecords.Best best, bool newTime, bool newMoves)
        {
            if (!ready || shown || !isActiveAndEnabled) return false;
            var missing = Missing();
            if (missing != null) return Unusable($"참조가 비어 있음: {missing}");
            timeText.text = FormatTime(seconds);
            movesText.text = $"{moves}회";
            bestTimeText.text = best.HasTime ? FormatTime(best.Seconds) + (newTime ? "  (새 기록)" : "") : "--:--.--";
            bestMovesText.text = best.HasMoves ? $"{best.Moves}회" + (newMoves ? "  (새 기록)" : "") : "--";
            var wasActive = display.activeSelf;
            if (!wasActive) display.SetActive(true);
            if (!display.activeInHierarchy || !canvas.isActiveAndEnabled || !retryButton.IsActive())
            {
                if (!wasActive) display.SetActive(false);
                return Unusable("결과 화면·Canvas·다시 도전 버튼 중 꺼진 것이 있음");
            }
            retryButton.interactable = true;
            SetNotice(null);
            player.SetInputBlocked(true);
            blocking = true;
            FirstPersonPlayer.ReleaseCursor();
            shown = true;
            ArchiveUi.Select(retryButton.gameObject);
            return true;
        }

        /// <summary>
        /// Shelf result time with hundredths, always two digits after the point: MM:SS.cc below an hour, else
        /// HH:MM:SS.cc (18.4 → 00:18.40, 3723.45 → 01:02:03.45). Rounded to the nearest hundredth so a best kept as a
        /// float in PlayerPrefs (18.4 → 18.39999…) shows the same digits; only the display, never the comparison.
        /// </summary>
        public static string FormatTime(double seconds)
        {
            var total = seconds > 0d ? (long)Math.Round(seconds * 100d, MidpointRounding.AwayFromZero) : 0L;
            var cs = total % 100;
            var s = total / 100 % 60;
            var m = total / 6000 % 60;
            var h = total / 360000;
            return h > 0 ? $"{h:00}:{m:00}:{s:00}.{cs:00}" : $"{m:00}:{s:00}.{cs:00}";
        }

        bool Unusable(string reason)
        {
            if (!unusableReported) Debug.LogError($"[서가 결과] 결과 화면을 표시하지 않았습니다 — {reason}. 목표 도달 안내는 화면 아래에 남습니다.", this);
            unusableReported = true;
            return false;
        }

        /// <summary>
        /// 다시 도전 could not start: the same result stays with `reason` under the button and 다시 도전 can be pressed
        /// again. The input block and the free cursor of the result stay (taken again if anything lifted them).
        /// </summary>
        public void ShowRetryFailure(string reason)
        {
            if (!shown) return;
            SetNotice(reason);
            retryButton.interactable = true;
            player.SetInputBlocked(true);
            blocking = true;
            FirstPersonPlayer.ReleaseCursor();
            ArchiveUi.Select(retryButton.gameObject);
        }

        void SetNotice(string text)
        {
            var on = !string.IsNullOrEmpty(text);
            if (noticeText == null)
            {
                if (on) Debug.LogWarning($"[서가 결과] 안내 글자(noticeText) 참조가 비어 있어 화면에 표시하지 못했습니다: {text}", this);
                return;
            }
            noticeText.text = on ? text : "";
            if (noticeText.gameObject.activeSelf != on) noticeText.gameObject.SetActive(on);
        }

        /// <summary>Hidden again; the player's input comes back (from the next frame).</summary>
        public void Hide()
        {
            if (!shown) return;
            shown = false;
            SetNotice(null);
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
