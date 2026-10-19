using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WordRoom.Data;
using WordRoom.Data.Validation;
using WordRoom.UnityLayer;

namespace WordRoom.EditorTools
{
    /// <summary>Editor menu and batch-mode entry points for the data check scene, a Windows check build and a package check.</summary>
    public static class WordRoomDataTools
    {
        public const string ScenePath = "Assets/WordRoom/Scenes/DataCheck.unity";
        const string DefaultBuild = "Builds/WordRoomDataCheck_Win64/WordRoomDataCheck.exe";

        [MenuItem("WordRoom/Data/Create Data Check Scene")]
        public static void CreateDataCheckScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Main Camera");
            var cam = camera.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
            camera.tag = "MainCamera";
            var data = new GameObject("WordRoomData");
            data.AddComponent<WordRoomDataService>();
            data.AddComponent<WordRoomDataInspector>();
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"데이터 확인 장면 저장: {ScenePath}");
        }

        [MenuItem("WordRoom/Data/Check Package Against Python Reference")]
        public static void CheckPackage()
        {
            var package = Path.Combine(Application.streamingAssetsPath, PackageFormat.DefaultFolderName);
            var reference = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "DataValidation", "real", "python_reference.json"));
            var d = WordRoomDataLoader.Load(new DirectoryPackageSource(package));
            Debug.Log(d.LoadReport.ToString());
            if (File.Exists(reference)) Debug.Log(ReferenceComparer.Compare(d, ReferenceComparer.LoadReference(reference)).ToString());
            else Debug.LogWarning($"Python 기준값이 없습니다: {reference}");
            Debug.Log(DatasetBenchmark.Run(d).ToString());
        }

        [MenuItem("WordRoom/Data/Build Windows Data Check")]
        public static void BuildWindowsMenu() => BuildWindows(DefaultBuild);

        /// <summary>Batch mode: -executeMethod WordRoom.EditorTools.WordRoomDataTools.BatchBuildWindows [-wordroomBuildPath path.exe]</summary>
        public static void BatchBuildWindows()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-wordroomBuildPath");
            var path = i >= 0 && i + 1 < args.Length ? args[i + 1] : DefaultBuild;
            if (!File.Exists(ScenePath)) CreateDataCheckScene();
            var ok = BuildWindows(path);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        /// <summary>Batch mode: creates the scene only.</summary>
        public static void BatchCreateScene()
        {
            CreateDataCheckScene();
            EditorApplication.Exit(0);
        }

        public static bool BuildWindows(string path)
        {
            if (!File.Exists(ScenePath)) CreateDataCheckScene();
            var package = Path.Combine(Application.streamingAssetsPath, PackageFormat.DefaultFolderName, PackageFormat.Manifest);
            if (!File.Exists(package)) Debug.LogWarning($"데이터 패키지가 없어 빌드에 포함되지 않습니다: {package}");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"빌드 {s.result}: {s.outputPath} · {s.totalSize / 1048576.0:F1} MB · {s.totalTime.TotalSeconds:F0}초 · 오류 {s.totalErrors}");
            return s.result == BuildResult.Succeeded;
        }
    }
}
