using UnityEngine;
using UnityEngine.UI;
using WordRoom.Settings;

namespace WordRoom.World
{
    /// <summary>
    /// Puts the player's UI size (GameSettings.UiScale) on a Scale With Screen Size canvas by changing its reference
    /// resolution (1920×1080 divided by the scale), the value CanvasScaler itself keeps using every frame — so the
    /// size never snaps back, and 100% is exactly the 1920×1080 layout. Applied on enable and on every change.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasScaler))]
    public sealed class UiScaleBinding : MonoBehaviour
    {
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        CanvasScaler scaler;

        /// <summary>Scale With Screen Size at 1920×1080, match 0.5, plus this binding. Used by every WordRoom canvas.</summary>
        public static CanvasScaler Configure(GameObject canvasObject)
        {
            var s = canvasObject.GetComponent<CanvasScaler>();
            if (s == null) s = canvasObject.AddComponent<CanvasScaler>();
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = ReferenceResolution;
            s.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            s.matchWidthOrHeight = 0.5f;
            if (canvasObject.GetComponent<UiScaleBinding>() == null) canvasObject.AddComponent<UiScaleBinding>();
            return s;
        }

        void OnEnable()
        {
            scaler = GetComponent<CanvasScaler>();
            Apply();
            GameSettings.Changed += Apply;
        }

        void OnDisable() => GameSettings.Changed -= Apply;

        void Apply()
        {
            if (scaler != null) scaler.referenceResolution = ReferenceResolution / GameSettings.UiScale;
        }
    }
}
