using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WordRoom.Network;

namespace WordRoom.Game
{
    /// <summary>
    /// One row of the lobby's room list (made from the template row of the Coop Canvas and reused): display name,
    /// participants / maximum, whether it can be joined, and the selection (background and frame). Holds the Room.Name
    /// it shows; the click is bound once by CoopLobbyScreen. Long names are cut with an ellipsis inside their own
    /// column, so the count and the state keep their place.
    /// Also reports left pointer clicks (Clicked; never Submit, a right click or a drag, which sends no click) for the
    /// screen's double click. Binding changes whenever the row stops showing the same Room.Name (another room, Unbind,
    /// disabled), so a click pressed on one room and released on another, or an earlier click, is never taken for it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopRoomRow : MonoBehaviour, IPointerDownHandler, IPointerClickHandler
    {
        [SerializeField] Button button;
        [SerializeField] TextMeshProUGUI nameText;
        [SerializeField] TextMeshProUGUI countText;
        [SerializeField] TextMeshProUGUI stateText;
        [Tooltip("행 테두리(선택 시 밝게)")]
        [SerializeField] Image[] frame = new Image[0];

        LobbyRoom shown;
        bool shownSelected;
        bool bound;
        int pressedBinding = -1;            // Binding when the left button went down here; -1 = no press
        int pressedPointer;

        /// <summary>A left click released on this row, pressed on the same pointer while it showed the same room.</summary>
        public event Action<CoopRoomRow, PointerEventData> Clicked;

        public Button Button => button;
        public string RoomName => bound ? shown.Name : null;

        /// <summary>Grows whenever the row stops showing the same Room.Name (another room, Unbind, disabled).</summary>
        public int Binding { get; private set; }

        public bool IsComplete => button != null && nameText != null && countText != null && stateText != null;

        public void Show(LobbyRoom room, bool selected)
        {
            if (bound && shown.Equals(room) && shownSelected == selected) return;   // nothing changed: no text rebuild
            if (!bound || shown.Name != room.Name) Rebind();
            shown = room;
            shownSelected = selected;
            bound = true;
            nameText.text = room.DisplayName;
            countText.text = $"{room.PlayerCount} / {room.MaxPlayers}명";
            CoopText.Availability(room.Availability, stateText);
            ArchiveUi.SetNormalColor(button, selected ? ArchiveUi.Chosen : ArchiveUi.Walnut);
            foreach (var edge in frame)
                if (edge != null) edge.color = selected ? (Color)ArchiveUi.Ivory : (Color)ArchiveUi.Brass;
        }

        public void Unbind()
        {
            bound = false;
            Rebind();
        }

        void OnDisable() => Rebind();

        void Rebind()
        {
            Binding++;
            pressedBinding = -1;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            pressedBinding = Binding;
            pressedPointer = eventData.pointerId;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || eventData.dragging) return;
            var same = pressedBinding == Binding && pressedPointer == eventData.pointerId;
            pressedBinding = -1;
            if (!same || !bound) return;                                   // pressed on another room (or before a rebind)
            var handlers = Clicked;
            if (handlers != null) handlers(this, eventData);
        }
    }

    static class CoopText
    {
        /// <summary>Color and words together (never color alone).</summary>
        public static void Availability(RoomAvailability a, TextMeshProUGUI t)
        {
            switch (a)
            {
                case RoomAvailability.Open:
                    t.text = "입장 가능";
                    t.color = ArchiveUi.Success;
                    break;
                case RoomAvailability.Full:
                    t.text = "정원 초과";
                    t.color = ArchiveUi.Error;
                    break;
                case RoomAvailability.Closed:
                    t.text = "입장 닫힘";
                    t.color = ArchiveUi.Error;
                    break;
                default:
                    t.text = "사라짐";
                    t.color = ArchiveUi.IvoryDim;
                    break;
            }
        }
    }
}
