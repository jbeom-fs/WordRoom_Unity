using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace WordRoom.Game
{
    /// <summary>
    /// Pointer input of the map viewport (on its background, a raycast target inside the viewport only): the wheel
    /// and a left-button drag. Buttons are outside the viewport, so their presses never become map drags. Only
    /// forwards to MapView; added to the prefab in the Editor.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapViewportInput : MonoBehaviour, IScrollHandler, IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>Wheel over the viewport: screen position, wheel direction (+1 up = zoom in, -1 down), event camera.</summary>
        public event Action<Vector2, int, Camera> Zoomed;

        /// <summary>Left-button drag on the viewport: screen delta of this step, event camera.</summary>
        public event Action<Vector2, Camera> Dragged;

        bool dragging;

        public void OnScroll(PointerEventData e)
        {
            var y = e.scrollDelta.y;
            if (Mathf.Approximately(y, 0f)) return;
            Zoomed?.Invoke(e.position, y > 0f ? 1 : -1, e.enterEventCamera);
        }

        public void OnInitializePotentialDrag(PointerEventData e) => e.useDragThreshold = true;

        public void OnBeginDrag(PointerEventData e) => dragging = e.button == PointerEventData.InputButton.Left;

        public void OnDrag(PointerEventData e)
        {
            if (dragging && e.button == PointerEventData.InputButton.Left) Dragged?.Invoke(e.delta, e.pressEventCamera);
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left) dragging = false;
        }

        void OnDisable() => dragging = false;
    }
}
