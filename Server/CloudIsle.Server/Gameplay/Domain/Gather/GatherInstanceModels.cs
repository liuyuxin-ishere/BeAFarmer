using System;
using System.Collections.Generic;

namespace CloudIsle.Domain.Gather
{
    /// <summary>
    /// 稳定采集实例规格：<see cref="InstanceId"/> 为 MapInstance.instanceId，
    /// <see cref="ConfigNodeId"/> 为 GatherNode.id（类型），二者不可互换。
    /// </summary>
    public readonly struct NodeSpec
    {
        public NodeSpec(int instanceId, int configNodeId)
        {
            InstanceId = instanceId;
            ConfigNodeId = configNodeId;
        }

        public int InstanceId { get; }
        /// <summary>GatherNode 类型 id；不是 instanceId、Code、下标或坐标。</summary>
        public int ConfigNodeId { get; }
    }

    /// <summary>
    /// 稳定设施占位规格（仅映射；不加工业务）。
    /// <see cref="InstanceId"/> 为 MapInstance.instanceId，
    /// <see cref="ConfigFacilityId"/> 为 Facility.id。
    /// </summary>
    public readonly struct FacilitySpec
    {
        public FacilitySpec(int instanceId, int configFacilityId)
        {
            InstanceId = instanceId;
            ConfigFacilityId = configFacilityId;
        }

        public int InstanceId { get; }
        public int ConfigFacilityId { get; }
    }

    /// <summary>
    /// 采集实例授权结果：成功时 Spec 取正式映射类型，NextReadyTime 取唯一存档行冷却（须 ≥0）。
    /// 失败（含重复目标行、负冷却）不创建/改写状态，不去重洗数据。
    /// </summary>
    public sealed class NodeAuthorizeResult
    {
        NodeAuthorizeResult(
            bool success,
            int code,
            NodeSpec spec,
            long nextReadyTime,
            IReadOnlyList<string> diagnostics)
        {
            Success = success;
            Code = code;
            Spec = spec;
            NextReadyTime = nextReadyTime;
            Diagnostics = diagnostics ?? Array.AsReadOnly(Array.Empty<string>());
        }

        public bool Success { get; }
        public int Code { get; }
        public NodeSpec Spec { get; }
        public long NextReadyTime { get; }
        public IReadOnlyList<string> Diagnostics { get; }

        public static NodeAuthorizeResult Ok(NodeSpec spec, long nextReadyTime, IReadOnlyList<string>? diagnostics = null)
            => new(true, 0, spec, nextReadyTime, Freeze(diagnostics));

        public static NodeAuthorizeResult Fail(int code, IReadOnlyList<string>? diagnostics = null)
            => new(false, code, default, 0, Freeze(diagnostics));

        static IReadOnlyList<string> Freeze(IReadOnlyList<string>? source)
        {
            if (source == null || source.Count == 0)
                return Array.AsReadOnly(Array.Empty<string>());
            var arr = new string[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i] ?? "";
            return Array.AsReadOnly(arr);
        }
    }
}
