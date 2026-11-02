using UnityEngine;

namespace Game.Framework.Home
{
    /// <summary>
    /// 稳定锚点（HomePlotLayout / HomeNodeLayout / 冻结 Config200）→ 灰盒显示世界坐标的单一映射。
    /// 不改布局表；采集点若落入地块网格 AABB 则外推到最近外侧，避免 Node 遮挡 Plot，且屏幕反查仍走真实 Collider。
    /// 东/南外侧另推到 HUD 净空带，配合正式 HomeHud/Gather 面板几何，使全部 Node 中心可点可见。
    /// </summary>
    public static class HomeDisplayLayout
    {
        /// <summary>地块网格外接盒（含灰盒 1.1 边长余量）。</summary>
        public const float PlotAabbMinX = -4.2f;
        public const float PlotAabbMaxX = 4.2f;
        public const float PlotAabbMinZ = -2.7f;
        public const float PlotAabbMaxZ = 2.7f;

        /// <summary>节点外推到网格外后的额外间距（相对地块碰撞体）。</summary>
        public const float NodeExteriorMargin = 1.6f;

        /// <summary>东侧外侧节点显示 X 下限（避让右侧 Gather 窄条投影）。</summary>
        public const float EastHudClearX = 5.8f;

        /// <summary>南侧外侧节点显示 Z 上限（避让底左 HomeHud 投影）。</summary>
        public const float SouthHudClearZ = -5.8f;

        /// <summary>
        /// Plot：恒等。Node：锚点在地块 AABB 内则推到最近外侧；已在外侧则恒等后再做东/南 HUD 净空。
        /// </summary>
        public static Vector3 ToDisplayWorld(string kind, float anchorX, float anchorY, float anchorZ)
        {
            if (!string.Equals(kind, HomeInstanceKinds.Node, System.StringComparison.Ordinal))
                return new Vector3(anchorX, anchorY, anchorZ);

            float x = anchorX;
            float z = anchorZ;
            bool insideX = x >= PlotAabbMinX && x <= PlotAabbMaxX;
            bool insideZ = z >= PlotAabbMinZ && z <= PlotAabbMaxZ;
            if (insideX && insideZ)
            {
                float distWest = x - PlotAabbMinX;
                float distEast = PlotAabbMaxX - x;
                float distSouth = z - PlotAabbMinZ;
                float distNorth = PlotAabbMaxZ - z;
                float min = distWest;
                int edge = 0; // 0W 1E 2S 3N
                if (distEast < min) { min = distEast; edge = 1; }
                if (distSouth < min) { min = distSouth; edge = 2; }
                if (distNorth < min) { edge = 3; }

                switch (edge)
                {
                    case 0: x = PlotAabbMinX - NodeExteriorMargin; break;
                    case 1: x = PlotAabbMaxX + NodeExteriorMargin; break;
                    case 2: z = PlotAabbMinZ - NodeExteriorMargin; break;
                    default: z = PlotAabbMaxZ + NodeExteriorMargin; break;
                }
            }

            // HUD 净空：仅显示映射；不改冻结锚点、不 Clamp 屏幕坐标
            if (x >= PlotAabbMaxX)
                x = Mathf.Max(x, EastHudClearX);
            if (z <= PlotAabbMinZ)
                z = Mathf.Min(z, SouthHudClearZ);

            return new Vector3(x, anchorY, z);
        }

        public static Vector3 ToDisplayWorld(HomeNodeLayout.NodeRow row)
        {
            return ToDisplayWorld(HomeInstanceKinds.Node, row.PosX, row.PosY, row.PosZ);
        }

        public static Vector3 ToDisplayWorld(HomePlotLayout.PlotRow row)
        {
            return ToDisplayWorld(HomeInstanceKinds.Plot, row.PosX, row.PosY, row.PosZ);
        }
    }
}
