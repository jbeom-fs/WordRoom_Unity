using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
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
    /// User-run tool (menu WordRoom/Shelf/독서실 프리팹·씬 구성): turns the files written by
    /// Tools/Blender/export_octagonal_reading_room.py into the reading-room prefabs and wires them into
    /// ShelfCorridorPrototype. Everything is measured from the export manifest and the imported models and checked
    /// before anything is saved; on a problem nothing is saved and the reason is logged. Running it again rebuilds the
    /// same assets in place (same paths and GUIDs, no duplicates in the scene).
    /// - Models: FBX import settings (no cameras, lights or animation; file scale) and their materials remapped to URP
    ///   Lit materials made from the manifest (linear Blender colors, roughness → smoothness, metallic, the lamp glass
    ///   emission) with the baked wood albedo textures (repeat, mipmaps).
    /// - OctagonalReadingRoom.prefab: the fixed room model, a floor collider at the center, eight wall colliders at
    ///   the bookcases, the furniture root, four room titles (gilt text in the arch band under the lamp of sides 0, 2,
    ///   4 and 6) and warm point/spot lights after the Blender lighting.
    /// - ReadingDesk.prefab (pivot on the floor) with its worktop anchor and body collider; ReadingStand.prefab (pivot
    ///   at the worktop) with a BookAnchor on the surface of its 32° panel (+X right, +Y uphill, -Z out of the panel)
    ///   and no text or collider of its own.
    /// - Books/OpenBook_01…08.prefab from Assets/Models/OctagonalReadingRoom/Books (their own materials): each model
    ///   turned so its underside center lies on the anchor (facing read from its geometry and UVs) with a box collider
    ///   fitted to the book, which is what the player aims at.
    /// - ShelfFadeCanvas.prefab: the black blackout panel over every shelf screen.
    /// - Scene: one room instance at the origin, one fade canvas, the crosshair on the demo object, the demo's
    ///   room/prefab/fade/crosshair references, the player at the center, warmer ambient light and a dimmer key light.
    ///   The scene is saved.
    /// Axes: side 0 must be Blender +Y (station 01) at Unity +Z. If the import puts Bookcase_01/03 elsewhere, the room
    /// model instance gets the matching 180° turn or one-axis mirror (logged; never on a prefab root). Furniture models
    /// have one root node whose import transform Unity drops, so their facing is read from their own geometry
    /// (drawer pulls toward the reader, panel rising toward the bookcase).
    /// </summary>
    public static class ShelfReadingRoomSetup
    {
        const string ModelFolder = "Assets/Models/OctagonalReadingRoom";
        const string RoomModelPath = ModelFolder + "/OctagonalReadingRoom_v1.fbx";
        const string DeskModelPath = ModelFolder + "/ReadingDesk_v1.fbx";
        const string StandModelPath = ModelFolder + "/ReadingStand_v1.fbx";
        const string ManifestPath = ModelFolder + "/OctagonalReadingRoom_v1.json";
        const string TextureFolder = ModelFolder + "/Textures";
        const string MaterialFolder = ModelFolder + "/Materials";
        const string PrefabFolder = "Assets/Prefabs/Shelf";
        public const string RoomPrefabPath = PrefabFolder + "/OctagonalReadingRoom.prefab";
        public const string DeskPrefabPath = PrefabFolder + "/ReadingDesk.prefab";
        public const string StandPrefabPath = PrefabFolder + "/ReadingStand.prefab";
        public const string FadePrefabPath = "Assets/Prefabs/UI/ShelfFadeCanvas.prefab";
        const string BookModelFolder = ModelFolder + "/Books";
        public const string BookPrefabFolder = PrefabFolder + "/Books";
        public const int BookCount = 8;
        const float WordAnchorClearance = 0.006f;          // the Blender WordAnchor floats this far above the panel
        const string ScenePath = "Assets/WordRoom/Scenes/ShelfCorridorPrototype.unity";
        const string FontPath = "Assets/Font/Pretendard-Black SDF.asset";
        const string LitShader = "Universal Render Pipeline/Lit";

        const int FadeSortingOrder = 400;                 // over the menu (200), reader (250) and result (300)
        const float WallThickness = 0.4f;
        const float WallInset = 0.035f;                   // wall collider face just inside the cabinet fronts
        const float TitleHeight = 2.645f;                 // open arch band under the lamp: books end at 2.59 m, crown starts at 2.71 m
        const float TitleRadius = 5.03f;                  // just in front of the arches and pilasters (5.07 m), behind the crown overhang
        static readonly Color Gilt = new Color32(0xE8, 0xC9, 0x8A, 0xFF);

        [Serializable] sealed class Point { public float x, y, z; public Vector3 V => new Vector3(x, y, z); }

        [Serializable]
        sealed class MaterialEntry
        {
            public string source, name, texture;
            public float[] baseColor, emission;
            public float roughness, metallic, emissionStrength;
        }

        [Serializable]
        sealed class Measurements
        {
            public float worktopHeight, deskRadius, deskWidth, deskDepth, deskHeight, standOffsetOnDesk;
            public float standWidth, standDepth, standHeight, panelTiltDegrees, textClearWidth, textClearHeight;
            public Point wordAnchor;
            public float bookcaseInnerApothem, floorTop, floorHalfExtent, ceilingHeight;
            public Point[] lamps, readingLights;
        }

        [Serializable]
        sealed class Manifest
        {
            public string source;
            public float grainU, grainV;
            public MaterialEntry[] materials;
            public Measurements measurements;
        }

        sealed class SetupError : Exception
        {
            public SetupError(string message) : base(message) { }
        }

        [MenuItem("WordRoom/Shelf/독서실 프리팹·씬 구성")]
        public static void Run()
        {
            try
            {
                Build();
            }
            catch (SetupError e)
            {
                Debug.LogError($"[독서실 구성] 중단: {e.Message}");
                throw;
            }
        }

        /// <summary>Batch entry (-executeMethod): exit code 0 on success, 1 on any problem.</summary>
        public static void RunBatch()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[독서실 구성] 실패: {e}");
                EditorApplication.Exit(1);
            }
        }

        static void Build()
        {
            var log = new StringBuilder();
            var manifest = LoadManifest();
            var m = manifest.measurements;
            var materials = BuildMaterials(manifest, log);
            ConfigureModel(RoomModelPath, materials);
            ConfigureModel(DeskModelPath, materials);
            ConfigureModel(StandModelPath, materials);

            var roomModel = Load<GameObject>(RoomModelPath);
            var deskModel = Load<GameObject>(DeskModelPath);
            var standModel = Load<GameObject>(StandModelPath);
            var font = Load<TMP_FontAsset>(FontPath);
            var fix = AxisFix(roomModel, m, log);

            EnsureFolder(PrefabFolder);
            BuildDesk(deskModel, FurnitureFix(deskModel, true, log), m, log);
            BuildStand(standModel, FurnitureFix(standModel, false, log), m, log);
            BuildBooks(log);
            BuildRoom(roomModel, fix, m, font, log);
            BuildFade(log);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            WireScene(log);
            AssetDatabase.SaveAssets();
            Debug.Log($"[독서실 구성] 완료\n{log}");
        }

        // ---- inputs ------------------------------------------------------------------------------------------------

        static Manifest LoadManifest()
        {
            if (!File.Exists(ManifestPath)) throw new SetupError($"{ManifestPath}가 없습니다. Tools/Blender/export_octagonal_reading_room.py를 먼저 실행하세요.");
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(ManifestPath, Encoding.UTF8));
            if (manifest?.measurements == null || manifest.materials == null || manifest.materials.Length == 0)
                throw new SetupError($"{ManifestPath}의 형식이 올바르지 않습니다");
            var m = manifest.measurements;
            Check(m.deskWidth > 1f && m.deskDepth > 0.4f && Mathf.Abs(m.deskHeight - m.worktopHeight) < 0.01f, $"책상 치수 이상: {m.deskWidth}×{m.deskDepth}×{m.deskHeight}");
            Check(m.standWidth > 0.3f && m.standWidth < m.deskWidth, $"독서대 폭 이상: {m.standWidth}");
            Check(m.bookcaseInnerApothem > m.deskRadius + m.deskDepth * 0.5f + 0.5f, $"책장({m.bookcaseInnerApothem})과 책상 뒤({m.deskRadius + m.deskDepth * 0.5f}) 사이 통로가 0.5m 미만");
            Check(m.wordAnchor != null, "WordAnchor 측정값 없음");
            return manifest;
        }

        static T Load<T>(string path) where T : Object
        {
            var asset = typeof(Component).IsAssignableFrom(typeof(T))
                ? AssetDatabase.LoadAssetAtPath<GameObject>(path)?.GetComponent(typeof(T)) as T
                : AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null) throw new SetupError($"{path}를 불러오지 못했습니다");
            return asset;
        }

        static void Check(bool ok, string message)
        {
            if (!ok) throw new SetupError(message);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ---- materials and models ----------------------------------------------------------------------------------

        static Dictionary<string, Material> BuildMaterials(Manifest manifest, StringBuilder log)
        {
            var shader = Shader.Find(LitShader);
            if (shader == null) throw new SetupError($"셰이더 {LitShader}를 찾지 못했습니다");
            EnsureFolder(MaterialFolder);
            var result = new Dictionary<string, Material>();
            var textured = 0;
            foreach (var e in manifest.materials)
            {
                Texture2D texture = null;
                if (!string.IsNullOrEmpty(e.texture))
                {
                    var texPath = $"{TextureFolder}/{e.texture}";
                    var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
                    if (importer == null) throw new SetupError($"결 텍스처 {texPath}가 없습니다");
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = true;
                    importer.wrapMode = TextureWrapMode.Repeat;
                    importer.mipmapEnabled = true;
                    importer.anisoLevel = 8;
                    importer.filterMode = FilterMode.Trilinear;
                    importer.maxTextureSize = 2048;
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    importer.alphaSource = TextureImporterAlphaSource.None;
                    importer.SaveAndReimport();
                    texture = Load<Texture2D>(texPath);
                    textured++;
                }
                var path = $"{MaterialFolder}/{e.name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                var created = mat == null;
                if (created) mat = new Material(shader) { name = e.name };
                else mat.shader = shader;
                var linear = new Color(e.baseColor[0], e.baseColor[1], e.baseColor[2], 1f);
                mat.SetColor("_BaseColor", texture != null ? Color.white : linear.gamma);
                mat.SetTexture("_BaseMap", texture);
                mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - e.roughness));
                mat.SetFloat("_Metallic", Mathf.Clamp01(e.metallic));
                if (e.emissionStrength > 0f && e.emission != null && e.emission.Length >= 3)
                {
                    var em = new Color(e.emission[0], e.emission[1], e.emission[2], 1f).gamma * (e.emissionStrength * 0.6f);
                    mat.SetColor("_EmissionColor", em);
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                }
                else
                {
                    mat.SetColor("_EmissionColor", Color.black);
                    mat.DisableKeyword("_EMISSION");
                }
                if (created) AssetDatabase.CreateAsset(mat, path);
                else EditorUtility.SetDirty(mat);
                result[e.name] = mat;
            }
            log.AppendLine($"- 재질 {result.Count}개(결 텍스처 {textured}개) → {MaterialFolder}");
            return result;
        }

        static void ConfigureModel(string path, Dictionary<string, Material> materials)
        {
            if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) throw new SetupError($"{path}가 없거나 모델이 아닙니다");
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importBlendShapes = false;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.None;
            importer.generateSecondaryUV = false;
            importer.addCollider = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
            foreach (var pair in materials)
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), pair.Key), pair.Value);
            importer.SaveAndReimport();

            var model = Load<GameObject>(path);
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                foreach (var mat in r.sharedMaterials)
                    if (mat == null || AssetDatabase.GetAssetPath(mat) == path)
                        throw new SetupError($"{path}의 '{r.name}' 재질 '{(mat != null ? mat.name : "없음")}'이 매니페스트 재질로 연결되지 않았습니다");
        }

        /// <summary>
        /// Model-instance correction that puts Bookcase_01 (Blender +Y) at +Z and Bookcase_03 (Blender +X) at +X:
        /// identity normally; a 180° turn or a one-axis mirror when the importer placed them elsewhere.
        /// </summary>
        static Matrix4x4 AxisFix(GameObject roomModel, Measurements m, StringBuilder log)
        {
            var c1 = CenterOf(roomModel, "Bookcase_01");
            var c3 = CenterOf(roomModel, "Bookcase_03");
            var front = c1.z;
            var right = c3.x;
            Check(Mathf.Abs(front) > 4f && Mathf.Abs(right) > 4f, $"책장 위치를 판별하지 못했습니다(Bookcase_01 {c1}, Bookcase_03 {c3})");
            Matrix4x4 fix;
            string what;
            if (front > 0f && right > 0f) { fix = Matrix4x4.identity; what = "그대로"; }
            else if (front < 0f && right < 0f) { fix = Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f)); what = "Y 180° 회전"; }
            else if (front > 0f) { fix = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f)); what = "X 반전"; }
            else { fix = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)); what = "Z 반전"; }
            var bounds = BoundsOf(roomModel, Matrix4x4.identity);
            log.AppendLine($"- 방 모델 축: Bookcase_01 {c1:F2}, Bookcase_03 {c3:F2} → {what} · 크기 {bounds.size:F2}");
            Check(Mathf.Abs(bounds.max.y - m.ceilingHeight) < 0.1f, $"방 모델 위끝 {bounds.max.y:F2}m가 천장 높이 {m.ceilingHeight}m와 다릅니다(단위 확인)");
            Check(Mathf.Abs(bounds.extents.x - 5.565f) < 0.15f, $"방 모델 반폭 {bounds.extents.x:F2}m가 원본(5.57m)과 다릅니다(단위 확인)");
            return fix;
        }

        /// <summary>
        /// Furniture models have a single root node, whose import transform Unity drops, so their axes can differ from
        /// the room's. Facing is read from the geometry: the desk's brass drawer pulls are on the reader side (-Z), the
        /// stand's panel rises toward the bookcase (+Z). Both are symmetric left to right, so a turn is all they need.
        /// </summary>
        static Matrix4x4 FurnitureFix(GameObject model, bool isDesk, StringBuilder log)
        {
            var toRoot = model.transform.worldToLocalMatrix;
            float sum = 0f;
            var count = 0;
            if (isDesk)
            {
                foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh;
                    var mats = mf.GetComponent<MeshRenderer>().sharedMaterials;
                    var tr = toRoot * mf.transform.localToWorldMatrix;
                    var verts = mesh.vertices;
                    for (var s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
                    {
                        if (mats[s] == null || !mats[s].name.StartsWith("Metal_")) continue;
                        foreach (var i in mesh.GetIndices(s))
                        {
                            sum += tr.MultiplyPoint3x4(verts[i]).z;
                            count++;
                        }
                    }
                }
            }
            else
            {
                var top = float.MinValue;
                var points = new List<Vector3>();
                foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    var tr = toRoot * mf.transform.localToWorldMatrix;
                    foreach (var v in mf.sharedMesh.vertices)
                    {
                        var p = tr.MultiplyPoint3x4(v);
                        points.Add(p);
                        top = Mathf.Max(top, p.y);
                    }
                }
                foreach (var p in points)
                    if (p.y > top - 0.01f)
                    {
                        sum += p.z;
                        count++;
                    }
            }
            Check(count > 0, $"{model.name}: 방향을 판별할 형상을 찾지 못했습니다");
            var z = sum / count;
            var turn = isDesk ? z > 0f : z < 0f;
            log.AppendLine($"- {model.name} 축: {(isDesk ? "서랍 손잡이" : "판 위쪽 모서리")} 평균 Z {z:F3} → {(turn ? "Y 180° 회전" : "그대로")}");
            return turn ? Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f)) : Matrix4x4.identity;
        }

        /// <summary>Mesh-bounds center of the named child in the model root's space (works on the asset, not in a scene).</summary>
        static Vector3 CenterOf(GameObject model, string child)
        {
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                if (mf.name == child && mf.sharedMesh != null)
                    return (model.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix).MultiplyPoint3x4(mf.sharedMesh.bounds.center);
            throw new SetupError($"모델에서 '{child}'를 찾지 못했습니다");
        }

        /// <summary>Renderer bounds of `root`'s children in `root` space (through `space`).</summary>
        static Bounds BoundsOf(GameObject root, Matrix4x4 space)
        {
            var any = false;
            var b = new Bounds();
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var toRoot = space * root.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = toRoot.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                    else b.Encapsulate(p);
                }
            }
            if (!any) throw new SetupError($"'{root.name}'에 메시가 없습니다");
            return b;
        }

        static GameObject AddModel(Transform parent, GameObject model, Matrix4x4 fix, bool isStatic)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.name = "Model";
            instance.transform.SetParent(parent, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = fix.rotation;
            instance.transform.localScale = fix.lossyScale;
            if (isStatic)
                foreach (var t in instance.GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(t.gameObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            return instance;
        }

        static T SavePrefab<T>(GameObject root, string path) where T : Component
        {
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path, out var ok);
            Object.DestroyImmediate(root);
            if (!ok || saved == null) throw new SetupError($"{path} 저장 실패");
            return saved.GetComponent<T>();
        }

        // ---- furniture ---------------------------------------------------------------------------------------------

        static ShelfDesk BuildDesk(GameObject model, Matrix4x4 fix, Measurements m, StringBuilder log)
        {
            var root = new GameObject("ReadingDesk");
            var view = AddModel(root.transform, model, fix, false);
            var b = BoundsOf(root, Matrix4x4.identity);
            Check(Mathf.Abs(b.size.x - m.deskWidth) < 0.03f && Mathf.Abs(b.size.z - m.deskDepth) < 0.03f && Mathf.Abs(b.max.y - m.worktopHeight) < 0.02f && Mathf.Abs(b.min.y) < 0.02f,
                $"책상 모델 크기 {b.size:F3}(바닥 {b.min.y:F3}, 상판 {b.max.y:F3})가 원본 {m.deskWidth:F3}×{m.worktopHeight:F3}×{m.deskDepth:F3}과 다릅니다");
            var top = new GameObject("Top").transform;
            top.SetParent(root.transform, false);
            top.localPosition = new Vector3(0f, m.worktopHeight, m.standOffsetOnDesk);
            var body = root.AddComponent<BoxCollider>();
            body.center = b.center;
            body.size = b.size;
            var desk = root.AddComponent<ShelfDesk>();
            desk.EditorSetup(view.transform, top, body, m.deskWidth, m.deskDepth);
            log.AppendLine($"- 책상 프리팹: 상판 {m.deskWidth:F2}×{m.deskDepth:F2}m, 높이 {m.worktopHeight:F2}m, 독서대 기준점 상판 중앙(+Z {m.standOffsetOnDesk:F3}m)");
            return SavePrefab<ShelfDesk>(root, DeskPrefabPath);
        }

        static ShelfLectern BuildStand(GameObject model, Matrix4x4 fix, Measurements m, StringBuilder log)
        {
            var root = new GameObject("ReadingStand");
            var view = AddModel(root.transform, model, fix, false);
            var b = BoundsOf(root, Matrix4x4.identity);
            Check(Mathf.Abs(b.size.x - m.standWidth) < 0.02f && Mathf.Abs(b.min.y) < 0.02f && Mathf.Abs(b.max.y - m.standHeight) < 0.02f,
                $"독서대 모델 크기 {b.size:F3}(아래 {b.min.y:F3}, 위 {b.max.y:F3})가 원본 폭 {m.standWidth:F3}, 높이 {m.standHeight:F3}와 다릅니다");

            var anchor = FindChild(view.transform, "WordAnchor");
            var expected = m.wordAnchor.V;                               // manifest values are in the corrected frame
            if (anchor != null)
            {
                var at = root.transform.InverseTransformPoint(anchor.position);
                Check((at - expected).magnitude < 0.01f, $"WordAnchor 위치 {at:F4}가 원본 {expected:F4}와 다릅니다");
            }

            // The book rests on the panel surface: the WordAnchor point minus its clearance, along the panel normal.
            // Frame: +X right, +Y uphill (toward the bookcase), -Z out of the panel (the normal, toward the reader).
            var tilt = m.panelTiltDegrees * Mathf.Deg2Rad;
            var normal = new Vector3(0f, Mathf.Cos(tilt), -Mathf.Sin(tilt));
            var bookAnchor = new GameObject("BookAnchor").transform;
            bookAnchor.SetParent(root.transform, false);
            bookAnchor.SetLocalPositionAndRotation(expected - normal * WordAnchorClearance, Quaternion.Euler(90f - m.panelTiltDegrees, 0f, 0f));
            Check((bookAnchor.localRotation * Vector3.back - normal).magnitude < 1e-3f, "책 기준점의 -Z가 판의 법선과 다릅니다");

            var lectern = root.AddComponent<ShelfLectern>();       // no collider: only the book is aimed at
            lectern.EditorSetup(bookAnchor, m.standWidth);
            log.AppendLine($"- 독서대 프리팹: 폭 {m.standWidth:F3}m, 높이 {m.standHeight:F3}m, 판 {m.panelTiltDegrees:F0}°, 책 기준점 {bookAnchor.localPosition:F4}(글자·조준 Collider 없음)");
            return SavePrefab<ShelfLectern>(root, StandPrefabPath);
        }

        /// <summary>
        /// One prefab per open-book model: the model turned so its underside center sits at the root and its axes match
        /// the stand's book anchor (+X right, +Y head, -Z thickness), and a box collider fitted to the book for aiming.
        /// The materials are the models' own (already remapped in their import settings).
        /// </summary>
        static void BuildBooks(StringBuilder log)
        {
            EnsureFolder(BookPrefabFolder);
            for (var i = 0; i < BookCount; i++)
            {
                var path = $"{BookModelFolder}/OpenBook_{i + 1:00}.fbx";
                var model = Load<GameObject>(path);
                foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                    foreach (var mat in r.sharedMaterials)
                        if (mat == null) throw new SetupError($"{path}의 '{r.name}'에 재질이 없습니다");
                var frame = BookFrame(model, out var contact, out var report);

                var root = new GameObject($"OpenBook_{i + 1:00}");
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
                inst.name = "Model";
                inst.transform.SetParent(root.transform, false);
                inst.transform.localRotation = frame;
                inst.transform.localPosition = -(frame * contact);
                var b = BoundsOf(root, Matrix4x4.identity);
                Check(Mathf.Abs(b.max.z) < 0.002f && b.min.z < -0.01f && Mathf.Abs(b.center.x) < 0.01f && Mathf.Abs(b.center.y) < 0.01f,
                    $"{path}: 밑면 중심을 맞추지 못했습니다(경계 {b.min:F4}~{b.max:F4})");
                Check(b.size.x > b.size.y && b.size.x < 0.62f && b.size.y < 0.278f,
                    $"{path}: 크기 {b.size:F3}가 독서대 판(0.62×0.28m)에 맞지 않습니다");

                var aim = root.AddComponent<BoxCollider>();
                var depth = Mathf.Max(b.size.z + 0.01f, 0.04f);                   // out of the panel, slightly into it
                aim.size = new Vector3(b.size.x + 0.02f, b.size.y + 0.02f, depth);
                aim.center = new Vector3(b.center.x, b.center.y, 0.004f - depth * 0.5f);
                var book = root.AddComponent<ShelfBook>();
                book.EditorSetup(i, aim);
                SavePrefab<ShelfBook>(root, $"{BookPrefabFolder}/OpenBook_{i + 1:00}.prefab");
                log.AppendLine($"- 책 {i + 1:00}: {b.size.x:F3}×{b.size.y:F3}×{-b.min.z:F4}m · {report}");
            }
        }

        /// <summary>
        /// Rotation taking the model's own axes to the book frame (+X right, +Y head, -Z thickness) and the underside
        /// center in model space. Thickness is the shortest extent, pointing away from the side the pivot plane is on;
        /// right is the long in-plane axis, its sign from the UVs (u grows to the right in all eight models, v toward the
        /// head), so a turned import is found; a mirrored one is refused.
        /// </summary>
        static Quaternion BookFrame(GameObject model, out Vector3 contact, out string report)
        {
            var toRoot = model.transform.worldToLocalMatrix;
            var points = new List<Vector3>();
            var uvs = new List<Vector2>();
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var tr = toRoot * mf.transform.localToWorldMatrix;
                var v = mesh.vertices;
                var uv = mesh.uv;
                Check(uv.Length == v.Length, $"{model.name}: UV가 없습니다");
                for (var k = 0; k < v.Length; k++)
                {
                    points.Add(tr.MultiplyPoint3x4(v[k]));
                    uvs.Add(uv[k]);
                }
            }
            Check(points.Count > 0, $"{model.name}: 메시가 없습니다");
            var min = points[0];
            var max = points[0];
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var size = max - min;
            int thin = 0, wide = 0;
            for (var a = 1; a < 3; a++)
            {
                if (size[a] < size[thin]) thin = a;
                if (size[a] > size[wide]) wide = a;
            }
            Check(thin != wide, $"{model.name}: 축을 판별하지 못했습니다");
            var normal = Axis(thin) * (Mathf.Abs(min[thin]) < Mathf.Abs(max[thin]) ? 1f : -1f);
            var right = Axis(wide) * (Correlation(points, uvs, wide, true) >= 0f ? 1f : -1f);
            var head = Vector3.Cross(right, normal);
            var headAxis = 3 - thin - wide;
            var vHead = Correlation(points, uvs, headAxis, false) * Vector3.Dot(head, Axis(headAxis));
            Check(vHead > 0f, $"{model.name}: UV v가 머리 쪽과 반대입니다(좌우가 뒤집힌 가져오기)");
            var center = (min + max) * 0.5f;
            contact = center - normal * (size[thin] * 0.5f);
            report = $"두께 {normal}, 오른쪽 {right}, 머리 {head}";
            return Quaternion.Inverse(Quaternion.LookRotation(-normal, head));
        }

        static Vector3 Axis(int a) => a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward;

        /// <summary>Pearson correlation of a UV coordinate (u or v) with one position axis.</summary>
        static float Correlation(List<Vector3> points, List<Vector2> uvs, int axis, bool u)
        {
            double mp = 0, mu = 0;
            for (var k = 0; k < points.Count; k++)
            {
                mp += points[k][axis];
                mu += u ? uvs[k].x : uvs[k].y;
            }
            mp /= points.Count;
            mu /= points.Count;
            double num = 0, dp = 0, du = 0;
            for (var k = 0; k < points.Count; k++)
            {
                var a = points[k][axis] - mp;
                var b = (u ? uvs[k].x : uvs[k].y) - mu;
                num += a * b;
                dp += a * a;
                du += b * b;
            }
            return dp > 0 && du > 0 ? (float)(num / System.Math.Sqrt(dp * du)) : 0f;
        }

        static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        // ---- room ----------------------------------------------------------------------------------------------------

        static ShelfRoomView BuildRoom(GameObject model, Matrix4x4 fix, Measurements m, TMP_FontAsset font, StringBuilder log)
        {
            var root = new GameObject("OctagonalReadingRoom");
            AddModel(root.transform, model, fix, true);

            var floor = new GameObject("Floor").transform;
            floor.SetParent(root.transform, false);
            var outer = 5.6f;                                            // past the bookcase backs (5.565 m)
            var floorBox = floor.gameObject.AddComponent<BoxCollider>();
            floorBox.center = new Vector3(0f, m.floorTop - 0.1f, 0f);
            floorBox.size = new Vector3(2f * outer, 0.2f, 2f * outer);

            var walls = new GameObject("Walls").transform;
            walls.SetParent(root.transform, false);
            var face = m.bookcaseInnerApothem + WallInset;
            var length = 2f * (face + WallThickness) * Mathf.Tan(ShelfLayout.HalfStep * Mathf.Deg2Rad) + 0.05f;
            for (var s = 0; s < ShelfFurniturePlan.Sides; s++)
            {
                var dir = ShelfRoomView.SideDirection(s);
                var wall = new GameObject($"Wall_{s}").transform;
                wall.SetParent(walls, false);
                wall.SetLocalPositionAndRotation(dir * (face + WallThickness * 0.5f) + Vector3.up * (m.ceilingHeight * 0.5f), Quaternion.LookRotation(dir));
                var box = wall.gameObject.AddComponent<BoxCollider>();
                box.size = new Vector3(length, m.ceilingHeight, WallThickness);
            }

            var furniture = new GameObject("Furniture").transform;
            furniture.SetParent(root.transform, false);

            var titleRoot = new GameObject("Titles").transform;
            titleRoot.SetParent(root.transform, false);
            var titles = new List<TMP_Text>();
            foreach (var side in new[] { 0, 2, 4, 6 })
            {
                var dir = ShelfRoomView.SideDirection(side);
                {
                    var go = new GameObject($"Title_{side}", typeof(RectTransform));
                    go.transform.SetParent(titleRoot, false);
                    var t = go.AddComponent<TextMeshPro>();
                    t.rectTransform.SetLocalPositionAndRotation(dir * TitleRadius + Vector3.up * TitleHeight, Quaternion.LookRotation(dir));
                    t.rectTransform.sizeDelta = new Vector2(1.3f, 0.25f);
                    t.font = font;
                    t.text = "";
                    t.color = Gilt;
                    t.richText = false;
                    t.alignment = TextAlignmentOptions.Center;
                    t.textWrappingMode = TextWrappingModes.NoWrap;
                    t.overflowMode = TextOverflowModes.Ellipsis;
                    t.enableAutoSizing = true;
                    t.fontSizeMax = 2.2f;                                      // about 9 cm letters
                    t.fontSizeMin = 1f;
                    t.fontSize = 2.2f;
                    var r = t.GetComponent<MeshRenderer>();
                    if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    titles.Add(t);
                }
            }

            var lights = new GameObject("Lights").transform;
            lights.SetParent(root.transform, false);
            var reading = m.readingLights ?? new Point[0];
            for (var i = 0; i < reading.Length; i++)
            {
                var p = reading[i].V;
                var flat = new Vector3(p.x, 0f, p.z);
                AddLight(lights, $"Reading_{i + 1:00}", LightType.Point, flat + Vector3.up * (m.ceilingHeight - 0.35f), Quaternion.identity,
                    new Color(1f, 0.87f, 0.7f), 1.1f, 4.5f, 0f);
            }
            var lamps = m.lamps ?? new Point[0];
            for (var i = 0; i < lamps.Length; i++)
            {
                var p = lamps[i].V;
                var inward = new Vector3(-p.x, 0f, -p.z).normalized;
                var at = p + inward * 0.25f - Vector3.up * 0.12f;
                var aimAt = new Vector3(p.x, 1.3f, p.z);
                AddLight(lights, $"ShelfWash_{i + 1:00}", LightType.Spot, at, Quaternion.LookRotation(aimAt - at), new Color(1f, 0.76f, 0.5f), 2.2f, 3.6f, 110f);
            }

            var view = root.AddComponent<ShelfRoomView>();
            view.EditorSetup(floor, furniture, titles.ToArray(), m.deskRadius, m.bookcaseInnerApothem);
            log.AppendLine($"- 방 프리팹: 책장 안쪽 {m.bookcaseInnerApothem:F3}m, 책상 반경 {m.deskRadius:F2}m, 벽 Collider 8개, 제목 {titles.Count}개, 조명 {reading.Length + lamps.Length}개");
            return SavePrefab<ShelfRoomView>(root, RoomPrefabPath);
        }

        static void AddLight(Transform parent, string name, LightType type, Vector3 position, Quaternion rotation, Color color, float intensity, float range, float spotAngle)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(position, rotation);
            var light = go.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            if (type == LightType.Spot)
            {
                light.spotAngle = spotAngle;
                light.innerSpotAngle = spotAngle * 0.5f;
            }
            light.shadows = LightShadows.None;
            light.lightmapBakeType = LightmapBakeType.Realtime;
        }

        // ---- blackout ------------------------------------------------------------------------------------------------

        static ShelfScreenFade BuildFade(StringBuilder log)
        {
            EnsureFolder(Path.GetDirectoryName(FadePrefabPath)?.Replace('\\', '/'));
            var root = new GameObject("Shelf Fade Canvas", typeof(RectTransform));
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = FadeSortingOrder;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            root.AddComponent<GraphicRaycaster>();
            var group = root.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
            var black = new GameObject("Black", typeof(RectTransform));
            black.transform.SetParent(root.transform, false);
            var rt = (RectTransform)black.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var image = black.AddComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = true;
            var fade = root.AddComponent<ShelfScreenFade>();
            fade.EditorSetup(group, canvas);
            canvas.enabled = false;                                        // clear until a transition
            log.AppendLine($"- 암전 화면 프리팹: 정렬 {FadeSortingOrder}(메뉴·읽기·결과 위)");
            return SavePrefab<ShelfScreenFade>(root, FadePrefabPath);
        }

        // ---- scene ---------------------------------------------------------------------------------------------------

        static void WireScene(StringBuilder log)
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            // Loaded after the scene: opening it unloads unused assets, prefabs loaded before included.
            var roomPrefab = Load<ShelfRoomView>(RoomPrefabPath);
            var deskPrefab = Load<ShelfDesk>(DeskPrefabPath);
            var standPrefab = Load<ShelfLectern>(StandPrefabPath);
            var fadePrefab = Load<ShelfScreenFade>(FadePrefabPath);
            var demo = Object.FindFirstObjectByType<ShelfCorridorDemo>(FindObjectsInactive.Include);
            if (demo == null) throw new SetupError($"{ScenePath}에 ShelfCorridorDemo가 없습니다");

            var rooms = Object.FindObjectsByType<ShelfRoomView>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            ShelfRoomView room = null;
            foreach (var r in rooms)
            {
                if (room == null && PrefabUtility.GetCorrespondingObjectFromSource(r) == roomPrefab) room = r;
                else Object.DestroyImmediate(r.gameObject);               // one room only
            }
            if (room == null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(roomPrefab.gameObject, scene);
                room = go.GetComponent<ShelfRoomView>();
            }
            room.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            room.transform.localScale = Vector3.one;

            var fades = Object.FindObjectsByType<ShelfScreenFade>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            ShelfScreenFade fade = null;
            foreach (var f in fades)
            {
                if (fade == null && PrefabUtility.GetCorrespondingObjectFromSource(f) == fadePrefab) fade = f;
                else Object.DestroyImmediate(f.gameObject);
            }
            if (fade == null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(fadePrefab.gameObject, scene);
                fade = go.GetComponent<ShelfScreenFade>();
            }

            var so = new SerializedObject(demo);
            Set(so, "roomView", room);
            Set(so, "deskPrefab", deskPrefab);
            Set(so, "lecternPrefab", standPrefab);
            var books = so.FindProperty("bookPrefabs");
            if (books == null) throw new SetupError("ShelfCorridorDemo에 'bookPrefabs' 필드가 없습니다");
            books.arraySize = BookCount;
            for (var i = 0; i < BookCount; i++)
                books.GetArrayElementAtIndex(i).objectReferenceValue = Load<ShelfBook>($"{BookPrefabFolder}/OpenBook_{i + 1:00}.prefab");
            Set(so, "fade", fade);
            var crosshair = demo.GetComponent<ShelfCrosshair>();
            if (crosshair == null) crosshair = demo.gameObject.AddComponent<ShelfCrosshair>();
            Set(so, "crosshair", crosshair);
            so.ApplyModifiedPropertiesWithoutUndo();

            var player = Object.FindFirstObjectByType<FirstPersonPlayer>(FindObjectsInactive.Include);
            if (player != null) player.transform.SetPositionAndRotation(new Vector3(0f, 0.05f, 0f), Quaternion.identity);   // room center, facing side 0

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.30f, 0.25f, 0.20f);
            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;   // no blue sky on the oiled wood
            RenderSettings.customReflectionTexture = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (l.type == LightType.Directional && !l.transform.IsChildOf(room.transform)) l.intensity = 0.35f;   // the room is lit by its own lamps

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new SetupError($"{ScenePath} 저장 실패");
            log.AppendLine($"- 씬: 독서실 1개(원점), 암전 화면 1개, ShelfCorridorDemo 참조 연결, 플레이어 중앙 시작, 환경광·주광 조정 → {ScenePath}");
        }

        static void Set(SerializedObject so, string field, Object value)
        {
            var p = so.FindProperty(field);
            if (p == null) throw new SetupError($"ShelfCorridorDemo에 '{field}' 필드가 없습니다");
            p.objectReferenceValue = value;
        }
    }
}
