// Editor/BodyFitProportion.cs
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Hinata.BodyFit.Editor
{
    /// <summary>
    /// 1 段目: ボーンごとの太さ合わせ。
    /// 体と衣装の断面をボーンの区間ごとに比べ、衣装だけに効くスケール用の子ボーンを作って
    /// 衣装メッシュの割り当てを付け替える (MA Scale Adjuster と同じ仕組み)。体型の大きな差をここで吸収し、
    /// 残りの数 mm をソルバーが仕上げる。
    ///
    /// 推定の考え方: 「衣装の一番内側の層は、元の体のどこかに密着していた」とみなす。
    /// 断面を角度で区切り、衣装の内径 / 体の外径 の比のうち小さい方 (密着していた側) を基準に倍率を決める。
    /// 比が大きすぎる (= 全周でゆったりしている) 場合は、元からゆとりのあるデザインとみなして縮めない。
    /// </summary>
    internal static class BodyFitProportion
    {
        internal struct Options
        {
            public float Ease;          // 密着部分に残す隙間 (m)
            public float Strength;      // 0..1 推定倍率をどれだけ適用するか
            public float MinScale;
            public float MaxScale;
            public float LooseRatio;    // 内径/外径 がこれ以上なら「元からゆったり」とみなして縮めない
            public bool KeepShoes;      // 靴 (Foot/Toes) は拡縮しない
            public bool Verbose;        // ボーンごとの倍率・内訳をログに出す
            public Vector3 Forward;     // アバターの前方向 (ワールド)。胴体の前後ずれの補正に使う
        }

        static readonly (HumanBodyBones bone, HumanBodyBones[] next)[] Bones =
        {
            (HumanBodyBones.Hips, new[] { HumanBodyBones.Spine }),
            (HumanBodyBones.Spine, new[] { HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck }),
            (HumanBodyBones.Chest, new[] { HumanBodyBones.UpperChest, HumanBodyBones.Neck }),
            (HumanBodyBones.UpperChest, new[] { HumanBodyBones.Neck }),
            (HumanBodyBones.LeftUpperLeg, new[] { HumanBodyBones.LeftLowerLeg }),
            (HumanBodyBones.RightUpperLeg, new[] { HumanBodyBones.RightLowerLeg }),
            (HumanBodyBones.LeftLowerLeg, new[] { HumanBodyBones.LeftFoot }),
            (HumanBodyBones.RightLowerLeg, new[] { HumanBodyBones.RightFoot }),
            (HumanBodyBones.LeftFoot, new[] { HumanBodyBones.LeftToes }),
            (HumanBodyBones.RightFoot, new[] { HumanBodyBones.RightToes }),
            (HumanBodyBones.LeftToes, new HumanBodyBones[0]),   // 向きは Foot → Toes を流用
            (HumanBodyBones.RightToes, new HumanBodyBones[0]),
            (HumanBodyBones.LeftUpperArm, new[] { HumanBodyBones.LeftLowerArm }),
            (HumanBodyBones.RightUpperArm, new[] { HumanBodyBones.RightLowerArm }),
            (HumanBodyBones.LeftLowerArm, new[] { HumanBodyBones.LeftHand }),
            (HumanBodyBones.RightLowerArm, new[] { HumanBodyBones.RightHand }),
            (HumanBodyBones.LeftHand, new[] { HumanBodyBones.LeftMiddleProximal }),
            (HumanBodyBones.RightHand, new[] { HumanBodyBones.RightMiddleProximal }),
        };

        const int Slices = 4;
        const int Sectors = 12;
        const float DominantWeight = 0.6f;

        sealed class BoneData
        {
            public HumanBodyBones Id;
            public Transform Bone;
            public Matrix4x4 ToLocal;     // ワールド → ボーン空間 (スケールなし)
            public int Axis;              // ボーンの長さ方向 (ローカル軸 0/1/2)
            public float AxisSign;
            public readonly List<Vector3> Body = new List<Vector3>();
            public readonly List<Vector3> Cloth = new List<Vector3>();
            public readonly List<int> ClothMesh = new List<int>();
            public bool Estimated;
            public string Note = "";
            public float Coverage;        // 衣装が断面の何割を囲んでいるか
            public Vector3 Offset;        // 衣装を体の中心へずらす量 (ワールド)
            public readonly List<string> Rejected = new List<string>();
            public bool HasEffect => (Scale - Vector3.one).sqrMagnitude >= 1e-6f || Offset.sqrMagnitude >= 1e-8f;
            public Vector3 Scale = Vector3.one;
            public Vector3 SegA, SegB;    // ボーンの区間 (ワールド)
            public float MeanRadius;      // 体の断面の平均半径
        }

        internal sealed class Result
        {
            public string Log;
        }

        internal static Result Apply(Transform avatarRoot,
                                     IList<SkinnedMeshRenderer> bodies,
                                     IList<(SkinnedMeshRenderer smr, bool[] excluded)> outfits,
                                     Options opt)
        {
            var log = new StringBuilder();
            var animator = avatarRoot.GetComponent<Animator>();
            var result = new Result();
            if (animator == null || !animator.isHuman)
            {
                result.Log = "  体型合わせ: Humanoid の Animator が見つからないためスキップ\n";
                return result;
            }

            // --- 対象ボーンと、それに属するトランスフォーム (MA Scale Adjuster のプロキシ含む) ---
            var data = new List<BoneData>();
            var owner = new Dictionary<Transform, BoneData>();
            foreach (var (id, next) in Bones)
            {
                var b = animator.GetBoneTransform(id);
                if (b == null) continue;

                Transform child = null;
                foreach (var nid in next)
                {
                    child = animator.GetBoneTransform(nid);
                    if (child != null) break;
                }
                var toLocal = Matrix4x4.TRS(b.position, b.rotation, Vector3.one).inverse;
                Vector3 dir, segEnd;
                if (child != null) { dir = toLocal.MultiplyVector(child.position - b.position); segEnd = child.position; }
                else if (b.parent != null) { dir = toLocal.MultiplyVector(b.position - b.parent.position); segEnd = b.position + (b.position - b.parent.position); }
                else continue;
                if (dir.sqrMagnitude < 1e-10f) continue;
                int axis = 0;
                for (int i = 1; i < 3; i++) if (Mathf.Abs(dir[i]) > Mathf.Abs(dir[axis])) axis = i;

                var bd = new BoneData
                {
                    Id = id, Bone = b, ToLocal = toLocal, Axis = axis, AxisSign = Mathf.Sign(dir[axis]),
                    SegA = b.position, SegB = segEnd,
                };
                data.Add(bd);
                owner[b] = bd;
                foreach (Transform c in b)
                    if (c.name == "ScaleProxy") owner[c] = bd;
            }
            if (data.Count == 0) { result.Log = "  体型合わせ: 対象ボーンなし\n"; return result; }

            // --- 体: 支配的なウェイトのボーンで分類 + 近傍探索用グリッド ---
            const float cell = 0.05f;
            var bodyGrid = new Dictionary<Vector3Int, List<(Vector3 p, BoneData bd)>>(FastHash.I);
            Vector3Int CellOf(Vector3 v) => new Vector3Int(
                Mathf.FloorToInt(v.x / cell), Mathf.FloorToInt(v.y / cell), Mathf.FloorToInt(v.z / cell));

            foreach (var smr in bodies)
            {
                foreach (var (world, bone, _) in Dominant(smr, null))
                {
                    var bd = ResolveDirect(bone, owner);
                    if (bd == null) continue;
                    bd.Body.Add(bd.ToLocal.MultiplyPoint3x4(world));
                    var c = CellOf(world);
                    if (!bodyGrid.TryGetValue(c, out var l)) bodyGrid[c] = l = new List<(Vector3, BoneData)>();
                    l.Add((world, bd));
                }
            }

            foreach (var bd in data)
            {
                if (bd.Body.Count == 0) continue;
                int a0 = bd.Axis, u0 = (a0 + 1) % 3, w0 = (a0 + 2) % 3;
                double sr = 0;
                foreach (var p in bd.Body) sr += Mathf.Sqrt(p[u0] * p[u0] + p[w0] * p[w0]);
                bd.MeanRadius = (float)(sr / bd.Body.Count);
            }

            // --- 衣装: ボーンごとに「近くにある体の部位」の多数決で対象ボーンを決める ---
            //   名前や Merge Armature の統合方法に依存しない。
            //   ただしボーンの原点が対象ボーンの軸の近くにあるものだけ (スカートのボーン等は除外 → 平行移動で追従させる)
            var clothVerts = new List<(Vector3 world, Transform bone, int mesh)>();
            int meshIndex = -1;
            var votes = new Dictionary<Transform, Dictionary<BoneData, int>>();
            foreach (var (smr, ex) in outfits)
            {
                meshIndex++;
                foreach (var (world, bone, _) in Dominant(smr, ex))
                {
                    clothVerts.Add((world, bone, meshIndex));
                    var near = NearestBody(world, bodyGrid, CellOf, 0.05f);
                    if (near == null) continue;
                    if (!votes.TryGetValue(bone, out var v)) votes[bone] = v = new Dictionary<BoneData, int>();
                    v.TryGetValue(near, out int n0);
                    v[near] = n0 + 1;
                }
            }

            var map = new Dictionary<Transform, BoneData>();
            foreach (var kv in votes)
            {
                var t = kv.Key;
                var direct = ResolveDirect(t, owner);
                if (direct != null) { map[t] = direct; continue; }

                int total = kv.Value.Values.Sum();
                var best = kv.Value.OrderByDescending(e => e.Value).First();
                if (total < 20 || best.Value < total * 0.5f)
                {
                    if (total >= 20) best.Key.Rejected.Add($"{t.name}: 多数決 {best.Value}/{total}");
                    continue;
                }
                // 「その場で拡縮してよいボーン」= 原点が体の軸の近くにある (関節に相当する) ものだけ。
                // 胸元のリボンのボーンのように体の表面近くにあるボーンを拡縮すると、その場で膨らむだけで
                // 胸の拡大に付いていかず、衣装の内側に沈む。そういうボーンは対応付けず、平行移動で追従させる
                float dist = DistToSegment(t.position, best.Key.SegA, best.Key.SegB);
                float limit = Mathf.Max(0.015f, best.Key.MeanRadius * 0.35f);
                if (dist > limit)
                {
                    best.Key.Rejected.Add($"{t.name}: 軸から {dist * 100f:0.0}cm");
                    continue;
                }
                map[t] = best.Key;
            }

            // 関節の位置合わせは Merge Armature の前 (BodyFitBuild.AlignJoints) で済ませているので、ここでは移動なし
            var snap = new Dictionary<Transform, Vector3>();
            var bdSnap = new Dictionary<BoneData, Vector3>();

            // (上半身の位置は、ユーザーが衣装を置いた位置を基準にする: BodyFitBuild.AlignJoints)
            Vector3 GOf(BoneData b) => Vector3.zero;
            var head = animator.GetBoneTransform(HumanBodyBones.Head);

            foreach (var (world, bone, mi) in clothVerts)
                if (map.TryGetValue(bone, out var bd))
                {
                    snap.TryGetValue(bone, out var sd);
                    bd.Cloth.Add(bd.ToLocal.MultiplyPoint3x4(world + sd + GOf(bd)));
                    bd.ClothMesh.Add(mi);
                }

            // --- ボーンごとに倍率を推定 ---
            var few = new List<string>();
            foreach (var bd in data)
            {
                if (bd.Body.Count < 30 || bd.Cloth.Count < 30)
                {
                    if (bd.Cloth.Count > 0 || bd.Body.Count > 0)
                        few.Add($"{bd.Id}(体{bd.Body.Count}/衣装{bd.Cloth.Count}" +
                                (bd.Rejected.Count > 0 ? $"; 除外: {string.Join(", ", bd.Rejected.Take(3))}" : "") + ")");
                    continue;
                }
                // 手は指で断面が測れないので、対応付けと関節合わせにだけ使う
                if (bd.Id == HumanBodyBones.LeftHand || bd.Id == HumanBodyBones.RightHand) continue;
                Estimate(bd, opt, log);
            }
            // 左右で倍率を揃える (片側だけ小物が乗っている等で推定がぶれるのを防ぐ。片側しかなければ反対側に写す)
            var byId = data.ToDictionary(d => d.Id);
            foreach (var (l, r) in new[]
            {
                (HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg),
                (HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg),
                (HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot),
                (HumanBodyBones.LeftToes, HumanBodyBones.RightToes),
                (HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm),
                (HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm),
                (HumanBodyBones.LeftHand, HumanBodyBones.RightHand),
            })
            {
                if (!byId.TryGetValue(l, out var bl) || !byId.TryGetValue(r, out var br)) continue;
                if (bl.Estimated && br.Estimated)
                {
                    var avg = (bl.Scale + br.Scale) * 0.5f;
                    bl.Scale = br.Scale = avg;
                    bl.Coverage = br.Coverage = Mathf.Max(bl.Coverage, br.Coverage);
                }
                else if (bl.Estimated) { br.Scale = bl.Scale; br.Coverage = bl.Coverage; br.Note = "(左から)"; }
                else if (br.Estimated) { bl.Scale = br.Scale; bl.Coverage = br.Coverage; bl.Note = "(右から)"; }
            }
            // Toes は Foot の倍率を引き継ぐ (つま先側の衣装が少なく推定できないことが多い)
            foreach (var (foot, toes) in new[] { (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes), (HumanBodyBones.RightFoot, HumanBodyBones.RightToes) })
                if (byId.TryGetValue(foot, out var fb) && byId.TryGetValue(toes, out var tb) && !tb.Estimated)
                { tb.Scale = fb.Scale; tb.Coverage = fb.Coverage; tb.Note = "(Foot から)"; }

            if (opt.Verbose)
            foreach (var bd in data)
                if (bd.HasEffect)
                    log.AppendLine($"    {bd.Id}: x{bd.Scale.x:0.00} / y{bd.Scale.y:0.00} / z{bd.Scale.z:0.00}" +
                                   (bd.Offset.sqrMagnitude >= 1e-8f ? $" / 中心ずらし {bd.Offset.magnitude * 1000f:0.0}mm" : "") +
                                   $"  {bd.Note}");
            if (opt.Verbose && few.Count > 0) log.AppendLine($"    サンプル不足: {string.Join(", ", few)}");

            // --- 付け替え ---
            //   対象ボーンに割り当てたボーン: その場で拡縮 (Scale Adjuster と同じ)
            //   それ以外 (リボン・スカート等): 拡縮された祖先に合わせて平行移動
            // 追従するボーンは、そのボーンが動かす頂点の重心がどれだけ動くかで平行移動量を決める
            // (首のボーンに付いた胸元のリボンのように、ボーンの原点と頂点が離れていることがあるため)。メッシュごとに分ける
            var centroid = new Dictionary<(Transform, int), Vector3>();
            {
                var sum = new Dictionary<(Transform, int), (Vector3 s, int n)>();
                foreach (var (world, bone, mi) in clothVerts)
                {
                    sum.TryGetValue((bone, mi), out var e);
                    sum[(bone, mi)] = (e.s + world, e.n + 1);
                }
                foreach (var kv in sum) centroid[kv.Key] = kv.Value.s / kv.Value.n;
            }

            var proxies = new Dictionary<(Transform, int), Transform>();
            var created = new List<Transform>();
            int rebound = 0, followed = 0;
            for (int mi = 0; mi < outfits.Count; mi++)
            {
                var smr = outfits[mi].smr;
                var bones = smr.bones;
                bool changed = false;
                for (int i = 0; i < bones.Length; i++)
                {
                    var t = bones[i];
                    if (t == null) continue;
                    // 対応付けたボーンはメッシュ共通、追従はメッシュごと
                    var key = map.ContainsKey(t) || snap.ContainsKey(t) ? (t, -1) : (t, mi);
                    if (!proxies.TryGetValue(key, out var proxy))
                    {
                        Vector3? c = centroid.TryGetValue((t, mi), out var cv) ? cv : (Vector3?)null;
                        proxy = MakeProxy(t, c, map, snap, owner, bdSnap, avatarRoot, created, ref followed, GOf, head);
                        proxies[key] = proxy;
                    }
                    if (proxy == null) continue;
                    bones[i] = proxy;
                    changed = true;
                }
                if (changed) { smr.bones = bones; rebound++; }
            }

            ProtectFromPhysBones(avatarRoot, created);

            // 診断: メッシュごとに、頂点を多く持つボーンとその扱い
            if (opt.Verbose)
            {
                var counts = new Dictionary<(int, Transform), int>();
                foreach (var (_, bone, mi) in clothVerts)
                {
                    counts.TryGetValue((mi, bone), out int c0);
                    counts[(mi, bone)] = c0 + 1;
                }
                for (int mi = 0; mi < outfits.Count; mi++)
                {
                    var top = counts.Where(kv => kv.Key.Item1 == mi).OrderByDescending(kv => kv.Value).Take(4).ToList();
                    if (top.Count == 0) continue;
                    var parts = new List<string>();
                    foreach (var kv in top)
                    {
                        var t = kv.Key.Item2;
                        var key = map.ContainsKey(t) || snap.ContainsKey(t) ? (t, -1) : (t, mi);
                        proxies.TryGetValue(key, out var px);
                        string how;
                        if (map.TryGetValue(t, out var mb)) how = $"→{mb.Id}";
                        else if (px != null) how = $"追従 {(px.position - t.position).magnitude * 1000f:0.0}mm";
                        else how = "未処理";
                        parts.Add($"{t.name}[{kv.Value}] {how}");
                    }
                    log.AppendLine($"    ボーン内訳 {outfits[mi].smr.name}: {string.Join(", ", parts)}");
                }
            }

            log.Insert(0, $"  体型合わせ: {data.Count(d => d.HasEffect)} ボーンを調整 / " +
                          $"{map.Count} 個の衣装ボーンを対応付け / 追従 {followed} / {rebound} メッシュに適用\n");
            result.Log = log.ToString();
            return result;
        }

        static Transform MakeProxy(Transform t, Vector3? centroid, Dictionary<Transform, BoneData> map, Dictionary<Transform, Vector3> snap,
                                   Dictionary<Transform, BoneData> owner, Dictionary<BoneData, Vector3> bdSnap,
                                   Transform root, List<Transform> created, ref int followed,
                                   System.Func<BoneData, Vector3> gOf, Transform head)
        {
            // 関節に相当するが頂点を直接持たない衣装ボーン: 平行移動のみ
            if (!map.ContainsKey(t) && snap.TryGetValue(t, out var jd))
                return Add(created, NewChild(t, "BodyFitJoint", t.InverseTransformVector(jd), Quaternion.identity, Vector3.one));

            if (map.TryGetValue(t, out var bd))
            {
                snap.TryGetValue(t, out var sd);
                sd += gOf(bd);
                if (!bd.HasEffect && sd.sqrMagnitude < 1e-8f) return null;
                var off = t.InverseTransformVector(bd.Offset + sd);
                // 倍率は対象ボーン (bd.Bone) の軸で決めているので、向きの違うボーンでは
                // 「向きを合わせる → 拡縮 → 向きを戻す」の 3 段にする (行列は親子の積なので歪みも正確に表現される)
                var rel = Quaternion.Inverse(t.rotation) * bd.Bone.rotation;
                if (Quaternion.Angle(rel, Quaternion.identity) < 0.5f)
                    return Add(created, NewChild(t, "BodyFitScale", off, Quaternion.identity, bd.Scale));
                var r1 = Add(created, NewChild(t, "BodyFitScale_R", off, rel, Vector3.one));
                var r2 = NewChild(r1, "BodyFitScale_S", Vector3.zero, Quaternion.identity, bd.Scale);
                return NewChild(r2, "BodyFitScale", Vector3.zero, Quaternion.Inverse(rel), Vector3.one);
            }

            // 拡縮された一番近い祖先を探し、その拡縮でこのボーンの原点がどこへ動くかを求めて平行移動
            for (var a = t.parent; a != null && a != root; a = a.parent)
            {
                bool mappedA = map.TryGetValue(a, out var abd);
                bool snappedA = snap.TryGetValue(a, out var asd);
                // アバター側のボーン (首のボーンの親の胸など): 対応する衣装ボーンと同じ拡縮・移動を使う
                if (!mappedA && owner.TryGetValue(a, out var ob) && ob.Estimated)
                {
                    mappedA = true; abd = ob;
                    snappedA = bdSnap.TryGetValue(ob, out asd);
                }
                if (!mappedA && !snappedA) continue;
                if (!mappedA)
                {
                    // 関節ボーン (頂点なし) の子孫: 関節と同じだけ平行移動
                    followed++;
                    return Add(created, NewChild(t, "BodyFitFollow", t.InverseTransformVector(asd), Quaternion.identity, Vector3.one));
                }
                // a に MA Scale Adjuster (関節合わせでアバターから写したもの等) が付いていれば、その拡縮も含める。
                // Scale Adjuster は a に直接割り当てられたメッシュにしか効かないので、子ボーンに付いた小物 (胸元のリボン等) は置いていかれる
                // 上半身の位置合わせ: 区間の外 (フードなど) も一緒に動かす。ただし頭の下 (帽子など) は動かさない
                bool underHead = false;
                for (var h = t; h != null && h != root; h = h.parent) if (h == head) { underHead = true; break; }
                if (!underHead) asd += gOf(abd);

                var maProxy = a.Find("ScaleProxy");
                var maScale = maProxy != null ? maProxy.localScale : Vector3.one;
                bool hasMa = (maScale - Vector3.one).sqrMagnitude > 1e-6f;
                if (!abd.HasEffect && !hasMa && asd.sqrMagnitude < 1e-8f) return null;

                var rot = abd.Bone.rotation;
                var pivot = centroid ?? t.position;
                var local = Quaternion.Inverse(rot) * (pivot - a.position);
                // 頂点がその区間の外 (胸に対する頭の飾りなど) なら太さの拡縮は効かせず、移動だけ合わせる
                var seg = abd.SegB - abd.SegA;
                float along = seg.sqrMagnitude > 1e-8f ? Vector3.Dot(pivot - abd.SegA, seg) / seg.sqrMagnitude : 0f;
                var sc = along >= -0.25f && along <= 1.25f ? Vector3.Scale(abd.Scale, maScale) : Vector3.one;
                var off = along >= -0.25f && along <= 1.25f ? abd.Offset : Vector3.zero;
                var moved = a.position + asd + off + rot * Vector3.Scale(local, sc);
                var delta = moved - pivot;
                if (delta.sqrMagnitude < 1e-10f) return null;

                followed++;
                return Add(created, NewChild(t, "BodyFitFollow", t.InverseTransformVector(delta), Quaternion.identity, Vector3.one));
            }
            return null;
        }

        static Transform Add(List<Transform> list, Transform t) { list.Add(t); return t; }

        static Transform NewChild(Transform parent, string name, Vector3 localPos, Quaternion localRot, Vector3 localScale)
        {
            var c = new GameObject(name).transform;
            c.SetParent(parent, false);
            c.localPosition = localPos;
            c.localRotation = localRot;
            c.localScale = localScale;
            return c;
        }

        /// <summary>
        /// 追加した子オブジェクトが PhysBone の揺れ骨として扱われないよう、ignoreTransforms に入れる。
        /// </summary>
        static void ProtectFromPhysBones(Transform root, List<Transform> created)
        {
            if (created.Count == 0) return;
            foreach (var pb in root.GetComponentsInChildren<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>(true))
            {
                var pbRoot = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                foreach (var c in created)
                {
                    if (!c.IsChildOf(pbRoot)) continue;
                    if (pb.ignoreTransforms == null) pb.ignoreTransforms = new List<Transform>();
                    if (!pb.ignoreTransforms.Contains(c)) pb.ignoreTransforms.Add(c);
                }
            }
        }

        /// <summary>対象ボーン本体、または Scale Adjuster のプロキシ (体側)。統合された衣装ボーンも含む。</summary>
        static BoneData ResolveDirect(Transform t, Dictionary<Transform, BoneData> owner)
        {
            var cur = t;
            for (int depth = 0; depth < 3 && cur != null; depth++)
            {
                if (owner.TryGetValue(cur, out var bd)) return bd;
                var parent = cur.parent;
                if (parent == null) return null;
                bool proxy = cur.name == "ScaleProxy";
                bool coincident = (cur.position - parent.position).sqrMagnitude <= 0.002f * 0.002f;
                if (!proxy && !coincident) return null;
                cur = parent;
            }
            return null;
        }

        static BoneData NearestBody(Vector3 p, Dictionary<Vector3Int, List<(Vector3 p, BoneData bd)>> grid,
                                    System.Func<Vector3, Vector3Int> cellOf, float radius)
        {
            var c = cellOf(p);
            float best = radius * radius;
            BoneData res = null;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (!grid.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var l)) continue;
                foreach (var e in l)
                {
                    float d2 = (e.p - p).sqrMagnitude;
                    if (d2 < best) { best = d2; res = e.bd; }
                }
            }
            return res;
        }

        static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-10f));
            return (a + ab * t - p).magnitude;
        }

        /// <summary>各頂点の (ワールド位置, 支配的なボーン, ウェイト)。ウェイトが閾値未満・除外の頂点は返さない。</summary>
        static IEnumerable<(Vector3 world, Transform bone, float weight)> Dominant(SkinnedMeshRenderer smr, bool[] excluded)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) yield break;
            var posed = BodyFitSolver.Pose(smr, mesh);
            var bones = smr.bones;
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (bpv.Length == 0) yield break;

            int wi = 0;
            for (int v = 0; v < posed.World.Length; v++)
            {
                int cnt = bpv[v];
                int best = -1; float bw = 0f;
                for (int k = 0; k < cnt; k++, wi++)
                {
                    var w = weights[wi];
                    if (w.weight > bw) { bw = w.weight; best = w.boneIndex; }
                }
                if (best < 0 || bw < DominantWeight || best >= bones.Length || bones[best] == null) continue;
                if (excluded != null && v < excluded.Length && excluded[v]) continue;
                yield return (posed.World[v], bones[best], bw);
            }
        }

        static void Estimate(BoneData bd, Options opt, StringBuilder log)
        {
            int a = bd.Axis, u = (a + 1) % 3, w = (a + 2) % 3;

            float tMin = float.MaxValue, tMax = float.MinValue;
            foreach (var p in bd.Body) { tMin = Mathf.Min(tMin, p[a]); tMax = Mathf.Max(tMax, p[a]); }
            float len = tMax - tMin;
            if (len < 1e-3f) return;

            var bodyR = new float[Slices, Sectors];
            var clothR = new float[Slices, Sectors];
            var bodyL = new List<float>[Slices, Sectors];
            var clothL = new List<float>[Slices, Sectors];
            for (int i = 0; i < Slices; i++)
            for (int j = 0; j < Sectors; j++)
            {
                bodyR[i, j] = -1f; clothR[i, j] = float.MaxValue;
                bodyL[i, j] = new List<float>(); clothL[i, j] = new List<float>();
            }

            bool Cell(Vector3 p, out int si, out int sj, out float r)
            {
                si = Mathf.FloorToInt((p[a] - tMin) / len * Slices);
                sj = 0; r = 0f;
                if (si < 0 || si >= Slices) return false;
                r = Mathf.Sqrt(p[u] * p[u] + p[w] * p[w]);
                if (r < 1e-4f) return false;
                float ang = Mathf.Atan2(p[w], p[u]);
                sj = Mathf.Clamp(Mathf.FloorToInt((ang + Mathf.PI) / (2f * Mathf.PI) * Sectors), 0, Sectors - 1);
                return true;
            }

            // 区間の 3/4 以上にまたがる衣装メッシュだけで測る (太ももベルトのような局所的な小物で推定がぶれないように)
            var sliceMask = new Dictionary<int, int>();
            for (int k = 0; k < bd.Cloth.Count; k++)
            {
                if (!Cell(bd.Cloth[k], out int si, out _, out _)) continue;
                sliceMask.TryGetValue(bd.ClothMesh[k], out int m);
                sliceMask[bd.ClothMesh[k]] = m | (1 << si);
            }
            var useMesh = new HashSet<int>();
            foreach (var kv in sliceMask)
            {
                int bits = 0;
                for (int b = 0; b < Slices; b++) if ((kv.Value & (1 << b)) != 0) bits++;
                if (bits >= Slices - 1) useMesh.Add(kv.Key);
            }
            bool filter = useMesh.Count > 0;

            // 区画ごとの半径は外れ値に強い分位点で取る (体: 外側 90%、衣装: 内側 10%)
            float Pct(List<float> l, float q)
            {
                l.Sort();
                return l[Mathf.Clamp(Mathf.FloorToInt((l.Count - 1) * q), 0, l.Count - 1)];
            }
            void FillCloth(Vector3 shift)
            {
                for (int i = 0; i < Slices; i++)
                for (int j = 0; j < Sectors; j++) { clothL[i, j].Clear(); clothR[i, j] = float.MaxValue; }
                for (int k = 0; k < bd.Cloth.Count; k++)
                {
                    if (filter && !useMesh.Contains(bd.ClothMesh[k])) continue;
                    if (Cell(bd.Cloth[k] + shift, out int i, out int j, out float r)) clothL[i, j].Add(r);
                }
                for (int i = 0; i < Slices; i++)
                for (int j = 0; j < Sectors; j++)
                    if (clothL[i, j].Count >= 3) clothR[i, j] = Pct(clothL[i, j], 0.1f);
            }


            foreach (var p in bd.Body)
                if (Cell(p, out int i, out int j, out float r)) bodyL[i, j].Add(r);
            for (int i = 0; i < Slices; i++)
            for (int j = 0; j < Sectors; j++)
                if (bodyL[i, j].Count >= 3) bodyR[i, j] = Pct(bodyL[i, j], 0.9f);

            FillCloth(Vector3.zero);
            var covered = new bool[Sectors];
            for (int i = 0; i < Slices; i++)
            for (int j = 0; j < Sectors; j++)
                if (clothR[i, j] != float.MaxValue) covered[j] = true;
            bd.Coverage = covered.Count(c => c) / (float)Sectors;

            // 筒状に囲んでいる手足の衣装 (ニーハイ・袖・靴) は、断面の中心を体の中心に合わせる。
            // 胴体は前開き・襟・胸などで断面が非対称なので対象外
            bool limb = bd.Id != HumanBodyBones.Hips && bd.Id != HumanBodyBones.Spine &&
                        bd.Id != HumanBodyBones.Chest && bd.Id != HumanBodyBones.UpperChest;
            if (limb && bd.Coverage >= 0.75f)
            {
                Vector2 bMin = new Vector2(float.MaxValue, float.MaxValue), bMax = -bMin;
                Vector2 cMin = bMin, cMax = -bMin;
                foreach (var p in bd.Body)
                {
                    var q = new Vector2(p[u], p[w]);
                    bMin = Vector2.Min(bMin, q); bMax = Vector2.Max(bMax, q);
                }
                for (int k = 0; k < bd.Cloth.Count; k++)
                {
                    if (filter && !useMesh.Contains(bd.ClothMesh[k])) continue;
                    var p = bd.Cloth[k];
                    if (p[a] < tMin || p[a] > tMax) continue;
                    var q = new Vector2(p[u], p[w]);
                    cMin = Vector2.Min(cMin, q); cMax = Vector2.Max(cMax, q);
                }
                if (cMin.x < cMax.x)
                {
                    var d2 = (bMin + bMax) * 0.5f - (cMin + cMax) * 0.5f;
                    bool footBone = bd.Id == HumanBodyBones.LeftFoot || bd.Id == HumanBodyBones.RightFoot ||
                                    bd.Id == HumanBodyBones.LeftToes || bd.Id == HumanBodyBones.RightToes;
                    float cap = footBone ? 0.06f : 0.03f; // 靴は足首の高さが素体ごとに大きく違う
                    if (d2.magnitude > cap) d2 = d2.normalized * cap;
                    if (d2.magnitude > 0.001f)
                    {
                        var dl = Vector3.zero; dl[u] = d2.x; dl[w] = d2.y;
                        bd.Offset = bd.Bone.rotation * dl;

                        // ずらした後の位置で内径を測り直す
                        FillCloth(dl);
                    }
                }
            }

            string torsoShift = "";

            // 角度ごとに u 方向 / w 方向へ振り分けて比を集める
            var ru = new List<float>(); var rw = new List<float>();
            var bu = new List<float>(); var bw = new List<float>();
            for (int i = 0; i < Slices; i++)
            for (int j = 0; j < Sectors; j++)
            {
                if (bodyR[i, j] <= 0f || clothR[i, j] == float.MaxValue) continue;
                float ratio = clothR[i, j] / bodyR[i, j];
                float ang = -Mathf.PI + (j + 0.5f) * 2f * Mathf.PI / Sectors;
                float cu = Mathf.Abs(Mathf.Cos(ang)), cw = Mathf.Abs(Mathf.Sin(ang));
                if (cu >= 0.7f) { ru.Add(ratio); bu.Add(bodyR[i, j]); }
                if (cw >= 0.7f) { rw.Add(ratio); bw.Add(bodyR[i, j]); }
            }

            // 手足は 1/4 がきつければ全周を合わせる。
            // 胴体は胸の前だけが当たるなど断面が非対称なので、太らせるのは半分以上がきついときだけ (残りは押し出しに任せる)。
            // 縮めるのは手足と同じく、一番きつい 1/4 でさえゆるいときだけ (ゆったりした服の腹回りを細くしない)
            float su, sw, fu, fw;
            if (limb)
            {
                su = Factor(ru, bu, opt, 0.25f, out fu);
                sw = Factor(rw, bw, opt, 0.25f, out fw);
            }
            else
            {
                su = TorsoFactor(ru, bu, opt, out fu);
                sw = TorsoFactor(rw, bw, opt, out fw);
            }

            // 中心ずらしは筒状にぴったりした衣装向け。ゆったりした袖などは元から中心が体とずれている (垂れている) ので、
            // ゆったりしているほど弱める (手首で袖が折れたように持ち上がるのを防ぐ)
            if (limb && bd.Offset.sqrMagnitude > 0f)
            {
                float loose = Mathf.Max(fu, fw);
                float wgt = Mathf.Clamp01((opt.LooseRatio - loose) / Mathf.Max(opt.LooseRatio - 1f, 1e-3f));
                if (loose <= 1f) wgt = 1f;
                bd.Offset *= wgt;
            }

            bool footId = bd.Id == HumanBodyBones.LeftFoot || bd.Id == HumanBodyBones.RightFoot ||
                          bd.Id == HumanBodyBones.LeftToes || bd.Id == HumanBodyBones.RightToes;
            if (footId && opt.KeepShoes)
            {
                // 靴は形の決まった硬いもの。大きさは変えず (足は隠す)、位置合わせだけ行う
                bd.Estimated = true;
                bd.Note = $"(衣装内径/体 = {fu:0.00} / {fw:0.00}, 靴は拡縮しない)";
                return;
            }

            var s = Vector3.one;
            s[u] = su; s[w] = sw;

            // 足とつま先は長さ方向も見る (靴が短くて指が出る対策)。伸ばす方向のみ
            string axial = "";
            bool foot = bd.Id == HumanBodyBones.LeftFoot || bd.Id == HumanBodyBones.RightFoot ||
                        bd.Id == HumanBodyBones.LeftToes || bd.Id == HumanBodyBones.RightToes;
            if (foot)
            {
                float bodyTip = bd.Body.Max(p => p[a] * bd.AxisSign);
                float clothTip = bd.Cloth.Max(p => p[a] * bd.AxisSign);
                if (clothTip > 1e-3f && bodyTip + opt.Ease > clothTip)
                {
                    float sa = Mathf.Lerp(1f, (bodyTip + opt.Ease) / clothTip, opt.Strength);
                    s[a] = Mathf.Min(opt.MaxScale, sa);
                    axial = $" / 長さ x{s[a]:0.00}";
                }
            }
            bd.Scale = s;
            bd.Estimated = true;
            bd.Note = $"(衣装内径/体 = {fu:0.00} / {fw:0.00}{axial}{torsoShift})";
        }

        static float TorsoFactor(List<float> ratios, List<float> bodyRadii, Options opt, out float f)
        {
            float grow = Factor(ratios, bodyRadii, opt, 0.5f, out float f50);
            f = f50;
            if (f50 < 1f && grow > 1f) return grow;     // 半分以上が体より内側
            // 胴体は縮めない (ゆったりしたデザインを細くしない。余りは胸の余りの処理などに任せる)
            return 1f;
        }

        static float Factor(List<float> ratios, List<float> bodyRadii, Options opt, float quantile, out float f)
        {
            f = 1f;
            if (ratios.Count < 3) return 1f;
            // 体の半分以上内側にある衣装 (靴の中敷き・膝当ての裏など) は外れ値として除く
            var sorted = ratios.Where(x => x >= 0.45f).OrderBy(x => x).ToList();
            if (sorted.Count < 3) return 1f;
            f = sorted[Mathf.Clamp(Mathf.FloorToInt(sorted.Count * quantile), 0, sorted.Count - 1)];
            if (f <= 0f) return 1f;
            if (f >= opt.LooseRatio) return 1f; // 全周ゆったり → 元からのデザインとみなす

            float meanR = bodyRadii.Average();
            float target = (1f + opt.Ease / Mathf.Max(meanR, 1e-3f)) / f;
            float s = Mathf.Lerp(1f, target, opt.Strength);
            return Mathf.Clamp(s, opt.MinScale, opt.MaxScale);
        }
    }
}
