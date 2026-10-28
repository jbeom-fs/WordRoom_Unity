using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.Network;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// Coop lobby UI (same building blocks and palette as the other menus): the 협동 button of the main panel and the
    /// Coop Canvas (lobby, create panel, waiting room, room row template, four participant rows). Single-line texts
    /// that may get long (room and participant names, RoomCode, status) do not wrap and end with an ellipsis inside
    /// their own fixed or flexible column, so counts and buttons keep their place.
    /// </summary>
    public static partial class ArchiveUiBuilder
    {
        public const string CoopPrefabPath = PrefabFolder + "/CoopCanvas.prefab";

        /// <summary>Above the main menu (100), below nothing else of the MainMenu scene.</summary>
        public const int CoopOrder = 120;

        /// <summary>The main panel's 협동 button (between 시작 and 설정).</summary>
        public static Button AddCoopButton(Transform main) => AddButton(main, "CoopButton", "협동", 360f, 68f, 28f);

        /// <summary>
        /// Coop canvas (order 120): a dim overlay catching clicks (the main menu behind never reacts), the Lobby panel,
        /// the Create panel (its own overlay, inactive) and the Waiting panel (inactive). CoopLobbyView on the root holds
        /// every part. The root is saved active; the MainMenu scene instance is placed inactive (CoopLobbyScreen opens it).
        /// </summary>
        public static GameObject BuildCoopCanvas()
        {
            var root = CanvasRoot("Coop Canvas", CoopOrder, true);
            var overlay = NewRect("Overlay", root.transform);
            Stretch(overlay);
            AddImage(overlay.gameObject, ArchiveUi.Dim, true);

            var view = root.AddComponent<CoopLobbyView>();
            var so = new SerializedObject(view);
            BuildLobby(root.transform, so);
            BuildCreate(root.transform, so);
            BuildWaiting(root.transform, so);
            so.ApplyModifiedPropertiesWithoutUndo();
            return root;
        }

        // ---- lobby ----------------------------------------------------------------------------------------------
        static void BuildLobby(Transform root, SerializedObject so)
        {
            var panel = Panel(root, "Lobby");
            Center(panel, 1320f);
            Vertical(panel, 20f, 36, TextAnchor.UpperCenter);

            var header = NewRect("Header", panel);
            Horizontal(header, 16f, TextAnchor.MiddleLeft);
            Size(header.gameObject, 1248f, 64f);
            var title = AddText(header, "Title", "협동 로비", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            OneLine(title);
            Size(title.gameObject, 260f, 64f);
            var status = AddText(header, "Status", "연결되지 않음", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Right);
            OneLine(status);
            Size(status.gameObject, 0f, 64f, 1f);
            var retry = AddButton(header, "RetryButton", "다시 연결", 180f, 56f, 22f);

            var body = NewRect("Body", panel);
            Horizontal(body, 32f, TextAnchor.UpperLeft);
            Size(body.gameObject, 1248f, 624f);

            // room list
            var listColumn = NewRect("RoomList", body);
            Vertical(listColumn, 8f, 0, TextAnchor.UpperLeft).childForceExpandWidth = true;
            Size(listColumn.gameObject, 776f, 624f);
            var columns = NewRect("Columns", listColumn);
            Horizontal(columns, 16f, TextAnchor.MiddleLeft).padding = new RectOffset(24, 46, 0, 0);
            Size(columns.gameObject, -1f, 32f);
            ColumnLabel(columns, "NameLabel", "방 이름", 0f, 1f, TextAlignmentOptions.Left);
            ColumnLabel(columns, "CountLabel", "인원", 120f, -1f, TextAlignmentOptions.Right);
            ColumnLabel(columns, "StateLabel", "상태", 140f, -1f, TextAlignmentOptions.Center);
            var box = NewRect("ListBox", listColumn);
            Size(box.gameObject, -1f, 584f);
            AddImage(box.gameObject, ArchiveUi.Well, false);
            AddFrame(box);
            var scroll = AddScroll(box, out var content);
            Stretch(scroll.GetComponent<RectTransform>(), 8f);
            var template = RoomRow(content);
            var listMessage = AddText(box, "ListMessage", "로비 연결을 기다리는 중입니다.", 24f, ArchiveUi.IvoryDim, TextAlignmentOptions.Center);
            Stretch(listMessage.rectTransform, 40f);

            // side: selected room, create, RoomCode
            var side = NewRect("Side", body);
            Vertical(side, 12f, 0, TextAnchor.UpperLeft).childForceExpandWidth = true;
            Size(side.gameObject, 440f, 624f);
            SectionLabel(side, "SelectedLabel", "선택한 방");
            var selectedName = AddText(side, "SelectedName", "선택한 방 없음", 28f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            OneLine(selectedName);
            Size(selectedName.gameObject, -1f, 40f);
            var selectedCode = AddText(side, "SelectedCode", "", 20f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            OneLine(selectedCode);
            Size(selectedCode.gameObject, -1f, 30f);
            var infoRow = NewRect("SelectedInfo", side);
            Horizontal(infoRow, 16f, TextAnchor.MiddleLeft);
            Size(infoRow.gameObject, -1f, 32f);
            var selectedCount = AddText(infoRow, "SelectedCount", "", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            OneLine(selectedCount);
            Size(selectedCount.gameObject, 140f, 32f);
            var selectedState = AddText(infoRow, "SelectedState", "", 22f, ArchiveUi.Success, TextAlignmentOptions.Left);
            OneLine(selectedState);
            Size(selectedState.gameObject, 0f, 32f, 1f);
            var joinSelected = AddButton(side, "JoinSelectedButton", "선택한 방 입장", 440f, 60f, 24f);
            Divider(side, "Divider1");
            var create = AddButton(side, "CreateButton", "방 만들기", 440f, 60f, 24f);
            Divider(side, "Divider2");
            SectionLabel(side, "CodeLabel", "RoomCode로 참가");
            var codeInput = AddInputField(side, "CodeInput", "RoomCode 입력 (대소문자 구분)", 440f, 56f, 64);
            var joinCode = AddButton(side, "JoinCodeButton", "코드로 입장", 440f, 60f, 24f);

            var footer = NewRect("Footer", panel);
            Horizontal(footer, 16f, TextAnchor.MiddleLeft);
            Size(footer.gameObject, 1248f, 64f);
            var message = AddText(footer, "Message", "", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            message.overflowMode = TextOverflowModes.Ellipsis;
            Size(message.gameObject, 0f, 64f, 1f);
            var back = AddButton(footer, "BackButton", "뒤로", 200f, 60f, 24f);

            Set(so, "lobbyPanel", panel.gameObject);
            Set(so, "statusText", status);
            Set(so, "retryButton", retry);
            Set(so, "roomScroll", scroll);
            Set(so, "roomContent", content);
            Set(so, "roomRowTemplate", template);
            Set(so, "listMessage", listMessage);
            Set(so, "selectedName", selectedName);
            Set(so, "selectedCode", selectedCode);
            Set(so, "selectedCount", selectedCount);
            Set(so, "selectedState", selectedState);
            Set(so, "joinSelectedButton", joinSelected);
            Set(so, "createButton", create);
            Set(so, "codeInput", codeInput);
            Set(so, "joinCodeButton", joinCode);
            Set(so, "backButton", back);
            Set(so, "lobbyMessage", message);
        }

        /// <summary>The template of a room row (inactive): framed walnut button, name (flexible), count and state columns.</summary>
        static CoopRoomRow RoomRow(RectTransform content)
        {
            var r = NewRect("RoomRowTemplate", content);
            var image = AddImage(r.gameObject, new Color32(255, 255, 255, 255), true);   // tinted by the color block
            var b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = image;
            b.colors = Colors(ArchiveUi.Walnut);
            AddFrame(r);
            Horizontal(r, 16f, TextAnchor.MiddleLeft).padding = new RectOffset(20, 20, 0, 0);
            var name = AddText(r, "Name", "방 이름", 24f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            OneLine(name);
            Size(name.gameObject, 0f, 64f, 1f);
            var count = AddText(r, "Count", "1 / 4명", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Right);
            OneLine(count);
            Size(count.gameObject, 120f, 64f);
            var state = AddText(r, "State", "입장 가능", 22f, ArchiveUi.Success, TextAlignmentOptions.Center);
            OneLine(state);
            Size(state.gameObject, 140f, 64f);
            Size(r.gameObject, -1f, 64f);

            var row = r.gameObject.AddComponent<CoopRoomRow>();
            var so = new SerializedObject(row);
            Set(so, "button", b);
            Set(so, "nameText", name);
            Set(so, "countText", count);
            Set(so, "stateText", state);
            var frame = so.FindProperty("frame");
            var edges = new[] { "FrameTop", "FrameBottom", "FrameLeft", "FrameRight" };
            frame.arraySize = edges.Length;
            for (var i = 0; i < edges.Length; i++) frame.GetArrayElementAtIndex(i).objectReferenceValue = r.Find(edges[i]).GetComponent<Image>();
            so.ApplyModifiedPropertiesWithoutUndo();
            r.gameObject.SetActive(false);
            return row;
        }

        // ---- create ---------------------------------------------------------------------------------------------
        static void BuildCreate(Transform root, SerializedObject so)
        {
            var layer = NewRect("Create", root);
            Stretch(layer);
            AddImage(layer.gameObject, ArchiveUi.Dim, true);                 // the lobby behind never reacts

            var panel = Panel(layer, "Panel");
            Center(panel, 720f);
            Vertical(panel, 16f, 48, TextAnchor.UpperCenter);
            var title = AddText(panel, "Title", "방 만들기", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(title.gameObject, 624f, 60f);
            FieldLabel(panel, "NameLabel", "방 이름");
            var nameInput = AddInputField(panel, "NameInput", "방 이름 입력", 624f, 56f, CoopRules.MaxDisplayNameLength);
            var hint = AddText(panel, "NameHint", $"최대 {CoopRules.MaxDisplayNameLength}자 · 비우면 RoomCode를 이름으로 사용합니다", 18f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            Size(hint.gameObject, 624f, 28f);
            FieldLabel(panel, "SizeLabel", "최대 인원");
            var sizes = NewRect("Sizes", panel);
            Horizontal(sizes, 24f, TextAnchor.MiddleCenter);
            Size(sizes.gameObject, 624f, 60f);
            var sizeButtons = new Button[CoopRules.MaxPlayers - CoopRules.MinPlayers + 1];
            for (var i = 0; i < sizeButtons.Length; i++)
                sizeButtons[i] = AddButton(sizes, $"Size{CoopRules.MinPlayers + i}Button", $"{CoopRules.MinPlayers + i}명", 192f, 60f, 24f);
            var message = AddText(panel, "Message", "", 20f, ArchiveUi.IvoryDim, TextAlignmentOptions.Center);
            Size(message.gameObject, 624f, 30f);
            var footer = NewRect("Footer", panel);
            Horizontal(footer, 20f, TextAnchor.MiddleCenter);
            Size(footer.gameObject, 624f, 64f);
            var cancel = AddButton(footer, "CancelButton", "취소", 260f, 64f, 26f);
            var confirm = AddButton(footer, "CreateButton", "만들기", 260f, 64f, 26f);
            layer.gameObject.SetActive(false);

            Set(so, "createPanel", layer.gameObject);
            Set(so, "nameInput", nameInput);
            var p = so.FindProperty("sizeButtons");
            p.arraySize = sizeButtons.Length;
            for (var i = 0; i < sizeButtons.Length; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = sizeButtons[i];
            Set(so, "createConfirmButton", confirm);
            Set(so, "createCancelButton", cancel);
            Set(so, "createMessage", message);
        }

        // ---- waiting room ---------------------------------------------------------------------------------------
        static void BuildWaiting(Transform root, SerializedObject so)
        {
            var panel = Panel(root, "Waiting");
            Center(panel, 1080f);
            Vertical(panel, 16f, 40, TextAnchor.UpperCenter);

            var title = AddText(panel, "Title", "방 이름", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            OneLine(title);
            Size(title.gameObject, 1000f, 60f);

            var codeRow = NewRect("CodeRow", panel);
            Horizontal(codeRow, 16f, TextAnchor.MiddleLeft);
            Size(codeRow.gameObject, 1000f, 56f);
            var codeLabel = AddText(codeRow, "CodeLabel", "RoomCode", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            OneLine(codeLabel);
            Size(codeLabel.gameObject, 130f, 56f);
            var code = AddText(codeRow, "Code", "ABCD2345", 30f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            OneLine(code);
            Size(code.gameObject, 0f, 56f, 1f);
            var copy = AddButton(codeRow, "CopyButton", "복사", 140f, 52f, 22f);
            var count = AddText(codeRow, "Count", "1 / 4명", 26f, ArchiveUi.IvoryDim, TextAlignmentOptions.Right);
            OneLine(count);
            Size(count.gameObject, 160f, 56f);
            AddDifficultyRow(panel, so);

            SectionLabel(panel, "PlayersLabel", "참가자", 1000f);
            var list = NewRect("Players", panel);
            AddImage(list.gameObject, ArchiveUi.Well, false);
            AddFrame(list);
            var v = Vertical(list, 8f, 12, TextAnchor.UpperLeft);
            v.childForceExpandWidth = true;
            Size(list.gameObject, 1000f, 4 * 72f + 3 * 8f + 24f);
            var rows = new CoopPlayerRow[CoopRules.MaxPlayers];
            for (var i = 0; i < rows.Length; i++) rows[i] = PlayerRow(list, i);

            var note = AddText(panel, "StartNote", "", 20f, ArchiveUi.IvoryDim, TextAlignmentOptions.Center);
            Size(note.gameObject, 1000f, 30f);
            var buttons = NewRect("Buttons", panel);
            Horizontal(buttons, 24f, TextAnchor.MiddleCenter);
            Size(buttons.gameObject, 1000f, 64f);
            var readyButton = AddButton(buttons, "ReadyButton", "준비", 260f, 64f, 26f);
            var start = AddButton(buttons, "StartButton", "시작", 260f, 64f, 26f);
            start.interactable = false;                                       // no coop start yet
            var leave = AddButton(buttons, "LeaveButton", "나가기", 260f, 64f, 26f);
            var message = AddText(panel, "Message", "", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Center);
            message.overflowMode = TextOverflowModes.Ellipsis;
            Size(message.gameObject, 1000f, 32f);
            panel.gameObject.SetActive(false);

            Set(so, "waitingPanel", panel.gameObject);
            Set(so, "roomTitle", title);
            Set(so, "roomCode", code);
            Set(so, "copyCodeButton", copy);
            Set(so, "roomCount", count);
            var p = so.FindProperty("playerRows");
            p.arraySize = rows.Length;
            for (var i = 0; i < rows.Length; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = rows[i];
            Set(so, "readyButton", readyButton);
            Set(so, "readyLabel", readyButton.transform.Find("Label").GetComponent<TextMeshProUGUI>());
            Set(so, "startButton", start);
            Set(so, "startNote", note);
            Set(so, "leaveButton", leave);
            Set(so, "waitingMessage", message);
        }

        public const string DifficultyRowName = "DifficultyRow";

        /// <summary>
        /// The waiting room's difficulty row (label, 쉬움·보통·어려움 buttons, note) appended to `waiting` (its caller
        /// places it after the RoomCode row) and connected to the CoopLobbyView of `so`.
        /// </summary>
        public static RectTransform AddDifficultyRow(RectTransform waiting, SerializedObject so)
        {
            var row = NewRect(DifficultyRowName, waiting);
            Vertical(row, 8f, 0, TextAnchor.UpperLeft).childForceExpandWidth = true;
            Size(row.gameObject, 1000f, 108f);
            var buttonsRow = NewRect("Buttons", row);
            Horizontal(buttonsRow, 16f, TextAnchor.MiddleLeft);
            Size(buttonsRow.gameObject, -1f, 60f);
            var label = AddText(buttonsRow, "Label", "난이도", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            OneLine(label);
            Size(label.gameObject, 130f, 60f);
            var buttons = new Button[3];
            var names = new[] { "Easy", "Normal", "Hard" };
            for (var i = 0; i < buttons.Length; i++)
                buttons[i] = AddButton(buttonsRow, $"{names[i]}Button", GameLaunch.Label((Difficulty)i), 200f, 56f, 24f);
            var note = AddText(row, "Note", "난이도: 보통 · 호스트가 정합니다", 20f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            OneLine(note);
            Size(note.gameObject, -1f, 32f);
            var p = so.FindProperty("difficultyButtons") ?? throw new System.InvalidOperationException("CoopLobbyView에 'difficultyButtons' 필드가 없습니다");
            p.arraySize = buttons.Length;
            for (var i = 0; i < buttons.Length; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
            Set(so, "difficultyText", note);
            return row;
        }

        static CoopPlayerRow PlayerRow(RectTransform list, int index)
        {
            var r = NewRect($"Player{index}", list);
            AddImage(r.gameObject, ArchiveUi.Walnut, false);
            Horizontal(r, 16f, TextAnchor.MiddleLeft).padding = new RectOffset(24, 24, 0, 0);
            Size(r.gameObject, -1f, 72f);
            var name = AddText(r, "Name", $"참가자 {index + 1}", 26f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            OneLine(name);
            Size(name.gameObject, 0f, 72f, 1f);
            var host = AddText(r, "Host", "", 22f, ArchiveUi.Brass, TextAlignmentOptions.Center);
            OneLine(host);
            Size(host.gameObject, 120f, 72f);
            var ready = AddText(r, "Ready", "준비 안 됨", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Center);
            OneLine(ready);
            Size(ready.gameObject, 160f, 72f);
            var row = r.gameObject.AddComponent<CoopPlayerRow>();
            var so = new SerializedObject(row);
            Set(so, "nameText", name);
            Set(so, "hostText", host);
            Set(so, "readyText", ready);
            so.ApplyModifiedPropertiesWithoutUndo();
            r.gameObject.SetActive(false);                                    // shown per participant
            return row;
        }

        // ---- small parts ----------------------------------------------------------------------------------------
        /// <summary>
        /// Single-line text input: #211A16 inside, brass frame, ivory text with a clipped text area, dim placeholder,
        /// no rich text (typed tags stay as typed), `limit` characters.
        /// </summary>
        public static TMP_InputField AddInputField(Transform parent, string name, string placeholder, float width, float height, int limit)
        {
            var r = NewRect(name, parent);
            var background = AddImage(r.gameObject, new Color32(255, 255, 255, 255), true);   // tinted by the color block
            AddFrame(r);
            var area = NewRect("Text Area", r);
            Stretch(area, 14f);
            area.gameObject.AddComponent<RectMask2D>();
            var hint = AddText(area, "Placeholder", placeholder, 22f, new Color32(0xD8, 0xD0, 0xBF, 150), TextAlignmentOptions.Left);
            Stretch(hint.rectTransform);
            OneLine(hint);
            var text = AddText(area, "Text", "", 22f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            Stretch(text.rectTransform);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.richText = false;
            var field = r.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = hint;
            field.targetGraphic = background;
            field.colors = new ColorBlock
            {
                normalColor = ArchiveUi.Well,
                highlightedColor = new Color32(0x2B, 0x22, 0x1D, 255),
                pressedColor = ArchiveUi.Well,
                selectedColor = new Color32(0x2E, 0x24, 0x1E, 255),
                disabledColor = ArchiveUi.Disabled,
                colorMultiplier = 1f,
                fadeDuration = 0.08f,
            };
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.characterLimit = limit;
            field.richText = false;
            field.customCaretColor = true;
            field.caretColor = ArchiveUi.Ivory;
            field.caretWidth = 2;
            field.selectionColor = new Color32(0x86, 0x69, 0x3C, 180);
            if (Font != null) field.fontAsset = Font;
            field.pointSize = 22f;
            Size(r.gameObject, width, height);
            return field;
        }

        static void OneLine(TextMeshProUGUI t)
        {
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
        }

        static void ColumnLabel(RectTransform row, string name, string text, float width, float flexible, TextAlignmentOptions align)
        {
            var t = AddText(row, name, text, 20f, ArchiveUi.IvoryDim, align);
            OneLine(t);
            Size(t.gameObject, width, 32f, flexible);
        }

        static void SectionLabel(RectTransform parent, string name, string text, float width = -1f)
        {
            var t = AddText(parent, name, text, 24f, ArchiveUi.Brass, TextAlignmentOptions.BottomLeft);
            OneLine(t);
            Size(t.gameObject, width, 36f);
        }

        static void FieldLabel(RectTransform parent, string name, string text)
        {
            var t = AddText(parent, name, text, 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            OneLine(t);
            Size(t.gameObject, 624f, 32f);
        }

        /// <summary>A brass line FrameThickness thick across the column.</summary>
        static void Divider(RectTransform parent, string name)
        {
            var r = NewRect(name, parent);
            AddImage(r.gameObject, ArchiveUi.Brass, false);
            Size(r.gameObject, -1f, FrameThickness);
        }
    }
}
