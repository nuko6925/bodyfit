// Editor/BodyFitterEditor.cs
using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Hinata.BodyFit.Editor
{
    [CustomEditor(typeof(BodyFitter))]
    internal class BodyFitterEditor : UnityEditor.Editor
    {
        static GameObject _preview;
        static GameObject _hiddenOriginal;
        static bool _foldProportion, _foldSolver, _foldOther;

        void Prop(string name) => EditorGUILayout.PropertyField(serializedObject.FindProperty(name), true);

        void DrawSettings()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("基本", EditorStyles.boldLabel);
            Prop(nameof(BodyFitter.bodies));
            Prop(nameof(BodyFitter.excludeBones));
            Prop(nameof(BodyFitter.perMesh));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("詳細設定", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                _foldProportion = EditorGUILayout.Foldout(_foldProportion, "体型合わせ", true);
                if (_foldProportion)
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Prop(nameof(BodyFitter.alignJoints));
                        Prop(nameof(BodyFitter.autoProportion));
                        using (new EditorGUI.DisabledScope(!((BodyFitter)target).autoProportion))
                        {
                            Prop(nameof(BodyFitter.proportionStrength));
                            Prop(nameof(BodyFitter.proportionRange));
                            Prop(nameof(BodyFitter.looseRatio));
                        }
                        Prop(nameof(BodyFitter.hideFeetInShoes));
                    }

                _foldSolver = EditorGUILayout.Foldout(_foldSolver, "貫通補正", true);
                if (_foldSolver)
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Prop(nameof(BodyFitter.margin));
                        Prop(nameof(BodyFitter.maxDistance));
                        Prop(nameof(BodyFitter.reverseDepth));
                        Prop(nameof(BodyFitter.bodySmoothing));
                        Prop(nameof(BodyFitter.falloff));
                        Prop(nameof(BodyFitter.solverIterations));
                        Prop(nameof(BodyFitter.layerFollowDistance));
                        Prop(nameof(BodyFitter.islandMaxSize));
                    }

                _foldOther = EditorGUILayout.Foldout(_foldOther, "その他", true);
                if (_foldOther)
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Prop(nameof(BodyFitter.targets));
                        Prop(nameof(BodyFitter.ignoreBodyBones));
                        Prop(nameof(BodyFitter.excludeDescendants));
                        Prop(nameof(BodyFitter.excludeWeightThreshold));
                        Prop(nameof(BodyFitter.blendShapeName));
                        Prop(nameof(BodyFitter.blendShapeWeight));
                        Prop(nameof(BodyFitter.order));
                        Prop(nameof(BodyFitter.verboseLog));
                    }
            }

            serializedObject.ApplyModifiedProperties();
        }

        public override void OnInspectorGUI()
        {
            var fitter = (BodyFitter)target;
            var avatar = fitter.GetComponentInParent<VRCAvatarDescriptor>(true); // プレビュー中は元が非アクティブ

            if (avatar == null)
            {
                EditorGUILayout.HelpBox("アバター(VRC Avatar Descriptor)の中に配置してください。", MessageType.Warning);
            }
            else if (fitter.bodies.TrueForAll(b => b == null))
            {
                var auto = BodyFitBuild.FindAutoBody(avatar.transform, fitter);
                EditorGUILayout.HelpBox(
                    auto != null ? $"体のメッシュ: 自動検出 \"{auto.name}\"" : "体のメッシュを自動検出できません。bodies に指定してください。",
                    auto != null ? MessageType.Info : MessageType.Warning);
            }

            DrawSettings();

            using (new EditorGUI.DisabledScope(avatar == null))
            {
                if (GUILayout.Button("検出された衣装メッシュを個別設定 (Per Mesh) に追加"))
                    AddPerMesh(fitter, avatar.transform);
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(avatar == null || EditorApplication.isPlaying))
            {
                if (_preview == null)
                {
                    if (GUILayout.Button("プレビュー (ビルド結果を生成)", GUILayout.Height(28)))
                        CreatePreview(avatar.gameObject);
                }
                else
                {
                    if (GUILayout.Button("プレビューを更新", GUILayout.Height(28)))
                    {
                        var src = _hiddenOriginal;
                        ClearPreview();
                        if (src != null) CreatePreview(src);
                    }
                    if (GUILayout.Button("プレビューを終了"))
                        ClearPreview();
                }
            }

            if (_preview != null)
            {
                EditorGUILayout.HelpBox(
                    "プレビュー中は元のアバターを非表示にしています。\n" +
                    "プレビュー側の衣装の BlendShape \"" + fitter.blendShapeName + "\" を 0↔100 で切り替えると補正前後を比較できます。",
                    MessageType.None);
                BodyFitDebug.DrawInspector();
            }
        }

        static void AddPerMesh(BodyFitter fitter, Transform avatarRoot)
        {
            var bodies = fitter.bodies.Where(b => b != null).ToList();
            if (bodies.Count == 0)
            {
                var auto = BodyFitBuild.FindAutoBody(avatarRoot, fitter);
                if (auto != null) bodies.Add(auto);
            }

            Undo.RecordObject(fitter, "Add Per Mesh Settings");
            int added = 0;
            foreach (var smr in BodyFitBuild.ResolveTargets(fitter, bodies))
            {
                if (fitter.perMesh.Any(o => o != null && o.renderer == smr)) continue;
                fitter.perMesh.Add(new MeshOverride
                {
                    name = smr.name,
                    renderer = smr,
                    margin = fitter.margin,
                    maxDistance = fitter.maxDistance,
                    reverseDepth = fitter.reverseDepth,
                });
                added++;
            }
            EditorUtility.SetDirty(fitter);
            Debug.Log($"[BodyFit] 個別設定に {added} メッシュを追加しました");
        }

        static void CreatePreview(GameObject avatar)
        {
            GameObject result;
            BodyFitDebug.BeginCapture();
            try
            {
                result = AvatarProcessor.ManualProcessAvatar(avatar);
            }
            finally
            {
                BodyFitDebug.EndCapture(null);
            }
            if (result == null) return;

            // NDMF は 2m 前にずらして配置するので、元の位置に重ねて元を隠す
            result.transform.SetPositionAndRotation(avatar.transform.position, avatar.transform.rotation);
            result.name = avatar.name + " (BodyFit Preview)";
            result.hideFlags = HideFlags.DontSaveInEditor;

            avatar.SetActive(false);
            _hiddenOriginal = avatar;
            _preview = result;
            BodyFitDebug.EndCapture(result.transform);
            EditorGUIUtility.PingObject(result);
        }

        static void ClearPreview()
        {
            BodyFitDebug.Clear();
            if (_preview != null) Object.DestroyImmediate(_preview);
            if (_hiddenOriginal != null) _hiddenOriginal.SetActive(true);
            _preview = null;
            _hiddenOriginal = null;
        }

        [InitializeOnLoadMethod]
        static void Init()
        {
            // 再生・シーン保存の前にプレビューを片付ける(元のアバターが非表示のまま保存されないように)
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.ExitingEditMode) ClearPreview();
            };
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += (_, __) => ClearPreview();
            AssemblyReloadEvents.beforeAssemblyReload += ClearPreview;
        }
    }
}
