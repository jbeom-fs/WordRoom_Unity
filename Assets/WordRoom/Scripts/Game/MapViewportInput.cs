using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace WordRoom.Game
{
    /// <summary>
    /// Pointer input of the map viewport (on its background, a raycast target inside the viewport only): the wheel, a
    /// right-button drag (pan) and the left button for the drawing tool. Buttons are outside the viewport, so their
    /// presses never reach here. Only forwards; added to the prefab in the Editor.
    /// Left button: ToolDown on the press itself (no drag threshold, so the first point is never lost), ToolMove while
    /// it moves inside the viewport, ToolUp on release inside it. Leaving the viewport (the pointer exits it — also onto a
    /// tool button over it — or a move or the release lands outside its rect, checked with the rect itself), a right
    /// press, the wheel or Interrupt end the line at its last point inside (ToolStop keep); the input module may handle a
    /// release before that frame's move, so the release itself is checked too. The held left button never starts or
    /// resumes one — only a new press does. A left press during a pan is ignored. Cancel, disabling and focus loss drop
    /// it (ToolStop discard).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MapViewportInput : MonoBehaviour, IScrollHandler, IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Wheel over the viewport: screen position, wheel direction (+1 up = zoom in, -1 down), event camera.</summary>
        public event Action<Vector2, int, Camera> Zoomed;

        /// <summary>Right-button drag on the viewport: screen delta of this step, event camera.</summary>
        public event Action<Vector2, Camera> Panned;

        /// <summary>Left press on the viewport: screen position, event camera.</summary>
        public event Action<Vector2, Camera> ToolDown;

        /// <summary>The pressed left button moved, still inside the viewport.</summary>
        public event Action<Vector2, Camera> ToolMove;

        /// <summary>The left button was released (inside the viewport: leaving it ends the tool before).</summary>
        public event Action<Vector2, Camera> ToolUp;

        /// <summary>The tool's press ended without a release here: keep (true) what was made, or drop it (false).</summary>
        public event Action<bool> ToolStop;

        RectTransform rect;
        bool panning;
        bool tool;
        int toolPointer;
        bool hovered;

        /// <summary>A right-button pan is going on.</summary>
        public bool Panning => panning;

        /// <summary>The pointer is over the viewport background (not over a button or outside).</summary>
        public bool Hovered => hovered;

        void Awake() => rect = (RectTransform)transform;

        public void OnScroll(PointerEventData e)
        {
            var y = e.scrollDelta.y;
            if (Mathf.Approximately(y, 0f)) return;
            StopTool(true);                                                    // the line ends before the view changes
            Zoomed?.Invoke(e.position, y > 0f ? 1 : -1, e.enterEventCamera);
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Left)
            {
                if (panning || tool) return;                                   // never together with a pan
                tool = true;
                toolPointer = e.pointerId;
                ToolDown?.Invoke(e.position, e.pressEventCamera);
            }
            else if (e.button == PointerEventData.InputButton.Right) StopTool(true);   // the held left button never resumes it
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || !tool || e.pointerId != toolPointer) return;
            if (!Inside(e.position, e.pressEventCamera))
            {
                StopTool(true);                                                // released outside: never a point there
                return;
            }
            tool = false;
            ToolUp?.Invoke(e.position, e.pressEventCamera);
        }

        bool Inside(Vector2 screen, Camera eventCamera) => RectTransformUtility.RectangleContainsScreenPoint(rect, screen, eventCamera);

        /// <summary>The left button draws from the first movement (no threshold); the right button pans after the usual threshold.</summary>
        public void OnInitializePotentialDrag(PointerEventData e) => e.useDragThreshold = e.button != PointerEventData.InputButton.Left;

        public void OnBeginDrag(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right && !tool) panning = true;
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right)
            {
                if (panning) Panned?.Invoke(e.delta, e.pressEventCamera);
                return;
            }
            if (e.button != PointerEventData.InputButton.Left || !tool || e.pointerId != toolPointer) return;
            if (!Inside(e.position, e.pressEventCamera)) StopTool(true);      // ends at the last point inside
            else ToolMove?.Invoke(e.position, e.pressEventCamera);
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (e.button == PointerEventData.InputButton.Right) panning = false;
        }

        public void OnPointerEnter(PointerEventData e) => hovered = true;

        /// <summary>The pointer left the viewport (outside, or onto UI over it): a press of the tool ends at its last point inside.</summary>
        public void OnPointerExit(PointerEventData e)
        {
            hovered = false;
            StopTool(true);
        }

        /// <summary>Ends the tool's press keeping what was made (현재 위치, a tool change); the held button does nothing more.</summary>
        public void Interrupt() => StopTool(true);

        /// <summary>Drops the tool's press and the pan (closing, focus loss): nothing carries over to a later press.</summary>
        public void Cancel()
        {
            StopTool(false);
            panning = false;
        }

        void StopTool(bool keep)
        {
            if (!tool) return;
            tool = false;
            ToolStop?.Invoke(keep);
        }

        void OnApplicationFocus(bool focus)
        {
            if (!focus) Cancel();
        }

        void OnDisable()
        {
            Cancel();
            hovered = false;
        }
    }
}
