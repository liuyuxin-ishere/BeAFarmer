using System;

namespace Game.Framework.Build
{
    /// <summary>
    /// V03：显式内容策略。M2Formal 永不注入 M1 PatchDemo；无策略时保留 M1 res&gt;=101 历史行为。
    /// </summary>
    public static class M2PatchContentPolicy
    {
        public const string PolicyM1LegacyImplicit = "";

        /// <summary>
        /// 是否应将 PatchDemo 纳入本轮 staging/catalog/Yoo。
        /// </summary>
        /// <param name="contentPolicy">BuildProfile.contentPolicy；空=M1 隐式。</param>
        /// <param name="resVersion">资源版本。</param>
        public static bool IncludePatchDemo(string contentPolicy, int resVersion)
        {
            if (string.Equals(contentPolicy, M2BuildProfile.ContentPolicyM2Formal, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrEmpty(contentPolicy))
                return resVersion >= 101;
            throw new FormatException("unknown contentPolicy for PatchDemo gate: " + contentPolicy);
        }

        /// <summary>正式 M2 基包必须拒绝任何 PatchDemo 地址残留。</summary>
        public static void RejectPatchDemoArtifacts(string contentPolicy, string catalogText, string publishTreeFlat)
        {
            if (!string.Equals(contentPolicy, M2BuildProfile.ContentPolicyM2Formal, StringComparison.Ordinal))
                return;
            if (string.IsNullOrEmpty(catalogText))
                throw new InvalidOperationException("m2formal-catalog-missing");
            if (catalogText.IndexOf("PatchDemo", StringComparison.OrdinalIgnoreCase) >= 0
                || catalogText.IndexOf("m1_patch_message", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException("m2formal-catalog-must-not-contain-PatchDemo");
            }

            if (!string.IsNullOrEmpty(publishTreeFlat)
                && publishTreeFlat.IndexOf("PatchDemo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException("m2formal-publish-must-not-contain-PatchDemo");
            }
        }
    }
}
