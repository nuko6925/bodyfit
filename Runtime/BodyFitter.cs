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

        [Tooltip("貫通判定の基準にする体のメッシュ。空ならアバター内の \"Body\" を自動で使う。\n" +
                 "顔と体が別メッシュのアバターは両方を入れる。")]
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

        // ------------------------------------------------------------------
        //  貫通補正 (ソルバー)
        // ------------------------------------------------------------------

        [Tooltip("体の表面から最低これだけ離す (m)")]
        [Min(0f)] public float margin = 0.0015f;

        [Tooltip("体の表面からこの距離以内の頂点だけを判定する (m)。深く埋まっている場合は大きくする")]
        [Min(0.001f)] public float maxDistance = 0.05f;

        [Tooltip("体が衣装の面を突き抜けている箇所 (粗いポリゴンの面の中央から胸の先端が出る等) を、この深さまで検出する (m)。0 で無効")]
        [Min(0f)] public float reverseDepth = 0.02f;

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
