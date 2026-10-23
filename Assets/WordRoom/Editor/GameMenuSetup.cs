using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WordRoom.Game;
using WordRoom.RoomDebug;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// User-run setup of the menu flow. Gameplay.unity is made once as a copy of the development scene
    /// RoomGenerationDebug.unity (which itself is never changed) and then only completed: the room generator's
    /// automatic Inspector start is turned off and its stop advice points to the menu, GameplayBootstrap, PauseMenu
    /// and UiSound are added when missing, empty font/reference fields are filled, and an AudioListener is put on
    /// the player camera when the scene has none. MainMenu.unity is made once (menu camera with the only
    /// AudioListener, a static archive room prefab instance as background, the MainMenu object) and later only
    /// completed the same way. Build scenes: MainMenu first, Gameplay second, other entries kept in order. Running
    /// it again adds nothing twice and does not reset values the user changed; models, materials and lights are
    /// not touched. Scenes it changes are saved; the scene open before is reopened.
    /// </summary>
    public static class GameMenuSetup
    {
        const string DevScenePath = "Assets/WordRoom/Scenes/RoomGenerationDebug.unity";
        const string MainMenuPath = "Assets/WordRoom/Scenes/MainMenu.unity";
        const string GameplayPath = "Assets/WordRoom/Scenes/Gameplay.unity";
        const string FontPath = "Assets/Font/Pretendard-Black SDF.asset";
        const string RoomPrefabPath = ArchiveModelSetup.RoomPrefabPath;
        const string HaltAdvice = "Esc를 눌러 메인 메뉴로 돌아가세요";

        [MenuItem("WordRoom/메인 메뉴와 게임 설정 구성")]
        public static void Setup()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[메뉴 구성] Play 중에는 실행하지 않습니다.");
                return;
            }
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            var room = AssetDatabase.LoadAssetAtPath<GameObject>(RoomPrefabPath);
            if (font == null || room == null || !File.Exists(DevScenePath))
            {
                Debug.LogError($"[메뉴 구성] 필요한 자산이 없습니다: 폰트 {(font != null ? "있음" : FontPath)}, 방 프리팹 {(room != null ? "있음" : RoomPrefabPath)}, 개발용 씬 {(File.Exists(DevScenePath) ? "있음" : DevScenePath)}");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var returnTo = SceneManager.GetActiveScene().path;
            var notes = new List<string>();
            try
            {
                SetupGameplay(font, notes);
                SetupMainMenu(font, room, notes);
                SetupBuildScenes(notes);
                Debug.Log("[메뉴 구성] 완료: " + (notes.Count > 0 ? string.Join(" / ", notes) : "바꿀 항목이 없습니다"));
            }
            catch (Exception e)
            {
                Debug.LogError($"[메뉴 구성] 중단 — {e.Message}. 이미 저장한 씬 외에는 바뀌지 않았습니다.");
            }
            finally
            {
                if (!string.IsNullOrEmpty(returnTo) && File.Exists(returnTo) && SceneManager.GetActiveScene().path != returnTo)
                    EditorSceneManager.OpenScene(returnTo, OpenSceneMode.Single);
            }
        }

        static void SetupGameplay(TMP_FontAsset font, List<string> notes)
        {
            if (!File.Exists(GameplayPath))
            {
                if (!AssetDatabase.CopyAsset(DevScenePath, GameplayPath)) throw new InvalidOperationException($"{DevScenePath}를 {GameplayPath}로 복사하지 못했습니다");
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
            FillIfEmpty(pause, "font", font, "Gameplay: 게임 메뉴 폰트", notes);
            FillIfEmpty(pause, "game", game, "Gameplay: 게임 메뉴 연결", notes);
            Ensure<UiSound>(go, "Gameplay", notes);
            EnsureListener(game.Player != null ? game.Player.GetComponentInChildren<Camera>(true) : Camera.main, "Gameplay", notes);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static void SetupMainMenu(TMP_FontAsset font, GameObject room, List<string> notes)
        {
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
            var menu = menus.Length > 0 ? menus[0] : new GameObject("MainMenu").AddComponent<MainMenuController>();
            if (menus.Length == 0) notes.Add("MainMenu: 메뉴 오브젝트 추가");
            Ensure<UiSound>(menu.gameObject, "MainMenu", notes);
            FillIfEmpty(menu, "font", font, "MainMenu: 메뉴 폰트", notes);
            EnsureListener(Camera.main, "MainMenu", notes);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, MainMenuPath);
        }

        static void SetupBuildScenes(List<string> notes)
        {
            var others = EditorBuildSettings.scenes.Where(s => s.path != MainMenuPath && s.path != GameplayPath).ToList();
            var scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(MainMenuPath, true),
                new EditorBuildSettingsScene(GameplayPath, true),
            };
            scenes.AddRange(others);                                     // existing entries kept, in their order
            var before = string.Join(",", EditorBuildSettings.scenes.Select(s => $"{s.path}:{s.enabled}"));
            EditorBuildSettings.scenes = scenes.ToArray();
            if (before != string.Join(",", scenes.Select(s => $"{s.path}:{s.enabled}"))) notes.Add("빌드 씬 목록: MainMenu(첫 씬)·Gameplay 등록, 기존 항목 유지");
        }

        // ---- helpers ----------------------------------------------------------------------------------------
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
            if (listeners.Length > 1) notes.Add($"{scene}: 주의 — AudioListener가 {listeners.Length}개입니다(하나만 남기세요)");
            if (listeners.Length > 0) return;
            if (camera == null)
            {
                notes.Add($"{scene}: 카메라를 찾지 못해 AudioListener를 추가하지 않음");
                return;
            }
            camera.gameObject.AddComponent<AudioListener>();
            notes.Add($"{scene}: '{camera.name}'에 AudioListener 추가");
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
    }
}
