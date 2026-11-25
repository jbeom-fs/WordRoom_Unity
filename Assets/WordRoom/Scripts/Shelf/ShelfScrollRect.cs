using System;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordRoom.Shelf
{
    /// <summary>
    /// The reading screen's body ScrollRect, unchanged in behavior, that also tells when the EventSystem starts a
    /// left-button drag on it (the drag threshold was reached), whether or not the body then moves, and when it gets
    /// a wheel scroll. The reading screen cancels its pending link click then.
    /// </summary>
    public sealed class ShelfScrollRect : ScrollRect
    {
        /// <summary>A left-button drag started on this ScrollRect.</summary>
        public event Action DragStarted;

        /// <summary>The EventSystem delivered a wheel scroll to this ScrollRect (applied right after).</summary>
        public event Action Scrolled;

        public override void OnScroll(PointerEventData data)
        {
            Scrolled?.Invoke();
            base.OnScroll(data);
        }

        public override void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) DragStarted?.Invoke();
            base.OnBeginDrag(eventData);
        }
    }
}
