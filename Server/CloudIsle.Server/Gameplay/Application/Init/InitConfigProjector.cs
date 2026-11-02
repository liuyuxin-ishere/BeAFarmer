using System;
using System.Collections.Generic;
using CloudIsle.Config;
using CloudIsle.Domain.Init;

namespace CloudIsle.Application.Init
{
    /// <summary>
    /// 从同一不可变 <see cref="GameConfigSnapshot"/> 投影初始化规格。
    /// </summary>
    public static class InitConfigProjector
    {
        /// <summary>单参：ItemIds/UnlockLevels/StackDefault 均取自已校验快照。</summary>
        public static InitConfigView FromSnapshot(GameConfigSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.KnownItemIds.Count == 0)
                throw new InvalidOperationException("GameConfigSnapshot missing KnownItemIds; use LoadDirectory");

            PlayerInitRow init = snapshot.PlayerInit;
            var plots = new List<MapPlotView>();
            var nodes = new List<MapNodeView>();
            foreach (KeyValuePair<int, MapInstanceRow> pair in snapshot.MapInstances)
            {
                MapInstanceRow row = pair.Value;
                if (row.Kind == "Plot")
                    plots.Add(new MapPlotView(row.InstanceId));
                else if (row.Kind == "Node")
                    nodes.Add(new MapNodeView(row.InstanceId, row.TypeId));
            }

            plots.Sort((a, b) => a.InstanceId.CompareTo(b.InstanceId));
            nodes.Sort((a, b) => a.InstanceId.CompareTo(b.InstanceId));

            return new InitConfigView(
                init.InitialPlotCount,
                init.StarterTools,
                init.StarterSeeds,
                init.UnlockGiftTools,
                init.GrantSchemaVersion,
                plots,
                nodes,
                snapshot.ItemUnlockLevels,
                snapshot.KnownItemIds,
                snapshot.StackDefault);
        }
    }
}
