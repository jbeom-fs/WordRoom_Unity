using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WordRoom.RoomDebug;
using WordRoom.UnityLayer;

namespace WordRoom.EditorTools
{
    /// <summary>Creates a separate minimal scene for the room generator check (the DataCheck scene is not touched).</summary>
    public static class RoomDebugSceneTools
    {
        public const string ScenePath = "Assets/WordRoom/Scenes/RoomGenerationDebug.unity";

        [MenuItem("WordRoom/Room Debug/Create Room Generation Debug Scene")]
        public static void CreateScene()
        {
            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("방 생성 디버그 장면", $"{ScenePath}이 이미 있습니다. 새로 만들어 덮어쓸까요?", "덮어쓰기", "취소"))
                return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Main Camera");
            var cam = camera.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
            camera.tag = "MainCamera";
            var go = new GameObject("RoomGenerationDebug");
            go.AddComponent<WordRoomDataService>();
            go.AddComponent<RoomGenerationDebug>();
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = go;
            Debug.Log($"방 생성 디버그 장면 저장: {ScenePath}");
        }
    }
}
