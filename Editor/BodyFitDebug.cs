// Editor/BodyFitDebug.cs
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hinata.BodyFit.Editor
{
    /// <summary>
    /// プレビュー生成時に頂点ごとの判定結果と押し出しを記録し、シーンビューに線で描く。
    /// 座標はアバタールート基準で保存するので、プレビューの位置を動かしても追従する。
    /// </summary>
    internal static class BodyFitDebug
    {
        internal sealed class Entry
        {
            public string Name;
            public Vector3[] Pos;        // ルート基準
            public Vector3[] Final;      // 変位 (ルート基準)
            public byte[] Class;
        }

        static bool _capturing;
        static Vector3[] _bodySegs;
        internal static bool ShowBody = true;
        static readonly List<Entry> _entries = new List<Entry>();

        internal static Transform Root;
        // 記録の基準 (アバターの Hips)。Body Fit の後で別のツールが Armature を動かしても表示が付いていくようにする
        static Transform _anchor;
        internal static bool Visible = true;
        internal static bool ShowSmoothOnly;
        internal static bool ThroughMesh = true;
        internal static int Filter; // 0 = すべて

        internal static IReadOnlyList<Entry> Entries => _entries;
        internal static bool HasData => _entries.Count > 0 && Root != null;

        static readonly Color CInside = new Color(1f, 0.15f, 0.15f);
        static readonly Color CReverse = new Color(1f, 0.55f, 0f);
        static readonly Color CPulled = new Color(0.3f, 1f, 0.4f);
        static readonly Color CSmooth = new Color(0.2f, 0.8f, 1f, 0.6f);

        internal static void BeginCapture()
        {
            _entries.Clear();
            _bodySegs = null;
            Root = null;
            _anchor = null;
            Filter = 0;
            _capturing = true;
        }

        internal static void EndCapture(Transform root)
        {
            _capturing = false;
            Root = root != null && _anchor != null ? _anchor : root;
            SceneView.RepaintAll();
        }

        internal static void Clear()
        {
            _capturing = false;
            _entries.Clear();
            _bodySegs = null;
            Root = null;
            SceneView.RepaintAll();
        }

        internal static void Record(Transform avatarRoot, string name, BodyFitSolver.Target t)
        {
            if (!_capturing) return;
            _anchor = avatarRoot;
            RecordCore(avatarRoot, name, t);
        }

        static void RecordCore(Transform avatarRoot, string name, BodyFitSolver.Target t)
        {
            if (!_capturing) return;

            var pos = new List<Vector3>();
            var fin = new List<Vector3>();
            var cls = new List<byte>();

            for (int g = 0; g < t.GroupCount; g++)
            {
                var f = t.FinalDisp != null ? t.FinalDisp[g] : Vector3.zero;
                if (t.GClass[g] == BodyFitSolver.Cls.None && f.sqrMagnitude < 1e-12f) continue;

                pos.Add(avatarRoot.InverseTransformPoint(t.GPos[g]));
                fin.Add(avatarRoot.InverseTransformVector(f));
                cls.Add(t.GClass[g]);
            }

            _entries.Add(new Entry
            {
                Name = name, Pos = pos.ToArray(), Final = fin.ToArray(), Class = cls.ToArray(),
            });
        }

        /// <summary>判定に使った体の面をワイヤーフレームとして記録する。</summary>
        internal static void RecordBody(Transform avatarRoot, BodyFitSolver.Collider collider)
        {
            if (!_capturing) return;
            _anchor = avatarRoot;
            var pos = collider.Positions;
            var tri = collider.Triangles;
            var seen = new HashSet<long>(FastHash.I);
            var segs = new List<Vector3>(tri.Count * 2);

            void Edge(int a, int b)
            {
                long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seen.Add(k)) return;
                segs.Add(avatarRoot.InverseTransformPoint(pos[a]));
                segs.Add(avatarRoot.InverseTransformPoint(pos[b]));
            }

            for (int i = 0; i + 2 < tri.Count; i += 3)
            {
                Edge(tri[i], tri[i + 1]); Edge(tri[i + 1], tri[i + 2]); Edge(tri[i + 2], tri[i]);
            }

            var all = _bodySegs == null ? segs : _bodySegs.Concat(segs).ToList();
            _bodySegs = all.ToArray();
        }

        internal static int Count(Entry e, byte cls) => e.Class.Count(c => c == cls);

        [InitializeOnLoadMethod]
        static void Init() => SceneView.duringSceneGui += OnSceneGUI;

        static void OnSceneGUI(SceneView view)
        {
            if (!Visible || !HasData || Event.current.type != EventType.Repaint) return;

            var inside = new List<Vector3>();
            var reverse = new List<Vector3>();
            var pulled = new List<Vector3>();
            var smooth = new List<Vector3>();

            for (int ei = 0; ei < _entries.Count; ei++)
            {
                if (Filter != 0 && Filter - 1 != ei) continue;
                var e = _entries[ei];
                for (int i = 0; i < e.Pos.Length; i++)
                {
                    var p = e.Pos[i];
                    switch (e.Class[i])
                    {
                        case BodyFitSolver.Cls.Inside: inside.Add(p); inside.Add(p + e.Final[i]); break;
                        case BodyFitSolver.Cls.Reverse: reverse.Add(p); reverse.Add(p + e.Final[i]); break;
                        case BodyFitSolver.Cls.Pulled: pulled.Add(p); pulled.Add(p + e.Final[i]); break;
                        default:
                            if (ShowSmoothOnly) { smooth.Add(p); smooth.Add(p + e.Final[i]); }
                            break;
                    }
                }
            }

            var oldMatrix = Handles.matrix;
            var oldZ = Handles.zTest;
            Handles.matrix = Root.localToWorldMatrix;
            Handles.zTest = ThroughMesh ? UnityEngine.Rendering.CompareFunction.Always
                                        : UnityEngine.Rendering.CompareFunction.LessEqual;

            if (ShowBody && _bodySegs != null)
            {
                Handles.color = new Color(1f, 1f, 1f, 0.25f);
                Handles.DrawLines(_bodySegs);
            }

            Draw(smooth, CSmooth);
            Draw(inside, CInside);
            Draw(reverse, CReverse);
            Draw(pulled, CPulled);

            Handles.matrix = oldMatrix;
            Handles.zTest = oldZ;
        }

        static void Draw(List<Vector3> segs, Color c)
        {
            if (segs.Count == 0) return;
            Handles.color = c;
            Handles.DrawLines(segs.ToArray());
        }

        internal static void DrawInspector()
        {
            if (!HasData) return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("デバッグ表示", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            Visible = EditorGUILayout.Toggle("表示", Visible);
            ThroughMesh = EditorGUILayout.Toggle("メッシュ越しに表示", ThroughMesh);
            ShowBody = EditorGUILayout.Toggle("判定に使った体(白線)を表示", ShowBody);
            ShowSmoothOnly = EditorGUILayout.Toggle("周囲の追従(水色)も表示", ShowSmoothOnly);

            var names = new[] { "すべて" }.Concat(_entries.Select(e => e.Name)).ToArray();
            Filter = EditorGUILayout.Popup("メッシュ", Mathf.Clamp(Filter, 0, names.Length - 1), names);
            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();

            int ci = 0, cr = 0, cp = 0;
            for (int ei = 0; ei < _entries.Count; ei++)
            {
                if (Filter != 0 && Filter - 1 != ei) continue;
                ci += Count(_entries[ei], BodyFitSolver.Cls.Inside);
                cr += Count(_entries[ei], BodyFitSolver.Cls.Reverse);
                cp += Count(_entries[ei], BodyFitSolver.Cls.Pulled);
            }

            EditorGUILayout.HelpBox(
                $"赤: 内側と判定 → 押し出し ({ci})\n" +
                $"橙: 体が衣装の面を突き抜け → 押し出し ({cr})\n" +
                $"緑: 密着 → 体へ引き寄せ ({cp})\n" +
                "水色: 周囲に合わせて動いただけ\n" +
                "線は 補正前の頂点位置 → 補正後の位置",
                MessageType.None);
        }
    }
}
