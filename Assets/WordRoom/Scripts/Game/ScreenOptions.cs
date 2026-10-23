using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WordRoom.Settings;

namespace WordRoom.Game
{
    /// <summary>One screen setup: size in pixels and windowed or full screen.</summary>
    public readonly struct ScreenChoice
    {
        public readonly int Width;
        public readonly int Height;
        public readonly bool FullScreen;

        public ScreenChoice(int width, int height, bool fullScreen)
        {
            Width = width;
            Height = height;
            FullScreen = fullScreen;
        }

        public bool SameAs(ScreenChoice o) => Width == o.Width && Height == o.Height && FullScreen == o.FullScreen;
        public override string ToString() => $"{Width} x {Height} {(FullScreen ? "전체 화면" : "창 모드")}";
    }

    /// <summary>
    /// Screen modes and sizes the platform offers. Full screen uses FullScreenWindow (borderless, available on every
    /// desktop platform; the image is scaled to the display). Sizes come from Screen.resolutions without
    /// duplicates of the same width × height; windowed sizes larger than the display are left out. The current
    /// size is always offered, so the selection can show what is really on screen.
    /// </summary>
    public static class ScreenOptions
    {
        public static ScreenChoice Current => new ScreenChoice(Screen.width, Screen.height, Screen.fullScreenMode != FullScreenMode.Windowed);

        /// <summary>Default: full screen at the display's own size.</summary>
        public static ScreenChoice Default
        {
            get
            {
                var d = DisplaySize();
                return new ScreenChoice(d.x, d.y, true);
            }
        }

        public static List<Vector2Int> Sizes(bool fullScreen)
        {
            var display = DisplaySize();
            var sizes = Screen.resolutions.Select(r => new Vector2Int(r.width, r.height))
                              .Where(s => s.x > 0 && s.y > 0 && (fullScreen || (s.x <= display.x && s.y <= display.y)))
                              .Distinct().ToList();
            var current = new Vector2Int(Screen.width, Screen.height);
            if (!sizes.Contains(current) && current.x > 0 && current.y > 0) sizes.Add(current);
            if (fullScreen && !sizes.Contains(display)) sizes.Add(display);
            sizes.Sort((a, b) => a.x != b.x ? b.x.CompareTo(a.x) : b.y.CompareTo(a.y));   // largest first
            return sizes;
        }

        /// <summary>The nearest offered size for `c` in its mode (by pixel area), so a stored size the display no longer offers is replaced by a valid one.</summary>
        public static ScreenChoice Validate(ScreenChoice c)
        {
            var sizes = Sizes(c.FullScreen);
            if (sizes.Count == 0) return Current;
            var want = new Vector2Int(c.Width, c.Height);
            if (sizes.Contains(want)) return c;
            var best = sizes.OrderBy(s => Mathf.Abs(s.x * s.y - want.x * want.y)).First();
            return new ScreenChoice(best.x, best.y, c.FullScreen);
        }

        public static void Apply(ScreenChoice c) =>
            Screen.SetResolution(c.Width, c.Height, c.FullScreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);

        static Vector2Int DisplaySize()
        {
            var info = Screen.mainWindowDisplayInfo;
            if (info.width > 0 && info.height > 0) return new Vector2Int(info.width, info.height);
            var r = Screen.currentResolution;
            return new Vector2Int(r.width, r.height);
        }

        // Players only: the confirmed screen setup of an earlier run, validated against this display. The Editor's
        // Game view is left alone.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void ApplySavedOnStartup()
        {
            if (Application.isEditor || !GameSettings.TryGetScreen(out var w, out var h, out var full)) return;
            var c = Validate(new ScreenChoice(w, h, full));
            if (!c.SameAs(Current)) Apply(c);
        }
    }
}
