using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WordRoom.RoomDebug;
using WordRoom.World;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// User-run helper: adds RoomWorldView next to the selected (or the scene's) RoomGenerationDebug and fills only
    /// empty fields — the room/door prefabs (archive models after the replace menu), Camera.main, the archive room
    /// spacing 6.20 when Room Spacing is unset, and the debug component's worldView reference. Existing values,
    /// prefabs, cameras, lights and project settings are not changed. Marks the scene dirty; saving is up to the user.
    /// </summary>
    public static class RoomWorldViewSetup
    {
        const string RoomPrefabPath = "Assets/Prefabs/TestRoomPrefab.prefab";
        const string DoorPrefabPath = "Assets/Prefabs/TestDoor.prefab";

        [MenuItem("WordRoom/Room Debug/Add 3D Room View")]
        public static void Add()
        {
            var debug = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponent<RoomGenerationDebug>() : null;
            if (debug == null) debug = Object.FindAnyObjectByType<RoomGenerationDebug>();
            if (debug == null)
            {
                EditorUtility.DisplayDialog("3D 방 표시", "열린 장면에 RoomGenerationDebug가 없습니다. 먼저 방 생성 디버그 장면을 여세요.", "확인");
                return;
            }
            var go = debug.gameObject;
            var notes = new List<string>();
            var view = go.GetComponent<RoomWorldView>();
            if (view == null)
            {
                view = Undo.AddComponent<RoomWorldView>(go);
                notes.Add("RoomWorldView 추가");
            }

            var so = new SerializedObject(view);
            FillIfEmpty(so, "roomPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(RoomPrefabPath), RoomPrefabPath, notes);
            FillIfEmpty(so, "doorPrefab", AssetDatabase.LoadAssetAtPath<GameObject>(DoorPrefabPath), DoorPrefabPath, notes);
            FillIfEmpty(so, "debugCamera", Camera.main, "Main Camera", notes);
            var spacing = so.FindProperty("roomSpacing");
            if (spacing != null && spacing.floatValue <= 0f)                 // archive rooms: 6.20 outline, not the decorated mesh bounds
            {
                spacing.floatValue = ArchiveModelSetup.RoomSpacing;
                notes.Add($"roomSpacing ← {ArchiveModelSetup.RoomSpacing:0.00}");
            }
            so.ApplyModifiedProperties();

            var dso = new SerializedObject(debug);
            FillIfEmpty(dso, "worldView", view, "RoomGenerationDebug.worldView", notes);
            dso.ApplyModifiedProperties();

            if (Object.FindAnyObjectByType<Light>() == null)
                notes.Add("장면에 Light가 없습니다. 방이 어둡게 보이면 직접 Directional Light를 추가하세요(자동 추가하지 않음).");
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log("[3D 방] 설정 메뉴: " + (notes.Count > 0 ? string.Join(" / ", notes) : "바꿀 빈 항목이 없습니다") +
                      ". 장면 저장은 직접 하세요.");
        }

        static void FillIfEmpty(SerializedObject so, string field, Object value, string label, List<string> notes)
        {
            var p = so.FindProperty(field);
            if (p == null || p.objectReferenceValue != null) return;
            if (value == null)
            {
                notes.Add($"{field}: {label}을(를) 찾지 못해 비워 둠 — Inspector에서 지정");
                return;
            }
            p.objectReferenceValue = value;
            notes.Add($"{field} ← {label}");
        }
    }
}
