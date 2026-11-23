using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Shelf;
using WordRoom.World;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// User-run setup of the shelf prototype's Escape menu and result screen, limited to ShelfCorridorPrototype (the
    /// Gameplay, main menu and coop scenes and their prefabs are not opened or changed). The two shelf prefabs are
    /// built only when missing (never rebuilt). In the scene: ShelfPauseMenu and ShelfResultScreen are added to the
    /// ShelfCorridorDemo object when it has none, their player and UI references and the demo's pauseMenu/resultScreen
    /// are filled only when empty, one instance of each canvas is placed (inactive) only when the scene has none, and an
    /// EventSystem with the Input System UI module is added when the scene has none. Running it again adds nothing twice.
    /// </summary>
    public static partial class GameMenuSetup
    {
        const string ShelfScenePath = "Assets/WordRoom/Scenes/ShelfCorridorPrototype.unity";
        const string ShelfLabel = "ShelfCorridorPrototype";

        static readonly Link[] ShelfMenuLinks =
        {
            new Link("menuRoot", "", typeof(GameObject)),
            new Link("resumeButton", "Menu/ResumeButton", typeof(Button)),
            new Link("settingsButton", "Menu/SettingsButton", typeof(Button)),
            new Link("mainMenuButton", "Menu/MainMenuButton", typeof(Button)),
            new Link("quitButton", "Menu/QuitButton", typeof(Button)),
        };

        static readonly Link[] ShelfResultLinks =
        {
            new Link("display", "", typeof(GameObject)),
            new Link("canvas", "", typeof(Canvas)),
            new Link("timeText", "Result/TimeRow/Value", typeof(TMPro.TextMeshProUGUI)),
            new Link("movesText", "Result/MovesRow/Value", typeof(TMPro.TextMeshProUGUI)),
            new Link("pathText", "Result/PathText", typeof(TMPro.TextMeshProUGUI)),
            new Link("bestTimeText", "Result/BestTimeRow/Value", typeof(TMPro.TextMeshProUGUI)),
            new Link("bestMovesText", "Result/BestMovesRow/Value", typeof(TMPro.TextMeshProUGUI)),
            new Link("retryButton", "Result/RetryButton", typeof(Button)),
        };

        [MenuItem("WordRoom/서가 시안 메뉴·결과 구성")]
        public static void SetupShelfMenuAndResult()
        {
            if (!File.Exists(ShelfScenePath))
            {
                Debug.LogError($"[서가 시안 메뉴·결과 구성] {ShelfScenePath}가 없습니다(이 메뉴는 씬을 만들지 않음).");
                return;
            }
            Run("서가 시안 메뉴·결과 구성", (font, notes, saved) =>
            {
                var temporary = false;
                var menuPrefab = LoadOrBuild(ArchiveUiBuilder.ShelfMenuPrefabPath, ArchiveUiBuilder.BuildShelfMenuCanvas, typeof(Canvas), notes, saved, ref temporary);
                var resultPrefab = LoadOrBuild(ArchiveUiBuilder.ShelfResultPrefabPath, ArchiveUiBuilder.BuildShelfResultCanvas, typeof(Canvas), notes, saved, ref temporary);

                var start = notes.Count;
                var scene = EditorSceneManager.OpenScene(ShelfScenePath, OpenSceneMode.Single);
                var demos = Object.FindObjectsByType<ShelfCorridorDemo>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (demos.Length != 1) throw new InvalidOperationException($"{ShelfLabel} 씬의 ShelfCorridorDemo가 {demos.Length}개입니다(하나여야 함)");
                var demo = demos[0];
                var player = new SerializedObject(demo).FindProperty("player")?.objectReferenceValue as FirstPersonPlayer;
                if (player == null) notes.Add($"{Warning}{ShelfLabel}: ShelfCorridorDemo.player가 비어 있어 메뉴·결과의 player를 채우지 않았습니다");

                var pause = demo.GetComponent<ShelfPauseMenu>();
                if (pause == null)
                {
                    pause = demo.gameObject.AddComponent<ShelfPauseMenu>();
                    notes.Add($"{ShelfLabel}: ShelfPauseMenu 추가");
                }
                var result = demo.GetComponent<ShelfResultScreen>();
                if (result == null)
                {
                    result = demo.gameObject.AddComponent<ShelfResultScreen>();
                    notes.Add($"{ShelfLabel}: ShelfResultScreen 추가");
                }
                if (player != null)
                {
                    FillIfEmpty(pause, "player", player, $"{ShelfLabel}: Esc 메뉴 → 플레이어 연결", notes);
                    FillIfEmpty(result, "player", player, $"{ShelfLabel}: 결과 화면 → 플레이어 연결", notes);
                }
                var menu = Connect(pause, menuPrefab, ShelfMenuLinks, scene, ShelfLabel, "Shelf Menu Canvas", notes);
                if (menu.Placed != null) menu.Placed.SetActive(false);         // closed until Escape
                var screen = Connect(result, resultPrefab, ShelfResultLinks, scene, ShelfLabel, "Shelf Result Canvas", notes);
                if (screen.Placed != null) screen.Placed.SetActive(false);     // hidden until the goal
                FillIfEmpty(demo, "pauseMenu", pause, $"{ShelfLabel}: 서가 시안 → Esc 메뉴 연결", notes);
                FillIfEmpty(demo, "resultScreen", result, $"{ShelfLabel}: 서가 시안 → 결과 화면 연결", notes);
                EnsureEventSystem(ShelfLabel, notes);
                SaveIfChanged(scene, ShelfScenePath, start, notes, saved);
            });
        }
    }
}
