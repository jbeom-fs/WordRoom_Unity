using UnityEngine;
using UnityEngine.UI;
using WordRoom.Settings;

namespace WordRoom.World
{
    /// <summary>
    /// Puts the player's UI size (GameSettings.UiScale) on a Scale With Screen Size canvas by changing its reference
    /// resolution (the scaler's own reference — 1920×1080 in the WordRoom prefabs — divided by the scale), the value
    /// CanvasScaler itself keeps using every frame — so the size never snaps back, and 100% is exactly the saved
    /// layout. Applied on enable and when the setting changes (the same scale is not applied twice). Saved on the
    /// canvas with its CanvasScaler in the prefab; a missing scaler is reported once and nothing is added at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasScaler))]
    public sealed class UiScaleBinding : MonoBehaviour
    {
        /// <summary>Reference resolution of every WordRoom canvas at 100% (used by the Editor builder).</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        CanvasScaler scaler;
        Vector2 baseResolution;              // the scaler's saved reference, read once before any change
        float appliedScale = -1f;
        bool missingLogged;
        System.Action apply;                 // one delegate for every enable (no new one per subscription)

        void Awake()
        {
            apply = Apply;
            scaler = GetComponent<CanvasScaler>();
            if (scaler != null) baseResolution = scaler.referenceResolution;
        }

        void OnEnable()
        {
            if (scaler == null)
            {
                if (!missingLogged) Debug.LogError($"[UI 크기] '{name}'에 CanvasScaler가 없어 UI 크기 설정을 적용하지 않습니다. 프리팹의 Canvas에 CanvasScaler를 두세요.", this);
                missingLogged = true;
                return;
            }
            appliedScale = -1f;
            Apply();
            GameSettings.Changed += apply;
        }

        void OnDisable() => GameSettings.Changed -= apply;

        void Apply()
        {
            var scale = GameSettings.UiScale;
            if (scale == appliedScale || scale <= 0f) return;
            appliedScale = scale;
            scaler.referenceResolution = baseResolution / scale;
        }
    }
}
