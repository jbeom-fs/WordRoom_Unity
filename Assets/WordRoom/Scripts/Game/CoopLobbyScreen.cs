using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using WordRoom.Network;

namespace WordRoom.Game
{
    /// <summary>
    /// Screen control of the coop lobby in the MainMenu scene: opens the Coop Canvas (CoopLobbyView) from the main
    /// menu, shows what CoopLobby reports and sends the user's requests to it. Which panel is shown follows the real
    /// Photon state, never a click alone: the waiting room only while in a room (or leaving it), the lobby otherwise;
    /// the create panel sits over the lobby until the room is entered or it is cancelled.
    /// Lobby: connection status (with 다시 연결 after a failure), the room list as CoopLobby keeps it (rows made from the
    /// template once and reused; a row update keeps the selection and the scroll position), the selected room's
    /// details and 입장 (enabled only while it can be joined), 방 만들기, RoomCode entry (JoinRoom only — a wrong code
    /// never creates a room), 뒤로. "No connection yet", "waiting for the list" and "no rooms" are told apart.
    /// Waiting room: display name, RoomCode with 복사, count, participants with host and ready badges, the local
    /// ready toggle, 나가기 (the lobby comes back once Photon confirmed the leave). 시작 is shown but stays disabled:
    /// there is no coop start yet, and it never starts a single-player game instead.
    /// Escape does one thing per press: an input field being typed in keeps it, else the create panel closes, the
    /// waiting room leaves, the lobby goes back to the main menu. Listeners are bound once in Awake and removed in
    /// OnDestroy; CoopLobby.Changed is subscribed once as well, so repeated visits add nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopLobbyScreen : MonoBehaviour
    {
        [Tooltip("씬에 배치된 Coop Canvas의 CoopLobbyView(처음에는 비활성)")]
        [SerializeField] CoopLobbyView view;
        [Tooltip("Photon 연결·로비·방을 맡는 CoopLobby. 비우면 같은 오브젝트의 것을 씁니다.")]
        [SerializeField] CoopLobby lobby;

        readonly List<ButtonBinding> bindings = new List<ButtonBinding>();
        readonly List<CoopRoomRow> rows = new List<CoopRoomRow>();
        readonly List<Participant> participants = new List<Participant>();
        UnityAction<string> submitCode;
        Color[] sizeNormal;
        bool ready;
        bool open;
        bool createOpen;
        bool inputFocusedLastFrame;
        int chosenSize = CoopRules.DefaultMaxPlayers;
        string selected;                    // Room.Name of the selected list row; null = none
        int shownListVersion = -1;
        string shownSelected;
        string localMessage;                // the screen's own short note (copy, vanished selection); cleared by the next request
        bool localMessageIsError;
        string shownNotice;

        /// <summary>Raised when the screen closed back to the main menu.</summary>
        public event Action Closed;

        public bool IsOpen => open;

        /// <summary>Complete references (checked once in Awake; reported there).</summary>
        public bool Usable => ready;

        void Awake()
        {
            if (lobby == null) lobby = GetComponent<CoopLobby>();
            string missing = null;
            ArchiveUi.Need(view, nameof(view), ref missing);
            ArchiveUi.Need(lobby, nameof(lobby), ref missing);
            if (view != null)
            {
                var parts = view.MissingParts();
                if (parts != null) missing = missing == null ? $"view({parts})" : $"{missing}, view({parts})";
            }
            ready = missing == null;
            if (!ready)
            {
                Debug.LogError($"[협동] CoopLobbyScreen의 참조가 비어 있어 협동 로비를 열 수 없습니다: {missing}. WordRoom/협동 로비 구성 메뉴로 MainMenu 씬의 협동 UI를 배치·연결하세요.", this);
                return;
            }
            ArchiveUi.Bind(bindings, view.retryButton, Retry);
            ArchiveUi.Bind(bindings, view.joinSelectedButton, JoinSelected);
            ArchiveUi.Bind(bindings, view.createButton, OpenCreate);
            ArchiveUi.Bind(bindings, view.joinCodeButton, JoinCode);
            ArchiveUi.Bind(bindings, view.backButton, Close);
            for (var i = 0; i < view.sizeButtons.Length; i++)
            {
                var size = CoopRules.MinPlayers + i;
                ArchiveUi.Bind(bindings, view.sizeButtons[i], () => ChooseSize(size));
            }
            ArchiveUi.Bind(bindings, view.createConfirmButton, ConfirmCreate);
            ArchiveUi.Bind(bindings, view.createCancelButton, CloseCreate);
            ArchiveUi.Bind(bindings, view.copyCodeButton, CopyCode);
            ArchiveUi.Bind(bindings, view.readyButton, ToggleReady);
            ArchiveUi.Bind(bindings, view.leaveButton, Leave);
            submitCode = _ => JoinCode();                                  // Enter in the RoomCode field
            view.codeInput.onSubmit.AddListener(submitCode);
            view.nameInput.characterLimit = CoopRules.MaxDisplayNameLength;
            view.startButton.interactable = false;                         // no coop start yet (see the class summary)
            sizeNormal = new Color[view.sizeButtons.Length];
            for (var i = 0; i < sizeNormal.Length; i++) sizeNormal[i] = view.sizeButtons[i].colors.normalColor;
            view.roomRowTemplate.gameObject.SetActive(false);
            lobby.Changed += Refresh;
            if (view.gameObject.activeSelf) view.gameObject.SetActive(false);   // closed until the main menu opens it
        }

        void OnDestroy()
        {
            ArchiveUi.Unbind(bindings);
            if (!ready) return;
            if (view != null && view.codeInput != null) view.codeInput.onSubmit.RemoveListener(submitCode);
            if (lobby != null) lobby.Changed -= Refresh;
        }

        /// <summary>From the main menu. False when the screen cannot be used (reported once in Awake).</summary>
        public bool Open()
        {
            if (!ready || open) return ready;
            open = true;
            createOpen = false;
            selected = null;
            localMessage = null;
            shownNotice = null;
            shownListVersion = -1;
            view.codeInput.SetTextWithoutNotify("");
            view.gameObject.SetActive(true);
            lobby.Enter();                                                 // also refreshes through Changed
            Refresh();
            ArchiveUi.Select(view.createButton.gameObject);
            return true;
        }

        /// <summary>Back to the main menu; CoopLobby winds the connection down by itself (owned connection only).</summary>
        public void Close()
        {
            if (!open) return;
            open = false;
            createOpen = false;
            lobby.Exit();
            view.gameObject.SetActive(false);
            var handlers = Closed;
            if (handlers != null) handlers();
        }

        void Update()
        {
            if (!open) return;
            var kb = Keyboard.current;
            if (kb == null || !kb.escapeKey.wasPressedThisFrame) return;
            if (inputFocusedLastFrame || view.AnyInputFocused) return;     // Escape belongs to the field being typed in
            if (createOpen) CloseCreate();                                 // refused while the create request runs (consumed, never passed back)
            else if (InRoomView) Leave();
            else Close();
        }

        void LateUpdate()
        {
            inputFocusedLastFrame = open && view.AnyInputFocused;
        }

        bool InRoomView
        {
            get
            {
                var p = lobby.Phase;
                return p == CoopPhase.InRoom || p == CoopPhase.LeavingRoom;
            }
        }

        // ---- actions --------------------------------------------------------------------------------------------
        void Retry()
        {
            ClearLocalMessage();
            lobby.Retry();
        }

        void Select(CoopRoomRow row)
        {
            if (row.RoomName == null) return;
            selected = row.RoomName;
            ClearLocalMessage();
            Refresh();
        }

        void JoinSelected()
        {
            if (selected == null) return;
            ClearLocalMessage();
            lobby.JoinListed(selected);
        }

        void JoinCode()
        {
            if (!open || InRoomView) return;
            ClearLocalMessage();
            lobby.JoinByCode(view.codeInput.text);
        }

        void OpenCreate()
        {
            createOpen = true;
            ClearLocalMessage();
            view.nameInput.SetTextWithoutNotify("");
            ChooseSize(CoopRules.DefaultMaxPlayers);
            Refresh();
            ArchiveUi.Select(view.nameInput.gameObject);
        }

        /// <summary>The create request runs: cancelling now would only hide it while the room is still entered, so it is refused until the result.</summary>
        bool CreateRunning => lobby.Phase == CoopPhase.CreatingRoom;

        void CloseCreate()
        {
            if (CreateRunning) return;                                     // also for Escape; the buttons follow in Refresh
            createOpen = false;
            Refresh();
            ArchiveUi.Select(view.createButton.gameObject);
        }

        void ChooseSize(int size)
        {
            chosenSize = CoopRules.ClampMaxPlayers(size);
            for (var i = 0; i < view.sizeButtons.Length; i++)
                ArchiveUi.SetNormalColor(view.sizeButtons[i], CoopRules.MinPlayers + i == chosenSize ? ArchiveUi.Chosen : sizeNormal[i]);
        }

        void ConfirmCreate()
        {
            ClearLocalMessage();
            lobby.CreateRoom(view.nameInput.text, chosenSize);              // the panel stays (busy) until the room is entered
        }

        void CopyCode()
        {
            if (!lobby.TryGetRoom(out var room)) return;
            GUIUtility.systemCopyBuffer = room.Code;
            SetLocalMessage("RoomCode를 복사했습니다.", false);
        }

        void ToggleReady()
        {
            ClearLocalMessage();
            lobby.SetReady(!lobby.LocalReady);
        }

        void Leave()
        {
            ClearLocalMessage();
            lobby.LeaveRoom();
        }

        void SetLocalMessage(string text, bool error)
        {
            localMessage = text;
            localMessageIsError = error;
            Refresh();
        }

        void ClearLocalMessage() => localMessage = null;

        // ---- view ---------------------------------------------------------------------------------------------
        /// <summary>Redraws from CoopLobby (on its Changed and after the screen's own actions). Cheap when nothing changed.</summary>
        void Refresh()
        {
            if (!open) return;
            var phase = lobby.Phase;
            if (lobby.Notice != shownNotice)                               // a new result from Photon replaces the screen's own note
            {
                shownNotice = lobby.Notice;
                if (shownNotice != null) localMessage = null;
            }
            var room = phase == CoopPhase.InRoom || phase == CoopPhase.LeavingRoom;
            if (room) createOpen = false;                                  // entered: the create panel has done its job
            SetActive(view.waitingPanel, room);
            SetActive(view.lobbyPanel, !room);
            SetActive(view.createPanel, !room && createOpen);
            if (room) RefreshWaiting(phase);
            else RefreshLobby(phase);
        }

        void RefreshLobby(CoopPhase phase)
        {
            var idleInLobby = phase == CoopPhase.InLobby;
            view.statusText.text = StatusText(phase, out var statusColor);
            view.statusText.color = statusColor;
            SetActive(view.retryButton.gameObject, phase == CoopPhase.Disconnected || (phase == CoopPhase.Idle && lobby.NoticeIsError));

            var list = lobby.Rooms;
            if (selected != null && !list.TryGet(selected, out _))          // the selected room is gone from the list
            {
                selected = null;
                if (list.Received) SetLocalMessageSilently("선택한 방이 목록에서 사라졌습니다.", true);
            }
            if (list.Version != shownListVersion || selected != shownSelected) BindRows(list);

            var inLobbyPhases = idleInLobby || phase == CoopPhase.CreatingRoom || phase == CoopPhase.JoiningRoom;
            string listText = null;
            if (!inLobbyPhases) listText = phase == CoopPhase.Disconnected ? "연결이 끊겨 방 목록을 볼 수 없습니다." : "로비 연결을 기다리는 중입니다.";
            else if (!list.Received) listText = "방 목록을 받는 중입니다…";
            else if (list.Count == 0) listText = "열린 방이 없습니다. 방을 만들거나 RoomCode로 참가하세요.";
            view.listMessage.text = listText ?? "";
            SetActive(view.listMessage.gameObject, listText != null);

            LobbyRoom r = default;
            var has = selected != null && list.TryGet(selected, out r);
            view.selectedName.text = has ? r.DisplayName : "선택한 방 없음";
            view.selectedCode.text = has ? $"RoomCode  {r.Name}" : "";
            view.selectedCount.text = has ? $"{r.PlayerCount} / {r.MaxPlayers}명" : "";
            if (has) CoopText.Availability(r.Availability, view.selectedState);
            else view.selectedState.text = "";
            view.joinSelectedButton.interactable = idleInLobby && has && r.Availability == RoomAvailability.Open;
            view.createButton.interactable = idleInLobby;
            view.joinCodeButton.interactable = idleInLobby;
            view.codeInput.interactable = phase != CoopPhase.JoiningRoom && phase != CoopPhase.CreatingRoom;

            WriteMessage(view.lobbyMessage);
            if (!createOpen) return;
            var creating = phase == CoopPhase.CreatingRoom;
            view.createConfirmButton.interactable = idleInLobby;
            view.createCancelButton.interactable = !creating;             // usable again after an immediate or later failure
            view.nameInput.interactable = !creating;
            foreach (var b in view.sizeButtons) b.interactable = !creating;
            if (creating) view.createMessage.text = "방을 만드는 중입니다…";
            else WriteMessage(view.createMessage);
            if (creating) view.createMessage.color = ArchiveUi.IvoryDim;
        }

        /// <summary>Rows from the template as needed (never destroyed), bound in list order; the scroll position is left alone.</summary>
        void BindRows(RoomListCache list)
        {
            while (rows.Count < list.Count)
            {
                var row = Instantiate(view.roomRowTemplate, view.roomContent, false);
                row.name = $"Room Row {rows.Count}";
                var bound = row;
                ArchiveUi.Bind(bindings, row.Button, () => Select(bound));
                rows.Add(row);
            }
            for (var i = 0; i < rows.Count; i++)
            {
                var visible = i < list.Count;
                if (visible) rows[i].Show(list.Rooms[i], list.Rooms[i].Name == selected);
                else rows[i].Unbind();
                SetActive(rows[i].gameObject, visible);
            }
            shownListVersion = list.Version;
            shownSelected = selected;
        }

        void RefreshWaiting(CoopPhase phase)
        {
            var leaving = phase == CoopPhase.LeavingRoom;
            if (lobby.TryGetRoom(out var room))
            {
                view.roomTitle.text = room.DisplayName;
                view.roomCode.text = room.Code;
                view.roomCount.text = $"{room.PlayerCount} / {room.MaxPlayers}명";
            }
            view.copyCodeButton.interactable = !leaving;
            lobby.GetParticipants(participants);
            var readyCount = 0;
            for (var i = 0; i < view.playerRows.Length; i++)
            {
                var visible = i < participants.Count;
                SetActive(view.playerRows[i].gameObject, visible);
                if (!visible) continue;
                view.playerRows[i].Show(participants[i]);
                if (participants[i].IsReady) readyCount++;
            }
            if (participants.Count > view.playerRows.Length) Debug.LogWarning($"[협동] 참가자 {participants.Count}명 중 {view.playerRows.Length}명만 표시합니다(최대 인원 {CoopRules.MaxPlayers}).");

            var canAct = phase == CoopPhase.InRoom && !lobby.Busy;
            view.readyLabel.text = lobby.LocalReady ? "준비 취소" : "준비";
            view.readyButton.interactable = canAct && !lobby.ReadyPending;
            view.leaveButton.interactable = canAct;
            view.startButton.interactable = false;                         // start condition shown, start itself not connected yet
            var host = lobby.IsLocalHost;
            if (CoopRules.CanStart(host, participants.Count, readyCount))
                view.startNote.text = "시작 조건 충족 — 협동 게임 시작은 아직 연결되지 않았습니다";
            else if (host)
                view.startNote.text = $"{CoopRules.MinPlayers}명 이상 모두 준비하면 시작 조건이 충족됩니다 ({readyCount}/{participants.Count} 준비)";
            else
                view.startNote.text = $"호스트가 시작합니다 ({readyCount}/{participants.Count} 준비)";

            if (leaving)
            {
                view.waitingMessage.text = "방을 나가는 중입니다…";
                view.waitingMessage.color = ArchiveUi.IvoryDim;
            }
            else WriteMessage(view.waitingMessage);
        }

        void SetLocalMessageSilently(string text, bool error)
        {
            localMessage = text;
            localMessageIsError = error;
        }

        void WriteMessage(TextMeshProUGUI target)
        {
            var text = localMessage ?? lobby.Notice;
            var error = localMessage != null ? localMessageIsError : lobby.NoticeIsError;
            target.text = text ?? "";
            target.color = error ? (Color)ArchiveUi.Error : (Color)ArchiveUi.IvoryDim;
        }

        static string StatusText(CoopPhase phase, out Color color)
        {
            color = ArchiveUi.IvoryDim;
            switch (phase)
            {
                case CoopPhase.Connecting: return "연결중 ...";
                case CoopPhase.JoiningLobby: return "로비에 들어가는 중…";
                case CoopPhase.InLobby:
                    color = ArchiveUi.Success;
                    return "로비 연결됨";
                case CoopPhase.CreatingRoom: return "방을 만드는 중…";
                case CoopPhase.JoiningRoom: return "방에 들어가는 중…";
                case CoopPhase.LeavingRoom: return "방을 나가는 중…";
                case CoopPhase.LeavingLobby:
                case CoopPhase.Disconnecting: return "이전 연결을 정리하는 중…";
                case CoopPhase.Disconnected:
                    color = ArchiveUi.Error;
                    return "연결 끊김";
                default: return "연결되지 않음";
            }
        }

        static void SetActive(GameObject go, bool on)
        {
            if (go.activeSelf != on) go.SetActive(on);
        }
    }
}
