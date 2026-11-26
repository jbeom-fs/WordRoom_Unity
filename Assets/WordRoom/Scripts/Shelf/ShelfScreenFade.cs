using System.Collections;
using UnityEngine;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Short blackout between reading rooms (Shelf Fade Canvas, drawn over every other screen). Fades a black
    /// full-screen panel in real time (time scale independent); while any black is shown it blocks pointer input to the
    /// screens below. Clear and click-through when idle. Without its references it reports once and every fade
    /// finishes at once (the room still changes, only without the blackout).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfScreenFade : MonoBehaviour
    {
        [Tooltip("검은 화면 전체(Image 포함)의 CanvasGroup")]
        [SerializeField] CanvasGroup group;
        [Tooltip("다른 화면보다 위에 그리는 Canvas")]
        [SerializeField] Canvas canvas;
        [Tooltip("어두워지는 시간(초)")]
        [Min(0f)] [SerializeField] float fadeOutSeconds = 0.22f;
        [Tooltip("완전히 어두운 상태를 유지하는 최소 시간(초)")]
        [Min(0f)] [SerializeField] float holdSeconds = 0.08f;
        [Tooltip("다시 밝아지는 시간(초)")]
        [Min(0f)] [SerializeField] float fadeInSeconds = 0.28f;

        bool ready;

        public bool IsBlack => ready && group.alpha >= 0.999f;
        public float HoldSeconds => holdSeconds;

        void Awake()
        {
            ready = group != null && canvas != null;
            if (!ready)
            {
                Debug.LogError("[서가 전환] ShelfScreenFade의 group·canvas 참조가 비어 있어 암전 없이 방을 바꿉니다. ShelfCorridorPrototype 씬의 Shelf Fade Canvas를 확인하세요.", this);
                return;
            }
            Set(0f);
        }

        /// <summary>To full black (blocking input from the first frame).</summary>
        public IEnumerator FadeOut() => Fade(1f, fadeOutSeconds);

        /// <summary>Back to clear; input goes through again at the end.</summary>
        public IEnumerator FadeIn() => Fade(0f, fadeInSeconds);

        /// <summary>Clear at once (e.g. the run stopped mid-transition).</summary>
        public void Clear()
        {
            if (ready) Set(0f);
        }

        IEnumerator Fade(float target, float seconds)
        {
            if (!ready) yield break;
            var from = group.alpha;
            if (target > 0f) Set(Mathf.Max(from, 0.001f));                  // blocks clicks from now on
            var t = 0f;
            while (seconds > 0f && t < seconds)
            {
                t += Time.unscaledDeltaTime;
                Set(Mathf.Lerp(from, target, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / seconds))));
                yield return null;
            }
            Set(target);
        }

        void Set(float alpha)
        {
            group.alpha = alpha;
            var on = alpha > 0f;
            group.blocksRaycasts = on;
            group.interactable = false;
            if (canvas.enabled != on) canvas.enabled = on;
        }

#if UNITY_EDITOR
        /// <summary>Editor setup only.</summary>
        public void EditorSetup(CanvasGroup group, Canvas canvas)
        {
            this.group = group;
            this.canvas = canvas;
        }
#endif
    }
}
