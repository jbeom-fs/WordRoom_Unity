using TMPro;
using UnityEngine;

namespace WordRoom.Game
{
    /// <summary>
    /// One participant row of the coop result (made in the Editor, four in the Result Canvas, reused): the participant's
    /// name (cut with an ellipsis in its own column), a "나" badge for this device's player and its own distinct visited rooms.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopResultRow : MonoBehaviour
    {
        [SerializeField] TextMeshProUGUI nameText;
        [SerializeField] TextMeshProUGUI meText;
        [SerializeField] TextMeshProUGUI visitedText;

        public bool IsComplete => nameText != null && meText != null && visitedText != null;

        public void Show(string displayName, bool me, int visitedRooms)
        {
            nameText.text = displayName;
            meText.enabled = me;
            visitedText.text = $"{visitedRooms}개";
        }
    }
}
