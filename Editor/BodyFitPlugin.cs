// Editor/BodyFitPlugin.cs
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Hinata.BodyFit.Editor;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(BodyFitPlugin))]

namespace Hinata.BodyFit.Editor
{
    public class BodyFitPlugin : Plugin<BodyFitPlugin>
    {
        public override string QualifiedName => "hinata.body-fit";
        public override string DisplayName => "Body Fit";

        protected override void Configure()
        {
            // Merge Armature でボーンが統合される前に、除外ボーン → 頂点マスク を確定させる
            InPhase(BuildPhase.Resolving)
                .Run("Resolve Body Fit", BodyFitBuild.Resolve);

            // MA の後 = Scale Adjuster が実ボーン(プロキシ)に変換済みの状態で計算する
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("Apply Body Fit", BodyFitBuild.Apply);
        }
    }

    internal static class BodyFitBuild
    {
        sealed class Job
        {
            public BodyFitter Fitter;
            public List<SkinnedMeshRenderer> Bodies;
            public List<SkinnedMeshRenderer> Targets;
            public Dictionary<SkinnedMeshRenderer, bool[]> Excluded = new Dictionary<SkinnedMeshRenderer, bool[]>();
            public Dictionary<SkinnedMeshRenderer, bool[]> BodyIgnored = new Dictionary<SkinnedMeshRenderer, bool[]>();
            public Dictionary<SkinnedMeshRenderer, MeshOverride> Overrides = new Dictionary<SkinnedMeshRenderer, MeshOverride>();
            public string JointLog;
        }

        sealed class State
        {
            public List<Job> Jobs = new List<Job>();
        }

        internal static void Resolve(BuildContext ctx)
        {
            var state = ctx.GetState<State>();
            var root = ctx.AvatarRootTransform;

            foreach (var fitter in root.GetComponentsInChildren<BodyFitter>(true).OrderBy(f => f.order))
            {
                var job = new Job { Fitter = fitter };

                job.Bodies = fitter.bodies.Where(b => b != null).ToList();
                if (job.Bodies.Count == 0)
                {
                    var auto = FindAutoBody(root, fitter);
                    if (auto != null) job.Bodies.Add(auto);
                }

                // 関節合わせ: Merge Armature が統合する前に、衣装のボーンをアバターの対応ボーンの位置へ移す
                // (MA の「ベースアバターに位置を合わせる」をビルド時に行うのと同じ。アバター側の体型変更後の位置に合う)
                if (fitter.alignJoints) job.JointLog = AlignJoints(fitter, fitter.verboseLog, root.GetComponent<Animator>());

                job.Targets = ResolveTargets(fitter, job.Bodies);
                foreach (var o in fitter.perMesh)
                    if (o != null && o.renderer != null) job.Overrides[o.renderer] = o;

                var ignoreSet = new HashSet<Transform>();
                foreach (var b in fitter.ignoreBodyBones)
                    if (b != null) foreach (var t in b.GetComponentsInChildren<Transform>(true)) ignoreSet.Add(t);
                if (ignoreSet.Count > 0)
                    foreach (var body in job.Bodies)
                        if (body.sharedMesh != null)
                            job.BodyIgnored[body] = BuildMask(body, ignoreSet, fitter.excludeWeightThreshold);

                var excludeSet = BuildExcludeSet(fitter);
                if (excludeSet.Count > 0)
                {
                    foreach (var smr in job.Targets)
                        job.Excluded[smr] = BuildMask(smr, excludeSet, fitter.excludeWeightThreshold);
                }

                state.Jobs.Add(job);
            }
        }

        internal static void Apply(BuildContext ctx)
        {
            var state = ctx.GetState<State>();
            var log = new StringBuilder();
            var root = ctx.AvatarRootTransform;

            foreach (var job in state.Jobs)
            {
                var f = job.Fitter;
                if (f == null) continue;
                bool verbose = f.verboseLog;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var laps = new List<string>();
                long lapAt = 0;
                void Lap(string name) { laps.Add($"{name} {(sw.ElapsedMilliseconds - lapAt) / 1000f:0.0}"); lapAt = sw.ElapsedMilliseconds; }

                log.AppendLine($"■ {f.name}");
                if (!string.IsNullOrEmpty(job.JointLog)) log.AppendLine(job.JointLog);

                var bodies = job.Bodies.Where(b => b != null && b.sharedMesh != null).ToList();
                if (bodies.Count == 0)
                {
                    Debug.LogWarning($"[BodyFit] {f.name}: 体のメッシュが見つかりません (bodies を指定してください)", f);
                    continue;
                }
                var targets = job.Targets.Where(t => t != null && t.sharedMesh != null).ToList();
                bool Skipped(SkinnedMeshRenderer smr) => job.Overrides.TryGetValue(smr, out var o) && o.skip;
                bool[] Mask(SkinnedMeshRenderer smr)
                {
                    job.Excluded.TryGetValue(smr, out var m);
                    return m != null && m.Length == smr.sharedMesh.vertexCount ? m : null;
                }

                float cellSize = f.maxDistance;
                foreach (var o in job.Overrides.Values) cellSize = Mathf.Max(cellSize, o.maxDistance);
                cellSize = Mathf.Max(cellSize, 0.005f);

                if (verbose)
                    foreach (var b in bodies)
                    {
                        var active = new List<string>();
                        for (int i = 0; i < b.sharedMesh.blendShapeCount; i++)
                        {
                            float bw = b.GetBlendShapeWeight(i);
                            if (Mathf.Abs(bw) > 0.01f) active.Add($"{b.sharedMesh.GetBlendShapeName(i)}={bw:0.#}");
                        }
                        log.AppendLine($"  体: {b.name} ({b.sharedMesh.vertexCount} 頂点, {b.sharedMesh.triangles.Length / 3} 面)" +
                                       (active.Count > 0 ? $"  BlendShape: {string.Join(", ", active)}" : ""));
                    }

                // 体型合わせ前の衣装の位置 (小パーツを服の表面に合わせ直すため)
                var prePos = new Dictionary<SkinnedMeshRenderer, Vector3[]>();
                foreach (var smr in targets) prePos[smr] = BodyFitSolver.Pose(smr, smr.sharedMesh).World;
                Lap("前処理");

                // 1. 体型合わせ (衣装のボーン割り当てを、太さを変えたプロキシボーンへ付け替える)
                if (f.autoProportion && f.proportionStrength > 0f)
                {
                    var outfitList = targets.Where(t => !Skipped(t)).Select(t => (t, Mask(t))).ToList();
                    var pr = BodyFitProportion.Apply(root, bodies, outfitList,
                        new BodyFitProportion.Options
                        {
                            Ease = f.margin,
                            Strength = f.proportionStrength,
                            MinScale = f.proportionRange.x,
                            MaxScale = f.proportionRange.y,
                            LooseRatio = f.looseRatio,
                            KeepShoes = f.hideFeetInShoes,
                            Verbose = verbose,
                        });
                    log.Append(pr.Log);
                }
                Lap("体型合わせ");

                // 2. 靴に囲まれた足を隠す (判定からも外し、靴を足の形に膨らませない)
                var hide = f.hideFeetInShoes
                    ? FindFeetInShoes(root, bodies, targets, log, verbose)
                    : new Dictionary<SkinnedMeshRenderer, (byte[] mask, Vector3 left, Vector3 right)>();
                Lap("足の判定");

                // 3. 体の当たり判定
                var limbRoots = Limb.Roots(ctx.AvatarRootObject.GetComponent<Animator>());
                var bustBones = f.fitBust ? FindBustBones(ctx.AvatarRootObject.GetComponent<Animator>()) : new HashSet<Transform>();
                // デバッグ表示は Hips 基準で記録する (後から別のツールが Armature を動かしても表示がずれない)
                var anim = ctx.AvatarRootObject.GetComponent<Animator>();
                var debugAnchor = anim != null && anim.isHuman && anim.GetBoneTransform(HumanBodyBones.Hips) != null
                    ? anim.GetBoneTransform(HumanBodyBones.Hips) : root;
                var collider = new BodyFitSolver.Collider();
                foreach (var b in bodies)
                {
                    var bp = BodyFitSolver.Pose(b, b.sharedMesh);
                    job.BodyIgnored.TryGetValue(b, out var ign);
                    if (ign != null && ign.Length != b.sharedMesh.vertexCount) ign = null;
                    if (hide.TryGetValue(b, out var hm))
                    {
                        var ign2 = new bool[hm.mask.Length];
                        for (int i = 0; i < ign2.Length; i++) ign2[i] = hm.mask[i] != 0 || (ign != null && ign[i]);
                        ign = ign2;
                        int hidden = HideFeet(b, bp, hm.mask, hm.left, hm.right);
                        log.AppendLine($"  {b.name}: 靴の中の足 {hidden} 頂点を BlendShape \"BodyFit_HideFeet\" で隠しました");
                    }
                    // 胸の先などの細かい凹凸を、布が張ったようにならしてから判定に使う (服に体の細部が浮き出ない)
                    if (f.bodySmoothing > 0f) BodyFitSolver.Envelope(bp, f.bodySmoothing);
                    collider.Add(bp, BodyFitSolver.Pose(b, b.sharedMesh, applyBlendShapes: false), ign,
                                 Limb.OfVertices(b, b.sharedMesh, limbRoots), BustVertices(b, bustBones));
                    if (verbose)
                        log.AppendLine($"  体 {b.name}: スキニング差 (手計算 vs Unity) 最大 {bp.BakeDeviation * 1000f:F1} mm" +
                                       (bp.Baked ? "" : "  ※BakeMesh 失敗、手計算を使用"));
                }
                if (verbose && collider.Ignored > 0)
                    log.AppendLine($"  体側: ignoreBodyBones / 靴の中の足 により {collider.Ignored} 面を判定から除外");
                if (verbose && collider.Skipped > 0)
                    log.AppendLine($"  体側: 潰れた/裏返った三角形 {collider.Skipped} 枚を判定から除外");
                collider.Build(cellSize);
                BodyFitDebug.RecordBody(debugAnchor, collider);
                Lap("体の判定");

                // 4. 衣装の粒子を作る
                var prepared = new List<(SkinnedMeshRenderer smr, BodyFitSolver.Target t, BodyFitSolver.Settings s)>();
                var passive = new List<BodyFitSolver.Posed>();
                foreach (var smr in targets)
                {
                    var posed = BodyFitSolver.Pose(smr, smr.sharedMesh);
                    if (Skipped(smr))
                    {
                        passive.Add(posed);   // 補正はしないが、体を覆う面としては使う
                        log.AppendLine($"  {smr.name}: スキップ (個別設定)");
                        continue;
                    }
                    job.Overrides.TryGetValue(smr, out var ov);
                    var settings = ov == null
                        ? new BodyFitSolver.Settings { Margin = f.margin, MaxDistance = f.maxDistance, ReverseDepth = f.reverseDepth }
                        : new BodyFitSolver.Settings { Margin = ov.margin, MaxDistance = ov.maxDistance, ReverseDepth = ov.reverseDepth, Layer = ov.layer, Tight = ov.tight, Falloff = ov.falloff };
                    if (verbose && posed.BakeDeviation > 0.001f)
                        log.AppendLine($"  {smr.name}: スキニング差 最大 {posed.BakeDeviation * 1000f:F1} mm");

                    var t = BodyFitSolver.CreateTarget(posed, Mask(smr));
                    t.Name = smr.name;
                    if (settings.Tight)
                    {
                        var vl = Limb.OfVertices(smr, smr.sharedMesh, limbRoots);
                        if (vl != null)
                        {
                            t.GLimb = new byte[t.GroupCount];
                            for (int i = 0; i < vl.Length; i++) t.GLimb[t.Weld[i]] = vl[i];
                        }
                    }
                    if (prePos.TryGetValue(smr, out var pw) && pw.Length == posed.World.Length)
                    {
                        t.GPre = new Vector3[t.GroupCount];
                        var cnt = new int[t.GroupCount];
                        for (int i = 0; i < pw.Length; i++) { t.GPre[t.Weld[i]] += pw[i]; cnt[t.Weld[i]]++; }
                        for (int g = 0; g < cnt.Length; g++) if (cnt[g] > 0) t.GPre[g] /= cnt[g];
                    }
                    prepared.Add((smr, t, settings));
                }

                Lap("衣装の準備");

                // 5. ソルバー → 小パーツの追従
                var skeleton = BodyFitSolver.Skeleton(ctx.AvatarRootObject.GetComponent<Animator>());
                foreach (var p in prepared) BodyFitSolver.ComputeRadial(p.t, skeleton);
                var r = BodyFitClothSolver.Solve(
                    prepared.Select(p => p.t).ToList(), prepared.Select(p => p.s).ToList(), collider,
                    new BodyFitClothSolver.Params
                    {
                        Iterations = f.solverIterations,
                        Falloff = f.falloff,
                        LayerDistance = f.layerFollowDistance,
                        FitBust = f.fitBust,
                    }, passive);
                Lap("ソルバー");
                int islands = BodyFitSolver.FollowIslands(prepared.Select(p => p.t).ToList(), f.islandMaxSize, 0.03f);
                Lap("小パーツ");
                if (verbose)
                {
                    if (islands > 0) log.AppendLine($"  小パーツ {islands} 個を周囲の面に追従");
                    if (f.fitBust)
                        log.AppendLine(bustBones.Count == 0 ? "  胸の余り: 胸のボーンが見つからないためスキップ"
                                                            : $"  胸の余り: 胸ボーン {bustBones.Count} 本 / {r.BustPulled} 頂点を衣装のゆとりまで引き寄せ" +
                                                              (r.BustLog != null && r.BustLog.Count > 0 ? "\n    " + string.Join("\n    ", r.BustLog) : ""));
                    log.AppendLine($"  ソルバー: 粒子 {r.Particles} / 衝突候補 {r.Candidates} / レイヤー結合 {r.LayerPairs} / 体→衣装の拘束 {r.Bindings} / {r.Milliseconds} ms");
                }

                // 6. BlendShape 化
                int fixedMeshes = 0;
                float maxAll = 0f;
                foreach (var (smr, target, _) in prepared)
                {
                    var src = smr.sharedMesh;
                    var deltas = BodyFitSolver.ToDeltas(target, out var stats);
                    BodyFitDebug.Record(debugAnchor, smr.name, target);
                    if (deltas == null)
                    {
                        log.AppendLine($"  {smr.name}: 補正なし");
                        continue;
                    }

                    var mesh = Object.Instantiate(src);
                    mesh.name = src.name + "_BodyFit";
                    var shapeName = UniqueShapeName(mesh, string.IsNullOrEmpty(f.blendShapeName) ? "BodyFit" : f.blendShapeName);
                    mesh.AddBlendShapeFrame(shapeName, 100f, deltas, null, null);

                    // 既存の BlendShape ウェイトを引き継ぐ (インデックスは末尾追加なので変わらない)
                    var weights = new float[src.blendShapeCount];
                    for (int i = 0; i < weights.Length; i++) weights[i] = smr.GetBlendShapeWeight(i);

                    smr.sharedMesh = mesh;
                    for (int i = 0; i < weights.Length; i++) smr.SetBlendShapeWeight(i, weights[i]);
                    smr.SetBlendShapeWeight(mesh.GetBlendShapeIndex(shapeName), f.blendShapeWeight);

                    fixedMeshes++;
                    maxAll = Mathf.Max(maxAll, stats.MaxPush);
                    bool isTight = job.Overrides.TryGetValue(smr, out var ovl) && ovl.tight;
                    log.AppendLine($"  {smr.name}{(isTight ? " (密着)" : job.Overrides.ContainsKey(smr) ? " (個別設定)" : "")}: " +
                                   $"{stats.Moved} 頂点を補正 (最大 {stats.MaxPush * 1000f:F1} mm)" +
                                   (verbose ? $"  [内側 {target.InsideRay} / 体の突き抜け {target.ReverseHits} / 引き寄せ {target.Pulled}]" : ""));
                }

                Lap("BlendShape");
                log.AppendLine($"  所要時間 (秒): {string.Join(" / ", laps)}");
                log.AppendLine($"  → {fixedMeshes}/{prepared.Count} メッシュを補正 (最大 {maxAll * 1000f:F1} mm) / " +
                               $"ソルバー {r.Milliseconds / 1000f:0.0} 秒 / 合計 {sw.ElapsedMilliseconds / 1000f:0.0} 秒" +
                               (hide.Count > 0 ? " / 靴の中の足を非表示" : ""));
            }

            foreach (var job in state.Jobs)
                if (job.Fitter != null) Object.DestroyImmediate(job.Fitter);

            if (log.Length > 0) Debug.Log("[BodyFit]\n" + log);
        }

        // ---------------------------------------------------------------------

        /// <summary>
        /// 足 (Foot 以下にウェイトが乗る体の頂点) のうち半分以上が衣装に囲まれていれば「靴を履いている」とみなし、
        /// その足の頂点をすべて返す。囲まれている = 6 方向のレイのうち 5 方向以上で衣装に当たる。
        /// </summary>
        static Dictionary<SkinnedMeshRenderer, (byte[] mask, Vector3 left, Vector3 right)> FindFeetInShoes(
            Transform root, List<SkinnedMeshRenderer> bodies, List<SkinnedMeshRenderer> outfits, StringBuilder log, bool verbose)
        {
            var res = new Dictionary<SkinnedMeshRenderer, (byte[], Vector3, Vector3)>();
            var animator = root.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return res;
            var lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf == null || rf == null) return res;

            var side = new Dictionary<Transform, byte>();
            foreach (var t in lf.GetComponentsInChildren<Transform>(true)) side[t] = 1;
            foreach (var t in rf.GetComponentsInChildren<Transform>(true)) side[t] = 2;

            var cover = new BodyFitSolver.Collider();
            foreach (var o in outfits) cover.Add(BodyFitSolver.Pose(o, o.sharedMesh));
            cover.Build(0.01f);

            var dirs = new[] { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            var masks = new Dictionary<SkinnedMeshRenderer, byte[]>();
            int[] total = new int[3], enclosed = new int[3];
            var samples = new List<(Vector3 p, byte side)>();

            foreach (var b in bodies)
            {
                var mesh = b.sharedMesh;
                var bones = b.bones;
                var bpv = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                if (bpv.Length == 0) continue;
                var posed = BodyFitSolver.Pose(b, mesh);
                var mask = new byte[mesh.vertexCount];
                bool any = false;
                int wi = 0;
                for (int v = 0; v < mask.Length; v++)
                {
                    float wl = 0f, wr = 0f;
                    for (int k = 0; k < bpv[v]; k++, wi++)
                    {
                        var bw = weights[wi];
                        if (bw.boneIndex >= bones.Length || bones[bw.boneIndex] == null) continue;
                        if (side.TryGetValue(bones[bw.boneIndex], out var sd))
                        { if (sd == 1) wl += bw.weight; else wr += bw.weight; }
                    }
                    byte s = wl >= 0.5f ? (byte)1 : wr >= 0.5f ? (byte)2 : (byte)0;
                    if (s == 0) continue;
                    mask[v] = s; any = true;
                    samples.Add((posed.World[v], s));
                }
                if (any) masks[b] = mask;
            }

            // 囲まれているかの判定は割合だけ分かればよいので、左右それぞれ最大 600 頂点に間引いて並列で調べる
            var picked = new List<(Vector3 p, byte side)>();
            for (byte sd = 1; sd <= 2; sd++)
            {
                var list = samples.Where(x => x.side == sd).ToList();
                int stride = Mathf.Max(1, list.Count / 600);
                for (int i = 0; i < list.Count; i += stride) picked.Add(list[i]);
            }
            var isEnclosed = new bool[picked.Count];
            System.Threading.Tasks.Parallel.For(0, picked.Count, i =>
            {
                int hits = 0;
                for (int d = 0; d < dirs.Length; d++)
                {
                    if (cover.RayAny(picked[i].p, dirs[d], 0.2f)) hits++;
                    if (hits + (dirs.Length - d - 1) < 5) break;   // もう 5 方向に届かない
                    if (hits >= 5) break;
                }
                isEnclosed[i] = hits >= 5;
            });
            for (int i = 0; i < picked.Count; i++)
            {
                total[picked[i].side]++;
                if (isEnclosed[i]) enclosed[picked[i].side]++;
            }

            bool hideL = total[1] > 0 && enclosed[1] >= total[1] * 0.5f;
            bool hideR = total[2] > 0 && enclosed[2] >= total[2] * 0.5f;
            if (verbose) log.AppendLine($"  足の判定: 左 {enclosed[1]}/{total[1]} 頂点 (間引き後) が衣装に囲まれている{(hideL ? " → 隠す" : "")} / " +
                           $"右 {enclosed[2]}/{total[2]}{(hideR ? " → 隠す" : "")}");
            if (!hideL && !hideR) return res;

            foreach (var kv in masks)
            {
                var m = kv.Value;
                for (int i = 0; i < m.Length; i++)
                    if ((m[i] == 1 && !hideL) || (m[i] == 2 && !hideR)) m[i] = 0;
                res[kv.Key] = (m, lf.position, rf.position);
            }
            return res;
        }

        static string AlignJoints(BodyFitter fitter, bool verbose, Animator animator)
        {
            // 手・足の下 (指) は、名前で対応が取れないボーンを方向で対応付ける
            var dirParents = new HashSet<Transform>();
            if (animator != null && animator.isHuman)
                foreach (var hb in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                {
                    var t = animator.GetBoneTransform(hb);
                    if (t != null) dirParents.Add(t);
                }
            var renamed = new List<string>();

            var mergers = fitter.GetComponentsInChildren<nadena.dev.modular_avatar.core.ModularAvatarMergeArmature>(true);
            if (mergers.Length == 0)
            {
                var own = fitter.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarMergeArmature>();
                if (own == null) return "  関節合わせ: Merge Armature が見つからないためスキップ";
                mergers = new[] { own };
            }

            int n = 0, ns = 0; float max = 0f; string maxName = "";
            var scaled = new List<string>();
            int pairCount = 0, nAdj = 0, nRot = 0, nHand = 0;
            var handLog = new List<string>();
            var hands = new HashSet<Transform>();
            if (animator != null && animator.isHuman)
                foreach (var hb in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
                {
                    var t = animator.GetBoneTransform(hb);
                    if (t != null) hands.Add(t);
                }
            var adjusted = new List<string>();
            var diag = new List<string>();
            foreach (var m in mergers)
            {
                var target = m.mergeTarget.Get(m);
                // MA の GetBonesMapping は名前の完全一致のみで、実際の統合 (正規化した名前で照合) より対応が少ない。
                // ここでも正規化した名前で照合する (Upperleg_L ↔ Upper_leg_L 等)
                var pairs = target != null ? MapBones(m, target.transform, dirParents, renamed) : null;
                diag.Add($"{m.name}→{(target != null ? target.name : "null")} 対応 {(pairs == null ? "null" : pairs.Count.ToString())}組");
                if (pairs == null) continue;
                pairCount += pairs.Count;
                // 確認用: 脚のボーンの組をひとつ表示
                foreach (var (b0, m0) in pairs)
                {
                    if (b0 == null || m0 == null || !b0.name.ToLowerInvariant().Contains("leg")) continue;
                    diag.Add($"例 {b0.name}(scale {b0.localScale.x:0.###}, lossy {b0.lossyScale.x:0.###}) ↔ {m0.name}(scale {m0.localScale.x:0.###}, lossy {m0.lossyScale.x:0.###}, 距離 {(b0.position - m0.position).magnitude * 1000f:0.0}mm)");
                    break;
                }

                int Depth(Transform t) { int d = 0; for (var c = t; c != null; c = c.parent) d++; return d; }
                // 指 (手・足の下) は、位置に加えて骨の向きも合わせる。
                // 位置だけだと、指の付け根の骨に付いたメッシュ (手袋の指の筒) が元の向きのまま残り、アバターの指と交差する
                bool UnderHandOrFoot(Transform t)
                {
                    for (var c = t.parent; c != null; c = c.parent) if (dirParents.Contains(c)) return true;
                    return false;
                }
                // 手 (手のひら) は、指の付け根までの長さの比で大きさも合わせる (体型合わせは手を扱わないため)。
                // 指は拡縮しない: 長さの比で縮めると太さも細くなり、アバターの指に内側から押し出される。長さの違いは位置合わせで足りる
                var childrenOf = new Dictionary<Transform, List<(Transform merge, Transform bas)>>();
                foreach (var (b1, m1) in pairs)
                {
                    if (b1 == null || m1 == null || m1.parent == null) continue;
                    if (!childrenOf.TryGetValue(m1.parent, out var l1)) childrenOf[m1.parent] = l1 = new List<(Transform, Transform)>();
                    l1.Add((m1, b1));
                }
                var childPair = new Dictionary<Transform, (Transform merge, Transform bas)>();
                var childCount = new Dictionary<Transform, int>();
                foreach (var (b1, m1) in pairs)
                {
                    if (b1 == null || m1 == null || m1.parent == null) continue;
                    childCount.TryGetValue(m1.parent, out int cc);
                    childCount[m1.parent] = cc + 1;
                    childPair[m1.parent] = (m1, b1);
                }
                // 親から順に合わせる (親を動かすと子も動くので、その後で子を合わせる。対応のない子は親に付いていく)
                foreach (var (baseBone, mergeBone) in pairs.OrderBy(p => Depth(p.Item2)))
                {
                    if (baseBone == null || mergeBone == null) continue;

                    // ボーン自体のスケール (BSA の Upper_leg 0.8 等) も揃える。
                    // MA は位置しか合わせないので、アバター側だけ縮めると、衣装 (特に統合で付け替えられる靴などの子) が元の大きさのまま残る
                    if ((baseBone.localScale - mergeBone.localScale).sqrMagnitude > 1e-8f)
                    {
                        if (scaled.Count < 8) scaled.Add($"{mergeBone.name} x{baseBone.localScale.x:0.###}");
                        mergeBone.localScale = baseBone.localScale;
                        ns++;
                    }

                    // アバター側の MA Scale Adjuster (BSA の太さ調整) も衣装側へ写す。
                    // Scale Adjuster はそのボーンに直接割り当てられたメッシュにしか効かないので、衣装には効かない
                    var baseAdj = baseBone.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarScaleAdjuster>();
                    if (baseAdj != null && mergeBone.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarScaleAdjuster>() == null)
                    {
                        var adj = mergeBone.gameObject.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarScaleAdjuster>();
                        adj.Scale = baseAdj.Scale;
                        nAdj++;
                        if (adjusted.Count < 8) adjusted.Add($"{mergeBone.name} ({baseAdj.Scale.x:0.##}, {baseAdj.Scale.y:0.##}, {baseAdj.Scale.z:0.##})");
                    }

                    if (hands.Contains(baseBone) && childrenOf.TryGetValue(mergeBone, out var kids))
                    {
                        float rs = 0f; int rn = 0;
                        foreach (var (km, kb) in kids)
                        {
                            if (kb.parent != baseBone) continue;
                            float lm = (km.position - mergeBone.position).magnitude;
                            float lb = (kb.position - baseBone.position).magnitude;
                            if (lm > 1e-4f && lb > 1e-4f) { rs += lb / lm; rn++; }
                        }
                        if (rn > 0)
                        {
                            float ratio = Mathf.Clamp(rs / rn, 0.7f, 1.5f);
                            if (Mathf.Abs(ratio - 1f) > 0.02f)
                            {
                                mergeBone.localScale *= ratio;
                                nHand++;
                                if (handLog.Count < 6) handLog.Add($"{mergeBone.name} x{ratio:0.00}");
                            }
                        }
                    }

                    var d = (baseBone.position - mergeBone.position).magnitude;
                    if (d >= 1e-5f)
                    {
                        mergeBone.position = baseBone.position;
                        n++;
                        if (d > max) { max = d; maxName = mergeBone.name; }
                    }

                    // 指の骨の向き: 子が 1 本だけの骨を、子の方向がアバターと同じになるよう回す
                    if (UnderHandOrFoot(baseBone) &&
                        childCount.TryGetValue(mergeBone, out int nc) && nc == 1 &&
                        childPair.TryGetValue(mergeBone, out var cp) && cp.bas.parent == baseBone)
                    {
                        var from = cp.merge.position - mergeBone.position;
                        var to = cp.bas.position - baseBone.position;
                        if (from.sqrMagnitude > 1e-10f && to.sqrMagnitude > 1e-10f && Vector3.Angle(from, to) > 0.5f)
                        {
                            mergeBone.rotation = Quaternion.FromToRotation(from, to) * mergeBone.rotation;
                            nRot++;
                        }
                    }
                }
            }
            var msg = (verbose ? $"  関節合わせ: Merge Armature {mergers.Length} 個 / {string.Join(" / ", diag)}\n" : "") +
                      (n > 0 ? $"  関節合わせ: {n} ボーンをアバターの位置へ (最大 {max * 1000f:0.0}mm: {maxName})"
                             : $"  関節合わせ: 位置のずれなし (対応 {pairCount} 組)");
            if (ns > 0) msg += $"\n  関節合わせ: {ns} ボーンのスケールをアバターに合わせた" + (verbose ? $" ({string.Join(", ", scaled)})" : "");
            if (nAdj > 0) msg += $"\n  関節合わせ: {nAdj} ボーンにアバターと同じ Scale Adjuster を追加" + (verbose ? $" ({string.Join(", ", adjusted)})" : "");
            if (nRot > 0) msg += $"\n  関節合わせ: 指など {nRot} ボーンの向きをアバターに合わせた";
            if (nHand > 0) msg += $"\n  関節合わせ: 手 {nHand} ボーンの大きさを骨の長さに合わせた" + (verbose ? $" ({string.Join(", ", handLog)})" : "");
            if (renamed.Count > 0)
                msg += $"\n  関節合わせ: 名前の違う指などのボーン {renamed.Count} 本を方向で対応付け、アバターの名前に変更" +
                       (verbose ? $" ({string.Join(", ", renamed.Take(12))})" : "");
            return msg;
        }

        /// <param name="dirParents">この下では、名前で対応しないボーンを方向で対応付ける (アバターの手・足)</param>
        /// <param name="renamed">方向で対応付けて名前を変えたボーン (ログ用)</param>
        static List<(Transform, Transform)> MapBones(nadena.dev.modular_avatar.core.ModularAvatarMergeArmature m, Transform baseRoot,
                                                     HashSet<Transform> dirParents = null, List<string> renamed = null)
        {
            var result = new List<(Transform, Transform)>();
            string prefix = m.prefix ?? "", suffix = m.suffix ?? "";

            void Scan(Transform merge, Transform baseBone, bool byDir)
            {
                byDir |= dirParents != null && dirParents.Contains(baseBone);
                var baseChildren = new Dictionary<string, Transform>();
                foreach (Transform c in baseBone)
                {
                    var key = NormalizeBone(c.name);
                    if (!baseChildren.ContainsKey(key)) baseChildren[key] = c;
                }
                var used = new HashSet<Transform>();
                var unmatched = new List<Transform>();
                foreach (Transform t in merge)
                {
                    if (t.GetComponent<nadena.dev.modular_avatar.core.ModularAvatarMergeArmature>() != null) continue;
                    var name = t.name;
                    if (prefix.Length > 0 && name.StartsWith(prefix)) name = name.Substring(prefix.Length);
                    if (suffix.Length > 0 && name.EndsWith(suffix)) name = name.Substring(0, name.Length - suffix.Length);
                    if (!baseChildren.TryGetValue(NormalizeBone(name), out var b)) { unmatched.Add(t); continue; }
                    used.Add(b);
                    result.Add((b, t));
                    Scan(t, b, byDir);
                }

                // 指のように、クリエイターごとに名前の付け方が違うボーン: 親からの向きと長さが近いものを対応付ける。
                // 衣装側の名前をアバターに合わせ、MA がボーンを統合できるようにする (統合されないと手袋の指が手の動きに付いてこない)
                if (!byDir || unmatched.Count == 0) return;
                var cands = new List<(Transform t, Transform b, float score)>();
                foreach (var t in unmatched)
                {
                    var vt = t.position - merge.position;
                    if (vt.sqrMagnitude < 1e-8f) continue;
                    foreach (Transform b in baseBone)
                    {
                        if (used.Contains(b)) continue;
                        var vb = b.position - baseBone.position;
                        if (vb.sqrMagnitude < 1e-8f) continue;
                        float dot = Vector3.Dot(vt.normalized, vb.normalized);
                        float lenRatio = vt.magnitude / vb.magnitude;
                        if (dot < 0.85f || lenRatio < 0.5f || lenRatio > 2f) continue;
                        cands.Add((t, b, dot));
                    }
                }
                var takenT = new HashSet<Transform>();
                foreach (var (t, b, _) in cands.OrderByDescending(c => c.score))
                {
                    if (takenT.Contains(t) || used.Contains(b)) continue;
                    takenT.Add(t); used.Add(b);
                    var newName = prefix + b.name + suffix;
                    if (t.name != newName)
                    {
                        renamed?.Add($"{t.name}→{newName}");
                        t.name = newName;
                    }
                    result.Add((b, t));
                    Scan(t, b, true);
                }
            }

            Scan(m.transform, baseRoot, false);
            return result;
        }

        static string NormalizeBone(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name.ToLowerInvariant())
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            var s = sb.ToString();
            // "LeftHand" / "Left Hand" → "handl" (左右を末尾にそろえる)
            if (s.StartsWith("left")) s = s.Substring(4) + "l";
            else if (s.StartsWith("right")) s = s.Substring(5) + "r";
            else if (s.EndsWith("left")) s = s.Substring(0, s.Length - 4) + "l";
            else if (s.EndsWith("right")) s = s.Substring(0, s.Length - 5) + "r";
            return s;
        }

        /// <summary>胸のボーン: Chest / UpperChest の下で、名前に breast / bust / mune / oppai / chichi を含むもの (子孫も含む)。</summary>
        static HashSet<Transform> FindBustBones(Animator animator)
        {
            var set = new HashSet<Transform>();
            if (animator == null || !animator.isHuman) return set;
            foreach (var hb in new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest })
            {
                var chest = animator.GetBoneTransform(hb);
                if (chest == null) continue;
                foreach (Transform c in chest)
                {
                    var n = c.name.ToLowerInvariant();
                    if (!(n.Contains("breast") || n.Contains("bust") || n.Contains("mune") || n.Contains("oppai") || n.Contains("chichi"))) continue;
                    foreach (var t in c.GetComponentsInChildren<Transform>(true)) set.Add(t);
                }
            }
            return set;
        }

        static bool[] BustVertices(SkinnedMeshRenderer smr, HashSet<Transform> bustBones)
        {
            if (bustBones.Count == 0) return null;
            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            var isBust = new bool[bones.Length];
            bool any = false;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                // MA の Scale Adjuster で付け替えられた ScaleProxy はボーンの子なので親も見る
                for (var c = bones[i]; c != null && c != smr.transform; c = c.parent)
                    if (bustBones.Contains(c)) { isBust[i] = true; any = true; break; }
            }
            if (!any) return null;
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var res = new bool[mesh.vertexCount];
            if (bpv.Length == 0) return null;
            int wi = 0;
            for (int v = 0; v < res.Length; v++)
            {
                float sum = 0f;
                for (int k = 0; k < bpv[v]; k++, wi++)
                {
                    var bw = weights[wi];
                    if (bw.boneIndex < isBust.Length && isBust[bw.boneIndex]) sum += bw.weight;
                }
                res[v] = sum >= 0.3f;
            }
            return res;
        }

        static int HideFeet(SkinnedMeshRenderer smr, BodyFitSolver.Posed posed, byte[] mask, Vector3 leftAnkle, Vector3 rightAnkle)
        {
            var src = smr.sharedMesh;
            var deltas = new Vector3[src.vertexCount];
            int n = 0;
            for (int i = 0; i < deltas.Length; i++)
            {
                if (mask[i] == 0) continue;
                var target = mask[i] == 1 ? leftAnkle : rightAnkle;
                deltas[i] = posed.Skin[i].inverse.MultiplyVector(target - posed.World[i]);
                n++;
            }
            if (n == 0) return 0;

            var mesh = Object.Instantiate(src);
            mesh.name = src.name + "_BodyFitHide";
            var name = UniqueShapeName(mesh, "BodyFit_HideFeet");
            mesh.AddBlendShapeFrame(name, 100f, deltas, null, null);

            var weights = new float[src.blendShapeCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = smr.GetBlendShapeWeight(i);
            smr.sharedMesh = mesh;
            for (int i = 0; i < weights.Length; i++) smr.SetBlendShapeWeight(i, weights[i]);
            smr.SetBlendShapeWeight(mesh.GetBlendShapeIndex(name), 100f);
            return n;
        }

        internal static List<SkinnedMeshRenderer> ResolveTargets(BodyFitter fitter, List<SkinnedMeshRenderer> bodies)
        {
            return (fitter.targets.Count(t => t != null) > 0
                    ? fitter.targets.Where(t => t != null)
                    : fitter.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                .Where(t => t.sharedMesh != null && !bodies.Contains(t))
                .Distinct()
                .ToList();
        }

        static readonly Dictionary<int, (double time, SkinnedMeshRenderer result)> _autoBodyCache =
            new Dictionary<int, (double, SkinnedMeshRenderer)>();

        /// <summary>
        /// 体 (胴体) のメッシュを自動で探す。VRChat 向けアバターは「Body = 頭部」のことが多いので名前では決めず、
        /// アバター自身の Humanoid ボーンのうち、胴・腕・脚・手・足の何か所にウェイトが乗っているかで選ぶ。
        /// 頭・首だけのメッシュや、まだ統合されていない衣装 (自前のボーンに付いている) は選ばれない。
        /// </summary>
        internal static SkinnedMeshRenderer FindAutoBody(Transform avatarRoot, BodyFitter fitter)
        {
            // Inspector から毎フレーム呼ばれるので短時間キャッシュする
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            int key = fitter.GetInstanceID();
            if (_autoBodyCache.TryGetValue(key, out var c) && now - c.time < 2.0 && c.result != null) return c.result;

            var own = new HashSet<SkinnedMeshRenderer>(fitter.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            var candidates = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s.sharedMesh != null && !own.Contains(s))
                .ToList();

            var result = FindBodyByWeights(avatarRoot, candidates)
                         ?? candidates.FirstOrDefault(s => s.name.Equals("Body", System.StringComparison.OrdinalIgnoreCase))
                         ?? candidates.FirstOrDefault(s => s.transform.parent == avatarRoot &&
                                                           s.name.ToLowerInvariant().Contains("body"));
            _autoBodyCache[key] = (now, result);
            return result;
        }

        static SkinnedMeshRenderer FindBodyByWeights(Transform avatarRoot, List<SkinnedMeshRenderer> candidates)
        {
            var animator = avatarRoot.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return null;

            // Humanoid ボーン → 部位番号 (頭・首・目・顎は対象外、指は手にまとめる)
            var region = new Dictionary<Transform, int>();
            for (var hb = HumanBodyBones.Hips; hb < HumanBodyBones.LastBone; hb++)
            {
                var t = animator.GetBoneTransform(hb);
                if (t == null) continue;
                int r;
                switch (hb)
                {
                    case HumanBodyBones.Head: case HumanBodyBones.Neck: case HumanBodyBones.Jaw:
                    case HumanBodyBones.LeftEye: case HumanBodyBones.RightEye:
                        r = -1; break;
                    default:
                        if (hb >= HumanBodyBones.LeftThumbProximal && hb <= HumanBodyBones.LeftLittleDistal) r = (int)HumanBodyBones.LeftHand;
                        else if (hb >= HumanBodyBones.RightThumbProximal && hb <= HumanBodyBones.RightLittleDistal) r = (int)HumanBodyBones.RightHand;
                        else r = (int)hb;
                        break;
                }
                region[t] = r;
            }

            SkinnedMeshRenderer best = null;
            int bestRegions = 0, bestVerts = 0;
            foreach (var smr in candidates)
            {
                var mesh = smr.sharedMesh;
                var bones = smr.bones;
                if (bones.Length == 0) continue;

                var boneRegion = new int[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    boneRegion[i] = -2; // アバターの Humanoid ボーンの下にない
                    for (var t = bones[i]; t != null && t != avatarRoot; t = t.parent)
                        if (region.TryGetValue(t, out var r)) { boneRegion[i] = r; break; }
                }

                var bpv = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                if (bpv.Length == 0) continue;
                var seen = new HashSet<int>();
                int verts = 0, wi = 0;
                for (int v = 0; v < bpv.Length; v++)
                {
                    float bw = -1f; int bi = -1;
                    for (int k = 0; k < bpv[v]; k++, wi++)
                        if (weights[wi].weight > bw) { bw = weights[wi].weight; bi = weights[wi].boneIndex; }
                    if (bi < 0 || bi >= boneRegion.Length || boneRegion[bi] < 0) continue;
                    seen.Add(boneRegion[bi]);
                    verts++;
                }

                // 部位の数を優先 (体は手足まであるが、服は手・足がないことが多い)、同じなら頂点数
                if (seen.Count > bestRegions || (seen.Count == bestRegions && verts > bestVerts))
                { best = smr; bestRegions = seen.Count; bestVerts = verts; }
            }
            return bestRegions >= 3 ? best : null;
        }

        static HashSet<Transform> BuildExcludeSet(BodyFitter f)
        {
            var set = new HashSet<Transform>();
            foreach (var b in f.excludeBones)
            {
                if (b == null) continue;
                if (f.excludeDescendants)
                    foreach (var t in b.GetComponentsInChildren<Transform>(true)) set.Add(t);
                else
                    set.Add(b);
            }
            return set;
        }

        static bool[] BuildMask(SkinnedMeshRenderer smr, HashSet<Transform> exclude, float threshold)
        {
            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var mask = new bool[mesh.vertexCount];
            if (bpv.Length == 0) return mask;

            var boneExcluded = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++) boneExcluded[i] = bones[i] != null && exclude.Contains(bones[i]);

            int wi = 0;
            for (int v = 0; v < mask.Length; v++)
            {
                float sum = 0f;
                for (int k = 0; k < bpv[v]; k++, wi++)
                {
                    var bw = weights[wi];
                    if (bw.boneIndex < boneExcluded.Length && boneExcluded[bw.boneIndex]) sum += bw.weight;
                }
                mask[v] = sum >= threshold;
            }
            return mask;
        }

        static string UniqueShapeName(Mesh mesh, string baseName)
        {
            var name = baseName;
            for (int i = 1; mesh.GetBlendShapeIndex(name) >= 0; i++) name = $"{baseName}_{i}";
            return name;
        }
    }
}
