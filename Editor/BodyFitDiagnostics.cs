// Editor/BodyFitDiagnostics.cs
// 調査用: 選択した SkinnedMeshRenderer が、どのボーンに付いているかを Console に出す
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Hinata.BodyFit.Editor
{
    internal static class BodyFitDiagnostics
    {
        [MenuItem("Tools/Body Fit/Debug/選択したメッシュのボーンを調べる")]
        static void Dump()
        {
            var sb = new StringBuilder("[BodyFit 調査]\n");
            sb.AppendLine($"  プレイモード: {EditorApplication.isPlaying}");
            foreach (var go in Selection.gameObjects)
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    DumpOne(smr, sb);
            Debug.Log(sb.ToString());
        }

        [MenuItem("Tools/Body Fit/Debug/選択したメッシュのボーンを調べる", true)]
        static bool DumpValidate() => Selection.gameObjects.Length > 0;

        static void DumpOne(SkinnedMeshRenderer smr, StringBuilder sb)
        {
            var animator = smr.GetComponentInParent<Animator>(true);
            var root = animator != null ? animator.transform : smr.transform.root;
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;

            sb.AppendLine($"■ {Path(smr.transform, null)}");
            sb.AppendLine($"  Animator: {(animator == null ? "なし" : $"{animator.name} (enabled {animator.enabled}, humanoid {animator.isHuman}, Hips {(hips != null ? Path(hips, root) : "なし")})")}");
            sb.AppendLine($"  Root Bone: {(smr.rootBone != null ? Path(smr.rootBone, root) : "なし")}");

            var bones = smr.bones;
            var mesh = smr.sharedMesh;
            int nullCount = 0, underHips = 0, outside = 0;
            var outsideList = new List<string>();
            // 実際にウェイトが乗っているボーンだけを見る
            var used = new HashSet<int>();
            if (mesh != null)
                foreach (var bw in mesh.GetAllBoneWeights()) if (bw.weight > 0f) used.Add(bw.boneIndex);
            foreach (int i in used.OrderBy(x => x))
            {
                if (i >= bones.Length) continue;
                var b = bones[i];
                if (b == null) { nullCount++; continue; }
                if (hips != null && (b == hips || b.IsChildOf(hips))) underHips++;
                else { outside++; if (outsideList.Count < 15) outsideList.Add(Path(b, root)); }
            }
            sb.AppendLine($"  ウェイトのあるボーン {used.Count} 本: アバターの Hips 以下 {underHips} / それ以外 {outside} / 空 {nullCount}");
            foreach (var p in outsideList) sb.AppendLine($"    Hips の外: {p}");

            // 代表的なボーンの親子 (アバター側のボーンと一緒に動くか)
            foreach (int i in used.Take(5))
            {
                if (i >= bones.Length || bones[i] == null) continue;
                sb.AppendLine($"    例: {Path(bones[i], root)}");
            }
        }

        static string Path(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            if (root != null) parts.Add(root.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
