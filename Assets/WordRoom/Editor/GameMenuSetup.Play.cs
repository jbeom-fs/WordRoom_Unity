using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using WordRoom.Game;
using WordRoom.RoomDebug;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// User-run completion of the game menu's 끼임방지 and the result screen's coop part on the existing prefabs and the
    /// Gameplay scene. Prefabs are never rebuilt: GameMenuCanvas gets Menu/UnstuckButton ("끼임방지 (1회)", after 설정) when it
    /// has none — an existing one is reused with its label updated — and loses the note line of an earlier setup (Menu/UnstuckNote); ResultCanvas gets the coop part and a CoopResultView only when its root has none and the
    /// Result panel has none of the coop objects. A part found alone, or objects present but not connected, are reported
    /// and left as they are (nothing duplicated or overwritten). In Gameplay only empty references are filled: PauseMenu's
    /// unstuckButton and ResultScreen's coopResult — and only when exactly one instance of that canvas is in the scene; a
    /// PauseMenu still saved with the former note reference is saved again without it. Colors, fonts, layout and every
    /// other reference stay. Running it again adds nothing twice.
    /// </summary>
    public static partial class GameMenuSetup
    {
        static readonly Link[] UnstuckLinks =
        {
            new Link("unstuckButton", $"Menu/{ArchiveUiBuilder.UnstuckButtonName}", typeof(Button)),
        };

        static readonly Link[] CoopResultLinks =
        {
            new Link("coopResult", "", typeof(CoopResultView)),
        };

        [MenuItem("WordRoom/끼임방지·협동 결과 구성")]
        public static void SetupUnstuckAndCoopResult()
        {
            if (!File.Exists(GameplayPath))
            {
                Debug.LogError($"[끼임방지·협동 결과 구성] {GameplayPath}가 없습니다. 먼저 WordRoom/메인 메뉴와 게임 설정 구성을 실행하세요(이 메뉴는 씬을 만들지 않음).");
                return;
            }
            Run("끼임방지·협동 결과 구성", (font, notes, saved) =>
            {
                var menu = AssetDatabase.LoadAssetAtPath<GameObject>(ArchiveUiBuilder.GameMenuPrefabPath);
                var result = AssetDatabase.LoadAssetAtPath<GameObject>(ArchiveUiBuilder.ResultPrefabPath);
                if (menu == null) notes.Add($"{Warning}{ArchiveUiBuilder.GameMenuPrefabPath}가 없어 끼임방지를 추가하지 않았습니다. WordRoom/메인 메뉴와 게임 설정 구성을 먼저 실행하세요");
                else EnsureGameMenuUnstuck(notes, saved);
                if (result == null) notes.Add($"{Warning}{ArchiveUiBuilder.ResultPrefabPath}가 없어 협동 결과를 추가하지 않았습니다. WordRoom/결과 화면 구성을 먼저 실행하세요");
                else EnsureResultCoop(notes, saved);
                var start = notes.Count;
                var staleNoteReference = File.ReadAllText(GameplayPath).Contains("unstuckNote:");   // PauseMenu's former note field, still in the saved scene
                var scene = EditorSceneManager.OpenScene(GameplayPath, OpenSceneMode.Single);
                var games = Object.FindObjectsByType<RoomGenerationDebug>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (games.Length != 1) throw new InvalidOperationException($"Gameplay 씬의 RoomGenerationDebug가 {games.Length}개입니다(하나여야 함)");
                var pauses = Object.FindObjectsByType<PauseMenu>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (menu != null)
                {
                    if (pauses.Length == 1) LinkIntoOnlyInstance(pauses[0], menu, UnstuckLinks, scene, "GameMenu Canvas", notes);
                    else notes.Add($"{Warning}Gameplay: PauseMenu가 {pauses.Length}개라 끼임방지를 연결하지 않았습니다(하나여야 함)");
                }
                if (staleNoteReference && pauses.Length > 0)
                {
                    foreach (var p in pauses) EditorUtility.SetDirty(p);     // saved again without the removed field
                    notes.Add("Gameplay: PauseMenu의 이전 끼임방지 설명 참조(unstuckNote) 정리");
                }
                var screens = Object.FindObjectsByType<ResultScreen>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (result != null)
                {
                    if (screens.Length == 1) LinkIntoOnlyInstance(screens[0], result, CoopResultLinks, scene, "Result Canvas", notes);
                    else notes.Add($"{Warning}Gameplay: ResultScreen이 {screens.Length}개라 협동 결과를 연결하지 않았습니다(하나여야 함). WordRoom/결과 화면 구성을 먼저 확인하세요");
                }
                SaveIfChanged(scene, GameplayPath, start, notes, saved);
            });
        }

        /// <summary>Only empty references, only into the one placed instance of `prefab` (never a new instance here).</summary>
        static void LinkIntoOnlyInstance(Component owner, GameObject prefab, Link[] links, Scene scene, string what, List<string> notes)
        {
            var instances = Instances(prefab);
            if (instances.Count != 1)
            {
                notes.Add($"{Warning}Gameplay: {what} 인스턴스가 {instances.Count}개라 {owner.GetType().Name}의 {string.Join(", ", links.Select(l => l.Field))}를 연결하지 않았습니다(하나여야 함, 새로 배치하지 않음)");
                return;
            }
            Connect(owner, prefab, links, scene, "Gameplay", what, notes);
        }

        /// <summary>
        /// The saved GameMenuCanvas: Menu/UnstuckButton reused when it exists (only its label set to "끼임방지 (1회)"), added
        /// after 설정 when it does not; the note line an earlier setup made (Menu/UnstuckNote) removed with its space. Its
        /// position, colors and every other part stay; running again changes nothing.
        /// </summary>
        static void EnsureGameMenuUnstuck(List<string> notes, List<string> saved)
        {
            var path = ArchiveUiBuilder.GameMenuPrefabPath;
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var list = contents.transform.Find("Menu") as RectTransform;
                if (list == null)
                {
                    notes.Add($"{Warning}{path}에 Menu 목록이 없어 끼임방지를 추가하지 않았습니다(메뉴의 다른 기능은 그대로)");
                    return;
                }
                var changed = false;
                var note = list.Find(ArchiveUiBuilder.UnstuckNoteName);
                if (note != null)
                {
                    Object.DestroyImmediate(note.gameObject);                  // the former note line and its space
                    changed = true;
                    notes.Add($"{path}: 이전 구성의 Menu/{ArchiveUiBuilder.UnstuckNoteName} 제거");
                }
                var button = list.Find(ArchiveUiBuilder.UnstuckButtonName);
                if (button == null)
                {
                    var settings = list.Find("SettingsButton");
                    ArchiveUiBuilder.AddUnstuck(list, settings != null ? settings.GetSiblingIndex() + 1 : -1);
                    changed = true;
                    notes.Add($"{path}: Menu/{ArchiveUiBuilder.UnstuckButtonName}({ArchiveUiBuilder.UnstuckLabel}) 추가{(settings == null ? "(설정 버튼이 없어 목록 끝)" : "")} — 다른 항목은 그대로");
                }
                else
                {
                    var label = button.Find("Label") != null ? button.Find("Label").GetComponent<TextMeshProUGUI>() : null;
                    if (label == null) notes.Add($"{Warning}{path}의 Menu/{ArchiveUiBuilder.UnstuckButtonName}에 Label 텍스트가 없어 문구를 바꾸지 않았습니다. 직접 '{ArchiveUiBuilder.UnstuckLabel}'로 바꾸세요");
                    else if (label.text != ArchiveUiBuilder.UnstuckLabel)
                    {
                        label.text = ArchiveUiBuilder.UnstuckLabel;               // the existing button is kept; only its words change
                        changed = true;
                        notes.Add($"{path}: Menu/{ArchiveUiBuilder.UnstuckButtonName} 문구를 '{ArchiveUiBuilder.UnstuckLabel}'로 변경");
                    }
                }
                if (!changed) return;
                PrefabUtility.SaveAsPrefabAsset(contents, path, out var ok);
                if (!ok) throw new InvalidOperationException($"{path}에 끼임방지를 저장하지 못했습니다");
                if (!saved.Contains(path)) saved.Add(path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>The coop part of the saved ResultCanvas (before the spacer) and a connected CoopResultView, when none of it exists.</summary>
        static void EnsureResultCoop(List<string> notes, List<string> saved)
        {
            var path = ArchiveUiBuilder.ResultPrefabPath;
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var panel = contents.transform.Find("Result") as RectTransform;
                if (panel == null)
                {
                    notes.Add($"{Warning}{path}에 Result 패널이 없어 협동 결과를 추가하지 않았습니다(단독 결과는 그대로)");
                    return;
                }
                var names = new[] { ArchiveUiBuilder.CoopTitleName, ArchiveUiBuilder.CoopTimeRowName, ArchiveUiBuilder.CoopTeamRowName, ArchiveUiBuilder.CoopPlayersLabelName, ArchiveUiBuilder.CoopPlayersName };
                var present = names.Where(n => panel.Find(n) != null).ToArray();
                var view = contents.GetComponent<CoopResultView>();
                if (view != null && present.Length == names.Length) return;   // already there (its references are checked when shown)
                if (view != null || present.Length > 0)
                {
                    notes.Add($"{Warning}{path}에 협동 결과가 일부만 있습니다(CoopResultView {(view != null ? "있음" : "없음")}, 부품 {(present.Length > 0 ? string.Join(", ", present) : "없음")}). 바꾸지 않았습니다(중복 생성 안 함). 직접 연결하거나 정리한 뒤 다시 실행하세요");
                    return;
                }
                var solo = new[] { panel.Find("Title"), panel.Find("TimeRow"), panel.Find("VisitedRow") };
                if (solo.Any(t => t == null))
                {
                    notes.Add($"{Warning}{path}의 Result 패널에 단독 결과 부품(Title·TimeRow·VisitedRow)이 없어 협동 결과를 추가하지 않았습니다(단독 결과는 그대로)");
                    return;
                }
                var spacer = panel.Find("Spacer");
                var button = panel.Find("MainMenuButton");
                var at = spacer != null ? spacer.GetSiblingIndex() : button != null ? button.GetSiblingIndex() : panel.childCount;
                ArchiveUiBuilder.AddCoopResult(contents, panel, solo.Select(t => t.gameObject).ToArray(), at);
                PrefabUtility.SaveAsPrefabAsset(contents, path, out var ok);
                if (!ok) throw new InvalidOperationException($"{path}에 협동 결과를 저장하지 못했습니다");
                if (!saved.Contains(path)) saved.Add(path);
                notes.Add($"{path}: Result/{string.Join("·", names)}(비활성)와 CoopResultView 추가 — 단독 결과와 다른 항목은 그대로");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
    }
}
