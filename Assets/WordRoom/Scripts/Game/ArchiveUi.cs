using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordRoom.Game
{
    /// <summary>
    /// Runtime side of the archive-style menus (dark walnut panels, 4-unit brass frame, warm ivory text). The menus
    /// themselves are prefabs and scene objects saved in the Editor (WordRoom.EditorTools.ArchiveUiBuilder, run by
    /// WordRoom/메인 메뉴와 게임 설정 구성); nothing is built or added here at runtime. What stays is the palette
    /// (also used by the builder), selection, quitting, the reference check and the button binding: each listener
    /// is added once by code (the prefabs carry no Inspector click events), plays the click before its action, and
    /// is removed again by exactly that delegate, so listeners added by others are never touched.
    /// </summary>
    public static class ArchiveUi
    {
        public static readonly Color32 Walnut = new Color32(0x49, 0x35, 0x2A, 255);          // #49352A
        public static readonly Color32 PanelWalnut = new Color32(0x49, 0x35, 0x2A, 242);
        public static readonly Color32 Hover = new Color32(0x5E, 0x46, 0x37, 255);
        public static readonly Color32 Pressed = new Color32(0x33, 0x25, 0x1D, 255);
        public static readonly Color32 Focused = new Color32(0x6E, 0x55, 0x37, 255);         // keyboard selection, brass-tinted
        public static readonly Color32 Chosen = new Color32(0x86, 0x69, 0x3C, 255);          // chosen option (difficulty)
        public static readonly Color32 Disabled = new Color32(0x3A, 0x32, 0x2D, 150);
        public static readonly Color32 Brass = new Color32(0xA8, 0x87, 0x4B, 255);           // #A8874B
        public static readonly Color32 Ivory = new Color32(0xF2, 0xEB, 0xDD, 255);           // #F2EBDD
        public static readonly Color32 IvoryDim = new Color32(0xD8, 0xD0, 0xBF, 255);        // #D8D0BF
        public static readonly Color32 Track = new Color32(0x2B, 0x1F, 0x18, 255);
        public static readonly Color32 Dim = new Color32(0, 0, 0, 150);

        public static void Select(GameObject go)
        {
            if (go != null && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(go);
        }

        public static void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>Appends `field` to `missing` when `value` is not set (Unity null, also destroyed).</summary>
        public static void Need(UnityEngine.Object value, string field, ref string missing)
        {
            if (value == null) missing = missing == null ? field : $"{missing}, {field}";
        }

        /// <summary>Adds the click sound and `onClick` to `button` once; the delegate is recorded for Unbind.</summary>
        public static void Bind(List<ButtonBinding> bindings, Button button, Action onClick)
        {
            UnityAction action = () =>
            {
                UiSound.Click();
                onClick();
            };
            button.onClick.AddListener(action);
            bindings.Add(new ButtonBinding(button, action));
        }

        /// <summary>Removes only the delegates recorded by Bind (other code's and Inspector listeners stay).</summary>
        public static void Unbind(List<ButtonBinding> bindings)
        {
            foreach (var b in bindings)
                if (b.Button != null) b.Button.onClick.RemoveListener(b.Action);
            bindings.Clear();
        }

        /// <summary>Changes only the normal color of `s` (hover, pressed, selected, disabled stay as set in the Inspector).</summary>
        public static void SetNormalColor(Selectable s, Color color)
        {
            var c = s.colors;
            if (c.normalColor == color) return;
            c.normalColor = color;
            s.colors = c;
        }
    }

    public readonly struct ButtonBinding
    {
        public readonly Button Button;
        public readonly UnityAction Action;

        public ButtonBinding(Button button, UnityAction action)
        {
            Button = button;
            Action = action;
        }
    }
}
