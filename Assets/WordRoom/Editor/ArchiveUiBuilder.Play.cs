using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.Network;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// Parts added to the game menu and the result screen (same building blocks and palette): the game menu's 끼임방지 (1회)
    /// button (in the menu list, after 설정; no note line), and the result panel's coop part — 팀 목표 달성, 공동 소요
    /// 시간 and 팀 방문 방 수 rows, the participants label and one row per possible participant (name, 나, own visits) —
    /// saved inactive next to the single-play rows (CoopResultView switches them).
    /// </summary>
    public static partial class ArchiveUiBuilder
    {
        public const string UnstuckButtonName = "UnstuckButton";
        public const string UnstuckLabel = "끼임방지 (1회)";
        /// <summary>The note line under the button made by an earlier setup (no longer built; removed by the setup menu).</summary>
        public const string UnstuckNoteName = "UnstuckNote";

        public const string CoopTitleName = "CoopTitle";
        public const string CoopTimeRowName = "CoopTimeRow";
        public const string CoopTeamRowName = "CoopTeamRow";
        public const string CoopPlayersLabelName = "CoopPlayersLabel";
        public const string CoopPlayersName = "CoopPlayers";
        public const string CoopPlayerRowPrefix = "CoopPlayer";

        /// <summary>The 끼임방지 (1회) button in the game menu list, at sibling `at` (-1 = at the end). No note line.</summary>
        public static Button AddUnstuck(RectTransform list, int at)
        {
            var button = AddButton(list, UnstuckButtonName, UnstuckLabel, 360f, 64f, 26f);
            if (at >= 0) button.transform.SetSiblingIndex(at);
            return button;
        }

        /// <summary>
        /// The coop part of the result panel, inserted from sibling `at` (before the spacer and the 메인 메뉴 button), every
        /// part inactive, and a connected CoopResultView on `root` switching it against `solo` (the single-play title and rows).
        /// </summary>
        public static CoopResultView AddCoopResult(GameObject root, RectTransform panel, GameObject[] solo, int at)
        {
            var title = AddText(panel, CoopTitleName, "팀 목표 달성", 56f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(title.gameObject, 464f, 80f);
            ResultRow(panel, CoopTimeRowName, "공동 소요 시간", "00:00");
            ResultRow(panel, CoopTeamRowName, "팀 방문 방 수", "1개");
            var time = panel.Find($"{CoopTimeRowName}/Value").GetComponent<TextMeshProUGUI>();
            var team = panel.Find($"{CoopTeamRowName}/Value").GetComponent<TextMeshProUGUI>();
            var label = AddText(panel, CoopPlayersLabelName, "참가자별 방문 방 수", 22f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            Size(label.gameObject, 464f, 36f);
            var list = NewRect(CoopPlayersName, panel);
            Vertical(list, 6f, 0, TextAnchor.UpperCenter);
            Size(list.gameObject, 464f, -1f);
            var rows = new CoopResultRow[CoopRules.MaxPlayers];
            for (var i = 0; i < rows.Length; i++) rows[i] = CoopResultPlayerRow(list, $"{CoopPlayerRowPrefix}{i}");
            var parts = new[] { title.gameObject, panel.Find(CoopTimeRowName).gameObject, panel.Find(CoopTeamRowName).gameObject, label.gameObject, list.gameObject };
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i].transform.SetSiblingIndex(Mathf.Clamp(at + i, 0, panel.childCount - 1));
                parts[i].SetActive(false);                                     // single play first; CoopResultView switches
            }

            var view = root.AddComponent<CoopResultView>();
            var so = new SerializedObject(view);
            SetArray(so, "soloParts", solo);
            SetArray(so, "coopParts", parts);
            Set(so, "timeText", time);
            Set(so, "teamText", team);
            SetArray(so, "rows", rows);
            so.ApplyModifiedPropertiesWithoutUndo();
            return view;
        }

        /// <summary>One participant row: name (flexible, one line, ellipsis), 나 badge, own visited rooms.</summary>
        static CoopResultRow CoopResultPlayerRow(RectTransform list, string name)
        {
            var row = NewRect(name, list);
            Horizontal(row, 12f, TextAnchor.MiddleLeft);
            Size(row.gameObject, 464f, 44f);
            var n = AddText(row, "Name", "참가자", 24f, ArchiveUi.Ivory, TextAlignmentOptions.Left);
            n.textWrappingMode = TextWrappingModes.NoWrap;
            n.overflowMode = TextOverflowModes.Ellipsis;
            Size(n.gameObject, 0f, 44f, 1f);
            var me = AddText(row, "Me", "나", 20f, ArchiveUi.Success, TextAlignmentOptions.Center);
            Size(me.gameObject, 44f, 44f);
            me.enabled = false;
            var v = AddText(row, "Value", "1개", 26f, ArchiveUi.Ivory, TextAlignmentOptions.Right);
            Size(v.gameObject, 120f, 44f);
            var r = row.gameObject.AddComponent<CoopResultRow>();
            var so = new SerializedObject(r);
            Set(so, "nameText", n);
            Set(so, "meText", me);
            Set(so, "visitedText", v);
            so.ApplyModifiedPropertiesWithoutUndo();
            return r;
        }

        static void SetArray(SerializedObject so, string field, Object[] values)
        {
            var p = so.FindProperty(field) ?? throw new System.InvalidOperationException($"{so.targetObject.GetType().Name}에 '{field}' 필드가 없습니다");
            p.arraySize = values.Length;
            for (var i = 0; i < values.Length; i++) p.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }
    }
}
