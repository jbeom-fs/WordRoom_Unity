using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Game;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// The shelf prototype's own menu and result canvases, built from the same blocks and palette as the game menu and
    /// the result screen (dim overlay catching clicks, walnut panel with the 4-unit brass frame, ivory text, framed
    /// buttons). They are separate prefabs so the Gameplay and coop canvases stay untouched.
    /// </summary>
    public static partial class ArchiveUiBuilder
    {
        public const string ShelfMenuPrefabPath = PrefabFolder + "/ShelfMenuCanvas.prefab";
        public const string ShelfResultPrefabPath = PrefabFolder + "/ShelfResultCanvas.prefab";

        /// <summary>
        /// Escape menu (order 200): Overlay, the Menu panel with 메뉴 and the buttons 재시작 (ResumeButton) · 설정
        /// (SettingsButton, saved disabled) · 메인으로 (MainMenuButton, saved disabled) · 게임종료 (QuitButton).
        /// Saved active; the scene instance is placed inactive (ShelfPauseMenu opens it).
        /// </summary>
        public static GameObject BuildShelfMenuCanvas()
        {
            var root = CanvasRoot("Shelf Menu Canvas", GameMenuOrder, true);
            var overlay = NewRect("Overlay", root.transform);
            Stretch(overlay);
            AddImage(overlay.gameObject, ArchiveUi.Dim, true);                // clicks never reach the game

            var list = Panel(root.transform, "Menu");
            list.anchorMin = list.anchorMax = list.pivot = new Vector2(0.5f, 0.5f);
            list.sizeDelta = new Vector2(480f, 0f);
            list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Vertical(list, 18f, 44, TextAnchor.UpperCenter);
            var title = AddText(list, "Title", "메뉴", 40f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(title.gameObject, 392f, 60f);
            AddButton(list, "ResumeButton", "재시작", 360f, 64f, 26f);
            AddButton(list, "SettingsButton", "설정", 360f, 64f, 26f).interactable = false;
            AddButton(list, "MainMenuButton", "메인으로", 360f, 64f, 26f).interactable = false;
            AddButton(list, "QuitButton", "게임종료", 360f, 64f, 26f);
            return root;
        }

        /// <summary>
        /// Goal result (order 300): Overlay and the Result panel — 목표 도달, rows 소요 시간 (TimeRow), 이동 횟수 (MovesRow),
        /// the 지나온 단어 label and the wrapped path (PathText), rows 최단 시간 (BestTimeRow) and 최소 이동 (BestMovesRow),
        /// and 다시 도전 (RetryButton). Values ("Value" children) are written by ShelfResultScreen. Saved active; the
        /// scene instance is placed inactive.
        /// </summary>
        public static GameObject BuildShelfResultCanvas()
        {
            const float width = 544f;
            var root = CanvasRoot("Shelf Result Canvas", ResultOrder, true);
            var overlay = NewRect("Overlay", root.transform);
            Stretch(overlay);
            AddImage(overlay.gameObject, ArchiveUi.Dim, true);

            var panel = Panel(root.transform, "Result");
            Center(panel, width + 96f);
            Vertical(panel, 16f, 48, TextAnchor.UpperCenter);
            var title = AddText(panel, "Title", "목표 도달", 56f, ArchiveUi.Ivory, TextAlignmentOptions.Center);
            Size(title.gameObject, width, 80f);
            ShelfResultRow(panel, "TimeRow", "소요 시간", "00:00", width);
            ShelfResultRow(panel, "MovesRow", "이동 횟수", "0회", width);
            var pathLabel = AddText(panel, "PathLabel", "지나온 단어", 26f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            Size(pathLabel.gameObject, width, 40f);
            var path = AddText(panel, "PathText", "시작 → 목표", 26f, ArchiveUi.Ivory, TextAlignmentOptions.TopLeft);
            Size(path.gameObject, width, -1f);                                 // height follows the wrapped words
            var gap = NewRect("Gap", panel);
            Size(gap.gameObject, width, 4f);
            ShelfResultRow(panel, "BestTimeRow", "최단 시간", "--:--", width);
            ShelfResultRow(panel, "BestMovesRow", "최소 이동", "--", width);
            var spacer = NewRect("Spacer", panel);
            Size(spacer.gameObject, width, 8f);
            AddButton(panel, "RetryButton", "다시 도전", 360f, 68f, 28f);
            return root;
        }

        /// <summary>Label (left) and value (right, child "Value") in one row of `width`.</summary>
        static void ShelfResultRow(RectTransform panel, string name, string label, string preview, float width)
        {
            var row = NewRect(name, panel);
            Horizontal(row, 12f, TextAnchor.MiddleLeft);
            Size(row.gameObject, width, RowHeight);
            var l = AddText(row, "Label", label, 26f, ArchiveUi.IvoryDim, TextAlignmentOptions.Left);
            Size(l.gameObject, 200f, RowHeight);
            var v = AddText(row, "Value", preview, 30f, ArchiveUi.Ivory, TextAlignmentOptions.Right);
            Size(v.gameObject, width - 212f, RowHeight);
        }
    }
}
