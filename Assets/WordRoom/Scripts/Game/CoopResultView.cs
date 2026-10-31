using System;
using TMPro;
using UnityEngine;
using WordRoom.Generation;

namespace WordRoom.Game
{
    /// <summary>
    /// The coop part of the Result Canvas (made in the Editor next to the single-play rows; optional — without it the
    /// single-play result is unchanged and a coop game shows no result screen): 팀 목표 달성, the common time, the team's
    /// distinct visited rooms and one row per participant of the confirmed result (session order, at most four, this
    /// device's row marked 나). Everything is written once from the one confirmed SessionResult and the names taken
    /// when it is shown, and stays while the screen is shown; nothing is counted or timed here. Coop and single-play
    /// parts are switched against each other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopResultView : MonoBehaviour
    {
        [Tooltip("단독 결과에서만 보이는 부분(제목·소요 시간·방문 방 수 행)")]
        [SerializeField] GameObject[] soloParts = new GameObject[0];
        [Tooltip("협동 결과에서만 보이는 부분(팀 제목·공동 시간·팀 방문·참가자 안내·참가자 행)")]
        [SerializeField] GameObject[] coopParts = new GameObject[0];
        [SerializeField] TextMeshProUGUI timeText;
        [SerializeField] TextMeshProUGUI teamText;
        [Tooltip("참가자 행(최대 인원만큼, 위에서부터 사용)")]
        [SerializeField] CoopResultRow[] rows = new CoopResultRow[0];

        /// <summary>Why the coop result cannot be shown (null = usable).</summary>
        public string MissingParts()
        {
            string missing = null;
            ArchiveUi.Need(timeText, nameof(timeText), ref missing);
            ArchiveUi.Need(teamText, nameof(teamText), ref missing);
            if (coopParts == null || coopParts.Length == 0) Add(ref missing, "coopParts(비어 있음)");
            else
                for (var i = 0; i < coopParts.Length; i++) ArchiveUi.Need(coopParts[i], $"coopParts[{i}]", ref missing);
            if (soloParts != null)
                for (var i = 0; i < soloParts.Length; i++) ArchiveUi.Need(soloParts[i], $"soloParts[{i}]", ref missing);
            if (rows == null || rows.Length == 0) Add(ref missing, "rows(비어 있음)");
            else
                for (var i = 0; i < rows.Length; i++)
                {
                    ArchiveUi.Need(rows[i], $"rows[{i}]", ref missing);
                    if (rows[i] != null && !rows[i].IsComplete) Add(ref missing, $"rows[{i}](이름·나·방문 텍스트)");
                }
            return missing;
        }

        static void Add(ref string missing, string what) => missing = missing == null ? what : $"{missing}, {what}";

        /// <summary>The single-play layout (coop parts hidden).</summary>
        public void ShowSolo() => Switch(false);

        /// <summary>
        /// The confirmed coop result: common time, team visits and every participant of `result` in its order with its
        /// own visits (never the team's in its place); `localPlayer` gets 나; `nameOf` gives each name once (the same
        /// name twice stays two rows, told apart by PlayerId). False with why when there are more participants than rows.
        /// </summary>
        public bool ShowCoop(SessionResult result, string localPlayer, Func<string, string> nameOf, out string failure)
        {
            failure = null;
            var players = result.Players;
            if (players.Count > rows.Length)
            {
                failure = $"참가자 {players.Count}명을 표시할 행이 {rows.Length}개뿐입니다";
                return false;
            }
            timeText.text = ResultScreen.FormatTime(result.Seconds);
            teamText.text = $"{result.TeamVisitedRooms}개";
            for (var i = 0; i < rows.Length; i++)
            {
                var visible = i < players.Count;
                if (rows[i].gameObject.activeSelf != visible) rows[i].gameObject.SetActive(visible);
                if (visible) rows[i].Show(nameOf(players[i].PlayerId), players[i].PlayerId == localPlayer, players[i].VisitedRooms);
            }
            Switch(true);
            return true;
        }

        void Switch(bool coop)
        {
            if (soloParts != null)
                foreach (var p in soloParts)
                    if (p != null && p.activeSelf == coop) p.SetActive(!coop);
            if (coopParts != null)
                foreach (var p in coopParts)
                    if (p != null && p.activeSelf != coop) p.SetActive(coop);
        }
    }
}
