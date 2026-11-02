using System;
using System.Collections.Generic;

namespace Game.Framework.Home
{
    /// <summary>
    /// 冻结 Config200 MapInstance 中 13 个 Node 的稳定布局镜像。
    /// instanceId=地图实例；TypeId=GatherNode 类型 id（不可互换，不可用场景序号代替）。
    /// 与 Server/ConfigLuban/tbmapinstance.json 的 Node 行一致。
    /// </summary>
    public static class HomeNodeLayout
    {
        public readonly struct NodeRow
        {
            public NodeRow(int instanceId, int typeId, float x, float y, float z, string label)
            {
                InstanceId = instanceId;
                TypeId = typeId;
                PosX = x;
                PosY = y;
                PosZ = z;
                Label = label ?? "";
            }

            public int InstanceId { get; }
            /// <summary>GatherNode 类型 id（MapInstance.typeId）。</summary>
            public int TypeId { get; }
            public float PosX { get; }
            public float PosY { get; }
            public float PosZ { get; }
            public string Label { get; }
        }

        // 坐标锚点与冻结 Config200 一致；Label 为玩家可读中文区分（非技术 tree_a）。
        static readonly NodeRow[] Rows =
        {
            new NodeRow(2001, 1, -3.5f, 0.3f, 2f, "树木甲"),
            new NodeRow(2002, 1, -3.5f, 0.3f, 0f, "树木乙"),
            new NodeRow(2003, 2, -5f, 0.3f, 1f, "浆果甲"),
            new NodeRow(2004, 3, 5f, 0.3f, -1f, "矿石甲"),
            new NodeRow(2005, 1, -3.5f, 0.3f, -2f, "树木丙"),
            new NodeRow(2006, 1, -2f, 0.3f, 2f, "树木丁"),
            new NodeRow(2007, 2, -5f, 0.3f, -1f, "浆果乙"),
            new NodeRow(2008, 2, -5f, 0.3f, 3f, "浆果丙"),
            new NodeRow(2009, 3, 5f, 0.3f, 1f, "矿石乙"),
            new NodeRow(2010, 3, 5f, 0.3f, -3f, "矿石丙"),
            new NodeRow(2011, 4, 0f, 0.3f, -4f, "花丛甲"),
            new NodeRow(2012, 4, 2f, 0.3f, -4f, "花丛乙"),
            new NodeRow(2013, 4, -2f, 0.3f, -4f, "花丛丙"),
        };

        public const int ExpectedNodeCount = 13;

        public static IReadOnlyList<NodeRow> All => Rows;

        public static bool TryGet(int instanceId, out NodeRow row)
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
            if (Rows.Length != ExpectedNodeCount)
                throw new InvalidOperationException("home-node-count");
            var seen = new HashSet<int>();
            for (int i = 0; i < Rows.Length; i++)
            {
                int id = Rows[i].InstanceId;
                if (id < 2001 || id > 2013)
                    throw new InvalidOperationException("home-node-id-range:" + id);
                if (!seen.Add(id))
                    throw new InvalidOperationException("home-node-id-dup:" + id);
                if (Rows[i].TypeId < 1 || Rows[i].TypeId > 4)
                    throw new InvalidOperationException("home-node-type:" + id);
            }
        }
    }
}
