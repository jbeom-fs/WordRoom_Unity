using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.World;

namespace WordRoom.Shelf
{
    /// <summary>
    /// Reading screen of the shelf prototype: one lectern's document spread open in the middle of the screen over a
    /// dark overlay — headword, the lectern's sense number, short title and part of speech, and that one sense's
    /// definition in a scrolling body. While open the player's input is blocked (no walking, look, E or click-to-lock; gravity goes
    /// on) and the cursor is free. Link words are shown in a soft ink color and glow brighter under the pointer.
    /// Link clicks are decided from the mouse's own input events in the order they arrived (InputSystem.onEvent), with
    /// the position each press, move, wheel and release really had — never the frame's last mouse position. The link
    /// under a press or release is found when its event arrives, before the EventSystem applies this frame's wheel or
    /// drag to the body: that is the body layout shown when the button changed. The body position is kept with it.
    /// The steps are settled in LateUpdate, after the EventSystem has run for the frame, and every cancel is applied
    /// before LinkClicked. A left click raises LinkClicked with its headword once, only when the button was pressed and
    /// released on the same valid link, the body did not move from the press to the release (and up to the settling),
    /// and the press was not cancelled between. A press is cancelled, until the next press, when: a wheel input comes
    /// while it is held, or before it in the same input update (the order of press and scroll is then unknown); the
    /// body moves (wheel, scrollbar, inertia); the pointer once reaches the EventSystem drag threshold from where it was
    /// pressed; the body's ScrollRect starts a drag, or applies a wheel scroll this frame without a wheel event seen
    /// (ShelfScrollRect.DragStarted / Scrolled); the window loses focus; the screen is closed, disabled or no longer
    /// usable; or the frame's button or wheel state shows an input whose event (position and order) was not seen.
    /// Coming back over the link before releasing does not restore a cancelled press. A new press after the scrolling
    /// clicks at once. Plain text, particles and empty space do nothing.
    /// Escape or the close button closes it; when only the display (hidden in the hierarchy) or its Canvas is turned
    /// off while open, it is closed the same way. Closing gives the player's input back from the next frame with the
    /// position and view unchanged (the cursor locks again then). While open, and in the frame it was closed, it owns
    /// Escape (OwnsEscape), so the Escape menu never reads the same press. The display is the scene's Shelf Reader
    /// Canvas (prefab instance, inactive while closed), saved in the scene with its references; with incomplete
    /// references it reports once and Open returns false.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShelfReader : MonoBehaviour
    {
        const float DefaultDragThreshold = 10f;          // pixels, when no EventSystem is there to ask

        const float StillBody = 0.01f;                   // body offset (canvas units) still counted as "did not move"

        enum StepKind { Move, Press, Release, Scroll, Cancel }

        /// <summary>
        /// One mouse input of the open screen, in arrival order, with the position it had. Press and release also carry
        /// the link under that position and the body position, both as they were when the event arrived.
        /// </summary>
        struct Step
        {
            public StepKind Kind;
            public Vector2 Position;
            public int Link;
            public Vector2 Body;
        }

        [Tooltip("읽는 동안 입력을 막을 1인칭 플레이어")]
        [SerializeField] FirstPersonPlayer player;

        [Header("화면 (씬에 배치된 Shelf Reader Canvas)")]
        [Tooltip("읽기 화면 전체(화면을 검게 덮고 클릭을 막는 배경 포함). 닫혀 있는 동안 비활성")]
        [SerializeField] GameObject display;
        [SerializeField] Canvas canvas;
        [Tooltip("표제어")]
        [SerializeField] TextMeshProUGUI titleText;
        [Tooltip("독서대 뜻 표제 · 품사")]
        [SerializeField] TextMeshProUGUI subtitleText;
        [Tooltip("본문(뜻풀이). 링크는 <link>로 표시하고 이 텍스트에서 마우스 위치를 찾습니다.")]
        [SerializeField] TextMeshProUGUI bodyText;
        [Tooltip("본문 스크롤(드래그 시작을 알리는 ShelfScrollRect). viewport 밖 글자는 클릭되지 않습니다.")]
        [SerializeField] ShelfScrollRect scroll;
        [Tooltip("이동 실패 등 안내")]
        [SerializeField] TextMeshProUGUI noticeText;
        [SerializeField] Button closeButton;

        [Header("링크 표시")]
        [Tooltip("링크 단어 색(일반 본문보다 은은하게 구분)")]
        [SerializeField] Color linkColor = new Color32(0x8C, 0x4A, 0x14, 0xFF);
        [Tooltip("마우스를 올린 링크 단어 색")]
        [SerializeField] Color hoverColor = new Color32(0xB0, 0x52, 0x00, 0xFF);
        [Tooltip("마우스를 올린 링크 단어 뒤의 빛(반투명)")]
        [SerializeField] Color hoverGlow = new Color32(0xF2, 0xB8, 0x4A, 0x60);

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        readonly List<string> targets = new List<string>();      // link index → headword
        readonly List<Step> steps = new List<Step>();             // mouse inputs not settled yet, in arrival order
        readonly StringBuilder sb = new StringBuilder();
        Action<InputEventPtr, InputDevice> onEvent;
        bool listening;
        bool ready;
        bool open;
        bool blocking;
        bool invoking;
        int closedFrame = -1;
        int dragFrame = -1;                              // frame in which the body's ScrollRect last started a drag
        int scrollFrame = -1;                            // frame in which the body's ScrollRect last applied a wheel scroll
        int hovered = -1;
        int pressed = -1;                                // link under the current press, -1: none or cancelled
        Vector2 pressPosition;
        Vector2 pressBody;                               // body position when the current press arrived
        Vector2 eventPosition;                           // mouse position as of the last input event read
        bool eventButton;                                // left button as of the last input event read

        /// <summary>A link of the open document was clicked: the headword to go to (raised once per click).</summary>
        public event Action<string> LinkClicked;

        public bool IsOpen => open;
        public ShelfDocument Current { get; private set; }

        /// <summary>Escape belongs to this screen now: it is open, or it was closed in this frame.</summary>
        public bool OwnsEscape => open || Time.frameCount == closedFrame;

        /// <summary>The display can be seen and clicked: active in the hierarchy with its Canvas on.</summary>
        bool Usable => display != null && display.activeInHierarchy && canvas != null && canvas.isActiveAndEnabled;

        void Awake()
        {
            string missing = null;
            ArchiveUi.Need(player, nameof(player), ref missing);
            ArchiveUi.Need(display, nameof(display), ref missing);
            ArchiveUi.Need(canvas, nameof(canvas), ref missing);
            ArchiveUi.Need(titleText, nameof(titleText), ref missing);
            ArchiveUi.Need(subtitleText, nameof(subtitleText), ref missing);
            ArchiveUi.Need(bodyText, nameof(bodyText), ref missing);
            ArchiveUi.Need(scroll, nameof(scroll), ref missing);
            ArchiveUi.Need(noticeText, nameof(noticeText), ref missing);
            ArchiveUi.Need(closeButton, nameof(closeButton), ref missing);
            ready = missing == null && scroll.viewport != null && scroll.content != null;
            if (!ready)
            {
                Debug.LogError($"[서가 읽기] ShelfReader의 참조가 비어 있어 읽기 화면을 쓰지 않습니다: {missing ?? "scroll의 viewport·content"}. ShelfCorridorPrototype 씬의 Shelf Reader Canvas 참조를 Inspector에서 확인하세요.", this);
                return;
            }
            onEvent = OnInputEvent;
            ArchiveUi.Bind(bindings, closeButton, Close);
            scroll.DragStarted += OnDragStarted;
            scroll.Scrolled += OnScrolled;
            bodyText.richText = true;
            if (display.activeSelf) display.SetActive(false);                // closed until a lectern is read
        }

        void OnDisable()
        {
            CancelPress();
            Close();
            Listen(false);
        }

        void OnDestroy()
        {
            Listen(false);
            ArchiveUi.Unbind(bindings);
            if (scroll != null)
            {
                scroll.DragStarted -= OnDragStarted;
                scroll.Scrolled -= OnScrolled;
            }
        }

        void OnApplicationFocus(bool focus)
        {
            CancelPress();                                                   // a press from before the change never clicks
            steps.Clear();
            if (focus) SyncPointer();                                        // only presses after the focus came back count
        }

        /// <summary>Spreads `document` open (scrolled to the top). False when the screen cannot be used.</summary>
        public bool Open(ShelfDocument document)
        {
            if (!ready || open || !isActiveAndEnabled || document == null) return false;
            Current = document;
            targets.Clear();
            foreach (var p in document.Sense.Parts)
                if (p.IsLink) targets.Add(p.Target);
            hovered = -1;
            CancelPress();
            titleText.text = document.Word;
            subtitleText.text = string.IsNullOrEmpty(document.Pos) ? document.Title : $"{document.Title}  ·  「{document.Pos}」";
            noticeText.text = "";

            display.SetActive(true);
            if (!canvas.enabled) canvas.enabled = true;                      // turned off while it was open last time
            if (!Usable)
            {
                display.SetActive(false);
                Debug.LogError("[서가 읽기] 읽기 화면이나 Canvas를 켤 수 없어 열지 않았습니다.", this);
                return false;
            }
            open = true;
            Render();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            scroll.StopMovement();
            scroll.verticalNormalizedPosition = 1f;                          // from the top
            bodyText.ForceMeshUpdate();                                      // link positions of this document from the first frame
            player.SetInputBlocked(true);
            blocking = true;
            FirstPersonPlayer.ReleaseCursor();
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            steps.Clear();
            SyncPointer();
            Listen(true);
            return true;
        }

        /// <summary>Closes and gives input back (from the next frame); position and view stay as they were.</summary>
        public void Close()
        {
            if (!open) return;
            open = false;
            closedFrame = Time.frameCount;
            hovered = -1;
            CancelPress();
            Listen(false);
            Current = null;
            if (display != null) display.SetActive(false);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            if (blocking && player != null) player.SetInputBlocked(false);
            blocking = false;
        }

        /// <summary>A message under the document (e.g. why a move failed); the screen stays open.</summary>
        public void ShowNotice(string text)
        {
            if (ready) noticeText.text = text ?? "";
        }

        void Update()
        {
            if (!open) return;
            if (!Usable)
            {
                CloseUnusable();
                return;
            }
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                Close();
                return;
            }
            var mouse = Mouse.current;
            var link = mouse != null && Application.isFocused ? LinkAt(mouse.position.ReadValue()) : -1;
            if (link != hovered)                                             // highlight only; clicks are settled in LateUpdate
            {
                hovered = link;
                Render();
            }
        }

        /// <summary>
        /// Settles this frame's mouse inputs in arrival order, after every Update (the EventSystem's drag start of the
        /// same input is known by then), and raises at most one LinkClicked.
        /// </summary>
        void LateUpdate()
        {
            if (!open)
            {
                steps.Clear();
                return;
            }
            if (!Usable)
            {
                CloseUnusable();
                return;
            }
            if (dragFrame == Time.frameCount) CancelPress();                 // the drag started from a press made before this frame
            var sawPress = false;
            var sawRelease = false;
            var sawScroll = false;
            var clicked = -1;
            var threshold = DragThreshold();
            foreach (var step in steps)
            {
                switch (step.Kind)
                {
                    case StepKind.Cancel:
                        CancelPress();
                        break;
                    case StepKind.Scroll:
                        sawScroll = true;                                    // the body moves (or may): no held press survives it
                        CancelPress();
                        break;
                    case StepKind.Move:
                        if (pressed >= 0 && (step.Position - pressPosition).sqrMagnitude >= threshold * threshold) CancelPress();
                        break;
                    case StepKind.Press:
                        sawPress = true;
                        // After a wheel input of the same update the press may have hit the body before or after that
                        // scroll was shown: its link is unknown, so it never clicks.
                        pressed = sawScroll ? -1 : step.Link;
                        pressPosition = step.Position;
                        pressBody = step.Body;
                        break;
                    case StepKind.Release:
                        sawRelease = true;
                        var down = pressed;
                        CancelPress();
                        if (clicked < 0 && down >= 0 && step.Link == down && Same(step.Body, pressBody)) clicked = down;
                        break;
                }
            }
            steps.Clear();

            // The body moved since the press (scrollbar, inertia, a wheel applied this frame): neither the held press
            // nor a click settled now belongs to the layout it was made on.
            if (!Same(scroll.content.anchoredPosition, pressBody))
            {
                CancelPress();
                clicked = -1;
            }
            // The frame's button and wheel state must agree with the events read: an input without its event has no
            // known position or order, so nothing of it may click.
            var mouse = Mouse.current;
            if (mouse == null || (mouse.leftButton.wasPressedThisFrame && !sawPress) || (mouse.leftButton.wasReleasedThisFrame && !sawRelease) ||
                ((mouse.scroll.ReadValue() != Vector2.zero || scrollFrame == Time.frameCount) && !sawScroll))
            {
                CancelPress();
                clicked = -1;
            }
            if (clicked < 0 || invoking || !Application.isFocused) return;
            invoking = true;
            try
            {
                LinkClicked?.Invoke(targets[clicked]);
            }
            finally
            {
                invoking = false;
            }
        }

        /// <summary>Only the display or its Canvas was turned off while open: no hidden link may be clicked, closed as usual.</summary>
        void CloseUnusable()
        {
            CancelPress();
            steps.Clear();
            Debug.LogWarning("[서가 읽기] 읽기 화면이나 Canvas가 꺼져 읽기 화면을 닫습니다(입력 잠금 해제).", this);
            Close();
        }

        /// <summary>Drops the press in progress; only a new press can click.</summary>
        void CancelPress() => pressed = -1;

        void OnDragStarted()
        {
            dragFrame = Time.frameCount;
            CancelPress();
        }

        /// <summary>The ScrollRect got a wheel scroll from the EventSystem (this frame): a held press no longer matches the body.</summary>
        void OnScrolled()
        {
            scrollFrame = Time.frameCount;
            CancelPress();
        }

        static bool Same(Vector2 a, Vector2 b) => (a - b).sqrMagnitude <= StillBody * StillBody;

        float DragThreshold() => EventSystem.current != null ? Mathf.Max(1, EventSystem.current.pixelDragThreshold) : DefaultDragThreshold;

        // ---- mouse input events --------------------------------------------------------------------------------------

        void Listen(bool on)
        {
            if (on == listening || onEvent == null) return;
            if (on) InputSystem.onEvent += onEvent;
            else
            {
                InputSystem.onEvent -= onEvent;
                steps.Clear();
            }
            listening = on;
        }

        /// <summary>The event reading starts from the mouse's current state (no event of it is pending then).</summary>
        void SyncPointer()
        {
            var mouse = Mouse.current;
            eventPosition = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
            eventButton = mouse != null && mouse.leftButton.isPressed;
        }

        /// <summary>
        /// Called by the Input System for each event before it is applied and before the EventSystem runs for the frame:
        /// a mouse state event gives its position (when in the event), its wheel and then its left-button change, queued
        /// in that order (wheel before button when one event holds both, so such a press is taken as unknown). A press or
        /// release takes the link under it and the body position now, the layout on screen when it happened. Without
        /// focus every input cancels.
        /// </summary>
        void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (!open || !(device is Mouse mouse) || (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>())) return;
            if (!Application.isFocused)
            {
                steps.Add(new Step { Kind = StepKind.Cancel });
                return;
            }
            if (mouse.position.ReadValueFromEvent(eventPtr, out var position) && position != eventPosition)
            {
                eventPosition = position;
                steps.Add(new Step { Kind = StepKind.Move, Position = position });
            }
            if (mouse.scroll.ReadValueFromEvent(eventPtr, out var wheel) && wheel != Vector2.zero)
                steps.Add(new Step { Kind = StepKind.Scroll, Position = eventPosition });
            if (!mouse.leftButton.ReadValueFromEvent(eventPtr, out var value)) return;
            var down = mouse.leftButton.IsValueConsideredPressed(value);
            if (down == eventButton) return;
            eventButton = down;
            steps.Add(new Step
            {
                Kind = down ? StepKind.Press : StepKind.Release,
                Position = eventPosition,
                Link = LinkAt(eventPosition),
                Body = scroll.content.anchoredPosition,
            });
        }

        // ---- body ------------------------------------------------------------------------------------------------------

        /// <summary>The link index under the screen point, inside the visible part of the body; -1 when none.</summary>
        int LinkAt(Vector2 position)
        {
            if (!Usable) return -1;
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (!RectTransformUtility.RectangleContainsScreenPoint(scroll.viewport, position, cam)) return -1;
            var i = TMP_TextUtilities.FindIntersectingLink(bodyText, position, cam);
            var info = bodyText.textInfo;
            if (i < 0 || info == null || i >= info.linkCount) return -1;
            return int.TryParse(info.linkInfo[i].GetLinkID(), out var id) && id >= 0 && id < targets.Count ? id : -1;
        }

        /// <summary>
        /// The body as rich text: the document's one definition (its number is in the subtitle), plain parts literal
        /// (noparse), links tagged by index, the hovered one glowing.
        /// </summary>
        void Render()
        {
            if (Current == null) return;
            sb.Clear();
            var index = 0;
            foreach (var p in Current.Sense.Parts)
            {
                if (!p.IsLink)
                {
                    Literal(p.Text);
                    continue;
                }
                var hot = index == hovered;
                sb.Append("<link=\"").Append(index).Append("\">");
                if (hot) sb.Append("<mark=#").Append(ColorUtility.ToHtmlStringRGBA(hoverGlow)).Append("><u>");
                sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGBA(hot ? hoverColor : linkColor)).Append('>');
                Literal(p.Text);
                sb.Append("</color>");
                if (hot) sb.Append("</u></mark>");
                sb.Append("</link>");
                index++;
            }
            bodyText.text = sb.ToString();
        }

        void Literal(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            sb.Append("<noparse>").Append(text).Append("</noparse>");
        }
    }
}
