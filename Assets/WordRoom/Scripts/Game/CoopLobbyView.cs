using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordRoom.Game
{
    /// <summary>
    /// The parts of the Coop Canvas prefab (saved by WordRoom/협동 로비 구성; nothing is built at runtime): the lobby
    /// (connection status, 새로고침, room list with its template row, selected room, create, RoomCode entry, back), the create
    /// panel over it, and the waiting room (name, RoomCode with copy, count, four participant rows, ready, start,
    /// leave). Only references; CoopLobbyScreen drives them. The root is saved active; the scene instance is placed
    /// inactive and opened by the screen.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopLobbyView : MonoBehaviour
    {
        [Header("로비")]
        public GameObject lobbyPanel;
        public TextMeshProUGUI statusText;
        public Button retryButton;
        public ScrollRect roomScroll;
        public RectTransform roomContent;
        [Tooltip("방 목록 행 템플릿(비활성). 실행 중 필요한 만큼 복제해 재사용합니다.")]
        public CoopRoomRow roomRowTemplate;
        public TextMeshProUGUI listMessage;
        public TextMeshProUGUI selectedName;
        public TextMeshProUGUI selectedCode;
        public TextMeshProUGUI selectedCount;
        public TextMeshProUGUI selectedState;
        public Button joinSelectedButton;
        public Button createButton;
        public TMP_InputField codeInput;
        public Button joinCodeButton;
        public Button backButton;
        public TextMeshProUGUI lobbyMessage;

        [Header("방 만들기")]
        public GameObject createPanel;
        public TMP_InputField nameInput;
        [Tooltip("최대 인원 2·3·4명 순서")]
        public Button[] sizeButtons = new Button[3];
        public Button createConfirmButton;
        public Button createCancelButton;
        public TextMeshProUGUI createMessage;

        [Header("대기실")]
        public GameObject waitingPanel;
        public TextMeshProUGUI roomTitle;
        public TextMeshProUGUI roomCode;
        public Button copyCodeButton;
        public TextMeshProUGUI roomCount;
        public CoopPlayerRow[] playerRows = new CoopPlayerRow[4];
        public Button readyButton;
        public TextMeshProUGUI readyLabel;
        public Button startButton;
        public TextMeshProUGUI startNote;
        public Button leaveButton;
        public TextMeshProUGUI waitingMessage;

        [Header("대기실 난이도 (WordRoom/협동 로비 구성으로 추가)")]
        [Tooltip("쉬움·보통·어려움 순서. 호스트만 누를 수 있습니다.")]
        public Button[] difficultyButtons = new Button[3];
        [Tooltip("현재 난이도와 바꿀 수 있는 사람 안내")]
        public TextMeshProUGUI difficultyText;

        [Header("로비 새로고침 (WordRoom/협동 로비 구성으로 추가)")]
        [Tooltip("방 목록 새로고침. 비어 있으면 새로고침만 쓸 수 없고 로비의 다른 기능은 그대로입니다.")]
        public Button refreshButton;

        /// <summary>The lobby's 새로고침 button is connected (a canvas saved before it has none: no refresh then).</summary>
        public bool HasRefresh => refreshButton != null;

        /// <summary>The waiting room's difficulty parts are all there (a canvas saved before them has none: no start then).</summary>
        public bool HasDifficulty
        {
            get
            {
                if (difficultyText == null || difficultyButtons == null || difficultyButtons.Length != 3) return false;
                foreach (var b in difficultyButtons)
                    if (b == null) return false;
                return true;
            }
        }

        /// <summary>Missing parts by field name, or null when complete.</summary>
        public string MissingParts()
        {
            string missing = null;
            ArchiveUi.Need(lobbyPanel, nameof(lobbyPanel), ref missing);
            ArchiveUi.Need(statusText, nameof(statusText), ref missing);
            ArchiveUi.Need(retryButton, nameof(retryButton), ref missing);
            ArchiveUi.Need(roomScroll, nameof(roomScroll), ref missing);
            ArchiveUi.Need(roomContent, nameof(roomContent), ref missing);
            ArchiveUi.Need(roomRowTemplate, nameof(roomRowTemplate), ref missing);
            if (roomRowTemplate != null && !roomRowTemplate.IsComplete) Add(ref missing, "roomRowTemplate(행 참조)");
            if (roomRowTemplate != null && roomContent != null && roomRowTemplate.transform.parent != roomContent) Add(ref missing, "roomRowTemplate(roomContent 아래에 있어야 함)");
            ArchiveUi.Need(listMessage, nameof(listMessage), ref missing);
            ArchiveUi.Need(selectedName, nameof(selectedName), ref missing);
            ArchiveUi.Need(selectedCode, nameof(selectedCode), ref missing);
            ArchiveUi.Need(selectedCount, nameof(selectedCount), ref missing);
            ArchiveUi.Need(selectedState, nameof(selectedState), ref missing);
            ArchiveUi.Need(joinSelectedButton, nameof(joinSelectedButton), ref missing);
            ArchiveUi.Need(createButton, nameof(createButton), ref missing);
            ArchiveUi.Need(codeInput, nameof(codeInput), ref missing);
            ArchiveUi.Need(joinCodeButton, nameof(joinCodeButton), ref missing);
            ArchiveUi.Need(backButton, nameof(backButton), ref missing);
            ArchiveUi.Need(lobbyMessage, nameof(lobbyMessage), ref missing);
            ArchiveUi.Need(createPanel, nameof(createPanel), ref missing);
            ArchiveUi.Need(nameInput, nameof(nameInput), ref missing);
            if (sizeButtons == null || sizeButtons.Length != 3) Add(ref missing, "sizeButtons(3개)");
            else
                for (var i = 0; i < sizeButtons.Length; i++) ArchiveUi.Need(sizeButtons[i], $"sizeButtons[{i}]", ref missing);
            ArchiveUi.Need(createConfirmButton, nameof(createConfirmButton), ref missing);
            ArchiveUi.Need(createCancelButton, nameof(createCancelButton), ref missing);
            ArchiveUi.Need(createMessage, nameof(createMessage), ref missing);
            ArchiveUi.Need(waitingPanel, nameof(waitingPanel), ref missing);
            ArchiveUi.Need(roomTitle, nameof(roomTitle), ref missing);
            ArchiveUi.Need(roomCode, nameof(roomCode), ref missing);
            ArchiveUi.Need(copyCodeButton, nameof(copyCodeButton), ref missing);
            ArchiveUi.Need(roomCount, nameof(roomCount), ref missing);
            if (playerRows == null || playerRows.Length != 4) Add(ref missing, "playerRows(4개)");
            else
                for (var i = 0; i < playerRows.Length; i++)
                {
                    ArchiveUi.Need(playerRows[i], $"playerRows[{i}]", ref missing);
                    if (playerRows[i] != null && !playerRows[i].IsComplete) Add(ref missing, $"playerRows[{i}](행 참조)");
                }
            ArchiveUi.Need(readyButton, nameof(readyButton), ref missing);
            ArchiveUi.Need(readyLabel, nameof(readyLabel), ref missing);
            ArchiveUi.Need(startButton, nameof(startButton), ref missing);
            ArchiveUi.Need(startNote, nameof(startNote), ref missing);
            ArchiveUi.Need(leaveButton, nameof(leaveButton), ref missing);
            ArchiveUi.Need(waitingMessage, nameof(waitingMessage), ref missing);
            return missing;
        }

        static void Add(ref string missing, string what) => missing = missing == null ? what : $"{missing}, {what}";

        /// <summary>Either text field has the keyboard (typing): menu keys must not act then.</summary>
        public bool AnyInputFocused => (codeInput != null && codeInput.isFocused) || (nameInput != null && nameInput.isFocused);
    }
}
