// Editor/BodyFitSolver.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Hinata.BodyFit.Editor
{
    /// <summary>
    /// long / Vector3Int キー用のハッシュ。既定のハッシュは「上位 32bit XOR 下位 32bit」等の単純なもので、
    /// 辺キー (頂点番号の組) やグリッド座標では大量に衝突し、辞書が線形探索並みに遅くなるため値をかき混ぜる。
    /// </summary>
    internal sealed class FastHash : IEqualityComparer<long>, IEqualityComparer<Vector3Int>
    {
        internal static readonly FastHash I = new FastHash();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long k) => Mix((ulong)k);
        public bool Equals(Vector3Int a, Vector3Int b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public int GetHashCode(Vector3Int v) =>
            Mix(((ulong)(uint)v.x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)v.y * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)v.z * 0x165667B19E3779F9UL));
        static int Mix(ulong x)
        {
            x ^= x >> 33; x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL;
            x ^= x >> 33;
            return (int)x;
        }
    }

    /// <summary>頂点の部位。ウェイトが最も大きいボーンが、どの Humanoid ボーンの下にあるかで決める。</summary>
    internal static class Limb
    {
        public const byte Torso = 0, LeftArm = 1, RightArm = 2, LeftLeg = 3, RightLeg = 4, Any = 255;

        internal static Dictionary<Transform, byte> Roots(Animator animator)
        {
            var d = new Dictionary<Transform, byte>();
            if (animator == null || !animator.isHuman) return d;
            void Set(HumanBodyBones b, byte v) { var t = animator.GetBoneTransform(b); if (t != null) d[t] = v; }
            Set(HumanBodyBones.LeftUpperArm, LeftArm);
            Set(HumanBodyBones.RightUpperArm, RightArm);
            Set(HumanBodyBones.LeftUpperLeg, LeftLeg);
            Set(HumanBodyBones.RightUpperLeg, RightLeg);
            return d;
        }

        /// <summary>頂点ごとの部位。Humanoid でなければ null。</summary>
        internal static byte[] OfVertices(SkinnedMeshRenderer smr, Mesh mesh, Dictionary<Transform, byte> roots)
        {
            if (roots.Count == 0) return null;
            var bones = smr.bones;
            var boneLimb = new byte[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                boneLimb[i] = Torso;
                for (var c = bones[i]; c != null; c = c.parent)
                    if (roots.TryGetValue(c, out var v)) { boneLimb[i] = v; break; }
            }

            var res = new byte[mesh.vertexCount];
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (bpv.Length == 0) return res;
            int wi = 0;
            for (int v = 0; v < res.Length; v++)
            {
                float best = -1f;
                for (int k = 0; k < bpv[v]; k++, wi++)
                {
                    var bw = weights[wi];
                    if (bw.weight > best && bw.boneIndex < boneLimb.Length) { best = bw.weight; res[v] = boneLimb[bw.boneIndex]; }
                }
            }
            return res;
        }
    }

    internal static class BodyFitSolver
    {
        internal struct Settings
        {
            public float Margin;
            public float MaxDistance;
            public float ReverseDepth;
            public int Layer;
            public bool Tight;           // 体に密着させる (浮いている部分も引き寄せる)
        }

        internal struct Stats
        {
            public int Moved;
            public float MaxPush;
        }

        // ===================================================================
        //  スキニング (現在のボーン姿勢 + BlendShape をワールド座標に展開)
        // ===================================================================

        internal sealed class Posed
        {
            public Vector3[] World;          // ワールド座標の頂点
            public Matrix4x4[] Skin;         // 頂点ごとのスキニング行列 (メッシュ空間 → ワールド)
            public int[] Triangles;
            public float BakeDeviation;      // 手計算スキニングと Unity の BakeMesh の最大差 (m)
            public bool Baked;               // World に BakeMesh の結果を使ったか
        }

        internal static Posed Pose(SkinnedMeshRenderer smr, Mesh mesh, bool applyBlendShapes = true)
        {
            int vc = mesh.vertexCount;
            var verts = mesh.vertices;
            if (applyBlendShapes) ApplyBlendShapes(smr, mesh, verts);

            var skin = new Matrix4x4[vc];
            var bindposes = mesh.bindposes;
            var bones = smr.bones;
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var fallback = smr.transform.localToWorldMatrix;

            if (bindposes.Length == 0 || bpv.Length == 0)
            {
                for (int i = 0; i < vc; i++) skin[i] = fallback;
            }
            else
            {
                var boneMat = new Matrix4x4[bindposes.Length];
                for (int b = 0; b < bindposes.Length; b++)
                {
                    var bt = b < bones.Length ? bones[b] : null;
                    boneMat[b] = bt != null ? bt.localToWorldMatrix * bindposes[b] : fallback;
                }

                int wi = 0;
                for (int v = 0; v < vc; v++)
                {
                    int n = bpv[v];
                    var m = new Matrix4x4();
                    float total = 0f;
                    for (int k = 0; k < n; k++, wi++)
                    {
                        var bw = weights[wi];
                        if (bw.weight <= 0f || bw.boneIndex >= boneMat.Length) continue;
                        AddScaled(ref m, boneMat[bw.boneIndex], bw.weight);
                        total += bw.weight;
                    }
                    skin[v] = total > 1e-6f ? m : fallback;
                }
            }

            var world = new Vector3[vc];
            for (int i = 0; i < vc; i++) world[i] = skin[i].MultiplyPoint3x4(verts[i]);

            var posed = new Posed { World = world, Skin = skin, Triangles = mesh.triangles };
            if (applyBlendShapes) UseUnityBake(smr, posed);
            return posed;
        }

        /// <summary>
        /// 実際に表示される形状と一致させるため、Unity 自身のスキニング結果(BakeMesh)で World を置き換える。
        /// 手計算との差は BakeDeviation に記録する (スキン行列は BlendShape デルタの逆変換に引き続き使う)。
        /// </summary>
        static void UseUnityBake(SkinnedMeshRenderer smr, Posed posed)
        {
            var baked = new Mesh();
            try
            {
                smr.BakeMesh(baked, true);
                var bv = baked.vertices;
                if (bv.Length != posed.World.Length) return;

                // BakeMesh の座標系は Unity バージョンで解釈が揺れるので、2 候補のうち手計算に近いほうを採用
                var t = smr.transform;
                var mA = t.localToWorldMatrix;
                var mB = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                double errA = 0, errB = 0;
                for (int i = 0; i < bv.Length; i += Mathf.Max(1, bv.Length / 2000))
                {
                    errA += (mA.MultiplyPoint3x4(bv[i]) - posed.World[i]).sqrMagnitude;
                    errB += (mB.MultiplyPoint3x4(bv[i]) - posed.World[i]).sqrMagnitude;
                }
                var m = errA <= errB ? mA : mB;

                float dev = 0f;
                for (int i = 0; i < bv.Length; i++)
                {
                    var w = m.MultiplyPoint3x4(bv[i]);
                    dev = Mathf.Max(dev, (w - posed.World[i]).magnitude);
                    posed.World[i] = w;
                }
                posed.BakeDeviation = dev;
                posed.Baked = true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }

        static void AddScaled(ref Matrix4x4 acc, Matrix4x4 m, float w)
        {
            for (int i = 0; i < 16; i++) acc[i] += m[i] * w;
        }

        static void ApplyBlendShapes(SkinnedMeshRenderer smr, Mesh mesh, Vector3[] verts)
        {
            int count = mesh.blendShapeCount;
            if (count == 0) return;

            int vc = mesh.vertexCount;
            var d0 = new Vector3[vc];
            var d1 = new Vector3[vc];
            var dn = new Vector3[vc];
            var dt = new Vector3[vc];

            for (int s = 0; s < count; s++)
            {
                float w = smr.GetBlendShapeWeight(s);
                if (Mathf.Abs(w) < 1e-4f) continue;

                int fc = mesh.GetBlendShapeFrameCount(s);
                float fw0 = mesh.GetBlendShapeFrameWeight(s, 0);

                if (fc == 1 || w <= fw0)
                {
                    mesh.GetBlendShapeFrameVertices(s, 0, d0, dn, dt);
                    float t = fw0 != 0f ? w / fw0 : 0f;
                    for (int i = 0; i < vc; i++) verts[i] += d0[i] * t;
                    continue;
                }

                int k = 0;
                while (k < fc - 2 && w > mesh.GetBlendShapeFrameWeight(s, k + 1)) k++;
                float wa = mesh.GetBlendShapeFrameWeight(s, k);
                float wb = mesh.GetBlendShapeFrameWeight(s, k + 1);
                float u = wb != wa ? (w - wa) / (wb - wa) : 1f;

                mesh.GetBlendShapeFrameVertices(s, k, d0, dn, dt);
                mesh.GetBlendShapeFrameVertices(s, k + 1, d1, dn, dt);
                for (int i = 0; i < vc; i++) verts[i] += Vector3.LerpUnclamped(d0[i], d1[i], u);
            }
        }

        // ===================================================================
        //  体(コライダー)側: 三角形 + 溶接済み頂点法線 + 空間グリッド
        // ===================================================================

        internal enum InsideReason { None, Pseudo, Aligned, Boundary }

        internal sealed class Collider
        {
            readonly List<Vector3> _pos = new List<Vector3>();
            readonly List<int> _tri = new List<int>();
            readonly List<byte> _triLimb = new List<byte>();   // 三角形ごとの部位 (Limb.*)
            readonly List<bool> _triBust = new List<bool>();   // 胸の三角形か
            float _bustMinY = float.MaxValue, _bustMaxY = float.MinValue;
            internal bool HasBust => _bustMinY <= _bustMaxY;
            internal int BustTriangles { get; private set; }
            internal float BustMinY => _bustMinY;
            internal float BustMaxY => _bustMaxY;

            int[] _weld;              // 頂点 → 溶接グループ
            Vector3[] _gSmooth;       // グループごとの面積重み法線 (押し出し方向用)
            Vector3[] _gPseudo;       // グループごとの角度重み擬似法線 (内外判定用)
            Vector3[] _fn;            // 三角形ごとの面法線
            Dictionary<long, Vector3> _edge; // 溶接辺ごとの擬似法線 (隣接面法線の和)

            Dictionary<long, List<int>> _grid;
            float _cell;

            public int Skipped { get; private set; }
            public int TriangleCount => _tri.Count / 3;
            internal IReadOnlyList<Vector3> Positions => _pos;
            internal IReadOnlyList<int> Triangles => _tri;
            internal Vector3 VertexNormal(int i) => _gSmooth[_weld[i]];

            /// <param name="rest">BlendShape 未適用の姿勢。指定すると、BlendShape で潰れた/裏返った三角形を除外する。</param>
            public int Ignored { get; private set; }

            /// <param name="ignore">true の頂点を含む三角形は判定に使わない (靴の中の足など)</param>
            /// <param name="limb">頂点ごとの部位 (Limb.*)。null なら部位なし (どの部位の問い合わせにも当たる)</param>
            /// <param name="bust">頂点ごとに胸か (胸の余りを詰める処理で使う)</param>
            public void Add(Posed posed, Posed rest = null, bool[] ignore = null, byte[] limb = null, bool[] bust = null)
            {
                int baseIndex = _pos.Count;
                _pos.AddRange(posed.World);
                var t = FilterCollapsed(posed, rest);
                for (int i = 0; i < t.Length; i += 3)
                {
                    if (ignore != null && (ignore[t[i]] || ignore[t[i + 1]] || ignore[t[i + 2]])) { Ignored++; continue; }
                    _tri.Add(baseIndex + t[i]); _tri.Add(baseIndex + t[i + 1]); _tri.Add(baseIndex + t[i + 2]);
                    _triLimb.Add(limb != null ? limb[t[i]] : Limb.Any);
                    bool b = bust != null && (bust[t[i]] || bust[t[i + 1]] || bust[t[i + 2]]);
                    _triBust.Add(b);
                    if (b) BustTriangles++;
                    if (b)
                        for (int k = 0; k < 3; k++)
                        {
                            float y = posed.World[t[i + k]].y;
                            if (y < _bustMinY) _bustMinY = y;
                            if (y > _bustMaxY) _bustMaxY = y;
                        }
                }
            }

            /// <summary>最近傍点・なめらかな法線と、そこが胸の面か。スレッドセーフ。</summary>
            public bool ClosestBust(Vector3 p, float radius, out Vector3 point, out Vector3 normal, out bool bust)
            {
                normal = default; bust = false;
                int tri = ClosestTriIndex(p, radius, Limb.Any, out point, out var bc);
                if (tri < 0) return false;
                int g0 = _weld[_tri[tri * 3]], g1 = _weld[_tri[tri * 3 + 1]], g2 = _weld[_tri[tri * 3 + 2]];
                normal = (_gSmooth[g0] * bc.x + _gSmooth[g1] * bc.y + _gSmooth[g2] * bc.z).normalized;
                if (normal.sqrMagnitude < 0.5f) normal = _fn[tri];
                bust = _triBust[tri];
                return true;
            }

            /// <summary>
            /// 指定した部位の三角形だけを対象にした最近傍点と、そこでのなめらかな法線。
            /// 内股で反対の脚に吸い寄せられないよう、密着の引き寄せに使う。スレッドセーフ。
            /// </summary>
            /// <summary>最寄りの三角形の部位。見つからなければ Limb.Any。</summary>
            public byte LimbAt(Vector3 p, float radius)
            {
                int tri = ClosestTriIndex(p, radius, Limb.Any, out _, out _);
                return tri >= 0 ? _triLimb[tri] : Limb.Any;
            }

            public bool ClosestInLimb(Vector3 p, float radius, byte limb, out Vector3 point, out Vector3 normal)
            {
                normal = default;
                int bestTri = ClosestTriIndex(p, radius, limb, out point, out var bestBary);
                if (bestTri < 0) return false;
                int g0 = _weld[_tri[bestTri * 3]], g1 = _weld[_tri[bestTri * 3 + 1]], g2 = _weld[_tri[bestTri * 3 + 2]];
                normal = (_gSmooth[g0] * bestBary.x + _gSmooth[g1] * bestBary.y + _gSmooth[g2] * bestBary.z).normalized;
                if (normal.sqrMagnitude < 0.5f) normal = _fn[bestTri];
                return true;
            }

            int ClosestTriIndex(Vector3 p, float radius, byte limb, out Vector3 point, out Vector3 bestBary)
            {
                point = default; bestBary = default;
                int bestTri = -1;
                float rr = Mathf.Min(_cell, radius);
                while (true)
                {
                    float best = rr * rr;
                    var r = new Vector3(rr, rr, rr);
                    var c0 = CellOf(p - r); var c1 = CellOf(p + r);
                    for (int x = c0.x; x <= c1.x; x++)
                    for (int y = c0.y; y <= c1.y; y++)
                    for (int z = c0.z; z <= c1.z; z++)
                    {
                        if (!_grid.TryGetValue(Key(x, y, z), out var list)) continue;
                        for (int li = 0; li < list.Count; li++)
                        {
                            int ti = list[li];
                            byte tl = _triLimb[ti];
                            if (limb != Limb.Any && tl != Limb.Any && tl != limb) continue;
                            var a = _pos[_tri[ti * 3]]; var b = _pos[_tri[ti * 3 + 1]]; var c = _pos[_tri[ti * 3 + 2]];
                            var q = ClosestOnTriangle(p, a, b, c, out var bc);
                            float d2 = (p - q).sqrMagnitude;
                            if (d2 < best) { best = d2; bestTri = ti; bestBary = bc; point = q; }
                        }
                    }
                    if (bestTri >= 0 || rr >= radius) break;
                    rr = Mathf.Min(rr * 2f, radius);
                }
                return bestTri;
            }

            int[] FilterCollapsed(Posed posed, Posed rest)
            {
                var t = posed.Triangles;
                if (rest == null) return t;

                var kept = new List<int>(t.Length);
                var w = posed.World; var r = rest.World;
                for (int i = 0; i < t.Length; i += 3)
                {
                    var np = Vector3.Cross(w[t[i + 1]] - w[t[i]], w[t[i + 2]] - w[t[i]]);
                    var nr = Vector3.Cross(r[t[i + 1]] - r[t[i]], r[t[i + 2]] - r[t[i]]);
                    float ap = np.magnitude, ar = nr.magnitude;

                    // 痩せさせるシェイプキー (脚を細くする等) では面積が大きく減っても形は正常なので、
                    // ほぼ潰れきった面と、向きが反転した面だけを除外する
                    bool collapsed = ar > 1e-12f && ap < ar * 0.02f;
                    bool flipped = ap > 1e-12f && ar > 1e-12f && Vector3.Dot(np / ap, nr / ar) < -0.2f;
                    if (collapsed || flipped) { Skipped++; continue; }

                    kept.Add(t[i]); kept.Add(t[i + 1]); kept.Add(t[i + 2]);
                }
                return kept.ToArray();
            }

            public void Build(float cell)
            {
                var pos = _pos.ToArray();
                _weld = Weld(pos, out int gc);
                int tc = _tri.Count / 3;

                _fn = new Vector3[tc];
                _gSmooth = new Vector3[gc];
                _gPseudo = new Vector3[gc];
                _edge = new Dictionary<long, Vector3>(FastHash.I);
                _edgeN = new Dictionary<long, int>(FastHash.I);

                for (int ti = 0; ti < tc; ti++)
                {
                    int ia = _tri[ti * 3], ib = _tri[ti * 3 + 1], ic = _tri[ti * 3 + 2];
                    var a = pos[ia]; var b = pos[ib]; var c = pos[ic];
                    var cross = Vector3.Cross(b - a, c - a);
                    if (cross.sqrMagnitude < 1e-20f) continue;
                    var fn = cross.normalized;
                    _fn[ti] = fn;

                    int wa = _weld[ia], wb = _weld[ib], wc = _weld[ic];

                    _gSmooth[wa] += cross; _gSmooth[wb] += cross; _gSmooth[wc] += cross;

                    // 角度重み擬似法線 (Bærentzen & Aanæs 2005)
                    _gPseudo[wa] += fn * Vector3.Angle(b - a, c - a);
                    _gPseudo[wb] += fn * Vector3.Angle(c - b, a - b);
                    _gPseudo[wc] += fn * Vector3.Angle(a - c, b - c);

                    AddEdge(wa, wb, fn); AddEdge(wb, wc, fn); AddEdge(wc, wa, fn);
                }

                for (int g = 0; g < gc; g++)
                {
                    _gSmooth[g] = _gSmooth[g].normalized;
                    _gPseudo[g] = _gPseudo[g].normalized;
                }

                // 穴の縁 (面が片側にしかない辺と、その両端の頂点): 首の切れ目、判定から外した靴の中の足の境目など
                _gBoundary = new bool[gc];
                foreach (var kv in _edgeN)
                {
                    if (kv.Value != 1) continue;
                    _gBoundary[(int)(kv.Key >> 32)] = true;
                    _gBoundary[(int)(kv.Key & 0xffffffffL)] = true;
                }

                // 空間グリッド
                _cell = cell;
                _grid = new Dictionary<long, List<int>>(FastHash.I);
                for (int ti = 0; ti < tc; ti++)
                {
                    if (_fn[ti] == Vector3.zero) continue;
                    var a = pos[_tri[ti * 3]]; var b = pos[_tri[ti * 3 + 1]]; var c = pos[_tri[ti * 3 + 2]];
                    var c0 = CellOf(Vector3.Min(a, Vector3.Min(b, c)));
                    var c1 = CellOf(Vector3.Max(a, Vector3.Max(b, c)));
                    if ((long)(c1.x - c0.x + 1) * (c1.y - c0.y + 1) * (c1.z - c0.z + 1) > 4096) continue;

                    for (int x = c0.x; x <= c1.x; x++)
                    for (int y = c0.y; y <= c1.y; y++)
                    for (int z = c0.z; z <= c1.z; z++)
                    {
                        long key = Key(x, y, z);
                        if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
                        list.Add(ti);
                    }
                }
            }

            void AddEdge(int a, int b, Vector3 fn)
            {
                long k = EdgeKey(a, b);
                _edge.TryGetValue(k, out var v);
                _edge[k] = v + fn;
                _edgeN.TryGetValue(k, out int c);
                _edgeN[k] = c + 1;
            }

            Dictionary<long, int> _edgeN;  // 溶接辺ごとの面の数
            bool[] _gBoundary;              // 穴の縁の頂点

            bool BoundaryEdge(int a, int b) => _edgeN.TryGetValue(EdgeKey(a, b), out int c) && c == 1;

            static long EdgeKey(int a, int b) =>
                a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            /// <summary>
            /// 半径 radius 内の最近傍点を探す。
            /// normal: 押し出し方向用のなめらかな法線 / inside: 擬似法線による内外判定 (凹部でも正しい)
            /// </summary>
            /// <summary>
            /// 最近傍の三角形の頂点番号と重心座標。近くから探索範囲を広げていくので、すぐ近くに面があれば速い。
            /// 読み取りのみなので複数スレッドから同時に呼んでよい。
            /// </summary>
            public bool ClosestTri(Vector3 p, float radius, out int i0, out int i1, out int i2, out Vector3 bary)
            {
                i0 = i1 = i2 = -1; bary = default;
                int bestTri = -1;
                float rr = Mathf.Min(_cell, radius);
                while (true)
                {
                    float best = rr * rr;
                    var r = new Vector3(rr, rr, rr);
                    var c0 = CellOf(p - r); var c1 = CellOf(p + r);
                    for (int x = c0.x; x <= c1.x; x++)
                    for (int y = c0.y; y <= c1.y; y++)
                    for (int z = c0.z; z <= c1.z; z++)
                    {
                        if (!_grid.TryGetValue(Key(x, y, z), out var list)) continue;
                        for (int li = 0; li < list.Count; li++)
                        {
                            int ti = list[li];
                            var a = _pos[_tri[ti * 3]]; var b = _pos[_tri[ti * 3 + 1]]; var c = _pos[_tri[ti * 3 + 2]];
                            var q = ClosestOnTriangle(p, a, b, c, out var bc);
                            float d2 = (p - q).sqrMagnitude;
                            if (d2 < best) { best = d2; bestTri = ti; bary = bc; }
                        }
                    }
                    if (bestTri >= 0 || rr >= radius) break;
                    rr = Mathf.Min(rr * 2f, radius);
                }
                if (bestTri < 0) return false;
                i0 = _tri[bestTri * 3]; i1 = _tri[bestTri * 3 + 1]; i2 = _tri[bestTri * 3 + 2];
                return true;
            }

            /// <summary>レイが何かの面に当たるかだけを調べる (最初に見つかった時点で打ち切る)。スレッドセーフ。</summary>
            public bool RayAny(Vector3 o, Vector3 dir, float maxDist)
            {
                // レイに沿って 1 マスずつ進む (近いマスから調べるので、近くに面があればすぐ終わる)
                int steps = Mathf.CeilToInt(maxDist / _cell) + 1;
                var prev = new Vector3Int(int.MinValue, 0, 0);
                for (int s = 0; s <= steps; s++)
                {
                    var cc = CellOf(o + dir * Mathf.Min(s * _cell, maxDist));
                    // レイが斜めでもマスを飛ばさないよう、周囲 1 マスも調べる (重複は許容)
                    for (int x = cc.x - 1; x <= cc.x + 1; x++)
                    for (int y = cc.y - 1; y <= cc.y + 1; y++)
                    for (int z = cc.z - 1; z <= cc.z + 1; z++)
                    {
                        if (prev.x != int.MinValue && Mathf.Abs(x - prev.x) <= 1 && Mathf.Abs(y - prev.y) <= 1 && Mathf.Abs(z - prev.z) <= 1)
                            continue; // 前のステップで調べたマス
                        if (!_grid.TryGetValue(Key(x, y, z), out var list)) continue;
                        for (int li = 0; li < list.Count; li++)
                        {
                            int ti = list[li];
                            var a = _pos[_tri[ti * 3]]; var b = _pos[_tri[ti * 3 + 1]]; var c = _pos[_tri[ti * 3 + 2]];
                            if (RayTriangle(o, dir, a, b, c, out float d) && d > 1e-6f && d < maxDist) return true;
                        }
                    }
                    prev = cc;
                }
                return false;
            }

            public bool Closest(Vector3 p, float radius, out Vector3 point, out Vector3 normal, out bool inside, out InsideReason reason)
            {
                point = default; normal = default; inside = false; reason = InsideReason.None;
                float best = radius * radius;
                int bestTri = -1;
                Vector3 bestBary = default;

                var r = new Vector3(radius, radius, radius);
                var c0 = CellOf(p - r); var c1 = CellOf(p + r);

                for (int x = c0.x; x <= c1.x; x++)
                for (int y = c0.y; y <= c1.y; y++)
                for (int z = c0.z; z <= c1.z; z++)
                {
                    if (!_grid.TryGetValue(Key(x, y, z), out var list)) continue;
                    for (int li = 0; li < list.Count; li++)
                    {
                        int ti = list[li];
                        var a = _pos[_tri[ti * 3]]; var b = _pos[_tri[ti * 3 + 1]]; var c = _pos[_tri[ti * 3 + 2]];
                        var q = ClosestOnTriangle(p, a, b, c, out var bary);
                        float d2 = (p - q).sqrMagnitude;
                        if (d2 < best) { best = d2; bestTri = ti; bestBary = bary; point = q; }
                    }
                }

                if (bestTri < 0) return false;

                int g0 = _weld[_tri[bestTri * 3]], g1 = _weld[_tri[bestTri * 3 + 1]], g2 = _weld[_tri[bestTri * 3 + 2]];

                // 最近傍点の位置(頂点/辺/面内)に応じた擬似法線で符号を決める
                Vector3 pseudo;
                if (bestBary.x == 1f) pseudo = _gPseudo[g0];
                else if (bestBary.y == 1f) pseudo = _gPseudo[g1];
                else if (bestBary.z == 1f) pseudo = _gPseudo[g2];
                else if (bestBary.z == 0f) pseudo = _edge[EdgeKey(g0, g1)];
                else if (bestBary.y == 0f) pseudo = _edge[EdgeKey(g0, g2)];
                else if (bestBary.x == 0f) pseudo = _edge[EdgeKey(g1, g2)];
                else pseudo = _fn[bestTri];

                normal = (_gSmooth[g0] * bestBary.x + _gSmooth[g1] * bestBary.y + _gSmooth[g2] * bestBary.z).normalized;
                if (normal.sqrMagnitude < 0.5f) normal = _fn[bestTri];

                // 最近傍点が穴の縁 (首の切れ目など) にあるときは内外が決まらないので、内側とはみなさない。
                // 縁の法線は片側の面だけから作られるため、襟のように縁を囲む衣装が「めり込んでいる」と誤判定される
                bool onBoundary =
                    (bestBary.x == 1f && _gBoundary[g0]) || (bestBary.y == 1f && _gBoundary[g1]) || (bestBary.z == 1f && _gBoundary[g2]) ||
                    (bestBary.z == 0f && BoundaryEdge(g0, g1)) || (bestBary.y == 0f && BoundaryEdge(g0, g2)) || (bestBary.x == 0f && BoundaryEdge(g1, g2));
                if (onBoundary) { reason = InsideReason.Boundary; return true; }

                // 1. 擬似法線: 閉じたメッシュなら凹部でも正確だが、体メッシュの重なりや穴があると外れる
                bool pseudoInside = Vector3.Dot(p - point, pseudo) < 0f;

                // 2. 補間法線 + 方向一致: 最近傍点が法線のほぼ真上にあるなら、深い貫通とみなす
                //    (凹部の誤判定では最近傍点が斜め方向になるので弾ける)
                var toSurface = point - p;
                float len = toSurface.magnitude;
                bool alignedInside = len > 1e-6f && Vector3.Dot(toSurface / len, normal) > 0.7071f;

                if (pseudoInside) { inside = true; reason = InsideReason.Pseudo; }
                else if (alignedInside) { inside = true; reason = InsideReason.Aligned; }
                return true;
            }

            /// <summary>レイと体の三角形の最初の交差。exiting: 内側から外へ抜ける面に当たったか。</summary>
            public bool Raycast(Vector3 o, Vector3 dir, float maxDist, out float dist, out bool exiting)
                => RaycastTri(o, dir, maxDist, out dist, out exiting, out _);

            public bool RaycastTri(Vector3 o, Vector3 dir, float maxDist, out float dist, out bool exiting, out int hitTri)
            {
                dist = maxDist; exiting = false; hitTri = -1;
                bool hit = false;
                var end = o + dir * maxDist;
                var c0 = CellOf(Vector3.Min(o, end)); var c1 = CellOf(Vector3.Max(o, end));

                for (int x = c0.x; x <= c1.x; x++)
                for (int y = c0.y; y <= c1.y; y++)
                for (int z = c0.z; z <= c1.z; z++)
                {
                    if (!_grid.TryGetValue(Key(x, y, z), out var list)) continue;
                    foreach (int ti in list)
                    {
                        var a = _pos[_tri[ti * 3]]; var b = _pos[_tri[ti * 3 + 1]]; var c = _pos[_tri[ti * 3 + 2]];
                        if (RayTriangle(o, dir, a, b, c, out float d) && d > 1e-6f && d < dist)
                        {
                            dist = d;
                            exiting = Vector3.Dot(dir, _fn[ti]) > 0f;
                            hitTri = ti;
                            hit = true;
                        }
                    }
                }
                return hit;
            }

            // Möller–Trumbore (両面)
            static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
            {
                t = 0f;
                var e1 = b - a; var e2 = c - a;
                var pv = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, pv);
                if (Mathf.Abs(det) < 1e-12f) return false;
                float inv = 1f / det;
                var tv = o - a;
                float u = Vector3.Dot(tv, pv) * inv;
                if (u < 0f || u > 1f) return false;
                var qv = Vector3.Cross(tv, e1);
                float v = Vector3.Dot(d, qv) * inv;
                if (v < 0f || u + v > 1f) return false;
                t = Vector3.Dot(e2, qv) * inv;
                return true;
            }

            Vector3Int CellOf(Vector3 p) =>
                new Vector3Int(Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));

            static long Key(int x, int y, int z) =>
                ((long)(x + (1 << 20)) << 42) | ((long)(y + (1 << 20)) << 21) | (long)(z + (1 << 20));
        }

        // Real-Time Collision Detection (Ericson) 5.1.5
        static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 bary)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { bary = new Vector3(1, 0, 0); return a; }

            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                bary = new Vector3(1 - v, v, 0); return a + v * ab;
            }

            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                bary = new Vector3(1 - w, 0, w); return a + w * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                bary = new Vector3(0, 1 - w, w); return b + w * (c - b);
            }

            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww = vc * denom;
            bary = new Vector3(1 - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }

        // ===================================================================
        //  衣装側: 溶接済みの頂点グループと、ソルバーの結果
        // ===================================================================

        internal sealed class Target
        {
            internal string Name;
            internal Posed Posed;
            internal int[] Weld;
            internal int GroupCount;
            internal Vector3[] GPos;
            internal bool[] GExcluded;
            internal int InsideRay, ReverseHits, Pulled;
            internal byte[] GClass;
            internal Vector3[] FinalDisp; // 最終的なワールド変位
            internal Vector3[] GRadial;   // 体の骨格から外向きの方向 (深く埋まった頂点の脱出方向)
            internal Vector3[] GPre;      // 体型合わせ前の位置 (小パーツの追従用)
            internal byte[] GLimb;        // 部位 (Limb.*)。密着の引き寄せ先を同じ部位に限る
        }

        internal static Target CreateTarget(Posed outfit, bool[] excluded)
        {
            var w = outfit.World;
            var weld = Weld(w, out int gc);
            var t = new Target
            {
                Posed = outfit, Weld = weld, GroupCount = gc,
                GPos = new Vector3[gc], GExcluded = new bool[gc],
                GClass = new byte[gc],
            };
            for (int i = 0; i < w.Length; i++)
            {
                t.GPos[weld[i]] = w[i];
                if (excluded != null && excluded[i]) t.GExcluded[weld[i]] = true;
            }
            return t;
        }

        /// <summary>デバッグ表示用の頂点の分類</summary>
        internal static class Cls
        {
            public const byte None = 0, Inside = 2, Reverse = 5, Pulled = 6;
        }

        /// <summary>
        /// ボタン・タイピン・金具のような小さな独立パーツ(つながっていない島)を、
        /// 周囲の大きな面の変位にまとめて追従させる。パーツ単位で平行移動するので形が崩れない。
        /// </summary>
        internal static int FollowIslands(IList<Target> targets, float maxSize, float search)
        {
            if (maxSize <= 0f) return 0;

            var islands = new List<(Target t, List<int> groups)>();
            // 大きな面 (島以外) を三角形のまま集め、体型合わせ前の位置で作った当たり判定に入れる。
            // 島 (ボタン等) は、体型合わせ前に一番近かった三角形上の点 (重心座標) が最終的に動いた量に合わせる
            var bigPre = new List<Vector3>();
            var bigDisp = new List<Vector3>();
            var bigTris = new List<int>();

            foreach (var t in targets)
            {
                if (t.FinalDisp == null) continue;
                var pre = t.GPre ?? t.GPos;
                var adj = BuildAdjacency(t.Posed.Triangles, t.Weld, t.GroupCount);
                var comp = new int[t.GroupCount];
                for (int i = 0; i < comp.Length; i++) comp[i] = -1;
                var isBig = new bool[t.GroupCount];

                var stack = new Stack<int>();
                for (int start = 0; start < t.GroupCount; start++)
                {
                    if (comp[start] >= 0) continue;
                    var members = new List<int>();
                    comp[start] = start;
                    stack.Push(start);
                    var min = t.GPos[start]; var max = min;
                    while (stack.Count > 0)
                    {
                        int g = stack.Pop();
                        members.Add(g);
                        min = Vector3.Min(min, t.GPos[g]); max = Vector3.Max(max, t.GPos[g]);
                        foreach (var nb in adj[g])
                            if (comp[nb] < 0) { comp[nb] = start; stack.Push(nb); }
                    }

                    if ((max - min).magnitude <= maxSize) islands.Add((t, members));
                    else foreach (var g in members) isBig[g] = true;
                }

                int offset = bigPre.Count;
                for (int g = 0; g < t.GroupCount; g++)
                {
                    bigPre.Add(pre[g]);
                    bigDisp.Add(t.GPos[g] + t.FinalDisp[g] - pre[g]); // 体型合わせ前からの総移動量
                }
                var tris = t.Posed.Triangles;
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int a = t.Weld[tris[i]], b = t.Weld[tris[i + 1]], c = t.Weld[tris[i + 2]];
                    if (!isBig[a] || !isBig[b] || !isBig[c]) continue;
                    bigTris.Add(offset + a); bigTris.Add(offset + b); bigTris.Add(offset + c);
                }
            }
            if (islands.Count == 0 || bigTris.Count == 0) return 0;

            var col = new Collider();
            col.Add(new Posed { World = bigPre.ToArray(), Triangles = bigTris.ToArray() });
            col.Build(0.01f);

            // 島ごとの移動量 (島の頂点を最大 24 個まで間引いて平均。並列)
            var shift = new Vector3?[islands.Count];
            Parallel.For(0, islands.Count, k =>
            {
                var (t, groups) = islands[k];
                var tpre = t.GPre ?? t.GPos;
                int stride = Mathf.Max(1, groups.Count / 24);
                var sum = Vector3.zero; int cnt = 0;
                for (int j = 0; j < groups.Count; j += stride)
                {
                    int g = groups[j];
                    if (!col.ClosestTri(tpre[g], search, out int i0, out int i1, out int i2, out var bc)) continue;
                    sum += bigDisp[i0] * bc.x + bigDisp[i1] * bc.y + bigDisp[i2] * bc.z;
                    cnt++;
                }
                if (cnt > 0) shift[k] = sum / cnt;
            });

            int moved = 0;
            for (int k = 0; k < islands.Count; k++)
            {
                if (shift[k] == null) continue;
                var (t, groups) = islands[k];
                var tpre = t.GPre ?? t.GPos;
                var v = shift[k].Value;

                bool any = false;
                foreach (var g in groups)
                {
                    if (t.GExcluded[g]) continue;
                    t.FinalDisp[g] = tpre[g] + v - t.GPos[g];
                    any = true;
                }
                if (any) moved++;
            }
            return moved;
        }

        /// <summary>FinalDisp (ワールド) → メッシュ空間の BlendShape デルタ。動かない場合は null。</summary>
        internal static Vector3[] ToDeltas(Target t, out Stats stats)
        {
            stats = default;
            var disp = t.FinalDisp;
            if (disp == null) return null;

            // ワールド変位 → メッシュ空間 (スキニング行列の逆)
            var vc = t.Posed.World.Length;
            var deltas = new Vector3[vc];
            var skin = t.Posed.Skin;
            int moved = 0;
            float maxPush = 0f;
            for (int i = 0; i < vc; i++)
            {
                var d = disp[t.Weld[i]];
                float mag = d.magnitude;
                if (mag < 1e-6f) continue;

                deltas[i] = skin[i].inverse.MultiplyVector(d);
                moved++;
                if (mag > maxPush) maxPush = mag;
            }

            stats = new Stats { Moved = moved, MaxPush = maxPush };
            return moved > 0 ? deltas : null;
        }

        // ===================================================================

        /// <summary>
        /// 体の表面を「布を張った形」に近づける。各点を、隣接点の平均がそれより外側にあれば法線方向にだけ持ち上げる処理を繰り返す。
        /// 凹んだ所 (谷間・へそ・突起の根元) が埋まり、小さな突起は裾の広いなだらかな山になる。突起そのものは削らない。
        /// </summary>
        /// <param name="radius">ならす範囲 (m)。おおよそこの距離まで突起の裾が広がる</param>
        internal static void Envelope(Posed p, float radius, float maxLiftRatio = 0.2f)
        {
            var w = p.World;
            var weld = Weld(w, out int gc);
            var pos = new Vector3[gc];
            var cnt = new int[gc];
            for (int i = 0; i < w.Length; i++) { pos[weld[i]] += w[i]; cnt[weld[i]]++; }
            for (int g = 0; g < gc; g++) if (cnt[g] > 0) pos[g] /= cnt[g];

            var tris = p.Triangles;
            var nrm = new Vector3[gc];
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int a = weld[tris[i]], b = weld[tris[i + 1]], c = weld[tris[i + 2]];
                var cr = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                nrm[a] += cr; nrm[b] += cr; nrm[c] += cr;
            }
            for (int g = 0; g < gc; g++) nrm[g] = nrm[g].normalized;

            var adj = BuildAdjacency(tris, weld, gc);
            double es = 0; int ec = 0;
            for (int g = 0; g < gc; g++) foreach (var q in adj[g]) if (q > g) { es += (pos[g] - pos[q]).magnitude; ec++; }
            float edge = ec > 0 ? (float)(es / ec) : 0.005f;
            // 拡散の広がりは おおよそ 辺の長さ × √反復回数
            float k = radius / Mathf.Max(edge, 1e-4f);
            int iters = Mathf.Clamp(Mathf.RoundToInt(k * k), 1, 400);

            // 持ち上げる量の上限。小さな突起の根元は埋めるが、谷間のような大きなくぼみは埋めすぎない
            float maxLift = radius * maxLiftRatio;
            var cur = (Vector3[])pos.Clone();
            var next = new Vector3[gc];
            for (int it = 0; it < iters; it++)
            {
                Parallel.For(0, gc, g =>
                {
                    next[g] = cur[g];
                    var nb = adj[g];
                    if (nb.Length == 0 || nrm[g].sqrMagnitude < 0.5f) return;
                    var avg = Vector3.zero;
                    foreach (var q in nb) avg += cur[q];
                    avg /= nb.Length;
                    float d = Vector3.Dot(avg - cur[g], nrm[g]);
                    if (d <= 0f) return;
                    float lifted = Vector3.Dot(cur[g] - pos[g], nrm[g]);
                    d = Mathf.Min(d, maxLift - lifted);
                    if (d > 0f) next[g] = cur[g] + nrm[g] * d;
                });
                (cur, next) = (next, cur);
            }

            for (int i = 0; i < w.Length; i++) w[i] += cur[weld[i]] - pos[weld[i]];
        }

        /// <summary>位置が(ほぼ)同じ頂点を同じグループにまとめる。</summary>
        static int[] Weld(Vector3[] pos, out int groupCount)
        {
            const float q = 1e-4f; // 0.1mm
            var map = new Dictionary<Vector3Int, int>(pos.Length, FastHash.I);
            var result = new int[pos.Length];
            int next = 0;
            for (int i = 0; i < pos.Length; i++)
            {
                var key = new Vector3Int(
                    Mathf.RoundToInt(pos[i].x / q), Mathf.RoundToInt(pos[i].y / q), Mathf.RoundToInt(pos[i].z / q));
                if (!map.TryGetValue(key, out int g)) map[key] = g = next++;
                result[i] = g;
            }
            groupCount = next;
            return result;
        }

        /// <summary>
        /// 各頂点の「一番近い体の骨格線 (Humanoid ボーン同士を結んだ線分) から外向き」の方向を求める。
        /// 体に深く埋まった頂点は、最近傍点ではなくこの方向へ押し出す (ニーハイの前面が脚の裏側へ逃げる等を防ぐ)。
        /// 衣装自身のボーン (リボンやスカートの骨) を基準にすると、蝶結びのような形で四方に飛び散るため、体の骨格を使う。
        /// </summary>
        internal static void ComputeRadial(Target t, List<(Vector3 a, Vector3 b)> skeleton)
        {
            t.GRadial = new Vector3[t.GroupCount];
            if (skeleton == null || skeleton.Count == 0) return;
            for (int g = 0; g < t.GroupCount; g++)
            {
                var p = t.GPos[g];
                float best = float.MaxValue; var radial = Vector3.zero;
                foreach (var (a, b) in skeleton)
                {
                    var ab = b - a;
                    float u = ab.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                    var r = p - (a + ab * u);
                    float d2 = r.sqrMagnitude;
                    if (d2 < best) { best = d2; radial = r; }
                }
                if (radial.sqrMagnitude > 1e-8f) t.GRadial[g] = radial.normalized;
            }
        }

        internal static List<(Vector3 a, Vector3 b)> Skeleton(Animator animator)
        {
            var list = new List<(Vector3, Vector3)>();
            if (animator == null || !animator.isHuman) return list;
            void Seg(HumanBodyBones x, params HumanBodyBones[] ys)
            {
                var tx = animator.GetBoneTransform(x);
                if (tx == null) return;
                foreach (var y in ys)
                {
                    var ty = animator.GetBoneTransform(y);
                    if (ty != null) { list.Add((tx.position, ty.position)); return; }
                }
            }
            Seg(HumanBodyBones.Hips, HumanBodyBones.Spine);
            Seg(HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck);
            Seg(HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck);
            Seg(HumanBodyBones.UpperChest, HumanBodyBones.Neck);
            Seg(HumanBodyBones.Neck, HumanBodyBones.Head);
            foreach (var (s, ua, la, h, ul, ll, f, toe) in new[]
            {
                (HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                 HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes),
                (HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                 HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes),
            })
            {
                Seg(s, ua); Seg(ua, la); Seg(la, h);
                Seg(HumanBodyBones.Hips, ul); Seg(ul, ll); Seg(ll, f); Seg(f, toe);
            }
            return list;
        }

        internal static int[][] BuildAdjacency(int[] tris, int[] weld, int gc)
        {
            var sets = new HashSet<int>[gc];
            void Link(int a, int b)
            {
                if (a == b) return;
                (sets[a] ??= new HashSet<int>()).Add(b);
                (sets[b] ??= new HashSet<int>()).Add(a);
            }

            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = weld[tris[i]], b = weld[tris[i + 1]], c = weld[tris[i + 2]];
                Link(a, b); Link(b, c); Link(c, a);
            }

            var adj = new int[gc][];
            for (int g = 0; g < gc; g++)
            {
                if (sets[g] == null) { adj[g] = Array.Empty<int>(); continue; }
                adj[g] = new int[sets[g].Count];
                sets[g].CopyTo(adj[g]);
            }
            return adj;
        }
    }
}
