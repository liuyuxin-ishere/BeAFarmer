using System;
using System.Collections.Generic;

namespace Game.Framework.Home
{
    /// <summary>
    /// 冻结 Config200 MapInstance 中 24 块 Plot 的稳定布局镜像。
    /// 与 Server/ConfigLuban/tbmapinstance.json 的 Plot 行一致；不从场景物体顺序生成。
    /// </summary>
    public static class HomePlotLayout
    {
        public readonly struct PlotRow
        {
            public PlotRow(int instanceId, float x, float y, float z, string label)
            {
                InstanceId = instanceId;
                PosX = x;
                PosY = y;
                PosZ = z;
                Label = label ?? "";
            }

            public int InstanceId { get; }
            public float PosX { get; }
            public float PosY { get; }
            public float PosZ { get; }
            public string Label { get; }
        }

        static readonly PlotRow[] Rows =
        {
            new PlotRow(1001, -3.5f, 0.25f, 2.1f, "plot_0"),
            new PlotRow(1002, -2.1f, 0.25f, 2.1f, "plot_1"),
            new PlotRow(1003, -0.7f, 0.25f, 2.1f, "plot_2"),
            new PlotRow(1004, 0.7f, 0.25f, 2.1f, "plot_3"),
            new PlotRow(1005, 2.1f, 0.25f, 2.1f, "plot_4"),
            new PlotRow(1006, 3.5f, 0.25f, 2.1f, "plot_5"),
            new PlotRow(1007, -3.5f, 0.25f, 0.7f, "plot_6"),
            new PlotRow(1008, -2.1f, 0.25f, 0.7f, "plot_7"),
            new PlotRow(1009, -0.7f, 0.25f, 0.7f, "plot_8"),
            new PlotRow(1010, 0.7f, 0.25f, 0.7f, "plot_9"),
            new PlotRow(1011, 2.1f, 0.25f, 0.7f, "plot_10"),
            new PlotRow(1012, 3.5f, 0.25f, 0.7f, "plot_11"),
            new PlotRow(1013, -3.5f, 0.25f, -0.7f, "plot_12"),
            new PlotRow(1014, -2.1f, 0.25f, -0.7f, "plot_13"),
            new PlotRow(1015, -0.7f, 0.25f, -0.7f, "plot_14"),
            new PlotRow(1016, 0.7f, 0.25f, -0.7f, "plot_15"),
            new PlotRow(1017, 2.1f, 0.25f, -0.7f, "plot_16"),
            new PlotRow(1018, 3.5f, 0.25f, -0.7f, "plot_17"),
            new PlotRow(1019, -3.5f, 0.25f, -2.1f, "plot_18"),
            new PlotRow(1020, -2.1f, 0.25f, -2.1f, "plot_19"),
            new PlotRow(1021, -0.7f, 0.25f, -2.1f, "plot_20"),
            new PlotRow(1022, 0.7f, 0.25f, -2.1f, "plot_21"),
            new PlotRow(1023, 2.1f, 0.25f, -2.1f, "plot_22"),
            new PlotRow(1024, 3.5f, 0.25f, -2.1f, "plot_23"),
        };

        public const int ExpectedPlotCount = 24;

        public static IReadOnlyList<PlotRow> All => Rows;

        public static bool TryGet(int instanceId, out PlotRow row)
        {
            for (int i = 0; i < Rows.Length; i++)
            {
                if (Rows[i].InstanceId == instanceId)
                {
                    row = Rows[i];
                    return true;
                }
            }

            row = default;
            return false;
        }

        public static void ValidateOrThrow()
        {
            if (Rows.Length != ExpectedPlotCount)
                throw new InvalidOperationException("home-plot-count");
            var seen = new HashSet<int>();
            for (int i = 0; i < Rows.Length; i++)
            {
                int id = Rows[i].InstanceId;
                if (id <= 0 || !seen.Add(id))
                    throw new InvalidOperationException("home-plot-id-collision:" + id);
            }
        }
    }
}
