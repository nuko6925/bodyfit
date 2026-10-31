// Editor/BodyFitClothSolver.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Hinata.BodyFit.Editor
{
    /// <summary>
    /// 静的な布ソルバー。同じ Body Fitter の衣装メッシュ全体を 1 つの系として、次の制約を反復で同時に満たす。
    ///   - 体との非貫通 (衣装の頂点が体の内側にない / 体の頂点が衣装の面を抜けない)
    ///   - 形状保持 (変位がなめらか = 局所的な形を保つ。離れた場所は元の位置に戻ろうとする)
    ///   - レイヤー結合 (つながっていない近接面 = 裏地と表地・シャツとネクタイ・ボタン等を一緒に動かす)
    /// 結果は Target.FinalDisp (ワールド変位) に書き込む。
    /// </summary>
    internal static class BodyFitClothSolver
    {
        internal struct Params
        {
            public int Iterations;
            public float Falloff;        // なじませ範囲 (m)
            public float LayerDistance;  // レイヤー結合の距離 (m)
            public bool FitBust;         // 胸の余りを詰める
            public bool KeepHiddenLayers;// 表地が体の外にあれば、内側の層のめり込みは直さない
        }

        internal struct Result
        {
            public int Particles;
            public int Candidates;
            public int LayerPairs;
            public int Bindings;
            public int BustPulled;
            public List<string> BustLog;
            public long Milliseconds;
        }

        /// <param name="passive">補正しないが「外側を覆う面」として扱う衣装 (個別設定でスキップしたメッシュ)</param>
        internal static Result Solve(IList<BodyFitSolver.Target> targets, IList<BodyFitSolver.Settings> settings,
                                     BodyFitSolver.Collider body, Params prm, IList<BodyFitSolver.Posed> passive = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new Result();

            // ---------------------------------------------------------------
            // 粒子 = 全ターゲットの溶接済み頂点グループ
            // ---------------------------------------------------------------
            int n = 0;
            var offset = new int[targets.Count];
            for (int ti = 0; ti < targets.Count; ti++) { offset[ti] = n; n += targets[ti].GroupCount; }
            res.Particles = n;
            if (n == 0) return res;

            var x0 = new Vector3[n];
            var pinned = new bool[n];
            var margin = new float[n];
            var maxDist = new float[n];
            var reach = new float[n];      // 逆方向判定の範囲
            var layer = new int[n];        // 重ね順 (大きいほど外側。個別設定)
            var gamma = new float[n];      // 元の位置へ戻る強さ
            var owner = new int[n];
            var nbrs = new int[n][];
            var axis = new Vector3[n];     // 面の向き (表裏は区別しない軸)
            var radial = new Vector3[n];   // ボーンの軸から外向き (深い貫通の脱出方向)
            var tight = new bool[n];       // 体に密着させる
            var limb = new byte[n];        // 部位 (密着の引き寄せ先を同じ部位に限る)

            var allTris = new List<int>();
            for (int ti = 0; ti < targets.Count; ti++)
            {
                var t = targets[ti];
                var s = settings[ti];
                int o = offset[ti];
                var adj = BodyFitSolver.BuildAdjacency(t.Posed.Triangles, t.Weld, t.GroupCount);

                // 平均辺長からなじませ範囲を「辺の数」に換算 → 減衰率
                double edgeSum = 0; int edgeCnt = 0;
                for (int g = 0; g < t.GroupCount; g++)
                    foreach (var nb in adj[g])
                        if (nb > g) { edgeSum += (t.GPos[g] - t.GPos[nb]).magnitude; edgeCnt++; }
                float edge = edgeCnt > 0 ? (float)(edgeSum / edgeCnt) : 0.005f;
                float k = Mathf.Max(1f, (s.Falloff > 0f ? s.Falloff : prm.Falloff) / Mathf.Max(edge, 1e-4f));
                float gm = Mathf.Clamp(1f / (k * k), 0.0005f, 0.5f);

                for (int g = 0; g < t.GroupCount; g++)
                {
                    int p = o + g;
                    x0[p] = t.GPos[g];
                    pinned[p] = t.GExcluded[g];
                    margin[p] = s.Margin;
                    maxDist[p] = s.MaxDistance;
                    reach[p] = s.ReverseDepth;
                    layer[p] = s.Layer;
                    tight[p] = s.Tight;
                    limb[p] = t.GLimb != null ? t.GLimb[g] : Limb.Any;
                    gamma[p] = gm;
                    owner[p] = ti;
                    if (t.GRadial != null) radial[p] = t.GRadial[g];
                    var a = new int[adj[g].Length];
                    for (int j = 0; j < a.Length; j++) a[j] = o + adj[g][j];
                    nbrs[p] = a;
                }

                var tris = t.Posed.Triangles;
                for (int i = 0; i < tris.Length; i++) allTris.Add(o + t.Weld[tris[i]]);

                // 元の頂点ごとの法線を、向きを揃えてからグループに合成 (両面化の重複頂点対策)
                var w = t.Posed.World;
                var vn = new Vector3[w.Length];
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    var cr = Vector3.Cross(w[b] - w[a], w[c] - w[a]);
                    vn[a] += cr; vn[b] += cr; vn[c] += cr;
                }
                for (int i = 0; i < w.Length; i++)
                {
                    if (vn[i].sqrMagnitude < 1e-20f) continue;
                    var v = vn[i].normalized;
                    int p = o + t.Weld[i];
                    if (Vector3.Dot(axis[p], v) < 0f) v = -v;
                    axis[p] += v;
                }
            }

            for (int p = 0; p < n; p++)
                if (axis[p].sqrMagnitude > 1e-12f) axis[p] = axis[p].normalized;

            // 体は細かいグリッドで引き直す (反復中の近傍探索を速くする)
            body.Build(0.01f);

            // ---------------------------------------------------------------
            // 衝突候補: 初期位置で体から maxDistance 以内の粒子
            // ---------------------------------------------------------------
            var cand = new List<int>();
            var lastDist = new float[n];   // 前回問い合わせ時の最近傍点までの距離
            var lastSigned = new float[n]; // 前回問い合わせ時の符号付き距離 (補正後)
            var qpos = new Vector3[n];     // 前回問い合わせ時の位置
            var bodyOff = new Vector3[n];  // 初期位置での 体の最近傍点 → 粒子 (外向きの判定用)
            var initInside = new bool[n];
            var isCand = new bool[n];
            {
                Parallel.For(0, n, p =>
                {
                    if (pinned[p]) return;
                    if (body.Closest(x0[p], maxDist[p], out var q, out var nn, out _, out var why0))
                    {
                        isCand[p] = true;
                        lastDist[p] = (x0[p] - q).magnitude;
                        lastSigned[p] = why0 == BodyFitSolver.InsideReason.Boundary ? lastDist[p] : Vector3.Dot(x0[p] - q, nn);
                        qpos[p] = x0[p];
                        bodyOff[p] = x0[p] - q;
                    }
                });
                for (int p = 0; p < n; p++) if (isCand[p]) cand.Add(p);
            }
            res.Candidates = cand.Count;

            // ---------------------------------------------------------------
            // 密着させる粒子の部位を決め、内外 (bodyOff) を自分の部位の体の面を基準に取り直す。
            //   内股のように反対の脚が近い所では、最寄りの面だと表裏の判定が逆になるため
            // ---------------------------------------------------------------
            {
                // 1. 粒子ごとの候補: ウェイトの部位。決まらない (胴) なら最寄りの体の面の部位
                var guess = new byte[n];
                Parallel.For(0, n, p =>
                {
                    guess[p] = limb[p];
                    if (!tight[p] || pinned[p]) return;
                    if (limb[p] == Limb.Torso || limb[p] == Limb.Any)
                    {
                        byte near = body.LimbAt(x0[p], maxDist[p]);
                        if (near != Limb.Any) guess[p] = near;
                    }
                });

                // 2. つながった部品ごとに多数決 (ベルトの端だけ反対の脚になる、等を防ぐ)
                var seen = new bool[n];
                var stack = new Stack<int>();
                var members = new List<int>();
                var votes = new int[6];
                for (int s0 = 0; s0 < n; s0++)
                {
                    if (seen[s0] || !tight[s0] || pinned[s0]) continue;
                    members.Clear();
                    System.Array.Clear(votes, 0, votes.Length);
                    seen[s0] = true; stack.Push(s0);
                    while (stack.Count > 0)
                    {
                        int p = stack.Pop();
                        members.Add(p);
                        votes[guess[p] == Limb.Any ? 5 : guess[p]]++;
                        foreach (int q in nbrs[p])
                            if (!seen[q] && tight[q] && !pinned[q]) { seen[q] = true; stack.Push(q); }
                    }
                    // 腕・脚の票があればそれを優先 (胴の票は「部位が決まらなかった」ことが多い)
                    int win = -1;
                    for (int v = 1; v <= 4; v++) if (votes[v] > 0 && (win < 0 || votes[v] > votes[win])) win = v;
                    if (win < 0) win = votes[Limb.Torso] > 0 ? Limb.Torso : -1;
                    if (win < 0) continue;
                    foreach (int p in members) limb[p] = (byte)win;
                }

                // 3. 自分の部位の体の面を基準に内外を取り直す
                Parallel.For(0, n, p =>
                {
                    if (!tight[p] || pinned[p]) return;
                    if (body.ClosestInLimb(x0[p], maxDist[p], limb[p], out var q, out _))
                        bodyOff[p] = x0[p] - q;
                });
            }

            // ---------------------------------------------------------------
            // レイヤー結合: 法線方向に重なっている別の層を、相手メッシュごとに最寄り 1 つ (最大 K 個) 探す
            //   同じメッシュの厚み (表裏) も、別メッシュ (シャツ↔スカート↔ベルト) も対象
            // ---------------------------------------------------------------
            const int K = 4;
            var pn = new int[n * K];          // 相手
            var pDir = new Vector3[n * K];    // 前後関係: 相手 → 自分 の向き (初期)
            var pMin = new float[n * K];      // これより近づかない
            var pW = new float[n * K];        // 前後関係の補正を自分が負担する割合
            for (int i = 0; i < pn.Length; i++) pn[i] = -1;

            if (prm.LayerDistance > 0f)
            {
                float cell = prm.LayerDistance;
                var grid = new Dictionary<Vector3Int, List<int>>(FastHash.I);
                Vector3Int CellOf(Vector3 v) => new Vector3Int(
                    Mathf.FloorToInt(v.x / cell), Mathf.FloorToInt(v.y / cell), Mathf.FloorToInt(v.z / cell));
                for (int p = 0; p < n; p++)
                {
                    var c = CellOf(x0[p]);
                    if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
                    l.Add(p);
                }

                Parallel.For(0, n, p =>
                {
                    if (pinned[p]) return;
                    var ax = axis[p];
                    var c = CellOf(x0[p]);
                    var bestD = new float[K];
                    var bestQ = new int[K];
                    var bestOwner = new int[K];
                    int cnt = 0;
                    float lim = prm.LayerDistance * prm.LayerDistance;

                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out var l)) continue;
                        foreach (int q in l)
                        {
                            if (q == p || pinned[q]) continue;
                            var off = x0[q] - x0[p];
                            float d2 = off.sqrMagnitude;
                            if (d2 >= lim || d2 < 1e-12f || System.Array.IndexOf(nbrs[p], q) >= 0) continue;
                            // 別の層 = 自分の面の法線方向に重なっている相手だけ
                            if (ax.sqrMagnitude > 0.5f && Mathf.Abs(Vector3.Dot(off, ax)) < 0.7f * Mathf.Sqrt(d2)) continue;

                            int ow = owner[q];
                            int slot = -1;
                            for (int k = 0; k < cnt; k++) if (bestOwner[k] == ow) { slot = k; break; }
                            if (slot >= 0)
                            {
                                if (d2 < bestD[slot]) { bestD[slot] = d2; bestQ[slot] = q; }
                            }
                            else if (cnt < K)
                            {
                                bestD[cnt] = d2; bestQ[cnt] = q; bestOwner[cnt] = ow; cnt++;
                            }
                        }
                    }

                    for (int k = 0; k < cnt; k++)
                    {
                        int q = bestQ[k];
                        int idx = p * K + k;
                        var r0 = x0[p] - x0[q];
                        float len = r0.magnitude;
                        pn[idx] = q;
                        pDir[idx] = r0 / len;
                        pMin[idx] = Mathf.Max(len * 0.5f, Mathf.Min(len, 0.0008f));

                        // 重ね順が指定されたメッシュ同士は、初期の位置関係ではなく指定の順に従う
                        // (体型合わせ後に、リボンがカーディガンの内側へ入ってしまった場合など)
                        if (layer[p] != layer[q] && bodyOff[p].sqrMagnitude > 1e-12f)
                        {
                            var outDir = Vector3.Dot(axis[p], bodyOff[p]) >= 0f ? axis[p] : -axis[p];
                            if (outDir.sqrMagnitude < 0.5f) outDir = bodyOff[p].normalized;
                            if (layer[p] > layer[q]) { pDir[idx] = outDir; pMin[idx] = 0.0008f; pW[idx] = 1f; }
                            else { pDir[idx] = -outDir; pMin[idx] = 0.0008f; pW[idx] = 0f; }
                            continue;
                        }

                        // 体に近い粒子では、体から遠い側 (外側の層) だけが動いて前後関係を直す。
                        // 内側の層を体へ押し込むと衝突判定と綱引きになるため。
                        if (bodyOff[p].sqrMagnitude > 1e-12f)
                            pW[idx] = Vector3.Dot(pDir[idx], bodyOff[p]) > 0f ? 1f : 0f;
                        else
                            pW[idx] = 0.5f;
                    }
                });
                for (int i = 0; i < pn.Length; i++) if (pn[i] >= 0) res.LayerPairs++;
            }

            // ---------------------------------------------------------------
            // 密着: 一番内側の層だけを体へ引き寄せる。
            //   厚みのある服の表側や、上に重なったベルト等はレイヤー結合で付いてくるので、厚みや重なりが潰れない
            // ---------------------------------------------------------------
            var pull = new bool[n];
            var innermost = new bool[n];
            for (int p = 0; p < n; p++)
            {
                if (pinned[p]) continue;
                bool inner = true;
                for (int k = 0; k < K; k++)
                {
                    int idx = p * K + k;
                    if (pn[idx] < 0) break;
                    if (pW[idx] >= 1f) { inner = false; break; } // 自分より体側に別の層がある
                }
                innermost[p] = inner;
                pull[p] = tight[p] && inner;
            }

            // ---------------------------------------------------------------
            // 胸の余りを詰める: 衣装の胸が体の胸より大きくて浮いている所を、その衣装の「ゆとり」まで引き寄せる。
            //   ゆとり = 胸と同じ高さの脇・背中などで、その衣装が体から離れている距離 (中央値)。
            //   ゆったりした服はゆったりしたまま、胸の前だけが余らなくなる
            // ---------------------------------------------------------------
            var bustGap = new float[n];
            for (int p = 0; p < n; p++) bustGap[p] = -1f;
            int bustPulled = 0;
            if (prm.FitBust && body.HasBust)
            {
                var gap0 = new float[n];
                var onBust = new bool[n];
                var has = new bool[n];
                // 胸の生地は大きく浮いていることがあるので、maxDistance より広く探す
                Parallel.For(0, n, p =>
                {
                    if (pinned[p] || tight[p]) return;
                    if (!body.ClosestBust(x0[p], Mathf.Max(maxDist[p], 0.1f), out var q, out var nn, out bool bst)) return;
                    gap0[p] = Vector3.Dot(x0[p] - q, nn);
                    onBust[p] = bst;
                    has[p] = true;
                });
                float yMin = body.BustMinY - 0.03f, yMax = body.BustMaxY + 0.03f;
                res.BustLog = new List<string>();
                for (int ti = 0; ti < targets.Count; ti++)
                {
                    int o = offset[ti], cnt = targets[ti].GroupCount;
                    var ref0 = new List<float>();
                    int nBust = 0; float maxBustGap = 0f;
                    for (int p = o; p < o + cnt; p++)
                    {
                        if (!has[p] || !innermost[p]) continue;
                        if (onBust[p]) { nBust++; maxBustGap = Mathf.Max(maxBustGap, gap0[p]); }
                        else if (gap0[p] > 0f && x0[p].y >= yMin && x0[p].y <= yMax) ref0.Add(gap0[p]);
                    }
                    if (nBust == 0) continue;
                    if (ref0.Count < 30)
                    {
                        res.BustLog.Add($"{targets[ti].Name}: 胸の高さの生地が少ないためスキップ (胸 {nBust} / 脇・背中 {ref0.Count})");
                        continue;
                    }
                    ref0.Sort();
                    float ease = ref0[ref0.Count / 2];
                    int pulled = 0;
                    for (int p = o; p < o + cnt; p++)
                    {
                        if (!has[p] || !innermost[p] || !onBust[p]) continue;
                        float target = Mathf.Max(ease, margin[p]);
                        if (gap0[p] <= target + 0.003f) continue;
                        bustGap[p] = target;
                        pulled++;
                    }
                    bustPulled += pulled;
                    res.BustLog.Add($"{targets[ti].Name}: ゆとり {ease * 1000f:0.0}mm (脇・背中 {ref0.Count}) / 胸の生地 {nBust} 頂点・最大の隙間 {maxBustGap * 1000f:0.0}mm → {pulled} 頂点を引き寄せ");
                }
            }
            res.BustPulled = bustPulled;

            // ---------------------------------------------------------------
            // 逆方向の拘束: 体の各頂点を、その法線上で一番外側にある衣装の面に結び付ける
            //   (外側の面が体より外にあれば OK。裏地だけを抜けているような見えない貫通は無視される)
            // ---------------------------------------------------------------
            var bindTri = new List<int>();
            var bindW = new List<Vector3>();
            var bindB = new List<Vector3>();
            var bindN = new List<Vector3>();
            var bindM = new List<float>();
            {
                var cloth = new BodyFitSolver.Collider();
                cloth.Add(new BodyFitSolver.Posed { World = x0, Triangles = allTris.ToArray() });
                int activeTris = allTris.Count / 3;
                if (passive != null) foreach (var pp in passive) cloth.Add(pp);
                cloth.Build(0.02f);

                float maxReach = 0f;
                for (int p = 0; p < n; p++) maxReach = Mathf.Max(maxReach, reach[p]);

                if (maxReach > 0f)
                {
                    var bpos = body.Positions;
                    int bn = bpos.Count;
                    var hitTri = new int[bn];
                    var hitW = new Vector3[bn];

                    Parallel.For(0, bn, i =>
                    {
                        hitTri[i] = -1;
                        var nb = body.VertexNormal(i);
                        if (nb.sqrMagnitude < 0.5f) return;
                        var b = bpos[i];
                        var start = b + nb * maxReach;
                        if (!cloth.RaycastTri(start, -nb, maxReach * 2f, out float d, out _, out int tri)) return;
                        if (tri >= activeTris) return; // 一番外側がスキップしたメッシュ → 見えないので何もしない

                        int a0 = allTris[tri * 3], a1 = allTris[tri * 3 + 1], a2 = allTris[tri * 3 + 2];
                        // その衣装の設定で逆方向判定が無効なら結び付けない
                        if (reach[a0] <= 0f && reach[a1] <= 0f && reach[a2] <= 0f) return;

                        var hp = start - nb * d;
                        hitTri[i] = tri;
                        hitW[i] = Barycentric(hp, x0[a0], x0[a1], x0[a2]);
                    });

                    for (int i = 0; i < bn; i++)
                    {
                        if (hitTri[i] < 0) continue;
                        int tri = hitTri[i];
                        int a0 = allTris[tri * 3];
                        bindTri.Add(tri);
                        bindW.Add(hitW[i]);
                        bindB.Add(bpos[i]);
                        bindN.Add(body.VertexNormal(i));
                        bindM.Add(margin[a0]);
                    }
                }
            }
            res.Bindings = bindTri.Count;

            // ---------------------------------------------------------------
            // 反復
            // ---------------------------------------------------------------
            var x = (Vector3[])x0.Clone();
            var d0 = new Vector3[n];
            var d1 = new Vector3[n];
            var hitFwd = new bool[n];
            var hitRev = new bool[n];
            var hitPull = new bool[n];
            var acc = new Vector3[n];
            var accN = new int[n];
            const float alpha = 0.6f;
            const float beta = 0.5f;

            int iters = Mathf.Max(1, prm.Iterations);
            for (int it = 0; it <= iters; it++)
            {
                bool last = it == iters;

                if (!last)
                {
                    // 1. 形状保持 (変位のラプラシアン平滑化 + 元の位置への減衰) と レイヤー結合
                    for (int p = 0; p < n; p++) d0[p] = x[p] - x0[p];
                    Parallel.For(0, n, p =>
                    {
                        if (pinned[p]) { d1[p] = Vector3.zero; return; }
                        var nb = nbrs[p];
                        var v = d0[p];
                        if (nb.Length > 0)
                        {
                            var avg = Vector3.zero;
                            foreach (int q in nb) avg += d0[q];
                            v = Vector3.Lerp(v, avg / nb.Length, alpha);
                        }
                        v *= 1f - gamma[p];
                        var pd = Vector3.zero; int pc = 0;
                        for (int k = 0; k < K; k++)
                        {
                            int q = pn[p * K + k];
                            if (q < 0) break;
                            pd += d0[q]; pc++;
                        }
                        if (pc > 0) v = Vector3.Lerp(v, pd / pc, beta);
                        d1[p] = v;
                    });
                    for (int p = 0; p < n; p++) x[p] = x0[p] + d1[p];
                }

                // 1b. レイヤーの前後関係を保つ (内側の層が外側の層を追い越さない)
                if (!last) ApplyOrder();

                // 2. 衣装の頂点 vs 体
                //    前回の問い合わせから十分離れたままの粒子は問い合わせを省略する (高速化)
                Parallel.For(0, cand.Count, ci =>
                {
                    int p = cand[ci];
                    float moved = (x[p] - qpos[p]).magnitude;
                    float tgt = margin[p];
                    if (!pull[p] && bustGap[p] < 0f && lastSigned[p] - moved > tgt + 0.002f)
                    {
                        if (last) hitFwd[p] = false;
                        return;
                    }

                    float r = Mathf.Clamp(lastDist[p] + moved + 0.005f, 0.01f, maxDist[p]);
                    qpos[p] = x[p];
                    if (!body.Closest(x[p], r, out var q, out var nn, out _, out var why))
                    {
                        lastDist[p] = r;
                        lastSigned[p] = r;
                        if (last) hitFwd[p] = false;
                        return;
                    }
                    lastDist[p] = (x[p] - q).magnitude;
                    float signed = Vector3.Dot(x[p] - q, nn);
                    // 最寄りが体の穴の縁 (首の切れ目など): 内外が決まらないので外側として扱う
                    if (why == BodyFitSolver.InsideReason.Boundary) signed = lastDist[p];
                    bool hit = signed < tgt;

                    // 外側の層 (表地) が体の外にあるなら、この内側の層 (裏地・襟の内側など) はめり込んでいても見えない。
                    // 押し出すと表地まで押し出されて不自然に膨らむので、そのままにする
                    if (hit && prm.KeepHiddenLayers && Covered(p))
                    {
                        lastSigned[p] = signed;
                        if (last) hitFwd[p] = false;
                        return;
                    }
                    bool done = false;
                    // 深く埋まっている: 最近傍点は反対側の面のことがあるので、ボーンの軸から外向きに抜ける
                    if (signed < -0.005f && radial[p].sqrMagnitude > 0.5f &&
                        body.Raycast(x[p], radial[p], maxDist[p], out float dex, out bool exitR) && exitR)
                    {
                        x[p] += radial[p] * (dex + tgt);
                        signed = tgt;
                        lastDist[p] = tgt;
                        done = true;
                    }
                    if (hit && !done)
                    {
                        x[p] += nn * (tgt - signed);
                        signed = tgt;
                    }
                    else if (pull[p] && signed > tgt &&
                             body.ClosestInLimb(x[p], maxDist[p], limb[p], out var q2, out var n2))
                    {
                        // 浮いている: 同じ部位の体の表面 + margin まで引き寄せる (内股で反対の脚に吸い寄せられない)
                        float s2 = Vector3.Dot(x[p] - q2, n2);
                        // 元の位置から maxDistance を超えて引き寄せない
                        if (s2 > tgt && (x[p] - n2 * (s2 - tgt) - x0[p]).sqrMagnitude <= maxDist[p] * maxDist[p])
                        {
                            x[p] -= n2 * (s2 - tgt);
                            if (last) hitPull[p] = true;
                        }
                        signed = tgt;
                    }
                    else if (bustGap[p] >= 0f && signed > bustGap[p])
                    {
                        // 胸の余り: 衣装のゆとりの距離まで引き寄せる
                        x[p] -= nn * (signed - bustGap[p]);
                        if (last) hitPull[p] = true;
                        signed = bustGap[p];
                    }
                    lastSigned[p] = signed;
                    if (last) hitFwd[p] = hit;
                });

                // 3. 体の頂点 vs 衣装の面
                if (bindTri.Count > 0)
                {
                    System.Array.Clear(acc, 0, n);
                    System.Array.Clear(accN, 0, n);
                    for (int bi = 0; bi < bindTri.Count; bi++)
                    {
                        int tri = bindTri[bi];
                        int a0 = allTris[tri * 3], a1 = allTris[tri * 3 + 1], a2 = allTris[tri * 3 + 2];
                        var w = bindW[bi];
                        var pnt = x[a0] * w.x + x[a1] * w.y + x[a2] * w.z;
                        var nb = bindN[bi];
                        float s = Vector3.Dot(pnt - bindB[bi], nb);
                        float m = bindM[bi];
                        if (s >= m) continue;

                        float c = m - s;
                        float ww = w.x * w.x + w.y * w.y + w.z * w.z;
                        if (ww < 1e-6f) continue;
                        Add(a0, nb * (c * w.x / ww));
                        Add(a1, nb * (c * w.y / ww));
                        Add(a2, nb * (c * w.z / ww));
                    }
                    for (int p = 0; p < n; p++)
                    {
                        if (accN[p] == 0 || pinned[p]) continue;
                        x[p] += acc[p] / accN[p];
                        if (last) hitRev[p] = true;
                    }
                }

                if (last) for (int k2 = 0; k2 < 3; k2++) ApplyOrder();
                for (int p = 0; p < n; p++) if (pinned[p]) x[p] = x0[p];
            }

            void ApplyOrder()
            {
                Parallel.For(0, n, p =>
                {
                    d1[p] = Vector3.zero;
                    if (pinned[p]) return;
                    var sum = Vector3.zero; int c = 0;
                    for (int k = 0; k < K; k++)
                    {
                        int idx = p * K + k;
                        int q = pn[idx];
                        if (q < 0) break;
                        if (pW[idx] <= 0f) continue;
                        float sp = Vector3.Dot(x[p] - x[q], pDir[idx]);
                        if (sp >= pMin[idx]) continue;
                        var corr = pDir[idx] * ((pMin[idx] - sp) * pW[idx]);
                        if (corr.sqrMagnitude > sum.sqrMagnitude) sum = corr; // 一番大きい違反を採用
                        c++;
                    }
                    if (c > 0) d1[p] = sum;
                });
                for (int p = 0; p < n; p++) x[p] += d1[p];
            }

            bool Covered(int p)
            {
                for (int k = 0; k < K; k++)
                {
                    int idx = p * K + k;
                    int q = pn[idx];
                    if (q < 0) break;
                    if (pW[idx] > 0f) continue;              // 相手は自分より内側 (または不明)
                    if (owner[q] != owner[p] && layer[q] < layer[p]) continue;
                    if (!isCand[q] || lastSigned[q] > margin[q]) return true; // 外側の層が体の外にある
                }
                return false;
            }

            void Add(int p, Vector3 v)
            {
                acc[p] += v;
                accN[p]++;
            }

            // ---------------------------------------------------------------
            // 結果を各ターゲットへ
            // ---------------------------------------------------------------
            for (int ti = 0; ti < targets.Count; ti++)
            {
                var t = targets[ti];
                int o = offset[ti];
                t.FinalDisp = new Vector3[t.GroupCount];
                for (int g = 0; g < t.GroupCount; g++)
                {
                    int p = o + g;
                    var d = x[p] - x0[p];
                    t.FinalDisp[g] = d;
                    t.GClass[g] = hitRev[p] ? BodyFitSolver.Cls.Reverse
                                : hitFwd[p] ? BodyFitSolver.Cls.Inside
                                : hitPull[p] ? BodyFitSolver.Cls.Pulled
                                : BodyFitSolver.Cls.None;
                    if (hitFwd[p]) t.InsideRay++;
                    if (hitRev[p]) t.ReverseHits++;
                    if (hitPull[p]) t.Pulled++;
                }
            }

            res.Milliseconds = sw.ElapsedMilliseconds;
            return res;
        }

        static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var v0 = b - a; var v1 = c - a; var v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-20f) return new Vector3(1f / 3f, 1f / 3f, 1f / 3f);
            float v = (d11 * d20 - d01 * d21) / den;
            float w = (d00 * d21 - d01 * d20) / den;
            return new Vector3(1f - v - w, v, w);
        }
    }
}
