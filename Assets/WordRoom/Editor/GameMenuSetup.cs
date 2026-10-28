using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.RoomDebug;
using WordRoom.World;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// User-run setup of the menu flow and of the fixed screen UI, which is saved in prefabs and scenes (nothing of
    /// it is made at runtime any more).
    /// UI prefabs (Assets/Prefabs/UI): SettingsPanel, MainMenuCanvas and GameMenuCanvas (each with a nested, hidden
    /// SettingsPanel instance), HudCanvas, PlayHintCanvas, plus the PlayHint outline material. Each is built only
    /// when its asset is missing; an existing prefab is used as it is (user edits and overrides stay).
    /// Scenes: Gameplay.unity is made once as a copy of the development scene when missing and then only completed:
    /// automatic Inspector start off, stop advice pointing to the menu, GameplayBootstrap · PauseMenu · UiSound
    /// added when missing, GameMenu/HUD/hint canvases placed and connected, one EventSystem with the Input System UI
    /// module, an AudioListener on the player camera when the scene has none. RoomGenerationDebug.unity (development)
    /// only gets the HUD and hint canvases its WordRoomHud/PlayHint use. MainMenu.unity is made once when missing
    /// (menu camera with the only AudioListener, static archive room, MainMenu object) and gets the MainMenu canvas
    /// and an EventSystem. Build scenes: MainMenu first, Gameplay second, other entries kept in order.
    /// Running it again adds nothing twice: references already set are kept, an existing single prefab instance is
    /// reused, and only empty references are filled. Duplicates or ambiguous setups (several instances of one UI,
    /// references into different instances or mixed with user-made UI, several EventSystems, other or duplicate
    /// input modules, inactive input components, incomplete menu actions) are reported with scene, component and
    /// objects, and stop only the automatic part they concern; nothing is deleted, replaced or enabled. UI fully
    /// connected to user-made objects is accepted as it is. A new EventSystem gets the Input System package's saved
    /// UI action references. Values the former runtime UI kept on the scene components (fonts, HUD order, hint
    /// size and position) are moved once onto that scene's instance (LegacyUiMigration). Models, materials, lights,
    /// cameras and the player are not touched. Only scenes with a change are saved (warnings alone save nothing);
    /// the log lists every saved asset, also when it stops early. The scene open before is reopened.
    /// </summary>
    public static partial class GameMenuSetup
    {
        const string DevScenePath = "Assets/WordRoom/Scenes/RoomGenerationDebug.unity";
        const string MainMenuPath = "Assets/WordRoom/Scenes/MainMenu.unity";
        const string GameplayPath = "Assets/WordRoom/Scenes/Gameplay.unity";
        const string FontPath = "Assets/Font/Pretendard-Black SDF.asset";
        const string RoomPrefabPath = ArchiveModelSetup.RoomPrefabPath;
        const string HaltAdvice = "Esc를 눌러 메인 메뉴로 돌아가세요";
        internal const string Warning = "주의 — ";

        /// <summary>A serialized field of a scene component and where its value lives inside the placed prefab instance ("" = its root).</summary>
        readonly struct Link
        {
            public readonly string Field;
            public readonly string Path;
            public readonly Type Type;

            public Link(string field, string path, Type type)
            {
                Field = field;
                Path = path;
                Type = type;
            }
        }

        static readonly Link[] PauseLinks =
        {
            new Link("menuRoot", "", typeof(GameObject)),
            new Link("list", "Menu", typeof(GameObject)),
            new Link("settings", "SettingsPanel", typeof(SettingsPanel)),
            new Link("continueButton", "Menu/ContinueButton", typeof(Button)),
            new Link("settingsButton", "Menu/SettingsButton", typeof(Button)),
            new Link("mainMenuButton", "Menu/MainMenuButton", typeof(Button)),
            new Link("quitButton", "Menu/QuitButton", typeof(Button)),
        };

        static readonly Link[] MainMenuLinks =
        {
            new Link("mainPanel", "Main", typeof(GameObject)),
            new Link("difficultyPanel", "Difficulty", typeof(GameObject)),
            new Link("settings", "SettingsPanel", typeof(SettingsPanel)),
            new Link("startButton", "Main/StartButton", typeof(Button)),
            new Link("settingsButton", "Main/SettingsButton", typeof(Button)),
            new Link("quitButton", "Main/QuitButton", typeof(Button)),
            new Link("difficultyButtons.Array.data[0]", "Difficulty/EasyButton", typeof(Button)),
            new Link("difficultyButtons.Array.data[1]", "Difficulty/NormalButton", typeof(Button)),
            new Link("difficultyButtons.Array.data[2]", "Difficulty/HardButton", typeof(Button)),
            new Link("backButton", "Difficulty/Footer/BackButton", typeof(Button)),
            new Link("startGameButton", "Difficulty/Footer/StartGameButton", typeof(Button)),
            new Link("message", "Difficulty/Message", typeof(TextMeshProUGUI)),
        };

        static readonly Link[] HudLinks =
        {
            new Link("canvas", "", typeof(Canvas)),
            new Link("currentText", "Panel/CurrentWord", typeof(TextMeshProUGUI)),
            new Link("targetText", "Panel/TargetWord", typeof(TextMeshProUGUI)),
        };

        static readonly Link[] HintLinks =
        {
            new Link("canvas", "", typeof(Canvas)),
            new Link("label", "Text", typeof(TextMeshProUGUI)),
        };

        static readonly Link[] ResultLinks =
        {
            new Link("display", "", typeof(GameObject)),
            new Link("canvas", "", typeof(Canvas)),
            new Link("timeText", "Result/TimeRow/Value", typeof(TextMeshProUGUI)),
            new Link("visitedText", "Result/VisitedRow/Value", typeof(TextMeshProUGUI)),
            new Link("mainMenuButton", "Result/MainMenuButton", typeof(Button)),
        };

        static readonly Link[] MapLinks =
        {
            new Link("display", "", typeof(GameObject)),
            new Link("canvas", "", typeof(Canvas)),
            new Link("view", "", typeof(MapView)),
        };

        sealed class UiPrefabs
        {
            public GameObject Settings, MainMenu, GameMenu, Hud, Hint, Result, Map;
        }

        [MenuItem("WordRoom/메인 메뉴와 게임 설정 구성")]
        public static void Setup()
        {
            var room = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPrefabPath);
            if (room == null || !File.Exists(DevScenePath))
            {
                Debug.LogError($"[메뉴 구성] 필요한 자산이 없습니다: 방 프리팹 {(room != null ? "있음" : RoomPrefabPath)}, 개발용 씬 {(File.Exists(DevScenePath) ? "있음" : DevScenePath)}");
                return;
            }
            Run("메뉴 구성", (font, notes, saved) =>
            {
                var ui = EnsurePrefabs(font, notes, saved);
                SetupGameplay(ui, notes, saved);
                SetupDevScene(ui, notes, saved);
                SetupMainMenu(room, ui, notes, saved);
                SetupCoop(notes, saved);                                 // 협동 button and lobby of the MainMenu scene (see GameMenuSetup.Coop)
                SetupBuildScenes(notes, saved);
            });
        }

        /// <summary>
        /// Only the goal result of the existing Gameplay scene: the ResultCanvas prefab (built only when missing), one
        /// placed instance and the ResultScreen references. Nothing else of any scene or prefab is checked or changed
        /// (no other UI, no value move, no materials); Gameplay is never made or copied here.
        /// </summary>
        [MenuItem("WordRoom/결과 화면 구성")]
        public static void SetupResultScreen()
        {
            if (!File.Exists(GameplayPath))
            {
                Debug.LogError($"[결과 구성] {GameplayPath}가 없습니다. 먼저 WordRoom/메인 메뉴와 게임 설정 구성을 실행하세요(이 메뉴는 씬을 만들지 않음).");
                return;
            }
            Run("결과 구성", (font, notes, saved) =>
            {
                var temporary = false;
                var prefab = LoadOrBuild(ArchiveUiBuilder.ResultPrefabPath, ArchiveUiBuilder.BuildResultCanvas, typeof(Canvas), notes, saved, ref temporary);
                var start = notes.Count;
                var scene = EditorSceneManager.OpenScene(GameplayPath, OpenSceneMode.Single);
                var games = Object.FindObjectsByType<RoomGenerationDebug>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (games.Length != 1) throw new InvalidOperationException($"Gameplay 씬의 RoomGenerationDebug가 {games.Length}개입니다(하나여야 함)");
                SetupResult(games[0], games[0].GetComponent<PauseMenu>(), prefab, scene, notes);
                SaveIfChanged(scene, GameplayPath, start, notes, saved);
            });
        }

        /// <summary>
        /// Only the map of the existing Gameplay scene: the MapCanvas prefab (built only when missing, with its word pool),
        /// one placed instance (inactive when new), the MapScreen references, and the game menu's and result screen's
        /// links to it when those are empty. Nothing else is checked or changed (no other UI, no value move, no
        /// materials); Gameplay is never made or copied here.
        /// </summary>
        [MenuItem("WordRoom/지도 화면 구성")]
        public static void SetupMapScreen()
        {
            if (!File.Exists(GameplayPath))
            {
                Debug.LogError($"[지도 구성] {GameplayPath}가 없습니다. 먼저 WordRoom/메인 메뉴와 게임 설정 구성을 실행하세요(이 메뉴는 씬을 만들지 않음).");
                return;
            }
            Run("지도 구성", (font, notes, saved) =>
            {
                var temporary = false;
                var prefab = LoadOrBuild(ArchiveUiBuilder.MapPrefabPath, ArchiveUiBuilder.BuildMapCanvas, typeof(MapView), notes, saved, ref temporary);
                var start = notes.Count;
                var scene = EditorSceneManager.OpenScene(GameplayPath, OpenSceneMode.Single);
                var games = Object.FindObjectsByType<RoomGenerationDebug>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (games.Length != 1) throw new InvalidOperationException($"Gameplay 씬의 RoomGenerationDebug가 {games.Length}개입니다(하나여야 함)");
                SetupMap(games[0], games[0].GetComponent<PauseMenu>(), prefab, scene, notes);
                SaveIfChanged(scene, GameplayPath, start, notes, saved);
            });
        }

        /// <summary>Shared frame of the menus: checks, scene save prompt, log of changes and saved assets, reopening the previous scene.</summary>
        static void Run(string tag, Action<TMP_FontAsset, List<string>, List<string>> body)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError($"[{tag}] Play 중에는 실행하지 않습니다.");
                return;
            }
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (font == null)
            {
                Debug.LogError($"[{tag}] 필요한 자산이 없습니다: 폰트 {FontPath}");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            ArchiveUiBuilder.Font = font;
            var returnTo = SceneManager.GetActiveScene().path;
            var notes = new List<string>();
            var saved = new List<string>();
            try
            {
                body(font, notes, saved);
                var warnings = notes.Count(n => n.StartsWith(Warning));
                var log = $"[{tag}] 완료: " + (notes.Count > 0 ? string.Join(" / ", notes) : "바꿀 항목이 없습니다") +
                          $"\n저장: {(saved.Count > 0 ? string.Join(", ", saved) : "없음")}";
                if (warnings > 0) Debug.LogWarning(log + $"\n주의 {warnings}건은 직접 확인하세요(도구는 삭제·교체하지 않았습니다).");
                else Debug.Log(log);
            }
            catch (Exception e)
            {
                Debug.LogError($"[{tag}] 중단 — {e.Message}\n저장된 항목: {(saved.Count > 0 ? string.Join(", ", saved) : "없음")}. " +
                               "그 밖의 변경은 저장하지 않았습니다(중단 시점에 열려 있던 씬의 미저장 변경은 이전 씬을 다시 열면서 버려집니다)." +
                               (notes.Count > 0 ? $"\n진행 기록: {string.Join(" / ", notes)}" : ""));
            }
            finally
            {
                if (!string.IsNullOrEmpty(returnTo) && File.Exists(returnTo) && SceneManager.GetActiveScene().path != returnTo)
                    EditorSceneManager.OpenScene(returnTo, OpenSceneMode.Single);
            }
        }

        // ---- prefabs ------------------------------------------------------------------------------------------
        /// <summary>
        /// The prefab at `path` as it is (never rebuilt or overwritten), or, when missing, built and saved in a
        /// temporary empty scene (opened once per run: builds never touch a project scene).
        /// </summary>
        static GameObject LoadOrBuild(string path, Func<GameObject> build, Type marker, List<string> notes, List<string> saved, ref bool temporary)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null)
            {
                if (marker != null && existing.GetComponent(marker) == null)
                    throw new InvalidOperationException($"{path}에 {marker.Name} 컴포넌트가 없습니다(기존 프리팹은 덮어쓰지 않음). 고치거나 옮긴 뒤 다시 실행하세요");
                return existing;
            }
            if (!temporary)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                temporary = true;
            }
            EnsureFolder(ArchiveUiBuilder.PrefabFolder);
            var root = build();
            try
            {
                var asset = PrefabUtility.SaveAsPrefabAsset(root, path, out var ok);
                if (!ok || asset == null) throw new InvalidOperationException($"{path} 프리팹을 저장하지 못했습니다");
                saved.Add(path);
                notes.Add($"프리팹 생성: {path}");
                return asset;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Loads each UI prefab; builds and saves only the missing ones.</summary>
        static UiPrefabs EnsurePrefabs(TMP_FontAsset font, List<string> notes, List<string> saved)
        {
            var temporary = false;
            GameObject Load(string path, Func<GameObject> build, Type marker) => LoadOrBuild(path, build, marker, notes, saved, ref temporary);

            var ui = new UiPrefabs();
            ui.Settings = Load(ArchiveUiBuilder.SettingsPrefabPath, ArchiveUiBuilder.BuildSettingsPanel, typeof(SettingsPanel));
            ui.MainMenu = Load(ArchiveUiBuilder.MainMenuPrefabPath, () => ArchiveUiBuilder.BuildMainMenuCanvas(ui.Settings), typeof(Canvas));
            ui.GameMenu = Load(ArchiveUiBuilder.GameMenuPrefabPath, () => ArchiveUiBuilder.BuildGameMenuCanvas(ui.Settings), typeof(Canvas));
            ui.Hud = Load(ArchiveUiBuilder.HudPrefabPath, ArchiveUiBuilder.BuildHudCanvas, typeof(Canvas));
            ui.Hint = Load(ArchiveUiBuilder.HintPrefabPath, () => ArchiveUiBuilder.BuildPlayHintCanvas(EnsureHintMaterial(font, notes, saved)), typeof(Canvas));
            ui.Result = Load(ArchiveUiBuilder.ResultPrefabPath, ArchiveUiBuilder.BuildResultCanvas, typeof(Canvas));
            ui.Map = Load(ArchiveUiBuilder.MapPrefabPath, ArchiveUiBuilder.BuildMapCanvas, typeof(MapView));
            return ui;
        }

        static Material EnsureHintMaterial(TMP_FontAsset font, List<string> notes, List<string> saved)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(ArchiveUiBuilder.HintMaterialPath);
            if (existing != null) return existing;
            if (font.material == null)
            {
                notes.Add($"{Warning}폰트 머티리얼이 없어 안내 외곽선 머티리얼을 만들지 않았습니다(안내는 외곽선 없이 표시)");
                return null;
            }
            var m = ArchiveUiBuilder.NewHintMaterial(font);
            AssetDatabase.CreateAsset(m, ArchiveUiBuilder.HintMaterialPath);
            saved.Add(ArchiveUiBuilder.HintMaterialPath);
            notes.Add($"안내 외곽선 머티리얼 생성: {ArchiveUiBuilder.HintMaterialPath}");
            return m;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        // ---- scenes -------------------------------------------------------------------------------------------
        static void SetupGameplay(UiPrefabs ui, List<string> notes, List<string> saved)
        {
            var start = notes.Count;
            if (!File.Exists(GameplayPath))
            {
                if (!AssetDatabase.CopyAsset(DevScenePath, GameplayPath)) throw new InvalidOperationException($"{DevScenePath}를 {GameplayPath}로 복사하지 못했습니다");
                saved.Add(GameplayPath);
                notes.Add("Gameplay 씬 생성(RoomGenerationDebug 구성 복사, 개발용 씬은 그대로)");
            }
            var scene = EditorSceneManager.OpenScene(GameplayPath, OpenSceneMode.Single);
            var games = Object.FindObjectsByType<RoomGenerationDebug>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (games.Length != 1) throw new InvalidOperationException($"Gameplay 씬의 RoomGenerationDebug가 {games.Length}개입니다(하나여야 함)");
            var game = games[0];
            var so = new SerializedObject(game);
            SetBool(so, "startWhenDataReady", false, "Gameplay: Inspector 단어 자동 시작 끔", notes);
            SetString(so, "haltAdvice", HaltAdvice, "Gameplay: 정지 안내를 메인 메뉴 복귀로", notes);
            so.ApplyModifiedPropertiesWithoutUndo();

            var go = game.gameObject;
            Ensure<GameplayBootstrap>(go, "Gameplay", notes);
            var pause = Ensure<PauseMenu>(go, "Gameplay", notes);
            FillIfEmpty(pause, "game", game, "Gameplay: 게임 메뉴 연결", notes);
            var menuLink = Connect(pause, ui.GameMenu, PauseLinks, scene, "Gameplay", "GameMenu Canvas", notes);
            if (menuLink.Placed != null) menuLink.Placed.SetActive(false);  // closed until Escape
            LegacyUiMigration.Menu(pause, menuLink, "Gameplay", notes, saved);
            Ensure<UiSound>(go, "Gameplay", notes);
            SetupHudAndHint(game, ui, scene, "Gameplay", notes, saved);
            SetupResult(game, pause, ui.Result, scene, notes);
            SetupMap(game, pause, ui.Map, scene, notes);
            EnsureEventSystem("Gameplay", notes);
            EnsureListener(game.Player != null ? game.Player.GetComponentInChildren<Camera>(true) : Camera.main, "Gameplay", notes);
            SaveIfChanged(scene, GameplayPath, start, notes, saved);
        }

        /// <summary>Development scene: only the HUD and hint canvases its components use (its development features stay).</summary>
        static void SetupDevScene(UiPrefabs ui, List<string> notes, List<string> saved)
        {
            var start = notes.Count;
            var scene = EditorSceneManager.OpenScene(DevScenePath, OpenSceneMode.Single);
            var games = Object.FindObjectsByType<RoomGenerationDebug>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (games.Length != 1)
            {
                notes.Add($"{Warning}RoomGenerationDebug 씬의 RoomGenerationDebug가 {games.Length}개라 HUD·안내를 구성하지 않았습니다");
                return;
            }
            SetupHudAndHint(games[0], ui, scene, "RoomGenerationDebug", notes, saved);
            SaveIfChanged(scene, DevScenePath, start, notes, saved);
        }

        static void SetupMainMenu(GameObject room, UiPrefabs ui, List<string> notes, List<string> saved)
        {
            var start = notes.Count;
            Scene scene;
            if (!File.Exists(MainMenuPath))
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var cameraObject = new GameObject("Menu Camera") { tag = "MainCamera" };
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.fieldOfView = 60f;
                var position = new Vector3(-2.2f, 1.6f, -2.2f);
                cameraObject.transform.SetPositionAndRotation(position, Quaternion.LookRotation(new Vector3(2.0f, 1.35f, 2.0f) - position));
                cameraObject.AddComponent<AudioListener>();
                var background = (GameObject)PrefabUtility.InstantiatePrefab(room, scene);
                background.name = "Background Room";                     // static view only: no generator, no player
                new GameObject("MainMenu", typeof(MainMenuController), typeof(UiSound));
                notes.Add("MainMenu 씬 생성(메뉴 카메라, 정적 배경 방, 메뉴)");
            }
            else scene = EditorSceneManager.OpenScene(MainMenuPath, OpenSceneMode.Single);

            var menus = Object.FindObjectsByType<MainMenuController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (menus.Length > 1) notes.Add($"{Warning}MainMenu: MainMenuController가 {menus.Length}개입니다(첫 번째만 구성, 삭제하지 않음)");
            var menu = menus.Length > 0 ? menus[0] : new GameObject("MainMenu").AddComponent<MainMenuController>();
            if (menus.Length == 0) notes.Add("MainMenu: 메뉴 오브젝트 추가");
            Ensure<UiSound>(menu.gameObject, "MainMenu", notes);
            EnsureArraySize(menu, "difficultyButtons", 3);
            var menuLink = Connect(menu, ui.MainMenu, MainMenuLinks, scene, "MainMenu", "MainMenu Canvas", notes);
            LegacyUiMigration.Menu(menu, menuLink, "MainMenu", notes, saved);
            EnsureEventSystem("MainMenu", notes);
            EnsureListener(Camera.main, "MainMenu", notes);
            SaveIfChanged(scene, MainMenuPath, start, notes, saved, !File.Exists(MainMenuPath));
        }

        static void SetupBuildScenes(List<string> notes, List<string> saved)
        {
            var others = EditorBuildSettings.scenes.Where(s => s.path != MainMenuPath && s.path != GameplayPath).ToList();
            var scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(MainMenuPath, true),
                new EditorBuildSettingsScene(GameplayPath, true),
            };
            scenes.AddRange(others);                                     // existing entries kept, in their order
            var before = string.Join(",", EditorBuildSettings.scenes.Select(s => $"{s.path}:{s.enabled}"));
            if (before == string.Join(",", scenes.Select(s => $"{s.path}:{s.enabled}"))) return;
            EditorBuildSettings.scenes = scenes.ToArray();
            saved.Add("빌드 씬 목록");
            notes.Add("빌드 씬 목록: MainMenu(첫 씬)·Gameplay 등록, 기존 항목 유지");
        }

        /// <summary>The scene's WordRoomHud and PlayHint (the generator's references, else the only one in the scene, else added to the generator) get their canvases.</summary>
        static void SetupHudAndHint(RoomGenerationDebug game, UiPrefabs ui, Scene scene, string label, List<string> notes, List<string> saved)
        {
            var hud = Controller<WordRoomHud>(game, "hud", label, notes);
            if (hud != null) LegacyUiMigration.Hud(hud, Connect(hud, ui.Hud, HudLinks, scene, label, "HUD Canvas", notes), label, notes, saved);
            var hint = Controller<PlayHint>(game, "hint", label, notes);
            if (hint != null) LegacyUiMigration.Hint(hint, Connect(hint, ui.Hint, HintLinks, scene, label, "PlayHint Canvas", notes), label, notes, saved);
        }

        /// <summary>
        /// Gameplay's goal result: the only ResultScreen of the scene (added to the generator's object when there is
        /// none; several are reported and left alone), its generator/menu references when empty, and one Result Canvas
        /// instance (placed inactive when new). No value move, no material: the result UI never existed before.
        /// </summary>
        static void SetupResult(RoomGenerationDebug game, PauseMenu pause, GameObject prefab, Scene scene, List<string> notes)
        {
            var all = Object.FindObjectsByType<ResultScreen>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 1)
            {
                notes.Add($"{Warning}Gameplay: ResultScreen이 {all.Length}개입니다({string.Join(", ", all.Select(r => $"'{PathOf(r.gameObject)}'"))}). 결과 화면 구성을 중단했습니다(삭제하지 않음)");
                return;
            }
            ResultScreen result;
            if (all.Length == 1) result = all[0];
            else
            {
                result = game.gameObject.AddComponent<ResultScreen>();
                notes.Add("Gameplay: ResultScreen 추가");
            }
            FillIfEmpty(result, "game", game, "Gameplay: 결과 화면 → 방 생성 제어 연결", notes);
            if (pause != null) FillIfEmpty(result, "pauseMenu", pause, "Gameplay: 결과 화면 → 게임 메뉴 연결", notes);
            var link = Connect(result, prefab, ResultLinks, scene, "Gameplay", "Result Canvas", notes);
            if (link.Placed != null) link.Placed.SetActive(false);            // hidden until the goal
        }

        /// <summary>
        /// Gameplay's map: the only MapScreen of the scene (added to the generator's object when there is none; several
        /// are reported and left alone), its generator / game menu / 3D view references when empty, one Map Canvas
        /// instance (placed inactive when new), and the game menu's and result screen's `map` when empty.
        /// </summary>
        static void SetupMap(RoomGenerationDebug game, PauseMenu pause, GameObject prefab, Scene scene, List<string> notes)
        {
            var all = Object.FindObjectsByType<MapScreen>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 1)
            {
                notes.Add($"{Warning}Gameplay: MapScreen이 {all.Length}개입니다({string.Join(", ", all.Select(m => $"'{PathOf(m.gameObject)}'"))}). 지도 구성을 중단했습니다(삭제하지 않음)");
                return;
            }
            MapScreen map;
            if (all.Length == 1) map = all[0];
            else
            {
                map = game.gameObject.AddComponent<MapScreen>();
                notes.Add("Gameplay: MapScreen 추가");
            }
            FillIfEmpty(map, "game", game, "Gameplay: 지도 → 방 생성 제어 연결", notes);
            if (pause != null) FillIfEmpty(map, "pauseMenu", pause, "Gameplay: 지도 → 게임 메뉴 연결", notes);
            var world = game.GetComponent<RoomWorldView>();
            if (world != null) FillIfEmpty(map, "worldView", world, "Gameplay: 지도 → 3D 방 표시 연결", notes);
            else notes.Add($"{Warning}Gameplay: '{PathOf(game.gameObject)}'에 RoomWorldView가 없어 MapScreen.worldView를 연결하지 않았습니다");
            var link = Connect(map, prefab, MapLinks, scene, "Gameplay", "Map Canvas", notes);
            if (link.Placed != null) link.Placed.SetActive(false);            // closed until Tab
            if (pause != null) LinkToMap(pause, "게임 메뉴", map, notes);
            var results = Object.FindObjectsByType<ResultScreen>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (results.Length == 1) LinkToMap(results[0], "결과 화면", map, notes);
            else if (results.Length > 1) notes.Add($"{Warning}Gameplay: ResultScreen이 {results.Length}개라 결과 화면의 지도 연결을 하지 않았습니다");
        }

        /// <summary>`owner.map` = `map` when empty; a different map already set is reported, not replaced.</summary>
        static void LinkToMap(Component owner, string label, MapScreen map, List<string> notes)
        {
            var so = new SerializedObject(owner);
            var p = so.FindProperty("map") ?? throw new InvalidOperationException($"{owner.GetType().Name}에 'map' 필드가 없습니다");
            if (p.objectReferenceValue == map) return;
            if (p.objectReferenceValue != null)
            {
                notes.Add($"{Warning}Gameplay: {label} '{PathOf(owner.gameObject)}'의 map이 다른 지도 '{PathOf(((Component)p.objectReferenceValue).gameObject)}'를 가리킵니다(바꾸지 않음)");
                return;
            }
            p.objectReferenceValue = map;
            so.ApplyModifiedPropertiesWithoutUndo();
            notes.Add($"Gameplay: {label} → 지도 연결");
        }

        static T Controller<T>(RoomGenerationDebug game, string field, string label, List<string> notes) where T : Component
        {
            var so = new SerializedObject(game);
            var p = so.FindProperty(field) ?? throw new InvalidOperationException($"RoomGenerationDebug에 '{field}' 필드가 없습니다");
            if (p.objectReferenceValue is T connected) return connected;
            var all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 1)
            {
                notes.Add($"{Warning}{label}: {typeof(T).Name}가 {all.Length}개이고 RoomGenerationDebug.{field}가 비어 있어 연결하지 않았습니다(삭제하지 않음)");
                return null;
            }
            T c;
            if (all.Length == 1) c = all[0];
            else
            {
                c = game.gameObject.AddComponent<T>();
                notes.Add($"{label}: {typeof(T).Name} 추가");
            }
            p.objectReferenceValue = c;
            so.ApplyModifiedPropertiesWithoutUndo();
            notes.Add($"{label}: RoomGenerationDebug.{field} 연결");
            return c;
        }

        // ---- connecting scene references to prefab instances -------------------------------------------------
        /// <summary>
        /// Result of Connect for one control component: the prefab instance its references now belong to (null when
        /// they point to user-made UI), the instance placed by this run, whether every link is set, and whether the
        /// automatic connection was stopped because the setup is ambiguous (reported as a warning).
        /// </summary>
        internal sealed class Connection
        {
            public GameObject Root;
            public GameObject Placed;
            public bool Complete;
            public bool Blocked;
        }

        /// <summary>
        /// Checks, then fills the empty `links` of `owner` from one instance of `prefab`. Always checked first, whether
        /// a link is empty or not: several instances of the prefab in the scene; set references belonging to different
        /// instances; references mixing an instance with UI outside the prefab; references to user-made UI next to an
        /// unused instance. Any of these is reported (scene, component, instances, fields) and stops the automatic
        /// placing and filling for this component; nothing set is changed and nothing is deleted. References that all
        /// point to user-made UI are accepted as they are when complete. Otherwise the instance the references point
        /// into, else the only instance in the scene, else a newly placed one gives the missing references.
        /// </summary>
        static Connection Connect(Component owner, GameObject prefab, Link[] links, Scene scene, string label, string what, List<string> notes)
        {
            var result = new Connection();
            var ownerName = $"{owner.GetType().Name}('{PathOf(owner.gameObject)}')";
            var so = new SerializedObject(owner);
            var empty = new List<Link>();
            var byRoot = new Dictionary<GameObject, List<string>>();
            var outside = new List<string>();
            foreach (var link in links)
            {
                var p = so.FindProperty(link.Field) ?? throw new InvalidOperationException($"{owner.GetType().Name}에 '{link.Field}' 필드가 없습니다");
                if (p.objectReferenceValue == null)
                {
                    empty.Add(link);
                    continue;
                }
                var r = InstanceRoot(p.objectReferenceValue, prefab);
                if (r == null) outside.Add(link.Field);
                else
                {
                    if (!byRoot.TryGetValue(r, out var fields)) byRoot[r] = fields = new List<string>();
                    fields.Add(link.Field);
                }
            }

            var instances = Instances(prefab);
            string stop = null;
            if (instances.Count > 1)
                stop = $"{what} 인스턴스가 {instances.Count}개입니다({string.Join(", ", instances.Select(i => $"'{PathOf(i)}'"))})";
            else if (byRoot.Count > 1)
                stop = $"참조가 서로 다른 {what} 인스턴스를 가리킵니다({string.Join(" / ", byRoot.Select(kv => $"'{PathOf(kv.Key)}': {string.Join(", ", kv.Value)}"))})";
            else if (byRoot.Count == 1 && outside.Count > 0)
                stop = $"참조가 {what} 인스턴스 '{PathOf(byRoot.Keys.First())}'({string.Join(", ", byRoot.Values.First())})와 프리팹 밖 UI({string.Join(", ", outside)})에 섞여 있습니다";
            else if (outside.Count > 0 && instances.Count == 1)
                stop = $"참조가 프리팹 밖 UI({string.Join(", ", outside)})를 가리키는데 연결되지 않은 {what} 인스턴스 '{PathOf(instances[0])}'가 따로 있습니다(중복 표시 가능)";
            else if (outside.Count > 0 && empty.Count > 0)
                stop = $"일부 참조만 프리팹 밖 UI({string.Join(", ", outside)})에 연결되어 나머지를 어디서 채울지 정할 수 없습니다. 빈 참조: {string.Join(", ", empty.Select(l => l.Field))}";
            if (stop != null)
            {
                notes.Add($"{Warning}{label}: {ownerName} — {stop}. 이 UI의 자동 배치·참조 채우기를 중단했습니다(기존 참조·UI는 삭제·교체하지 않음). 하나만 남기거나 Inspector에서 직접 연결하세요");
                result.Blocked = true;
                return result;
            }
            if (outside.Count > 0)
            {
                result.Complete = true;                                  // user-made UI, fully connected: kept as it is
                return result;
            }

            var root = byRoot.Count == 1 ? byRoot.Keys.First() : null;
            if (root == null && empty.Count > 0)
            {
                if (instances.Count == 1) root = instances[0];                  // reused; reported with each filled link below
                else
                {
                    root = result.Placed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    notes.Add($"{label}: {what} 배치");
                }
            }
            result.Root = root;
            var missing = 0;
            foreach (var link in empty)
            {
                var t = link.Path.Length == 0 ? root.transform : root.transform.Find(link.Path);
                Object value = null;
                if (t != null) value = link.Type == typeof(GameObject) ? t.gameObject : (Object)t.GetComponent(link.Type);
                if (value == null)
                {
                    missing++;
                    notes.Add($"{Warning}{label}: {ownerName}.{link.Field}에 연결할 '{PathOf(root)}/{link.Path}'({link.Type.Name})를 찾지 못했습니다");
                    continue;
                }
                so.FindProperty(link.Field).objectReferenceValue = value;
                notes.Add($"{label}: {owner.GetType().Name}.{link.Field} 연결('{PathOf(root)}')");
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            result.Complete = missing == 0;
            return result;
        }

        /// <summary>
        /// The nearest instance of `prefab` that `value` belongs to (its own object or an ancestor that is an instance
        /// root of the prefab, also when nested in another prefab); null when it is not inside one.
        /// </summary>
        static GameObject InstanceRoot(Object value, GameObject prefab)
        {
            var go = value as GameObject ?? (value as Component)?.gameObject;
            for (var t = go != null ? go.transform : null; t != null; t = t.parent)
                if (IsInstanceOf(t.gameObject, prefab)) return t.gameObject;
            return null;
        }

        static bool IsInstanceOf(GameObject go, GameObject prefab) =>
            PrefabUtility.IsAnyPrefabInstanceRoot(go) && PrefabUtility.GetCorrespondingObjectFromOriginalSource(go) == prefab;

        /// <summary>Every instance of `prefab` in the open scene (inactive and nested ones too).</summary>
        static List<GameObject> Instances(GameObject prefab)
        {
            var found = new List<GameObject>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (IsInstanceOf(t.gameObject, prefab)) found.Add(t.gameObject);
            return found;
        }

        internal static string PathOf(GameObject go)
        {
            var path = go.name;
            for (var t = go.transform.parent; t != null; t = t.parent) path = $"{t.name}/{path}";
            return path;
        }

        // ---- EventSystem ----------------------------------------------------------------------------------------
        /// <summary>
        /// The Input System's own UI actions asset (PlayerInputEditor.kDefaultInputActionsAssetPath in the installed
        /// package; that constant and InputSystemUIInputModuleEditor.ReassignActions are internal, so the same lookup
        /// is done here): its importer saves one InputActionReference sub-asset per action, which a scene can keep.
        /// </summary>
        const string DefaultUiActionsPath = "Packages/com.unity.inputsystem/InputSystem/Plugins/PlayerInput/DefaultInputActions.inputactions";

        /// <summary>Module fields set for a new module, with the action of the asset's "UI" map each one gets.</summary>
        static readonly (string Field, string Action)[] DefaultUiActions =
        {
            ("m_PointAction", "Point"),
            ("m_LeftClickAction", "Click"),
            ("m_MiddleClickAction", "MiddleClick"),
            ("m_RightClickAction", "RightClick"),
            ("m_ScrollWheelAction", "ScrollWheel"),
            ("m_MoveAction", "Navigate"),
            ("m_SubmitAction", "Submit"),
            ("m_CancelAction", "Cancel"),
            ("m_TrackedDevicePositionAction", "TrackedDevicePosition"),
            ("m_TrackedDeviceOrientationAction", "TrackedDeviceOrientation"),
        };

        /// <summary>Actions the menus need (pointer, click, scroll, keyboard move, submit, cancel).</summary>
        static readonly (string Field, string Name)[] RequiredUiActions =
        {
            ("m_PointAction", "포인터"),
            ("m_LeftClickAction", "클릭"),
            ("m_ScrollWheelAction", "스크롤"),
            ("m_MoveAction", "이동"),
            ("m_SubmitAction", "제출"),
            ("m_CancelAction", "취소"),
        };

        /// <summary>
        /// One EventSystem with one enabled InputSystemUIInputModule whose menu actions are saved references to existing
        /// actions. A scene without an EventSystem gets one made here with the package's saved UI action references
        /// (never the runtime default actions). An existing EventSystem is only checked: several EventSystems, an
        /// inactive object or component, other or duplicate input modules, a disabled module and missing, temporary or
        /// broken action references are reported; the user's input setup is never replaced or enabled. Only an
        /// EventSystem without any input module gets a configured InputSystemUIInputModule. A complete setup is left
        /// unchanged, so running again changes nothing.
        /// </summary>
        static void EnsureEventSystem(string scene, List<string> notes)
        {
            var systems = Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (systems.Length > 1)
            {
                notes.Add($"{Warning}{scene}: EventSystem이 {systems.Length}개입니다({string.Join(", ", systems.Select(s => $"'{PathOf(s.gameObject)}'"))}). 하나만 남기세요(삭제하지 않음)");
                return;
            }
            if (systems.Length == 0)
            {
                if (!TryDefaultUiActions(out var asset, out var references, out var failure))
                {
                    notes.Add($"{Warning}{scene}: {failure}. 저장 가능한 UI 액션이 없어 EventSystem을 만들지 않았습니다");
                    return;
                }
                var go = new GameObject("EventSystem", typeof(EventSystem));
                AssignUiActions(go.AddComponent<InputSystemUIInputModule>(), asset, references);
                notes.Add($"{scene}: EventSystem(InputSystemUIInputModule, {DefaultUiActionsPath} 액션) 추가");
                return;
            }

            var es = systems[0];
            var esPath = PathOf(es.gameObject);
            if (!es.gameObject.activeInHierarchy) notes.Add($"{Warning}{scene}: EventSystem '{esPath}'이 계층에서 비활성입니다(켜지 않음)");
            if (!es.enabled) notes.Add($"{Warning}{scene}: '{esPath}'의 EventSystem 컴포넌트가 꺼져 있습니다(켜지 않음)");
            var modules = es.GetComponents<BaseInputModule>();
            var uiModules = modules.OfType<InputSystemUIInputModule>().ToArray();
            var others = modules.Where(m => !(m is InputSystemUIInputModule)).ToArray();
            if (others.Length > 0)
                notes.Add($"{Warning}{scene}: '{esPath}'에 다른 입력 모듈이 있습니다({string.Join(", ", others.Select(m => $"{m.GetType().Name}{(m.enabled ? "" : "(꺼짐)")}"))}). Input System 전용 프로젝트이므로 정리하세요(바꾸거나 끄지 않음)");
            if (uiModules.Length > 1)
                notes.Add($"{Warning}{scene}: '{esPath}'에 InputSystemUIInputModule이 {uiModules.Length}개입니다. 하나만 남기세요(삭제하지 않음)");
            if (uiModules.Length == 0)
            {
                if (others.Length > 0)
                {
                    notes.Add($"{Warning}{scene}: '{esPath}'에 InputSystemUIInputModule이 없습니다. 다른 모듈이 있어 추가하지 않았습니다");
                    return;
                }
                if (!TryDefaultUiActions(out var asset, out var references, out var failure))
                {
                    notes.Add($"{Warning}{scene}: {failure}. '{esPath}'에 입력 모듈을 추가하지 않았습니다");
                    return;
                }
                AssignUiActions(es.gameObject.AddComponent<InputSystemUIInputModule>(), asset, references);
                notes.Add($"{scene}: '{esPath}'에 InputSystemUIInputModule({DefaultUiActionsPath} 액션) 추가");
                return;
            }
            foreach (var module in uiModules)
            {
                if (!module.enabled) notes.Add($"{Warning}{scene}: '{esPath}'의 InputSystemUIInputModule이 꺼져 있습니다(켜지 않음)");
                CheckUiActions(module, scene, esPath, notes);
            }
        }

        /// <summary>Reports menu actions that are not set, not saved (temporary runtime references) or point to no action.</summary>
        static void CheckUiActions(InputSystemUIInputModule module, string scene, string esPath, List<string> notes)
        {
            var so = new SerializedObject(module);
            var problems = new List<string>();
            if (so.FindProperty("m_ActionsAsset")?.objectReferenceValue == null) problems.Add("Actions Asset 없음");
            foreach (var (field, name) in RequiredUiActions)
            {
                var p = so.FindProperty(field);
                if (p == null)
                {
                    problems.Add($"{name}(필드 {field} 없음 — 설치된 Input System 구조 확인 필요)");
                    continue;
                }
                var reference = p.objectReferenceValue as InputActionReference;
                if (reference == null) problems.Add($"{name}(참조 없음)");
                else if (!EditorUtility.IsPersistent(reference)) problems.Add($"{name}(저장되지 않는 임시 참조)");
                else if (reference.action == null) problems.Add($"{name}(가리키는 action 없음)");
            }
            if (problems.Count == 0) return;
            notes.Add($"{Warning}{scene}: '{esPath}'의 InputSystemUIInputModule 메뉴 입력 액션이 불완전합니다: {string.Join(", ", problems)}. " +
                      "실행 시 기본 액션 자동 생성은 모든 액션이 비었을 때만 동작하고 저장되지 않으므로 정상 구성으로 보지 않습니다. Inspector에서 Actions Asset과 액션을 지정하세요(바꾸지 않음)");
        }

        static bool TryDefaultUiActions(out InputActionAsset asset, out Dictionary<string, InputActionReference> references, out string failure)
        {
            references = new Dictionary<string, InputActionReference>();
            failure = null;
            asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(DefaultUiActionsPath);
            if (asset == null)
            {
                failure = $"Input System 기본 UI 액션 자산 {DefaultUiActionsPath}을 찾지 못했습니다";
                return false;
            }
            var saved = AssetDatabase.LoadAllAssetsAtPath(DefaultUiActionsPath).OfType<InputActionReference>().ToArray();
            foreach (var (field, action) in DefaultUiActions)
            {
                var match = saved.FirstOrDefault(r => r.action != null && r.action.actionMap != null && r.action.actionMap.name == "UI" &&
                                                      string.Equals(r.action.name, action, StringComparison.OrdinalIgnoreCase));
                if (match != null) references[field] = match;
            }
           var availableReferences = references;
            var missing = RequiredUiActions
                .Where(a => !availableReferences.ContainsKey(a.Field))
                .Select(a => a.Name)
                .ToArray();

            if (missing.Length == 0) return true;

            failure = $"{DefaultUiActionsPath}에 저장된 UI 액션 참조가 없습니다: {string.Join(", ", missing)}";
            return false;
        }

        /// <summary>A module made by this tool: the saved asset and action references, written as serialized fields.</summary>
        static void AssignUiActions(InputSystemUIInputModule module, InputActionAsset asset, Dictionary<string, InputActionReference> references)
        {
            var so = new SerializedObject(module);
            so.FindProperty("m_ActionsAsset").objectReferenceValue = asset;
            foreach (var (field, _) in DefaultUiActions)
            {
                var p = so.FindProperty(field);
                if (p != null) p.objectReferenceValue = references.TryGetValue(field, out var r) ? r : null;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---- helpers ------------------------------------------------------------------------------------------
        static T Ensure<T>(GameObject go, string scene, List<string> notes) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) return c;
            notes.Add($"{scene}: {typeof(T).Name} 추가");
            return go.AddComponent<T>();
        }

        /// <summary>Exactly one listener: added to `camera` only when the open scene has none (never a second one).</summary>
        static void EnsureListener(Camera camera, string scene, List<string> notes)
        {
            var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (listeners.Length > 1) notes.Add($"{Warning}{scene}: AudioListener가 {listeners.Length}개입니다(하나만 남기세요)");
            if (listeners.Length > 0) return;
            if (camera == null)
            {
                notes.Add($"{Warning}{scene}: 카메라를 찾지 못해 AudioListener를 추가하지 않음");
                return;
            }
            camera.gameObject.AddComponent<AudioListener>();
            notes.Add($"{scene}: '{camera.name}'에 AudioListener 추가");
        }

        static void EnsureArraySize(Object target, string field, int size)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field) ?? throw new InvalidOperationException($"'{field}' 필드를 찾지 못했습니다");
            if (p.arraySize >= size) return;
            p.arraySize = size;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void FillIfEmpty(Object target, string field, Object value, string note, List<string> notes)
        {
            var so = new SerializedObject(target);
            var p = so.FindProperty(field);
            if (p == null || p.objectReferenceValue != null) return;
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            notes.Add(note);
        }

        static void SetBool(SerializedObject so, string field, bool value, string note, List<string> notes)
        {
            var p = so.FindProperty(field) ?? throw new InvalidOperationException($"'{field}' 필드를 찾지 못했습니다");
            if (p.boolValue == value) return;
            p.boolValue = value;
            notes.Add(note);
        }

        static void SetString(SerializedObject so, string field, string value, string note, List<string> notes)
        {
            var p = so.FindProperty(field) ?? throw new InvalidOperationException($"'{field}' 필드를 찾지 못했습니다");
            if (p.stringValue == value) return;
            p.stringValue = value;
            notes.Add(note);
        }

        /// <summary>Saves the scene when this step changed something (warnings alone change nothing).</summary>
        static void SaveIfChanged(Scene scene, string path, int start, List<string> notes, List<string> saved, bool force = false)
        {
            if (!force && !notes.Skip(start).Any(n => !n.StartsWith(Warning))) return;
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, path)) throw new InvalidOperationException($"{path}를 저장하지 못했습니다");
            if (!saved.Contains(path)) saved.Add(path);
        }
    }
}
