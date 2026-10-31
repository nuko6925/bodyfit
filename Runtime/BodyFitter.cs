// Runtime/BodyFitter.cs
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Hinata.BodyFit
{
    /// <summary>衣装メッシュごとの個別設定。一覧にないメッシュは Body Fitter 本体の設定を使う。</summary>
    [System.Serializable]
    public class MeshOverride
    {
        public string name;
        public SkinnedMeshRenderer renderer;

        [Tooltip("このメッシュは補正しない (他の衣装を覆う面としては引き続き使う)")]
        public bool skip;

        [Tooltip("体に密着させる (ニーハイ・タイツ・手袋など)。体から浮いている部分も体の表面 + margin まで引き寄せる")]
        public bool tight;

        [Tooltip("重ね順。大きいほど外側。近くにある別メッシュとの前後関係を、元の位置ではなくこの順に従わせる")]
        public int layer;

        [Tooltip("体の表面から最低これだけ離す (m)")]
        [Min(0f)] public float margin = 0.0015f;

        [Tooltip("体の表面からこの距離以内の頂点だけを判定する (m)")]
        [Min(0.001f)] public float maxDistance = 0.05f;

        [Tooltip("体が衣装の面を突き抜けている箇所を、この深さまで検出する (m)。0 で無効")]
        [Min(0f)] public float reverseDepth = 0.02f;

        [Tooltip("押し出した部分の周囲がなじむ範囲 (m)。0 なら Body Fitter 本体の設定。" +
                 "パーカーなどゆったりした服で、胸などに生地が貼り付いたようになる場合は大きくする (目安 0.06〜0.1)")]
        [Min(0f)] public float falloff = 0f;

        [Tooltip("パーカーなどゆったりした服用。体のくぼみ (胸の上下・谷間など) をこの範囲 (m) で埋めた形に対して判定し、" +
                 "出っ張った所から出っ張った所へ布が張った形にする (目安 0.04〜0.08)。0 で無効")]
        [Min(0f)] public float drape = 0f;
    }

    /// <summary>
    /// 衣装のルートに付ける。ビルド時 (NDMF) に、衣装をアバターの体型に合わせ、
    /// 体への貫通を直す BlendShape を衣装メッシュに追加する。元のメッシュ・プレハブは変更しない。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Body Fit/Body Fitter")]
    public class BodyFitter : MonoBehaviour, IEditorOnly
    {
        // ------------------------------------------------------------------
        //  基本
        // ------------------------------------------------------------------

        [Tooltip("貫通判定の基準にする体のメッシュ。空なら、胴・腕・脚にウェイトが乗っている体のメッシュを自動で探す。\n" +
                 "顔と体が別メッシュのアバターで顔の貫通も直したい場合は両方を入れる。")]
        public List<SkinnedMeshRenderer> bodies = new List<SkinnedMeshRenderer>();

        [Tooltip("補正しないボーン (スカートや袖口など、体から離れて揺れる部分)。子孫も含む")]
        public List<Transform> excludeBones = new List<Transform>();

        [Tooltip("メッシュごとの個別設定")]
        public List<MeshOverride> perMesh = new List<MeshOverride>();

        // ------------------------------------------------------------------
        //  体型合わせ
        // ------------------------------------------------------------------

        [Tooltip("衣装のボーンの位置・スケール・Scale Adjuster を、アバターの対応するボーンに揃える (Merge Armature の前に実行)")]
        public bool alignJoints = true;

        [Tooltip("アバター側の MA Scale Adjuster (体型改変の太さ) を衣装にも写す。衣装がもともとその素体に合っている (対応衣装) 場合に使う。" +
                 "非対応衣装では太さは体型合わせが実際の体を測って合わせるので、写すと二重に太くなる")]
        public bool copyScaleAdjusters = false;

        [Tooltip("体と衣装の断面をボーンごとに比べ、衣装の太さを合わせる")]
        public bool autoProportion = true;

        [Tooltip("推定した太さの倍率をどれだけ適用するか (0 = 無効, 1 = そのまま)")]
        [Range(0f, 1f)] public float proportionStrength = 1f;

        [Tooltip("太さの倍率の下限と上限")]
        public Vector2 proportionRange = new Vector2(0.85f, 1.2f);

        [Tooltip("衣装の内径/体 の比がこれ以上なら、元からゆったりしたデザインとみなして縮めない")]
        [Range(1.05f, 2f)] public float looseRatio = 1.35f;

        [Tooltip("靴が足を囲んでいる場合、靴の中の足を BlendShape で隠す (靴は拡縮しない)")]
        public bool hideFeetInShoes = true;

        [Tooltip("衣装の胸が体の胸より大きくて浮いている場合に、その衣装のゆとり (胸の高さの脇・背中での隙間) まで引き寄せる。" +
                 "アバターに胸のボーン (Breast 等) が必要")]
        public bool fitBust = true;

        // ------------------------------------------------------------------
        //  貫通補正 (ソルバー)
        // ------------------------------------------------------------------

        [Tooltip("体の表面から最低これだけ離す (m)")]
        [Min(0f)] public float margin = 0.0015f;

        [Tooltip("体の表面からこの距離以内の頂点だけを判定する (m)。深く埋まっている場合は大きくする")]
        [Min(0.001f)] public float maxDistance = 0.05f;

        [Tooltip("体が衣装の面を突き抜けている箇所 (粗いポリゴンの面の中央から胸の先端が出る等) を、この深さまで検出する (m)。0 で無効")]
        [Min(0f)] public float reverseDepth = 0.02f;

        [Tooltip("体の細かい凹凸 (胸の先・へそ・谷間など) を、布が張ったようにならしてから判定する範囲 (m)。服に体の細部の形が浮き出るのを防ぐ (目安 0.01〜0.015)。0 で無効。重ね着の多い衣装では崩れることがある")]
        [Min(0f)] public float bodySmoothing = 0f;

        [Tooltip("表地が体の外にあれば、その内側の層 (裏地・襟やフードの内側など) が体にめり込んでいても押し出さない。" +
                 "見えない層を押し出して表地まで不自然に膨らむのを防ぐ")]
        public bool keepHiddenLayers = true;

        [Tooltip("押し出した部分の周囲がなじむ範囲 (m)")]
        [Min(0.001f)] public float falloff = 0.03f;

        [Tooltip("ソルバーの反復回数")]
        [Range(10, 300)] public int solverIterations = 60;

        [Tooltip("この距離以内で重なっている別の層 (裏地と表地、シャツとスカート等) の前後関係を保つ (m)")]
        [Min(0f)] public float layerFollowDistance = 0.01f;

        [Tooltip("この大きさ (m) 以下の独立したパーツ (ボタン・金具など) は、変形させずに周囲の面に合わせて平行移動する。0 で無効")]
        [Min(0f)] public float islandMaxSize = 0.05f;

        // ------------------------------------------------------------------
        //  その他
        // ------------------------------------------------------------------

        [Tooltip("補正する衣装メッシュ。空ならこのオブジェクト以下の SkinnedMeshRenderer すべて")]
        public List<SkinnedMeshRenderer> targets = new List<SkinnedMeshRenderer>();

        [Tooltip("体のうち、判定に使わない部位のボーン (子孫も含む)")]
        public List<Transform> ignoreBodyBones = new List<Transform>();

        [Tooltip("除外ボーンの子孫も除外する")]
        public bool excludeDescendants = true;

        [Tooltip("除外ボーンへのウェイトがこれ以上の頂点を除外する")]
        [Range(0.01f, 1f)] public float excludeWeightThreshold = 0.5f;

        [Tooltip("追加する BlendShape の名前")]
        public string blendShapeName = "BodyFit";

        [Range(0f, 100f)] public float blendShapeWeight = 100f;

        [Tooltip("複数の Body Fitter がある場合の処理順 (小さい順)")]
        public int order = 0;

        [Tooltip("ビルド時に詳しいログ (ボーンごとの倍率・メッシュごとの補正量など) を Console に出す")]
        public bool verboseLog = false;
    }
}
