using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace WordRoom.Game
{
    /// <summary>
    /// Tells whether a slider is being held with the pointer and reports when it is let go. Used where applying
    /// the value while dragging would move the slider itself (UI size): the value then applies on release, while
    /// keyboard changes apply at once. A press on the track changes the slider's value before this component's
    /// OnPointerDown may run (handler order on one object is the component order), so users of Held decide after
    /// the EventSystem has finished the frame (SettingsPanel.LateUpdate), not inside onValueChanged. Placed on the
    /// slider in the prefab; never added at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SliderRelease : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        public bool Held { get; private set; }
        public event Action Released;

        public void OnPointerDown(PointerEventData eventData) => Held = true;

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!Held) return;
            Held = false;
            Released?.Invoke();
        }

        void OnDisable()
        {
            if (!Held) return;
            Held = false;
            Released?.Invoke();
        }
    }
}
