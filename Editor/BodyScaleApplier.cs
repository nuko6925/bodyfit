// Editor/BodyScaleApplier.cs
// アバター (または衣装) のボーンに、プリセットのスケールと MA Scale Adjuster をまとめて適用する。
// Tools > Body Fit > Body Scale、または Hierarchy の右クリック > Body Fit > Body Scale... から開く。
// Undo 可。何度実行しても同じ結果になる。

using System.Collections.Generic;
using System.Linq;
using System.Text;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;

namespace Hinata.BodyFit.Editor
{
    internal class BodyScaleWindow : EditorWindow
    {
        const string PrefKey = "Hinata.BodyFit.BodyScale.Preset";
        BodyScalePreset _preset;

        [MenuItem("Tools/Body Fit/Body Scale")]
        static void Open() => GetWindow<BodyScaleWindow>("Body Scale");

        [MenuItem("GameObject/Body Fit/Body Scale...", false, 0)]
        static void OpenFromHierarchy(MenuCommand cmd)
        {
            // Hierarchy の右クリックは選択数ぶん呼ばれるので 1 回だけ開く
            if (cmd.context != null && cmd.context != Selection.activeObject) return;
            Open();
        }

        void OnEnable()
        {
            var path = AssetDatabase.GUIDToAssetPath(EditorPrefs.GetString(PrefKey, ""));
            if (!string.IsNullOrEmpty(path)) _preset = AssetDatabase.LoadAssetAtPath<BodyScalePreset>(path);
        }

        void OnSelectionChange() => Repaint();

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            _preset = (BodyScalePreset)EditorGUILayout.ObjectField("プリセット", _preset, typeof(BodyScalePreset), false);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetString(PrefKey, _preset != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_preset)) : "");

            if (_preset == null)
            {
                EditorGUILayout.HelpBox("Project ウィンドウで 右クリック > Create > Body Fit > Body Scale Preset からプリセットを作成してください。", MessageType.Info);
                return;
            }

            var targets = Selection.gameObjects.Where(g => g.scene.IsValid()).ToArray();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("適用先 (Hierarchy で選択中)", EditorStyles.boldLabel);
            if (targets.Length == 0)
                EditorGUILayout.HelpBox("アバターのルートを Hierarchy で選択してください (複数可)。", MessageType.None);
            else
                foreach (var t in targets) EditorGUILayout.LabelField("  " + t.name);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(targets.Length == 0))
                if (GUILayout.Button("適用", GUILayout.Height(28)))
                    BodyScaleApplier.Apply(_preset, targets);

            EditorGUILayout.HelpBox(
                "衣装には適用しなくて構いません。Body Fitter の「関節合わせ」がビルド時にアバター側のスケールと Scale Adjuster を衣装へ写します。",
                MessageType.None);
        }
    }

    public static class BodyScaleApplier
    {
        const string UndoName = "Apply Body Scale";

        public static void Apply(BodyScalePreset preset, IEnumerable<GameObject> targets)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            int group = Undo.GetCurrentGroup();

            var log = new StringBuilder();
            foreach (var go in targets) Apply(preset, go, log);

            Undo.CollapseUndoOperations(group);
            Debug.Log("[BodyScale]\n" + log);
        }

        public static void Apply(BodyScalePreset preset, GameObject root, StringBuilder log)
        {
            log.AppendLine($"■ {root.name} ← {preset.name}");

            var armature = FindChild(root.transform, "Armature", allowPrefix: true);
            if (armature == null)
            {
                log.AppendLine("  Armature が見つからないためスキップ");
                return;
            }

            if (preset.setRootScale)
            {
                SetLocalScale(root.transform, Vector3.one * preset.rootScale);
                log.AppendLine($"  {root.name}: scale {preset.rootScale}");
            }

            foreach (var rule in preset.rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.path)) continue;
                var sides = rule.path.Contains("{S}") ? new[] { "L", "R" } : new[] { "" };
                foreach (var side in sides)
                {
                    var path = rule.path.Replace("{S}", side);
                    var bone = FindByPath(armature, path);
                    if (bone == null)
                    {
                        log.AppendLine($"  ✗ 見つからない: {path}");
                        continue;
                    }

                    if (rule.setScale) SetLocalScale(bone, Vector3.one * rule.scale);
                    if (rule.setAdjuster) SetAdjuster(bone, rule.adjuster, rule.adjustChildren);

                    log.AppendLine($"  ✓ {GetPath(armature, bone)}" +
                                   (rule.setScale ? $"  scale {rule.scale}" : "") +
                                   (rule.setAdjuster ? $"  MA({rule.adjuster.x}, {rule.adjuster.y}, {rule.adjuster.z})" : ""));
                }
            }
        }

        // ---- Transform / MA -----------------------------------------------------

        static void SetLocalScale(Transform t, Vector3 scale)
        {
            Undo.RecordObject(t, UndoName);
            t.localScale = scale;
            PrefabUtility.RecordPrefabInstancePropertyModifications(t);
        }

        static void SetAdjuster(Transform bone, Vector3 newScale, bool adjustChildren)
        {
            var adj = bone.GetComponent<ModularAvatarScaleAdjuster>();
            if (adj == null) adj = Undo.AddComponent<ModularAvatarScaleAdjuster>(bone.gameObject);

            var oldScale = adj.Scale;

            Undo.RecordObject(adj, UndoName);
            adj.Scale = newScale;
            PrefabUtility.RecordPrefabInstancePropertyModifications(adj);

            if (!adjustChildren) return;

            // MA の ScaleAdjusterTool と同じ処理: 子の localPosition を (新スケール / 旧スケール) 倍する
            var o = Clamp(oldScale);
            var n = Clamp(newScale);
            var ratio = new Vector3(n.x / o.x, n.y / o.y, n.z / o.z);
            if ((ratio - Vector3.one).sqrMagnitude < 1e-12f) return;

            foreach (Transform child in bone)
            {
                Undo.RecordObject(child, UndoName);
                child.localPosition = Vector3.Scale(child.localPosition, ratio);
                PrefabUtility.RecordPrefabInstancePropertyModifications(child);
            }
        }

        static Vector3 Clamp(Vector3 v)
        {
            const float th = 1f / (1 << 14);
            return new Vector3(Mathf.Max(th, v.x), Mathf.Max(th, v.y), Mathf.Max(th, v.z));
        }

        // ---- ボーン検索 (大文字小文字・区切り文字を無視) ----------------------------

        static Transform FindByPath(Transform from, string path)
        {
            var cur = from;
            foreach (var name in path.Split('/'))
            {
                cur = FindChild(cur, name.Trim(), allowPrefix: false);
                if (cur == null) return null;
            }
            return cur;
        }

        static Transform FindChild(Transform parent, string name, bool allowPrefix)
        {
            var children = parent.Cast<Transform>().ToList();

            var hit = children.FirstOrDefault(c => string.Equals(c.name, name, System.StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            var key = Normalize(name);
            hit = children.FirstOrDefault(c => Normalize(c.name) == key);
            if (hit != null) return hit;

            // Armature のみ前方一致を許可 ("Armature.001" 等)
            if (allowPrefix)
                hit = children.FirstOrDefault(c => Normalize(c.name).StartsWith(key));

            return hit;
        }

        static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
                if (ch != '_' && ch != '.' && ch != ' ' && ch != '-')
                    sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        static string GetPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
