// Editor/BodyScalePreset.cs
using System.Collections.Generic;
using UnityEngine;

namespace Hinata.BodyFit.Editor
{
    [System.Serializable]
    public class BodyScaleRule
    {
        [Tooltip("Armature からの相対パス (例: Hips/Upper_leg_{S})。{S} は L と R に展開される。大文字小文字と区切り文字 (_ . - 空白) は無視")]
        public string path = "Hips";

        [Tooltip("ボーン自体の Transform スケールを変える (長さも太さも変わる)")]
        public bool setScale;
        public float scale = 1f;

        [Tooltip("MA Scale Adjuster を追加/更新する (このボーンに直接付いたメッシュの太さだけ変わる)")]
        public bool setAdjuster;
        public Vector3 adjuster = Vector3.one;

        [Tooltip("Scale Adjuster の変更に合わせて子ボーンの位置も調整する (MA の「子オブジェクトの位置を調整」と同じ)")]
        public bool adjustChildren = true;
    }

    /// <summary>Body Scale ウィンドウで適用する、ボーンのスケールと Scale Adjuster の組み合わせ。</summary>
    [CreateAssetMenu(fileName = "BodyScalePreset", menuName = "Body Fit/Body Scale Preset")]
    public class BodyScalePreset : ScriptableObject
    {
        [Tooltip("選択したオブジェクト (アバター/衣装のルート) 自体のスケールも変える")]
        public bool setRootScale;
        public float rootScale = 1f;

        public List<BodyScaleRule> rules = new List<BodyScaleRule>();
    }
}
