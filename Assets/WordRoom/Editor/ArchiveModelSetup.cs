using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using WordRoom.Generation;
using WordRoom.World;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// User-run tool: rebuilds TestRoomPrefab and TestDoor (same paths, .meta and GUIDs, same root objects) around
    /// the archive FBX models. Each prefab gets one nested instance of its model under the untouched root; nothing
    /// in the FBX files or their import settings is changed. Everything is measured from the imported models in
    /// prefab-root space (all intermediate transforms included) and checked against the archive design before
    /// anything is saved; on any problem both prefabs are left as they were.
    /// Room: legacy test walls/floor/DoorPoints and their colliders are removed; the model's four DoorPoints are the
    /// only ones; simple BoxColliders (floor, ceiling, wall pieces beside and above each doorway, measured per side)
    /// are rebuilt under ArchiveColliders; the existing Wall_*/WordTag/Word/WordText TMP texts (font kept) are
    /// moved onto the model's WordText marks; one shadowless point light. Door: the legacy cube body is removed and
    /// the model's Door body gets a BoxCollider from its mesh bounds. Materials: URP Lit copies of the FBX materials
    /// in Assets/Models/ArchiveMaterials, assigned as overrides on the model instances. Running it again reuses
    /// what it made (no duplicates). Scenes are not changed or saved.
    /// Axis: FBX is right-handed, Unity left-handed, so the importer mirrors one axis. If the imported walls lie on
    /// the opposite side of their names (Wall_Right at -X, or Wall_Up at -Z), the model child gets that one axis
    /// mirrored back (scale -1 on that axis); this is logged and never applied to a prefab root.
    /// </summary>
    public static class ArchiveModelSetup
    {
        public const string RoomPrefabPath = "Assets/Prefabs/TestRoomPrefab.prefab";
        public const string DoorPrefabPath = "Assets/Prefabs/TestDoor.prefab";
        const string RoomModelPath = "Assets/Models/ArchiveRoom_v1.fbx";
        const string DoorModelPath = "Assets/Models/ArchiveDoor_v1.fbx";
        const string FontPath = "Assets/Font/Pretendard-Black SDF.asset";
        const string MaterialFolder = "Assets/Models/ArchiveMaterials";
        const string LitShader = "Universal Render Pipeline/Lit";

        /// <summary>Center distance of archive rooms (floor outline 6.20 × 6.20); RoomWorldView.roomSpacing uses it explicitly.</summary>
        public const float RoomSpacing = 6.2f;

        const string ColliderRootName = "ArchiveColliders";
        const string LightName = "InteriorLight";
        const float Tolerance = 0.01f;

        // Design values checked after import (meters, prefab-root space).
        const float HalfOutline = 3.1f;        // floor outline and shared boundary
        const float DoorThickness = 0.1f;
        const float DoorWidth = 1.3f;
        const float DoorHeight = 2.2f;
        const float SignHeight = 1.5f;

        // Sign text: 0.82 × 0.22 m at scale 0.1 (TMP 3D sizes in local units).
        const float SignScale = 0.1f;
        static readonly Vector2 SignRect = new Vector2(8.2f, 2.2f);

        static readonly string[] SideNames = { "Up", "Down", "Left", "Right" };          // indexed by Direction
        static readonly string[] LegacyWallParts = { "Wall", "Wall (1)", "Wall (2)", "DoorPoint" };

        /// <summary>
        /// URP Lit versions of the FBX materials, by FBX material name. Base colors are the FBX DiffuseColor values
        /// (linear, read from the files on 2026-10-23) converted to sRGB; metallic/smoothness follow the FBX
        /// reflection factor and shininess loosely. The FBX textures (StoneFloor/DarkWalnut BaseColor) point to
        /// ArtSource outside Assets and are not in the project, so only colors are used.
        /// </summary>
        static readonly (string name, Color linear, float metallic, float smoothness, float emission)[] Palette =
        {
            ("IvoryPlaster", new Color(0.68669f, 0.63076f, 0.521f), 0f, 0.2f, 0f),
            ("StoneFloor", new Color(0.13843f, 0.12744f, 0.10702f), 0f, 0.2f, 0f),
            ("DarkWalnut", new Color(0.06663f, 0.0356f, 0.02315f), 0f, 0.35f, 0f),
            ("TealMetal", new Color(0.02122f, 0.07036f, 0.06663f), 0.6f, 0.5f, 0f),
            ("AgedBrass", new Color(0.39157f, 0.24228f, 0.07036f), 0.8f, 0.55f, 0f),
            ("WarmLight", new Color(1f, 0.76052f, 0.46208f), 0f, 0.5f, 3f),
        };

        sealed class SetupError : Exception
        {
            public SetupError(string message) : base(message) { }
        }

        struct DoorMeasure
        {
            public float Width;
            public float Height;
        }

        [MenuItem("WordRoom/새 아카이브 모델로 프리팹 교체")]
        public static void Replace()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[아카이브 모델] Play 중에는 실행하지 않습니다. Play를 끝낸 뒤 다시 실행하세요.");
                return;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && (stage.assetPath == RoomPrefabPath || stage.assetPath == DoorPrefabPath))
            {
                Debug.LogError($"[아카이브 모델] {stage.assetPath}가 Prefab 편집 모드로 열려 있습니다. 닫은 뒤 다시 실행하세요(열린 편집 내용과 섞이지 않게).");
                return;
            }

            var log = new StringBuilder("[아카이브 모델] 프리팹 교체 완료 — 측정값은 프리팹 루트 공간(m)");
            GameObject doorRoot = null, roomRoot = null;
            try
            {
                var roomModel = Load<GameObject>(RoomModelPath, "방 모델");
                var doorModel = Load<GameObject>(DoorModelPath, "문 모델");
                var font = Load<TMP_FontAsset>(FontPath, "한글 TMP 폰트");
                var shader = Shader.Find(LitShader) ?? throw new SetupError($"셰이더 '{LitShader}'를 찾지 못했습니다(URP 확인).");

                doorRoot = PrefabUtility.LoadPrefabContents(DoorPrefabPath);
                roomRoot = PrefabUtility.LoadPrefabContents(RoomPrefabPath);
                var door = BuildDoor(doorRoot, doorModel, log);
                BuildRoom(roomRoot, roomModel, font, door, log);

                var doorModelRoot = FindModelInstance(doorRoot.transform, doorModel);
                var roomModelRoot = FindModelInstance(roomRoot.transform, roomModel);
                var used = UsedMaterialNames(doorModelRoot).Union(UsedMaterialNames(roomModelRoot)).ToList();
                var unknown = used.Where(n => Palette.All(p => p.name != n)).ToList();
                if (unknown.Count > 0) throw new SetupError($"모델 재질 슬롯에 대응할 재질이 없습니다: {string.Join(", ", unknown)}");
                var materials = EnsureMaterials(shader, log);
                AssignMaterials(doorModelRoot, materials);
                AssignMaterials(roomModelRoot, materials);
                log.Append($"\n재질: 모델 슬롯 {string.Join(", ", used.OrderBy(n => n))} → {MaterialFolder}의 URP Lit(텍스처 원본은 프로젝트에 없어 색상만 사용)");

                PrefabUtility.SaveAsPrefabAsset(doorRoot, DoorPrefabPath, out var doorSaved);
                if (!doorSaved) throw new SetupError($"{DoorPrefabPath} 저장 실패");
                PrefabUtility.SaveAsPrefabAsset(roomRoot, RoomPrefabPath, out var roomSaved);
                if (!roomSaved) throw new SetupError($"{RoomPrefabPath} 저장 실패(문 프리팹은 이미 저장됨)");
                AssetDatabase.SaveAssets();
                log.Append($"\n간격: 장면 RoomWorldView.Room Spacing을 {RoomSpacing:0.00}으로 두세요(자동 측정이 장식·수납부에 흔들리지 않게). 경계 ±{RoomSpacing / 2f:0.00}, 입장 기준 경계 + 0.15m.");
                Debug.Log(log.ToString());
            }
            catch (SetupError e)
            {
                Debug.LogError($"[아카이브 모델] 교체 중단 — 프리팹은 저장하지 않았습니다: {e.Message}");
            }
            catch (Exception e)
            {
                Debug.LogError("[아카이브 모델] 교체 중 오류 — 프리팹은 저장하지 않았습니다(아래 예외 확인).");
                Debug.LogException(e);
            }
            finally
            {
                if (doorRoot != null) PrefabUtility.UnloadPrefabContents(doorRoot);
                if (roomRoot != null) PrefabUtility.UnloadPrefabContents(roomRoot);
            }
        }

        // ---- door -----------------------------------------------------------------------------------------
        static DoorMeasure BuildDoor(GameObject root, GameObject model, StringBuilder log)
        {
            CheckRoot(root.transform, DoorPrefabPath);
            foreach (var legacy in DirectChildren(root.transform).Where(c => c.name == "Door" && !PrefabUtility.IsPartOfPrefabInstance(c.gameObject)).ToList())
                Object.DestroyImmediate(legacy.gameObject);                           // the old cube body
            var instance = EnsureModelInstance(root.transform, model);
            ResetLocal(instance, Vector3.one);

            var bodies = root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform && t.name == "Door").ToList();
            if (bodies.Count != 1) throw new SetupError($"문 프리팹에서 'Door' 본체가 {bodies.Count}개입니다(하나여야 함).");
            var body = bodies[0];
            if (!body.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh == null) throw new SetupError("문 모델 'Door'에 메시가 없습니다.");
            var mesh = filter.sharedMesh;
            foreach (var c in body.GetComponents<Collider>().Where(c => !(c is BoxCollider)).ToList()) Object.DestroyImmediate(c);
            if (!body.TryGetComponent<BoxCollider>(out var box)) box = body.gameObject.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;                                           // moves with the body when it slides up
            box.size = mesh.bounds.size;

            var b = BoundsIn(root.transform, body, false);
            if (Mathf.Abs(b.min.z) <= Tolerance && Mathf.Abs(b.max.z - DoorWidth) <= Tolerance)
            {
                // Imported with Z mirrored (body on +Z of the pivot). The body is symmetric in thickness, so a half turn
                // of the model child puts it back on -Z without a negative scale under the colliders.
                instance.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                b = BoundsIn(root.transform, body, false);
                log.Append("\n축: 문 본체가 피벗의 +Z쪽으로 임포트되어 모델 자식을 Y 180° 회전(두께 방향 대칭)");
            }
            log.Append($"\n문: 'Door' 닫힌 본체 X {b.min.x:0.###}~{b.max.x:0.###}, Y {b.min.y:0.###}~{b.max.y:0.###}, Z {b.min.z:0.###}~{b.max.z:0.###}, 중심 {Fmt(b.center)}" +
                       $" · BoxCollider(본체 로컬) center {Fmt(box.center)} size {Fmt(box.size)}");
            Expect(b.min.x, -DoorThickness / 2f, "문 두께(X) 최소");
            Expect(b.max.x, DoorThickness / 2f, "문 두께(X) 최대");
            Expect(b.min.y, 0f, "문 바닥(Y)");
            Expect(b.max.y, DoorHeight, "문 높이(Y)");
            Expect(b.min.z, -DoorWidth, "문 폭(Z) 최소");
            Expect(b.max.z, 0f, "문 폭(Z) 최대");
            return new DoorMeasure { Width = b.size.z, Height = b.max.y };
        }

        // ---- room -----------------------------------------------------------------------------------------
        static void BuildRoom(GameObject root, GameObject model, TMP_FontAsset font, DoorMeasure door, StringBuilder log)
        {
            var r = root.transform;
            CheckRoot(r, RoomPrefabPath);
            RemoveLegacyRoomParts(r, log);
            var instance = EnsureModelInstance(r, model);
            ResetLocal(instance, Vector3.one);

            // Axis check with the model unmirrored: each Wall_* must lie on the side its name says.
            var mirror = Vector3.one;
            var up = Pos(r, ModelNode(instance, "Wall_Up"));
            var down = Pos(r, ModelNode(instance, "Wall_Down"));
            var left = Pos(r, ModelNode(instance, "Wall_Left"));
            var right = Pos(r, ModelNode(instance, "Wall_Right"));
            bool upOk = up.z > 0 && down.z < 0, upSwapped = up.z < 0 && down.z > 0;
            bool rightOk = right.x > 0 && left.x < 0, rightSwapped = right.x < 0 && left.x > 0;
            if (upOk && rightOk) log.Append("\n축: 임포트된 벽 방향이 이름과 일치 — 모델 보정 없음");
            else if (upOk && rightSwapped)
            {
                mirror = new Vector3(-1f, 1f, 1f);
                log.Append($"\n축: 임포트 후 Wall_Right {Fmt(right)}·Wall_Left {Fmt(left)}로 좌우가 이름과 반대(오른손→왼손 좌표 변환으로 X 반전). 모델 자식 스케일 (-1, 1, 1)로 되돌림");
            }
            else if (rightOk && upSwapped)
            {
                mirror = new Vector3(1f, 1f, -1f);
                log.Append($"\n축: 임포트 후 Wall_Up {Fmt(up)}·Wall_Down {Fmt(down)}로 상하가 이름과 반대(좌표계 변환으로 Z 반전). 모델 자식 스케일 (1, 1, -1)로 되돌림");
            }
            else throw new SetupError($"벽 위치를 한 축 반전으로 설명할 수 없습니다: 상 {Fmt(up)}, 하 {Fmt(down)}, 좌 {Fmt(left)}, 우 {Fmt(right)}");
            ResetLocal(instance, mirror);

            // Floor: outline 6.20 × 6.20, top at Y 0.
            var floor = BoundsIn(r, ModelNode(instance, "Floor"), false);
            log.Append($"\n바닥: X {floor.min.x:0.###}~{floor.max.x:0.###}, Z {floor.min.z:0.###}~{floor.max.z:0.###}, Y {floor.min.y:0.###}~{floor.max.y:0.###}");
            Expect(floor.max.y, 0f, "바닥 윗면");
            Expect(floor.min.x, -HalfOutline, "바닥 외곽 -X");
            Expect(floor.max.x, HalfOutline, "바닥 외곽 +X");
            Expect(floor.min.z, -HalfOutline, "바닥 외곽 -Z");
            Expect(floor.max.z, HalfOutline, "바닥 외곽 +Z");
            var all = BoundsIn(r, instance.transform, true);
            log.Append($"\n모델 전체 Bounds(장식·수납부 포함): {Fmt(all.min)} ~ {Fmt(all.max)} · 방 간격은 바닥 외곽 기준 {RoomSpacing:0.00}");

            // DoorPoints: exactly the model's four, one per side by position (RoomPrefabLayout does the same at runtime).
            var points = r.GetComponentsInChildren<Transform>(true).Where(t => t.name == "DoorPoint").ToList();
            if (points.Count != 4) throw new SetupError($"DoorPoint가 {points.Count}개입니다(모델의 4개만 있어야 함).");
            var doorPoints = new Vector3[4];
            var seen = new bool[4];
            foreach (var t in points)
            {
                var p = Pos(r, t);
                var d = Classify(p) ?? throw new SetupError($"DoorPoint {Fmt(p)}의 방향을 정할 수 없습니다.");
                if (seen[(int)d]) throw new SetupError($"{Directions.Korean(d)} DoorPoint가 둘 이상입니다.");
                seen[(int)d] = true;
                doorPoints[(int)d] = p;
            }

            var colliders = RebuildColliderRoot(r);
            AddBox(colliders, "Floor", floor);
            var ceiling = BoundsIn(r, ModelNode(instance, "Ceiling"), false);
            AddBox(colliders, "Ceiling", ceiling);
            log.Append($"\n천장: 아랫면 Y {ceiling.min.y:0.###}, 범위 {Fmt(ceiling.min)} ~ {Fmt(ceiling.max)}");

            foreach (var d in Directions.DisplayOrder)
            {
                var side = SideNames[(int)d];
                var n = RoomPrefabLayout.Normal(d);
                var t = RoomPrefabLayout.Tangent(d);
                var p = doorPoints[(int)d];
                Expect(Vector3.Dot(p, n), HalfOutline, $"{Directions.Korean(d)} DoorPoint 경계 거리");
                Expect(p.y, 0f, $"{Directions.Korean(d)} DoorPoint 높이");
                var a = Vector3.Dot(p, t);
                Expect(Mathf.Abs(a), door.Width / 2f, $"{Directions.Korean(d)} DoorPoint 폭 방향 위치(문 모서리)");

                // Doorway: from the DoorPoint corner across the door width toward the middle, door height up.
                var towardMiddle = a > 0 ? -1f : 1f;
                float o0 = Mathf.Min(a, a + towardMiddle * door.Width), o1 = Mathf.Max(a, a + towardMiddle * door.Width);
                var shell = BoundsIn(r, ModelNode(instance, $"Wall_{side}/Wall_{side}_Shell"), false);
                float tMin = Vector3.Dot(shell.min, t), tMax = Vector3.Dot(shell.max, t);
                if (tMin > tMax) (tMin, tMax) = (tMax, tMin);
                if (o0 < tMin || o1 > tMax || shell.max.y < door.Height) throw new SetupError($"{side} 벽 셸 {Fmt(shell.min)}~{Fmt(shell.max)} 안에 출입구 {o0:0.###}~{o1:0.###}이 들어가지 않습니다.");
                var inner = Mathf.Min(Vector3.Dot(shell.min, n), Vector3.Dot(shell.max, n));
                var outer = Mathf.Max(Vector3.Dot(shell.min, n), Vector3.Dot(shell.max, n));
                Expect(outer, HalfOutline, $"{Directions.Korean(d)} 벽 셸 바깥면(공유 경계)");
                AddWallPiece(colliders, $"Wall_{side}_A", shell, t, tMin, o0, shell.min.y, shell.max.y);
                AddWallPiece(colliders, $"Wall_{side}_B", shell, t, o1, tMax, shell.min.y, shell.max.y);
                AddWallPiece(colliders, $"Wall_{side}_Lintel", shell, t, o0, o1, door.Height, shell.max.y);
                var design = DesignDoorPoint(d);
                log.Append($"\n{Directions.Korean(d)}: DoorPoint {Fmt(p)}{((p - design).magnitude > Tolerance ? $" — 설계 {Fmt(design)}와 다름(문은 위치로 정렬됨)" : "")}" +
                           $" · 벽 셸 안쪽면 {inner:0.###}·바깥면 {outer:0.###}(두께 {outer - inner:0.###}) · 출입구 폭 {o1 - o0:0.###}({o0:0.###}~{o1:0.###})·높이 {door.Height:0.###}" +
                           $" · 개구부 양옆 벽 {o0 - tMin:0.###}/{tMax - o1:0.###}");

                PlaceSign(r, instance, d, font, log);
            }

            PlaceLight(r, instance, log);
        }

        /// <summary>Legacy test room parts: root 'floor', and the old wall pieces and DoorPoints under each Wall_* wrapper.</summary>
        static void RemoveLegacyRoomParts(Transform root, StringBuilder log)
        {
            var removed = new List<string>();
            foreach (var c in DirectChildren(root).Where(c => c.name == "floor").ToList())
            {
                removed.Add(c.name);
                Object.DestroyImmediate(c.gameObject);
            }
            foreach (var side in SideNames)
            {
                var wrapper = root.Find($"Wall_{side}");
                if (wrapper == null || PrefabUtility.IsPartOfPrefabInstance(wrapper.gameObject)) continue;
                foreach (var c in DirectChildren(wrapper).Where(c => LegacyWallParts.Contains(c.name)).ToList())
                {
                    removed.Add($"Wall_{side}/{c.name}");
                    Object.DestroyImmediate(c.gameObject);
                }
                foreach (var path in new[] { "WordTag", "WordTag/Word" })
                {
                    var t = wrapper.Find(path);
                    if (t == null) continue;
                    foreach (var col in t.GetComponents<Collider>()) Object.DestroyImmediate(col);
                    var mr = t.GetComponent<MeshRenderer>();
                    if (mr != null) Object.DestroyImmediate(mr);
                    var mf = t.GetComponent<MeshFilter>();
                    if (mf != null) Object.DestroyImmediate(mf);
                }
            }
            if (removed.Count > 0) log.Append($"\n구형 시험 메시 제거: {string.Join(", ", removed)}");
            var others = DirectChildren(root).Where(c => !c.name.StartsWith("Wall_") && c.name != ColliderRootName && c.name != LightName &&
                                                         !PrefabUtility.IsPartOfPrefabInstance(c.gameObject)).Select(c => c.name).ToList();
            if (others.Count > 0) log.Append($"\n보존한 기타 오브젝트: {string.Join(", ", others)}");
        }

        /// <summary>
        /// The sign path RoomView reads (root Wall_*/WordTag/Word with a TMP text below) is kept as a plain wrapper at
        /// the prefab root: the existing WordText (its TMP settings and font) is reused, created only when missing, and
        /// put on the model's WordText mark, facing into the room (TMP 3D reads from its -Z side).
        /// </summary>
        static void PlaceSign(Transform root, GameObject model, Direction d, TMP_FontAsset font, StringBuilder log)
        {
            var side = SideNames[(int)d];
            var mark = ModelNode(model, $"Wall_{side}/WordTag/Word/WordText");
            var plate = BoundsIn(root, ModelNode(model, $"Wall_{side}/WordTag/Word"), false);
            var n = RoomPrefabLayout.Normal(d);
            var p = Pos(root, mark);
            var plateFace = Mathf.Min(Vector3.Dot(plate.min, n), Vector3.Dot(plate.max, n));   // room-facing plate face
            var gap = plateFace - Vector3.Dot(p, n);                                             // text in front of it (> 0)
            if (gap <= 0f) throw new SetupError($"{side} 명판 글자 위치 {Fmt(p)}가 명판 앞면보다 방 안쪽이 아닙니다(간격 {gap:0.####}).");
            Expect(p.y, SignHeight, $"{Directions.Korean(d)} 명판 높이");

            var wrapper = Ensure(root, $"Wall_{side}");
            var tag = Ensure(wrapper, "WordTag");
            var word = Ensure(tag, "Word");
            foreach (var t in new[] { wrapper, tag, word }) t.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var t in new[] { wrapper, tag, word }) t.localScale = Vector3.one;
            var texts = word.GetComponentsInChildren<TMP_Text>(true);
            if (texts.Length > 1) throw new SetupError($"Wall_{side}/WordTag/Word 아래 TMP 텍스트가 {texts.Length}개입니다(하나여야 함).");
            TMP_Text text = texts.FirstOrDefault();
            if (text == null)
            {
                var go = new GameObject("WordText", typeof(RectTransform));
                go.transform.SetParent(word, false);
                var tmp = go.AddComponent<TextMeshPro>();
                tmp.color = Color.black;
                tmp.enableAutoSizing = true;
                tmp.fontSizeMin = 1f;
                tmp.fontSizeMax = 16f;
                tmp.textWrappingMode = TextWrappingModes.NoWrap;
                text = tmp;
            }
            if (text.font == null) text.font = font;
            text.alignment = TextAlignmentOptions.Center;
            text.text = "";
            var rect = text.rectTransform;
            rect.SetParent(word, false);
            rect.SetLocalPositionAndRotation(p, Quaternion.LookRotation(n, Vector3.up));
            rect.localScale = Vector3.one * SignScale;
            rect.sizeDelta = SignRect;
            log.Append($"\n  명판 {Directions.Korean(d)}: 글자 {Fmt(p)}(명판 앞면에서 방 안쪽 {gap:0.####}) · 명판 {plate.size.x:0.###}×{plate.size.y:0.###}×{plate.size.z:0.###} 중심 {Fmt(plate.center)} · 글자 영역 {SignRect.x * SignScale:0.##}×{SignRect.y * SignScale:0.##} · 폰트 {(text.font != null ? text.font.name : "없음")}");
        }

        static void PlaceLight(Transform root, GameObject model, StringBuilder log)
        {
            var panel = model.transform.Find("Ceiling/CeilingLightPanel");
            var at = panel != null ? BoundsIn(root, panel, false) : new Bounds(new Vector3(0f, 3f, 0f), Vector3.zero);
            var t = Ensure(root, LightName);
            t.SetLocalPositionAndRotation(new Vector3(at.center.x, at.min.y - 0.3f, at.center.z), Quaternion.identity);
            t.localScale = Vector3.one;
            if (!t.TryGetComponent<Light>(out var light)) light = t.gameObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.76052f, 0.46208f).gamma;
            light.intensity = 2f;
            light.range = 5.5f;                                                        // room corners ~5.0 m away; little reach into neighbors
            light.shadows = LightShadows.None;                                         // up to 5 active rooms: no realtime shadows per light
            light.renderMode = LightRenderMode.Auto;
            light.lightmapBakeType = LightmapBakeType.Realtime;
            log.Append($"\n실내 조명: Point 1개 {Fmt(t.localPosition)}, 범위 {light.range:0.#}, 세기 {light.intensity:0.#}, 그림자 없음(밝기는 사용자 확인)");
        }

        // ---- materials --------------------------------------------------------------------------------------
        static IEnumerable<string> UsedMaterialNames(GameObject model) =>
            model.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Select(SlotKey).Distinct();

        /// <summary>FBX material name of a slot: the imported material's name, or ours from an earlier run without its prefix.</summary>
        static string SlotKey(Material m)
        {
            if (m == null) return "(없음)";
            var key = m.name.Replace(" (Instance)", "");
            return key.StartsWith("Archive_") ? key.Substring("Archive_".Length) : key;
        }

        static Dictionary<string, Material> EnsureMaterials(Shader shader, StringBuilder log)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder(Path.GetDirectoryName(MaterialFolder).Replace('\\', '/'), Path.GetFileName(MaterialFolder));
            var result = new Dictionary<string, Material>();
            var created = new List<string>();
            foreach (var (name, linear, metallic, smoothness, emission) in Palette)
            {
                var path = $"{MaterialFolder}/Archive_{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(shader) { name = $"Archive_{name}" };
                    AssetDatabase.CreateAsset(mat, path);
                    created.Add(name);
                }
                else if (mat.shader != shader) mat.shader = shader;
                mat.SetColor("_BaseColor", linear.gamma);
                mat.SetFloat("_Metallic", metallic);
                mat.SetFloat("_Smoothness", smoothness);
                if (emission > 0f)
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", (linear * emission).gamma);
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;   // glow only; the room light is the point light
                }
                else mat.DisableKeyword("_EMISSION");
                EditorUtility.SetDirty(mat);
                result[name] = mat;
            }
            if (created.Count > 0) log.Append($"\n새 재질: {string.Join(", ", created.Select(n => $"Archive_{n}"))}");
            return result;
        }

        static void AssignMaterials(GameObject model, Dictionary<string, Material> materials)
        {
            foreach (var r in model.GetComponentsInChildren<Renderer>(true))
            {
                var slots = r.sharedMaterials;
                for (var i = 0; i < slots.Length; i++)
                {
                    var key = SlotKey(slots[i]);
                    if (!materials.TryGetValue(key, out var m)) throw new SetupError($"'{r.name}' 슬롯 {i}의 재질 '{key}'에 대응하는 재질이 없습니다.");
                    slots[i] = m;
                }
                r.sharedMaterials = slots;
            }
        }

        // ---- helpers ----------------------------------------------------------------------------------------
        static T Load<T>(string path, string label) where T : Object =>
            AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new SetupError($"{label} '{path}'를 찾지 못했습니다.");

        static void CheckRoot(Transform root, string path)
        {
            if (root.localPosition != Vector3.zero || root.localRotation != Quaternion.identity || root.localScale != Vector3.one)
                throw new SetupError($"{path} 루트가 기본 위치·회전·스케일(1,1,1)이 아닙니다: {Fmt(root.localPosition)} {Fmt(root.localEulerAngles)} {Fmt(root.localScale)}. 루트를 바로잡은 뒤 실행하세요.");
        }

        static IEnumerable<Transform> DirectChildren(Transform t)
        {
            foreach (Transform c in t) yield return c;
        }

        /// <summary>The one direct child that is an instance of `model`, created when missing (never a second one).</summary>
        static GameObject EnsureModelInstance(Transform root, GameObject model)
        {
            var found = DirectChildren(root).Where(c => PrefabUtility.IsAnyPrefabInstanceRoot(c.gameObject) &&
                                                         PrefabUtility.GetCorrespondingObjectFromOriginalSource(c.gameObject) == model).ToList();
            if (found.Count > 1) throw new SetupError($"'{root.name}'에 모델 '{model.name}' 인스턴스가 {found.Count}개 있습니다. 하나만 남기세요.");
            return found.Count == 1 ? found[0].gameObject : (GameObject)PrefabUtility.InstantiatePrefab(model, root);
        }

        static GameObject FindModelInstance(Transform root, GameObject model) =>
            DirectChildren(root).First(c => PrefabUtility.IsAnyPrefabInstanceRoot(c.gameObject) &&
                                            PrefabUtility.GetCorrespondingObjectFromOriginalSource(c.gameObject) == model).gameObject;

        static void ResetLocal(GameObject go, Vector3 scale)
        {
            go.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = scale;
        }

        static Transform ModelNode(GameObject model, string path) =>
            model.transform.Find(path) ?? throw new SetupError($"모델 '{model.name}'에 '{path}'가 없습니다.");

        static Transform Ensure(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) return t;
            t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        static Vector3 Pos(Transform root, Transform t) => root.InverseTransformPoint(t.position);

        /// <summary>Mesh bounds of `t` (and its children when asked) in root space, through every intermediate transform.</summary>
        static Bounds BoundsIn(Transform root, Transform t, bool children)
        {
            var filters = children ? t.GetComponentsInChildren<MeshFilter>(true) : t.GetComponents<MeshFilter>();
            Bounds? total = null;
            foreach (var f in filters)
            {
                if (f.sharedMesh == null) continue;
                var m = root.worldToLocalMatrix * f.transform.localToWorldMatrix;
                var local = f.sharedMesh.bounds;
                for (var i = 0; i < 8; i++)
                {
                    var corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (total == null) total = new Bounds(p, Vector3.zero);
                    else
                    {
                        var b = total.Value;
                        b.Encapsulate(p);
                        total = b;
                    }
                }
            }
            return total ?? throw new SetupError($"'{t.name}'에 메시가 없습니다.");
        }

        static Direction? Classify(Vector3 p)
        {
            var ax = Mathf.Abs(p.x);
            var az = Mathf.Abs(p.z);
            if (Mathf.Abs(ax - az) < Tolerance) return null;
            if (ax > az) return p.x > 0 ? Direction.Right : Direction.Left;
            return p.z > 0 ? Direction.Up : Direction.Down;
        }

        /// <summary>Generated colliders live under one root child, rebuilt on every run.</summary>
        static Transform RebuildColliderRoot(Transform root)
        {
            var old = root.Find(ColliderRootName);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var t = new GameObject(ColliderRootName).transform;
            t.SetParent(root, false);
            return t;
        }

        static void AddBox(Transform parent, string name, Bounds b)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var box = go.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size;
        }

        /// <summary>Part of a wall slab between t0 and t1 along its length `t`, y0 to y1 high, full slab thickness.</summary>
        static void AddWallPiece(Transform parent, string name, Bounds slab, Vector3 t, float t0, float t1, float y0, float y1)
        {
            if (t1 - t0 < 0.001f || y1 - y0 < 0.001f) return;
            var center = slab.center;
            var size = slab.size;
            center -= t * Vector3.Dot(center, t);
            center += t * ((t0 + t1) / 2f);
            size -= Vector3.Scale(size, t);
            size += t * (t1 - t0);
            center.y = (y0 + y1) / 2f;
            size.y = y1 - y0;
            AddBox(parent, name, new Bounds(center, size));
        }

        /// <summary>Design DoorPoint per side (door-width corner on the floor, middle of the shared boundary).</summary>
        static Vector3 DesignDoorPoint(Direction d)
        {
            switch (d)
            {
                case Direction.Up: return new Vector3(DoorWidth / 2f, 0f, HalfOutline);
                case Direction.Right: return new Vector3(HalfOutline, 0f, -DoorWidth / 2f);
                case Direction.Down: return new Vector3(-DoorWidth / 2f, 0f, -HalfOutline);
                default: return new Vector3(-HalfOutline, 0f, DoorWidth / 2f);
            }
        }

        static void Expect(float measured, float expected, string what)
        {
            if (Mathf.Abs(measured - expected) > Tolerance)
                throw new SetupError($"{what}: 측정 {measured:0.####}, 기대 {expected:0.####}(허용 ±{Tolerance})");
        }

        static string Fmt(Vector3 v) => RoomPrefabLayout.Fmt(v);
    }
}
