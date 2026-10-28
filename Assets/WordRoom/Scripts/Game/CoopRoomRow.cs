using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Network;

namespace WordRoom.Game
{
    /// <summary>
    /// One row of the lobby's room list (made from the template row of the Coop Canvas and reused): display name,
    /// participants / maximum, whether it can be joined, and the selection (background and frame). Holds the Room.Name
    /// it shows; the click is bound once by CoopLobbyScreen. Long names are cut with an ellipsis inside their own
    /// column, so the count and the state keep their place.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopRoomRow : MonoBehaviour
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

        public Button Button => button;
        public string RoomName => bound ? shown.Name : null;

        public bool IsComplete => button != null && nameText != null && countText != null && stateText != null;

        public void Show(LobbyRoom room, bool selected)
        {
            if (bound && shown.Equals(room) && shownSelected == selected) return;   // nothing changed: no text rebuild
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

        public void Unbind() => bound = false;
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
