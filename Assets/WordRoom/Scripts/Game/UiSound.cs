using UnityEngine;
using WordRoom.Settings;

namespace WordRoom.Game
{
    /// <summary>
    /// The shared sound-effect path of a scene: one 2D AudioSource and a short, quiet click made in code (the project
    /// has no audio assets). Every effect plays at its own base volume × GameSettings.SfxVolume, computed per play
    /// (never stored back, so nothing accumulates); the master volume is AudioListener.volume, applied once
    /// globally. 0% plays nothing. Safe without an AudioListener (Unity simply plays nothing then).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiSound : MonoBehaviour
    {
        const float ClickBaseVolume = 0.35f;
        const int SampleRate = 44100;

        static UiSound current;

        AudioSource source;
        AudioClip click;

        /// <summary>The scene's click for UI buttons; does nothing when the scene has no UiSound.</summary>
        public static void Click()
        {
            if (current != null) current.Play(current.click, ClickBaseVolume);
        }

        void Awake()
        {
            current = this;
            source = GetComponent<AudioSource>();
            if (source == null) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 1f;                                    // base volumes are per effect; no accumulation here
            click = MakeClick();
        }

        void OnDestroy()
        {
            if (current == this) current = null;
            if (click != null) Destroy(click);
        }

        void Play(AudioClip clip, float baseVolume)
        {
            var volume = baseVolume * GameSettings.SfxVolume;
            if (clip == null || source == null || volume <= 0f) return;
            source.PlayOneShot(clip, volume);
        }

        /// <summary>About 45 ms: a soft wooden tick (two damped tones), no click at the start or end.</summary>
        static AudioClip MakeClick()
        {
            var count = SampleRate * 45 / 1000;
            var data = new float[count];
            for (var i = 0; i < count; i++)
            {
                var t = (float)i / SampleRate;
                var attack = Mathf.Clamp01(i / 40f);
                var decay = Mathf.Exp(-t * 110f);
                data[i] = attack * decay * (0.6f * Mathf.Sin(2f * Mathf.PI * 1250f * t) + 0.25f * Mathf.Sin(2f * Mathf.PI * 2600f * t));
            }
            var clip = AudioClip.Create("WordRoom UI Click", count, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
