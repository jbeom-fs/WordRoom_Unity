using System;
using UnityEngine;

namespace WordRoom.Settings
{
    /// <summary>
    /// The player's own preferences, kept apart from any game session (difficulty, words, seed, map and doors are
    /// never stored here). Stored in PlayerPrefs under the "WordRoom.Settings." prefix only — other keys are never
    /// touched or deleted. Read once on first use; missing, invalid (NaN) or out-of-range values fall back to the
    /// default or are clamped. Setters clamp, store and raise Changed only when the value really changes, so
    /// listeners re-apply absolute values (never multiply onto earlier results). The screen size and mode are
    /// stored only through CommitScreen, after the player confirmed them. Main thread only.
    /// </summary>
    public static class GameSettings
    {
        public const int MinDisplayRadius = 1, MaxDisplayRadius = 3, DefaultDisplayRadius = 1;
        public const float MinMouseSensitivity = 0.2f, MaxMouseSensitivity = 3f, DefaultMouseSensitivity = 1f;
        public const float MinFieldOfView = 60f, MaxFieldOfView = 90f, DefaultFieldOfView = 60f;
        public const float MinUiScale = 0.8f, MaxUiScale = 1.25f, DefaultUiScale = 1f;
        public const float DefaultVolume = 1f;

        const string Prefix = "WordRoom.Settings.";
        const string KeyDisplayRadius = Prefix + "DisplayRadius";
        const string KeyMouseSensitivity = Prefix + "MouseSensitivity";
        const string KeyInvertMouseY = Prefix + "InvertMouseY";
        const string KeyFieldOfView = Prefix + "FieldOfView";
        const string KeyUiScale = Prefix + "UiScale";
        const string KeyMasterVolume = Prefix + "MasterVolume";
        const string KeySfxVolume = Prefix + "SfxVolume";
        const string KeyScreenWidth = Prefix + "ScreenWidth";
        const string KeyScreenHeight = Prefix + "ScreenHeight";
        const string KeyScreenFull = Prefix + "ScreenFullScreen";

        static bool loaded;
        static int displayRadius;
        static float mouseSensitivity;
        static bool invertMouseY;
        static float fieldOfView;
        static float uiScale;
        static float masterVolume;
        static float sfxVolume;

        /// <summary>Raised after any preference changed (also after ResetToDefaults). Subscribers unsubscribe on disable.</summary>
        public static event Action Changed;

        /// <summary>방 표시 거리: rooms within this Manhattan distance of the current room are shown (1 → up to 5, 2 → 13, 3 → 25).</summary>
        public static int DisplayRadius
        {
            get { Load(); return displayRadius; }
            set => SetInt(ref displayRadius, Mathf.Clamp(value, MinDisplayRadius, MaxDisplayRadius), KeyDisplayRadius);
        }

        /// <summary>Multiplier on the player's base look sensitivity (0.2–3.0, default 1.0).</summary>
        public static float MouseSensitivity
        {
            get { Load(); return mouseSensitivity; }
            set => SetFloat(ref mouseSensitivity, Clamp(value, MinMouseSensitivity, MaxMouseSensitivity, DefaultMouseSensitivity), KeyMouseSensitivity);
        }

        /// <summary>Inverts the vertical look only (default off).</summary>
        public static bool InvertMouseY
        {
            get { Load(); return invertMouseY; }
            set
            {
                Load();
                if (value == invertMouseY) return;
                invertMouseY = value;
                PlayerPrefs.SetInt(KeyInvertMouseY, value ? 1 : 0);
                Changed?.Invoke();
            }
        }

        /// <summary>Vertical field of view of the play camera in degrees (60–90, default 60).</summary>
        public static float FieldOfView
        {
            get { Load(); return fieldOfView; }
            set => SetFloat(ref fieldOfView, Clamp(value, MinFieldOfView, MaxFieldOfView, DefaultFieldOfView), KeyFieldOfView);
        }

        /// <summary>Scale of every screen UI (0.8–1.25, default 1.0); 3D signs and rooms are not affected.</summary>
        public static float UiScale
        {
            get { Load(); return uiScale; }
            set => SetFloat(ref uiScale, Clamp(value, MinUiScale, MaxUiScale, DefaultUiScale), KeyUiScale);
        }

        /// <summary>Overall volume 0–1 (AudioListener.volume; 0 mutes everything).</summary>
        public static float MasterVolume
        {
            get { Load(); return masterVolume; }
            set => SetFloat(ref masterVolume, Clamp(value, 0f, 1f, DefaultVolume), KeyMasterVolume);
        }

        /// <summary>Sound-effect volume 0–1, applied once to each effect's own base volume (the master volume applies on top, once).</summary>
        public static float SfxVolume
        {
            get { Load(); return sfxVolume; }
            set => SetFloat(ref sfxVolume, Clamp(value, 0f, 1f, DefaultVolume), KeySfxVolume);
        }

        /// <summary>The last confirmed screen size and mode, if any was ever confirmed (values not yet validated against the display).</summary>
        public static bool TryGetScreen(out int width, out int height, out bool fullScreen)
        {
            width = PlayerPrefs.GetInt(KeyScreenWidth, 0);
            height = PlayerPrefs.GetInt(KeyScreenHeight, 0);
            fullScreen = PlayerPrefs.GetInt(KeyScreenFull, 1) != 0;
            return width > 0 && height > 0;
        }

        /// <summary>Stores a screen size and mode the player confirmed (never an unconfirmed trial).</summary>
        public static void CommitScreen(int width, int height, bool fullScreen)
        {
            if (width <= 0 || height <= 0) return;
            PlayerPrefs.SetInt(KeyScreenWidth, width);
            PlayerPrefs.SetInt(KeyScreenHeight, height);
            PlayerPrefs.SetInt(KeyScreenFull, fullScreen ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>Back to the defaults for everything except the screen (which goes through its own confirmation).</summary>
        public static void ResetToDefaults()
        {
            Load();
            displayRadius = DefaultDisplayRadius;
            mouseSensitivity = DefaultMouseSensitivity;
            invertMouseY = false;
            fieldOfView = DefaultFieldOfView;
            uiScale = DefaultUiScale;
            masterVolume = DefaultVolume;
            sfxVolume = DefaultVolume;
            PlayerPrefs.SetInt(KeyDisplayRadius, displayRadius);
            PlayerPrefs.SetFloat(KeyMouseSensitivity, mouseSensitivity);
            PlayerPrefs.SetInt(KeyInvertMouseY, 0);
            PlayerPrefs.SetFloat(KeyFieldOfView, fieldOfView);
            PlayerPrefs.SetFloat(KeyUiScale, uiScale);
            PlayerPrefs.SetFloat(KeyMasterVolume, masterVolume);
            PlayerPrefs.SetFloat(KeySfxVolume, sfxVolume);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        /// <summary>Writes the changed preferences to disk (they are also written when the application quits).</summary>
        public static void Save() => PlayerPrefs.Save();

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            displayRadius = Mathf.Clamp(PlayerPrefs.GetInt(KeyDisplayRadius, DefaultDisplayRadius), MinDisplayRadius, MaxDisplayRadius);
            mouseSensitivity = Clamp(PlayerPrefs.GetFloat(KeyMouseSensitivity, DefaultMouseSensitivity), MinMouseSensitivity, MaxMouseSensitivity, DefaultMouseSensitivity);
            invertMouseY = PlayerPrefs.GetInt(KeyInvertMouseY, 0) == 1;
            fieldOfView = Clamp(PlayerPrefs.GetFloat(KeyFieldOfView, DefaultFieldOfView), MinFieldOfView, MaxFieldOfView, DefaultFieldOfView);
            uiScale = Clamp(PlayerPrefs.GetFloat(KeyUiScale, DefaultUiScale), MinUiScale, MaxUiScale, DefaultUiScale);
            masterVolume = Clamp(PlayerPrefs.GetFloat(KeyMasterVolume, DefaultVolume), 0f, 1f, DefaultVolume);
            sfxVolume = Clamp(PlayerPrefs.GetFloat(KeySfxVolume, DefaultVolume), 0f, 1f, DefaultVolume);
        }

        static float Clamp(float v, float min, float max, float fallback) =>
            float.IsNaN(v) || float.IsInfinity(v) ? fallback : Mathf.Clamp(v, min, max);

        static void SetInt(ref int field, int value, string key)
        {
            Load();
            if (value == field) return;
            field = value;
            PlayerPrefs.SetInt(key, value);
            Changed?.Invoke();
        }

        static void SetFloat(ref float field, float value, string key)
        {
            Load();
            if (Mathf.Approximately(value, field)) return;
            field = value;
            PlayerPrefs.SetFloat(key, value);
            Changed?.Invoke();
        }

        // Fresh state for every play session, also when the Editor keeps static state between plays.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            loaded = false;
            Changed = null;
        }

        // The master volume is global: applied once at start and on every change (absolute value, no accumulation).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void ApplyMasterVolume()
        {
            AudioListener.volume = MasterVolume;
            Changed -= ApplyListenerVolume;
            Changed += ApplyListenerVolume;
        }

        static void ApplyListenerVolume() => AudioListener.volume = MasterVolume;
    }
}
