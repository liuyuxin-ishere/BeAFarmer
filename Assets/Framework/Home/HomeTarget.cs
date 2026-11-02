namespace Game.Framework.Home
{
    /// <summary>
    /// 家园命中目标：稳定 instanceId + kind。ID 仅来自显式绑定/冻结配置，不由数组序或物体名推导。
    /// </summary>
    public readonly struct HomeTarget
    {
        public HomeTarget(int instanceId, string kind)
        {
            InstanceId = instanceId;
            Kind = kind ?? "";
        }

        /// <summary>稳定地图实例 ID（Plot 与玩法 plotId 同值，如 1001..1024）。</summary>
        public int InstanceId { get; }

        /// <summary>种类：Plot / Node / Facility。</summary>
        public string Kind { get; }

        public bool IsPlot =>
            string.Equals(Kind, HomeInstanceKinds.Plot, System.StringComparison.Ordinal);

        public bool IsNode =>
            string.Equals(Kind, HomeInstanceKinds.Node, System.StringComparison.Ordinal);
    }

    /// <summary>与冻结 MapInstance.kind 对齐的常量。</summary>
    public static class HomeInstanceKinds
    {
        public const string Plot = "Plot";
        public const string Node = "Node";
        public const string Facility = "Facility";
    }
}
