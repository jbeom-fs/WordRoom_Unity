using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.Network;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    public static partial class GameMenuSetup
    {
        static readonly Link[] CoopScreenLinks =
        {
            new Link("view", "", typeof(CoopLobbyView)),
        };

        static readonly Link[] CoopButtonLinks =
        {
            new Link("coopButton", "Main/CoopButton", typeof(Button)),
        };

        /// <summary>
        /// Only the coop lobby of the existing MainMenu scene (user-run; nothing here runs by itself):
        /// 1) MainMenuCanvas prefab: adds the 협동 button under Main (after 시작) only when there is no "Main/CoopButton";
        ///    everything else of the prefab stays as it is. 2) CoopCanvas prefab: built only when missing (an existing one
        ///    is used as it is, except that the waiting room's difficulty row and the lobby's 새로고침 button are added when
        ///    it has none). 3) MainMenu scene: one CoopLobby and one CoopLobbyScreen on the MainMenu object (added
        ///    when the scene has none; several are reported and left alone), the screen's lobby/view and the menu's
        ///    coopButton/coop references when empty, one Coop Canvas instance (placed inactive when new); the scene's
        ///    EventSystem is checked. Nothing is deleted or replaced; no network object is made besides CoopLobby. Neither
        ///    Gameplay nor the development scene is touched; the build scene list stays as it is.
        /// </summary>
        [MenuItem("WordRoom/협동 로비 구성")]
        public static void SetupCoopLobby()
        {
            if (!File.Exists(MainMenuPath))
            {
                Debug.LogError($"[협동 구성] {MainMenuPath}가 없습니다. 먼저 WordRoom/메인 메뉴와 게임 설정 구성을 실행하세요(이 메뉴는 씬을 만들지 않음).");
                return;
            }
            Run("협동 구성", (font, notes, saved) => SetupCoop(notes, saved));
        }

        /// <summary>The coop part, also run by WordRoom/메인 메뉴와 게임 설정 구성 after the main menu (so a newly built 협동 button is never left unconnected).</summary>
        static void SetupCoop(System.Collections.Generic.List<string> notes, System.Collections.Generic.List<string> saved)
        {
            var mainPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArchiveUiBuilder.MainMenuPrefabPath);
            if (mainPrefab == null) throw new InvalidOperationException($"{ArchiveUiBuilder.MainMenuPrefabPath}가 없습니다. 먼저 WordRoom/메인 메뉴와 게임 설정 구성을 실행하세요");
            EnsureCoopButton(notes, saved);
            var temporary = false;
            var coopPrefab = LoadOrBuild(ArchiveUiBuilder.CoopPrefabPath, ArchiveUiBuilder.BuildCoopCanvas, typeof(CoopLobbyView), notes, saved, ref temporary);
            EnsureCoopDifficulty(notes, saved);
            EnsureCoopRefresh(notes, saved);

            var start = notes.Count;
            var scene = EditorSceneManager.OpenScene(MainMenuPath, OpenSceneMode.Single);
            var menus = Object.FindObjectsByType<MainMenuController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (menus.Length != 1) throw new InvalidOperationException($"MainMenu 씬의 MainMenuController가 {menus.Length}개입니다(하나여야 함)");
            var menu = menus[0];
            var lobby = Single<CoopLobby>(menu.gameObject, notes);
            var screen = Single<CoopLobbyScreen>(menu.gameObject, notes);
            if (lobby != null && screen != null)
            {
                FillIfEmpty(screen, "lobby", lobby, "MainMenu: 협동 화면 → CoopLobby 연결", notes);
                var link = Connect(screen, coopPrefab, CoopScreenLinks, scene, "MainMenu", "Coop Canvas", notes);
                if (link.Placed != null) link.Placed.SetActive(false);              // closed until 협동
                var sceneView = new SerializedObject(screen).FindProperty("view").objectReferenceValue as CoopLobbyView;
                if (sceneView != null && !sceneView.HasRefresh)                    // e.g. an unpacked instance or a scene override: left as it is
                    notes.Add($"{Warning}MainMenu: 협동 화면이 쓰는 '{PathOf(sceneView.gameObject)}'에 새로고침 버튼(refreshButton)이 연결되지 않았습니다(바꾸지 않음). 직접 연결하면 새로고침을 쓸 수 있고, 그 전에도 로비의 다른 기능은 그대로입니다");
                Connect(menu, mainPrefab, CoopButtonLinks, scene, "MainMenu", "MainMenu Canvas", notes);
                FillIfEmpty(menu, "coop", screen, "MainMenu: 메인 메뉴 → 협동 화면 연결", notes);
            }
            EnsureEventSystem("MainMenu", notes);
            SaveIfChanged(scene, MainMenuPath, start, notes, saved);
        }

        /// <summary>The scene's only T (added to `owner` when there is none); several are reported and null is returned.</summary>
        static T Single<T>(GameObject owner, System.Collections.Generic.List<string> notes) where T : Component
        {
            var all = Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 1)
            {
                notes.Add($"{Warning}MainMenu: {typeof(T).Name}가 {all.Length}개입니다({string.Join(", ", all.Select(c => $"'{PathOf(c.gameObject)}'"))}). 협동 연결을 중단했습니다(삭제하지 않음)");
                return null;
            }
            if (all.Length == 1) return all[0];
            notes.Add($"MainMenu: {typeof(T).Name} 추가('{PathOf(owner)}')");
            return owner.AddComponent<T>();
        }

        /// <summary>
        /// Adds only the waiting room's difficulty row to the saved CoopCanvas prefab (after the RoomCode row) when it has
        /// none and its CoopLobbyView has no difficulty parts connected; everything else of the prefab stays as it is.
        /// </summary>
        static void EnsureCoopDifficulty(System.Collections.Generic.List<string> notes, System.Collections.Generic.List<string> saved)
        {
            var path = ArchiveUiBuilder.CoopPrefabPath;
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var view = contents.GetComponent<CoopLobbyView>();
                var waiting = contents.transform.Find("Waiting") as RectTransform;
                if (view == null || waiting == null)
                {
                    notes.Add($"{Warning}{path}에 CoopLobbyView 또는 Waiting 패널이 없어 난이도 선택을 추가하지 않았습니다(대기실 시작 불가)");
                    return;
                }
                if (view.HasDifficulty) return;                                   // already there (also user-made and connected)
                if (waiting.Find(ArchiveUiBuilder.DifficultyRowName) != null)
                {
                    notes.Add($"{Warning}{path}의 Waiting/{ArchiveUiBuilder.DifficultyRowName}가 있지만 CoopLobbyView에 연결되지 않았습니다(바꾸지 않음). 직접 연결하세요");
                    return;
                }
                var so = new SerializedObject(view);
                var row = ArchiveUiBuilder.AddDifficultyRow(waiting, so);
                var codeRow = waiting.Find("CodeRow");
                if (codeRow != null) row.SetSiblingIndex(codeRow.GetSiblingIndex() + 1);
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(contents, path, out var ok);
                if (!ok) throw new InvalidOperationException($"{path}에 난이도 선택을 저장하지 못했습니다");
                if (!saved.Contains(path)) saved.Add(path);
                notes.Add($"{path}: Waiting/{ArchiveUiBuilder.DifficultyRowName}(난이도 선택) 추가 — 다른 항목은 그대로");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// Adds only the lobby's 새로고침 button to the saved CoopCanvas prefab (Lobby/Header, before 다시 연결) when its
        /// CoopLobbyView has none connected and the header has no such button; everything else of the prefab stays as it is.
        /// </summary>
        static void EnsureCoopRefresh(System.Collections.Generic.List<string> notes, System.Collections.Generic.List<string> saved)
        {
            var path = ArchiveUiBuilder.CoopPrefabPath;
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var view = contents.GetComponent<CoopLobbyView>();
                var header = contents.transform.Find("Lobby/Header");
                if (view == null || header == null)
                {
                    notes.Add($"{Warning}{path}에 CoopLobbyView 또는 Lobby/Header가 없어 새로고침 버튼을 추가하지 않았습니다(로비의 다른 기능은 그대로). refreshButton을 직접 연결하세요");
                    return;
                }
                if (view.HasRefresh) return;                                      // already there (also user-made and connected)
                if (header.Find(ArchiveUiBuilder.RefreshButtonName) != null)
                {
                    notes.Add($"{Warning}{path}의 Lobby/Header/{ArchiveUiBuilder.RefreshButtonName}가 있지만 CoopLobbyView에 연결되지 않았습니다(바꾸지 않음). 직접 연결하세요");
                    return;
                }
                var button = ArchiveUiBuilder.AddRefreshButton(header);
                var retry = header.Find("RetryButton");
                if (retry != null) button.transform.SetSiblingIndex(retry.GetSiblingIndex());   // just before 다시 연결
                var so = new SerializedObject(view);
                so.FindProperty("refreshButton").objectReferenceValue = button;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(contents, path, out var ok);
                if (!ok) throw new InvalidOperationException($"{path}에 새로고침 버튼을 저장하지 못했습니다");
                if (!saved.Contains(path)) saved.Add(path);
                notes.Add($"{path}: Lobby/Header/{ArchiveUiBuilder.RefreshButtonName}(새로고침) 추가 — 다른 항목은 그대로");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>Adds only the 협동 button to the saved MainMenuCanvas prefab when it has none (after 시작).</summary>
        static void EnsureCoopButton(System.Collections.Generic.List<string> notes, System.Collections.Generic.List<string> saved)
        {
            var path = ArchiveUiBuilder.MainMenuPrefabPath;
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var main = contents.transform.Find("Main");
                if (main == null)
                {
                    notes.Add($"{Warning}{path}에 Main 패널이 없어 협동 버튼을 추가하지 않았습니다(메인 메뉴의 coopButton을 직접 연결하세요)");
                    return;
                }
                if (main.Find("CoopButton") != null) return;
                var button = ArchiveUiBuilder.AddCoopButton(main);
                var startButton = main.Find("StartButton");
                if (startButton != null) button.transform.SetSiblingIndex(startButton.GetSiblingIndex() + 1);
                PrefabUtility.SaveAsPrefabAsset(contents, path, out var ok);
                if (!ok) throw new InvalidOperationException($"{path}에 협동 버튼을 저장하지 못했습니다");
                saved.Add(path);
                notes.Add($"{path}: Main/CoopButton(협동) 추가 — 다른 항목은 그대로");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
    }
}
