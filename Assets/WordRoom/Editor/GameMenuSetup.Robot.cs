using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WordRoom.RoomDebug;
using WordRoom.World;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    public static partial class GameMenuSetup
    {
        const string RobotModelPath = "Assets/Models/ArchiveRobot_v1.fbx";
        const string RobotPrefabPath = "Assets/Prefabs/ArchiveRobotRemote.prefab";
        const string RobotMaterialFolder = "Assets/Models/ArchiveMaterials";
        const string RobotShader = "Universal Render Pipeline/Lit";
        const string RobotVisualName = "Visual";

        // The model report (Blender): look height 1.62m, floating 0.18m above the floor. Used only to tell a plausible
        // import from a scaled one and to lift a model that sits below the floor — the imported values are measured.
        const float RobotReportHeight = 1.62f;
        const float RobotReportHover = 0.18f;

        /// <summary>The robot's material slots (names used in the model) and their URP Lit look: ivory shell, brass trim, dark joints, dark screen, glowing accent.</summary>
        static readonly (string Slot, Color32 Color, float Metallic, float Smoothness, float Emission)[] RobotPalette =
        {
            ("Ivory", new Color32(0xE6, 0xDD, 0xC8, 0xFF), 0f, 0.45f, 0f),
            ("Brass", new Color32(0xA8, 0x87, 0x4B, 0xFF), 0.85f, 0.6f, 0f),
            ("Dark", new Color32(0x2B, 0x24, 0x20, 0xFF), 0.2f, 0.35f, 0f),
            ("Screen", new Color32(0x12, 0x18, 0x19, 0xFF), 0f, 0.85f, 0f),
            ("Accent", new Color32(0x72, 0xFF, 0xD5, 0xFF), 0f, 0.5f, 1.5f),   // each participant's color replaces it per robot (RemotePlayers)
        };

        /// <summary>
        /// The other coop participants' robot (user-run; nothing here runs by itself). 1) Reads the imported
        /// ArchiveRobot_v1.fbx (never changed or re-imported): hierarchy, renderers, triangles, material slots; a camera,
        /// light or floor-like part stops the tool (the model is not used then). 2) URP Lit materials ArchiveRobot_* in
        /// Assets/Models/ArchiveMaterials, made only when missing (existing ones are used as they are). 3) The display
        /// prefab Assets/Prefabs/ArchiveRobotRemote.prefab, built only when missing: root = feet, +Z front, the model under
        /// its Visual child with the materials put on the slots by name; size, facing (the Screen slot is the front) and
        /// centering are measured and corrected on Visual only; no Animator, collider or camera. An existing prefab is used
        /// as it is (problems are reported). 4) Gameplay scene: one RemotePlayers on the RoomGenerationDebug object (added
        /// when there is none) and only its empty references filled (prefab, Accent material, the game's remotePlayers);
        /// different references already set are reported and kept. The player, its camera and controller are not touched.
        /// </summary>
        [MenuItem("WordRoom/협동 로봇 외형 구성")]
        public static void SetupRobot()
        {
            if (!File.Exists(GameplayPath))
            {
                Debug.LogError($"[로봇 구성] {GameplayPath}가 없습니다. 먼저 WordRoom/메인 메뉴와 게임 설정 구성을 실행하세요(이 메뉴는 씬을 만들지 않음).");
                return;
            }
            Run("로봇 구성", (font, notes, saved) => ConfigureRobot(notes, saved));
        }

        static void ConfigureRobot(List<string> notes, List<string> saved)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(RobotModelPath) ?? throw new InvalidOperationException($"{RobotModelPath}가 없거나 모델로 가져오지 못했습니다");
            CheckRobotModel(model, notes);
            var materials = EnsureRobotMaterials(notes, saved);
            var prefab = LoadOrBuildRobot(model, materials, notes, saved);
            ConnectRobot(prefab, materials["Accent"], notes, saved);
        }

        /// <summary>What the import really holds; anything that is not the robot (camera, light, floor) stops the tool.</summary>
        static void CheckRobotModel(GameObject model, List<string> notes)
        {
            var cameras = model.GetComponentsInChildren<Camera>(true).Length;
            var lights = model.GetComponentsInChildren<Light>(true).Length;
            var floors = model.GetComponentsInChildren<Transform>(true)
                .Where(t => t != model.transform && new[] { "floor", "ground", "plane", "backdrop", "stage" }.Any(w => t.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(t => t.name).ToList();
            if (cameras > 0 || lights > 0 || floors.Count > 0)
                throw new InvalidOperationException($"{RobotModelPath}에 로봇이 아닌 대상(카메라 {cameras}, 조명 {lights}{(floors.Count > 0 ? $", 바닥으로 보이는 {string.Join(", ", floors)}" : "")})이 있어 외형으로 쓰지 않았습니다. " +
                                                    "로봇만 다시 내보내거나 FBX 가져오기의 카메라·조명 가져오기를 확인한 뒤 다시 실행하세요");
            var renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException($"{RobotModelPath}에 Renderer가 없습니다");
            long triangles = 0;
            foreach (var f in model.GetComponentsInChildren<MeshFilter>(true))
                if (f.sharedMesh != null)
                    for (var i = 0; i < f.sharedMesh.subMeshCount; i++) triangles += f.sharedMesh.GetIndexCount(i) / 3;
            var slots = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Select(m => m.name).Distinct().OrderBy(n => n);
            var animators = model.GetComponentsInChildren<Animator>(true).Length;
            var colliders = model.GetComponentsInChildren<Collider>(true).Length;
            notes.Add($"{RobotModelPath}: Renderer {renderers.Length}개, 삼각형 {triangles}개, 재질 슬롯 {string.Join(", ", slots)}" +
                      (animators > 0 ? $", Animator {animators}개(표시 프리팹에서 제거)" : "") + (colliders > 0 ? $", Collider {colliders}개(표시 프리팹에서 제거)" : ""));
        }

        /// <summary>URP Lit materials of the robot, made only when missing; an existing one is used as it is (never re-set).</summary>
        static Dictionary<string, Material> EnsureRobotMaterials(List<string> notes, List<string> saved)
        {
            var shader = Shader.Find(RobotShader) ?? throw new InvalidOperationException($"셰이더 '{RobotShader}'를 찾지 못했습니다(URP 확인)");
            if (!AssetDatabase.IsValidFolder(RobotMaterialFolder)) AssetDatabase.CreateFolder(Path.GetDirectoryName(RobotMaterialFolder).Replace('\\', '/'), Path.GetFileName(RobotMaterialFolder));
            var result = new Dictionary<string, Material>();
            foreach (var (slot, color, metallic, smoothness, emission) in RobotPalette)
            {
                var path = $"{RobotMaterialFolder}/ArchiveRobot_{slot}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(shader) { name = $"ArchiveRobot_{slot}" };
                    mat.SetColor("_BaseColor", color);
                    mat.SetFloat("_Metallic", metallic);
                    mat.SetFloat("_Smoothness", smoothness);
                    if (emission > 0f)
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", (Color)color * emission);
                        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;   // a glow only
                    }
                    AssetDatabase.CreateAsset(mat, path);
                    saved.Add(path);
                    notes.Add($"{path} 생성(URP Lit)");
                }
                result[slot] = mat;
            }
            return result;
        }

        /// <summary>The slot key of an imported material: its name without the model's prefix (e.g. ArchiveRobot_Brass → Brass).</summary>
        static string RobotSlot(Material m)
        {
            if (m == null) return null;
            var name = m.name.Replace(" (Instance)", "");
            var at = name.IndexOf("ArchiveRobot_", StringComparison.Ordinal);
            return at >= 0 ? name.Substring(at + "ArchiveRobot_".Length) : name;
        }

        /// <summary>
        /// The display prefab as it is when it exists (checked and reported only), otherwise built in a preview scene (no
        /// project scene touched): measured after import and corrected on Visual only.
        /// </summary>
        static GameObject LoadOrBuildRobot(GameObject modelAsset, Dictionary<string, Material> materials, List<string> notes, List<string> saved)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(RobotPrefabPath);
            if (existing != null)
            {
                if (existing.transform.Find(RobotVisualName) == null)
                    notes.Add($"{Warning}{RobotPrefabPath}에 {RobotVisualName} 자식이 없습니다(기존 프리팹은 바꾸지 않음). 발밑 원점·+Z 정면인지 직접 확인하세요");
                var extra = existing.GetComponentsInChildren<Collider>(true).Length + existing.GetComponentsInChildren<Rigidbody>(true).Length +
                            existing.GetComponentsInChildren<Camera>(true).Length + existing.GetComponentsInChildren<Light>(true).Length +
                            existing.GetComponentsInChildren<AudioListener>(true).Length;
                if (extra > 0) notes.Add($"{Warning}{RobotPrefabPath}에 충돌·카메라·조명 등 {extra}개가 있습니다(실행 중 인스턴스에서는 제거됨). 프리팹에서 직접 정리하세요");
                notes.Add($"{RobotPrefabPath}: 기존 표시 프리팹 사용(다시 만들지 않음)");
                return existing;
            }

            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("ArchiveRobotRemote");
                SceneManager.MoveGameObjectToScene(root, preview);
                var visual = new GameObject(RobotVisualName).transform;
                visual.SetParent(root.transform, false);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset, preview);
                model.transform.SetParent(visual, false);
                foreach (var a in model.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(a);   // no rig or animation is used
                foreach (var c in model.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);   // players pass through each other

                var unmatched = new SortedSet<string>();
                Renderer[] screens = null;
                foreach (var r in model.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    for (var i = 0; i < mats.Length; i++)
                    {
                        var slot = RobotSlot(mats[i]);
                        if (slot != null && materials.TryGetValue(slot, out var mat)) mats[i] = mat;
                        else unmatched.Add(mats[i] != null ? mats[i].name : "(없음)");
                    }
                    r.sharedMaterials = mats;
                }
                screens = model.GetComponentsInChildren<Renderer>(true).Where(r => r.sharedMaterials.Contains(materials["Screen"])).ToArray();
                if (unmatched.Count > 0)
                    notes.Add($"{Warning}재질 슬롯 {string.Join(", ", unmatched)}은(는) ArchiveRobot_Ivory·Brass·Dark·Screen·Accent와 이름이 맞지 않아 가져온 재질 그대로 두었습니다");

                // size: the imported height decides (1 when it is plausible; a centimeter or 100x import is scaled back)
                var b = RobotBounds(root);
                var scale = 1f;
                if (!Plausible(b.size.y))
                {
                    if (Plausible(b.size.y * 0.01f)) scale = 0.01f;
                    else if (Plausible(b.size.y * 100f)) scale = 100f;
                    else notes.Add($"{Warning}가져온 높이 {b.size.y:0.###}m가 보고 높이 {RobotReportHeight}m와 크게 달라 크기를 보정하지 않았습니다. Visual 크기를 직접 확인하세요");
                }
                visual.localScale = Vector3.one * scale;
                b = RobotBounds(root);
                if (b.size.y < Mathf.Max(b.size.x, b.size.z))
                    notes.Add($"{Warning}가져온 모델의 가장 긴 축이 위(Y)가 아닙니다(폭 {b.size.x:0.###}, 높이 {b.size.y:0.###}, 깊이 {b.size.z:0.###}m). 축은 보정하지 않았으니 Visual 회전을 직접 확인하세요");

                // facing: the Screen (face) side is the front, turned to +Z by quarter turns
                var yaw = 0f;
                if (screens.Length > 0)
                {
                    var face = screens.Select(r => r.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; }).center - b.center;
                    if (new Vector2(face.x, face.z).magnitude > 0.01f)
                    {
                        yaw = Mathf.Round(Mathf.Atan2(face.x, face.z) * Mathf.Rad2Deg / 90f) * 90f;
                        visual.localRotation = Quaternion.Euler(0f, -yaw, 0f);
                    }
                    else notes.Add($"{Warning}Screen 슬롯이 몸 중심과 같은 자리여서 정면을 판단하지 못했습니다. 로봇 정면이 +Z인지 직접 확인하세요");
                }
                else notes.Add($"{Warning}Screen 재질 슬롯을 찾지 못해 정면을 판단하지 못했습니다. 로봇 정면이 +Z인지 직접 확인하세요");

                // the body over the root (horizontally centered); a model below the floor is lifted to the reported hover
                b = RobotBounds(root);
                var shift = new Vector3(-b.center.x, 0f, -b.center.z);
                if (b.min.y < -0.01f)
                {
                    shift.y = RobotReportHover - b.min.y;
                    notes.Add($"{Warning}모델 아래쪽이 바닥 아래({b.min.y:0.###}m)에 있어 바닥 위 {RobotReportHover}m로 올렸습니다. 원점 위치를 확인하세요");
                }
                visual.localPosition += shift;
                b = RobotBounds(root);
                var radius = Mathf.Sqrt(Mathf.Max(b.min.x * b.min.x, b.max.x * b.max.x) + Mathf.Max(b.min.z * b.min.z, b.max.z * b.max.z));
                notes.Add($"{RobotPrefabPath}: 가져온 외형 폭 {b.size.x:0.000}m · 깊이 {b.size.z:0.000}m · 높이 {b.size.y:0.000}m(바닥 위 {b.min.y:0.000}~{b.max.y:0.000}m), " +
                          $"bounds 기준 최대 수평 반경 {radius:0.000}m — Visual 크기 {scale}, 회전 {-yaw}°, 이동 ({visual.localPosition.x:0.###}, {visual.localPosition.y:0.###}, {visual.localPosition.z:0.###})");
                if (b.max.y > 1.85f || radius > 0.45f)
                    notes.Add($"{Warning}외형이 플레이어(높이 1.8m·반지름 0.3m)보다 큽니다. 출입구(폭 1.3m·높이 2.2m) 통과 모습을 확인하세요(외형은 충돌하지 않음)");

                SetLayerAll(root.transform, 2);                            // Ignore Raycast: never the door aim or the floor search
                PrefabUtility.SaveAsPrefabAsset(root, RobotPrefabPath, out var ok);
                if (!ok) throw new InvalidOperationException($"{RobotPrefabPath}를 저장하지 못했습니다");
                saved.Add(RobotPrefabPath);
                notes.Add($"{RobotPrefabPath} 생성(루트 = 발밑, {RobotVisualName} 아래 모델)");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(RobotPrefabPath);
        }

        static bool Plausible(float height) => height > RobotReportHeight * 0.7f && height < RobotReportHeight * 1.3f;

        static Bounds RobotBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }

        static void SetLayerAll(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform child in t) SetLayerAll(child, layer);
        }

        /// <summary>Gameplay: one RemotePlayers next to the RoomGenerationDebug and only empty references filled.</summary>
        static void ConnectRobot(GameObject prefab, Material accent, List<string> notes, List<string> saved)
        {
            var start = notes.Count;
            var scene = EditorSceneManager.OpenScene(GameplayPath, OpenSceneMode.Single);
            var games = Object.FindObjectsByType<RoomGenerationDebug>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (games.Length != 1) throw new InvalidOperationException($"Gameplay 씬의 RoomGenerationDebug가 {games.Length}개입니다(하나여야 함)");
            var game = games[0];
            var all = Object.FindObjectsByType<RemotePlayers>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > 1)
            {
                notes.Add($"{Warning}Gameplay: RemotePlayers가 {all.Length}개입니다({string.Join(", ", all.Select(r => $"'{PathOf(r.gameObject)}'"))}). 연결하지 않았습니다(삭제하지 않음)");
                return;
            }
            RemotePlayers remote;
            if (all.Length == 1) remote = all[0];
            else
            {
                remote = game.gameObject.AddComponent<RemotePlayers>();
                notes.Add($"Gameplay: RemotePlayers 추가('{PathOf(game.gameObject)}')");
            }
            FillOrReport(remote, "robotPrefab", prefab, "Gameplay: 원격 로봇 프리팹 연결", notes);
            FillOrReport(remote, "accentMaterial", accent, "Gameplay: Accent 재질 연결", notes);
            FillOrReport(game, "remotePlayers", remote, "Gameplay: 방 생성 제어 → RemotePlayers 연결", notes);
            SaveIfChanged(scene, GameplayPath, start, notes, saved);
        }

        /// <summary>Fills an empty reference; one already pointing elsewhere is reported and kept.</summary>
        static void FillOrReport(Object target, string field, Object value, string note, List<string> notes)
        {
            var p = new SerializedObject(target).FindProperty(field) ?? throw new InvalidOperationException($"{target.GetType().Name}에 '{field}' 필드가 없습니다");
            if (p.objectReferenceValue != null && p.objectReferenceValue != value)
            {
                notes.Add($"{Warning}{target.GetType().Name}.{field}이(가) 이미 '{p.objectReferenceValue.name}'를 가리킵니다(바꾸지 않음)");
                return;
            }
            FillIfEmpty(target, field, value, note, notes);
        }
    }
}
