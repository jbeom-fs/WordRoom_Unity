using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordRoom.Game
{
    /// <summary>
    /// Keeps the keyboard-selected control of a ScrollRect's content inside its viewport: when the selection moves
    /// to a row above or below the visible part, the content scrolls just enough to show it. Mouse use is unchanged.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ScrollRect))]
    public sealed class ScrollFollowSelection : MonoBehaviour
    {
        readonly Vector3[] corners = new Vector3[4];
        ScrollRect scroll;
        GameObject last;

        void Awake() => scroll = GetComponent<ScrollRect>();

        void LateUpdate()
        {
            var es = EventSystem.current;
            var selected = es != null ? es.currentSelectedGameObject : null;
            if (selected == last) return;
            last = selected;
            if (selected == null || scroll.content == null || scroll.viewport == null || !selected.transform.IsChildOf(scroll.content)) return;

            ((RectTransform)selected.transform).GetWorldCorners(corners);
            float min = float.MaxValue, max = float.MinValue;
            foreach (var c in corners)
            {
                var y = scroll.viewport.InverseTransformPoint(c).y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
            var view = scroll.viewport.rect;
            var delta = max > view.yMax ? max - view.yMax : min < view.yMin ? min - view.yMin : 0f;
            if (Mathf.Approximately(delta, 0f)) return;
            var p = scroll.content.anchoredPosition;
            p.y -= delta;
            scroll.content.anchoredPosition = p;
            scroll.StopMovement();
        }
    }
}
