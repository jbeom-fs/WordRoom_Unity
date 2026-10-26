using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using WordRoom.World;
using Object = UnityEngine.Object;

namespace WordRoom.EditorTools
{
    /// <summary>
    /// One-time move of the UI values the former runtime-built screens kept on their scene components (menus: font;
    /// HUD: font, sortingOrder; hint: font, fontSize, offset — still read through hidden legacy fields) onto that
    /// scene's placed prefab instance, as instance overrides: the shared prefabs never change, so each scene keeps
    /// its own values. It runs only while the component's legacyUiMigrated flag is off and only when Connect left
    /// the references complete and unambiguous. The flag is set in the same scene save as the moved values, so a
    /// stopped, partial or unsaved run is tried again next time, and a finished one never runs again (later edits
    /// stay). A target already overridden in the scene is a user edit and is kept. Values that cannot be read (the
    /// field is not in the saved scene) are reported and the prefab value stays. For UI made by the user (not an
    /// instance of the prefab) nothing is changed; its old values are only reported. A font change keeps a text's
    /// material preset (the hint outline) usable: a preset made for another font atlas gets a copy for the new font.
    /// </summary>
    internal static class LegacyUiMigration
    {
        const string Flag = "legacyUiMigrated";
        static string Warning => GameMenuSetup.Warning;

        /// <summary>MainMenuController / PauseMenu: font → every text of the menu canvas (nested settings panel too).</summary>
        public static void Menu(Component menu, GameMenuSetup.Connection link, string label, List<string> notes, List<string> saved)
        {
            if (!Begin(menu, link, label, notes, out var so)) return;
            var report = new List<string>();
            var font = so.FindProperty("legacyFont").objectReferenceValue as TMP_FontAsset;
            var ok = true;
            if (link.Root == null) UserUi(report, font != null ? $"font {font.name}" : null);
            else ok = MoveFont(font, link.Root.GetComponentsInChildren<TMP_Text>(true), report, notes, saved);
            Finish(so, menu, label, report, notes, ok);
        }

        /// <summary>WordRoomHud: font → every HUD text, sortingOrder → the HUD canvas.</summary>
        public static void Hud(WordRoomHud hud, GameMenuSetup.Connection link, string label, List<string> notes, List<string> saved)
        {
            if (!Begin(hud, link, label, notes, out var so)) return;
            var report = new List<string>();
            var font = so.FindProperty("legacyFont").objectReferenceValue as TMP_FontAsset;
            var order = so.FindProperty("legacySortingOrder").intValue;
            var readable = order != int.MinValue;
            var ok = true;
            if (link.Root == null) UserUi(report, font != null ? $"font {font.name}" : null, readable ? $"sortingOrder {order}" : null);
            else
            {
                ok = MoveFont(font, link.Root.GetComponentsInChildren<TMP_Text>(true), report, notes, saved);
                var canvas = (Canvas)so.FindProperty("canvas").objectReferenceValue;
                if (!readable) report.Add($"sortingOrder: 저장된 값을 읽을 수 없음 — 프리팹 값 {canvas.sortingOrder} 사용");
                else Move(canvas, "m_SortingOrder", "sortingOrder", canvas.sortingOrder == order, () => canvas.sortingOrder = order, order.ToString(), report);
            }
            Finish(so, hud, label, report, notes, ok);
        }

        /// <summary>PlayHint: font, fontSize → the hint text; offset → its anchored position.</summary>
        public static void Hint(PlayHint hint, GameMenuSetup.Connection link, string label, List<string> notes, List<string> saved)
        {
            if (!Begin(hint, link, label, notes, out var so)) return;
            var report = new List<string>();
            var font = so.FindProperty("legacyFont").objectReferenceValue as TMP_FontAsset;
            var size = so.FindProperty("legacyFontSize").floatValue;
            var offset = so.FindProperty("legacyOffset").vector2Value;
            var sizeReadable = size > 0f;
            var offsetReadable = !float.IsNaN(offset.x) && !float.IsNaN(offset.y);
            var ok = true;
            if (link.Root == null)
                UserUi(report, font != null ? $"font {font.name}" : null, sizeReadable ? $"fontSize {size}" : null, offsetReadable ? $"offset {offset}" : null);
            else
            {
                var text = (TextMeshProUGUI)so.FindProperty("label").objectReferenceValue;
                ok = MoveFont(font, new TMP_Text[] { text }, report, notes, saved);
                if (!sizeReadable) report.Add($"fontSize: 저장된 값을 읽을 수 없음 — 프리팹 값 {text.fontSize} 사용");
                else Move(text, "m_fontSize", "fontSize", Mathf.Approximately(text.fontSize, size), () => text.fontSize = size, size.ToString(), report);
                var rect = text.rectTransform;
                if (!offsetReadable) report.Add($"offset: 저장된 값을 읽을 수 없음 — 프리팹 값 {rect.anchoredPosition} 사용");
                else Move(rect, "m_AnchoredPosition", "offset", rect.anchoredPosition == offset, () => rect.anchoredPosition = offset, offset.ToString(), report);
            }
            Finish(so, hint, label, report, notes, ok);
        }

        // ---- steps --------------------------------------------------------------------------------------------
        /// <summary>False when already moved (nothing to do) or when the UI is not ready (reported; tried again next run).</summary>
        static bool Begin(Component owner, GameMenuSetup.Connection link, string label, List<string> notes, out SerializedObject so)
        {
            so = new SerializedObject(owner);
            var flag = so.FindProperty(Flag) ?? throw new InvalidOperationException($"{owner.GetType().Name}에 '{Flag}' 필드가 없습니다");
            if (flag.boolValue) return false;
            if (link.Blocked || !link.Complete)
            {
                notes.Add($"{Warning}{label}: {owner.GetType().Name} 과거 UI 설정 이전 보류 — UI 참조가 아직 완성되지 않았습니다(완료 표시 안 함, 다음 실행에서 다시 시도)");
                return false;
            }
            return true;
        }

        /// <summary>Marks the move done (saved with the scene) only when every part succeeded; otherwise reports and leaves it for the next run.</summary>
        static void Finish(SerializedObject so, Component owner, string label, List<string> report, List<string> notes, bool ok)
        {
            var what = $"{label}: {owner.GetType().Name}('{GameMenuSetup.PathOf(owner.gameObject)}') 과거 UI 설정 이전";
            if (!ok)
            {
                notes.Add($"{Warning}{what} 일부 실패 — {string.Join(", ", report)}. 완료 표시를 하지 않아 다음 실행에서 남은 항목을 다시 시도합니다");
                return;
            }
            so.Update();
            so.FindProperty(Flag).boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            notes.Add($"{what} — {string.Join(", ", report)}");
        }

        static void UserUi(List<string> report, params string[] values)
        {
            var known = new List<string>();
            foreach (var v in values)
                if (v != null) known.Add(v);
            report.Add(known.Count > 0
                ? $"프리팹 밖 사용자 UI에 연결되어 과거 값({string.Join(", ", known)})을 적용하지 않음 — 필요하면 직접 반영하세요"
                : "프리팹 밖 사용자 UI에 연결됨, 읽을 수 있는 과거 값 없음");
        }

        /// <summary>One property: unchanged when already equal, kept when the scene overrides it (a user edit), else set as an instance override.</summary>
        static void Move(Object target, string property, string name, bool same, Action apply, string value, List<string> report)
        {
            if (same)
            {
                report.Add($"{name}: {value}(프리팹 값과 같음)");
                return;
            }
            var p = new SerializedObject(target).FindProperty(property);
            if (p != null && p.prefabOverride)
            {
                report.Add($"{name}: 씬에서 이미 수정된 값 유지(과거 값 {value} 적용 안 함)");
                return;
            }
            apply();
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            report.Add($"{name}: {value} 이전");
        }

        /// <summary>
        /// Puts `font` on every text that does not use it yet and has no scene override of its font. A text with the
        /// font's own material gets the new font's material; a text with a preset (e.g. the hint outline) keeps a
        /// preset that fits the new atlas. False when some text could not get a fitting material (left unchanged).
        /// </summary>
        static bool MoveFont(TMP_FontAsset font, IEnumerable<TMP_Text> texts, List<string> report, List<string> notes, List<string> saved)
        {
            if (font == null)
            {
                report.Add("font: 저장된 값을 읽을 수 없거나 비어 있음 — 프리팹 폰트 유지");
                return true;
            }
            if (font.material == null)
            {
                report.Add($"font {font.name}: 폰트 머티리얼이 없어 적용하지 못함");
                return false;
            }
            int moved = 0, same = 0, kept = 0, failed = 0;
            foreach (var t in texts)
            {
                if (t.font == font)
                {
                    same++;
                    continue;
                }
                var p = new SerializedObject(t).FindProperty("m_fontAsset");
                if (p != null && p.prefabOverride)
                {
                    kept++;
                    continue;
                }
                var current = t.fontSharedMaterial;
                var material = font.material;
                if (current != null && t.font != null && current != t.font.material)
                {
                    material = PresetFor(font, current, notes, saved);
                    if (material == null)
                    {
                        failed++;
                        continue;
                    }
                }
                t.font = font;
                t.fontSharedMaterial = material;
                PrefabUtility.RecordPrefabInstancePropertyModifications(t);
                moved++;
            }
            report.Add($"font {font.name}: 적용 {moved}, 이미 같음 {same}, 씬 수정값 유지 {kept}" + (failed > 0 ? $", 맞는 머티리얼이 없어 제외 {failed}" : ""));
            return failed == 0;
        }

        /// <summary>`preset` when it fits the font atlas, else "&lt;font&gt; - &lt;suffix&gt;.mat" next to the font (made once from the font material with the preset's outline).</summary>
        static Material PresetFor(TMP_FontAsset font, Material preset, List<string> notes, List<string> saved)
        {
            if (Fits(preset, font)) return preset;
            var fontPath = AssetDatabase.GetAssetPath(font);
            var folder = fontPath.StartsWith("Assets/") ? Path.GetDirectoryName(fontPath).Replace('\\', '/') : "Assets/Font";
            var cut = preset.name.LastIndexOf(" - ", StringComparison.Ordinal);
            var suffix = cut >= 0 ? preset.name.Substring(cut + 3) : preset.name;
            var path = $"{folder}/{font.name} - {suffix}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                if (Fits(existing, font)) return existing;
                notes.Add($"{Warning}{path}가 {font.name} 아틀라스와 맞지 않아 쓰지 않았습니다(덮어쓰지 않음)");
                return null;
            }
            if (!AssetDatabase.IsValidFolder(folder))
            {
                notes.Add($"{Warning}{folder} 폴더가 없어 {font.name}용 머티리얼을 만들지 않았습니다");
                return null;
            }
            var m = new Material(font.material) { name = Path.GetFileNameWithoutExtension(path) };
            foreach (var name in new[] { "_OutlineWidth", "_OutlineSoftness" })
                if (preset.HasProperty(name) && m.HasProperty(name)) m.SetFloat(name, preset.GetFloat(name));
            if (preset.HasProperty("_OutlineColor") && m.HasProperty("_OutlineColor")) m.SetColor("_OutlineColor", preset.GetColor("_OutlineColor"));
            if (preset.IsKeywordEnabled("OUTLINE_ON")) m.EnableKeyword("OUTLINE_ON");
            AssetDatabase.CreateAsset(m, path);
            saved.Add(path);
            notes.Add($"{font.name}용 머티리얼 생성(기존 '{preset.name}'의 외곽선 복사): {path}");
            return m;
        }

        static bool Fits(Material m, TMP_FontAsset font) =>
            m != null && font.atlasTexture != null && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") == font.atlasTexture;
    }
}
