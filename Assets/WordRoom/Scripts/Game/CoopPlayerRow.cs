using TMPro;
using UnityEngine;
using WordRoom.Network;

namespace WordRoom.Game
{
    /// <summary>
    /// One participant row of the waiting room (four are saved in the Coop Canvas): name (with "나" for this device),
    /// host badge and ready badge (word and color together). The name column cuts long names with an ellipsis.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopPlayerRow : MonoBehaviour
    {
        [SerializeField] TextMeshProUGUI nameText;
        [SerializeField] TextMeshProUGUI hostText;
        [SerializeField] TextMeshProUGUI readyText;

        public bool IsComplete => nameText != null && hostText != null && readyText != null;

        public void Show(Participant p)
        {
            nameText.text = p.IsLocal ? $"{p.Name} (나)" : p.Name;
            hostText.text = p.IsHost ? "호스트" : "";
            readyText.text = p.IsReady ? "준비 완료" : "준비 안 됨";
            readyText.color = p.IsReady ? (Color)ArchiveUi.Success : (Color)ArchiveUi.IvoryDim;
        }
    }
}
