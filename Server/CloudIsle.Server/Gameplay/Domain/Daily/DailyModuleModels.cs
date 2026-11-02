using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CloudIsle.Domain.Init;

namespace CloudIsle.Domain.Daily
{
    public enum ModuleAvailability
    {
        Available = 0,
        Unavailable = 1
    }

    /// <summary>各模块独立日切/初始化 CAS 标记；不进客户端 PlayerFullData。</summary>
    public sealed class DailyModuleMarker : IEquatable<DailyModuleMarker>
    {
        public DailyModuleMarker(
            string moduleKey,
            int initVersion,
            int lastResetDay,
            ModuleAvailability availability,
            string capabilityHash)
        {
            if (string.IsNullOrWhiteSpace(moduleKey))
                throw new ArgumentException("moduleKey required", nameof(moduleKey));
            if (initVersion < 0)
                throw new ArgumentOutOfRangeException(nameof(initVersion));
            if (availability != ModuleAvailability.Available
                && availability != ModuleAvailability.Unavailable)
                throw new ArgumentOutOfRangeException(nameof(availability));
            CapabilityHash = capabilityHash ?? "";
            ModuleKey = moduleKey;
            InitVersion = initVersion;
            LastResetDay = lastResetDay;
            Availability = availability;
        }

        public string ModuleKey { get; }
        public int InitVersion { get; }
        public int LastResetDay { get; }
        public ModuleAvailability Availability { get; }
        public string CapabilityHash { get; }

        public bool Equals(DailyModuleMarker? other)
        {
            if (other is null) return false;
            return string.Equals(ModuleKey, other.ModuleKey, StringComparison.Ordinal)
                   && InitVersion == other.InitVersion
                   && LastResetDay == other.LastResetDay
                   && Availability == other.Availability
                   && string.Equals(CapabilityHash, other.CapabilityHash, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => Equals(obj as DailyModuleMarker);
        public override int GetHashCode()
            => HashCode.Combine(ModuleKey, InitVersion, LastResetDay, (int)Availability, CapabilityHash);

        public static DailyModuleMarker Default(string moduleKey)
            => new(moduleKey, initVersion: 0, lastResetDay: 0, ModuleAvailability.Available, "");
    }

    /// <summary>单次命令固定的日切上下文（配置/时间/可选 RNG）。</summary>
    public sealed class DailyCommandContext
    {
        public DailyCommandContext(long nowMs, int configVersion, int resetHour, object? config = null)
        {
            if (nowMs < 0) throw new ArgumentOutOfRangeException(nameof(nowMs));
            if (configVersion < 1) throw new ArgumentOutOfRangeException(nameof(configVersion));
            DailyCalendar.CheckResetHour(resetHour);
            NowMs = nowMs;
            ConfigVersion = configVersion;
            ResetHour = resetHour;
            Config = config;
        }

        public long NowMs { get; }
        public int ConfigVersion { get; }
        public int ResetHour { get; }
        public object? Config { get; }
    }

    /// <summary>
    /// 单模块日切补丁。OwnedFieldKeys 用于合并时拒绝重叠/祖先冲突字段；
    /// Apply 返回同引用表示无资产字段变化（纯 marker）。
    /// </summary>
    public sealed class DailyPatch
    {
        readonly Func<PlayerAggregate, PlayerAggregate> _apply;

        public DailyPatch(
            string moduleKey,
            DailyModuleMarker nextMarker,
            IReadOnlyList<string> ownedFieldKeys,
            Func<PlayerAggregate, PlayerAggregate>? apply = null)
        {
            if (string.IsNullOrWhiteSpace(moduleKey))
                throw new ArgumentException("moduleKey required", nameof(moduleKey));
            if (nextMarker == null) throw new ArgumentNullException(nameof(nextMarker));
            if (!string.Equals(moduleKey, nextMarker.ModuleKey, StringComparison.Ordinal))
                throw new ArgumentException("marker moduleKey mismatch", nameof(nextMarker));
            ModuleKey = moduleKey;
            NextMarker = nextMarker;
            OwnedFieldKeys = FreezeKeys(ownedFieldKeys);
            _apply = apply ?? (a => a);
        }

        public string ModuleKey { get; }
        public DailyModuleMarker NextMarker { get; }
        public IReadOnlyList<string> OwnedFieldKeys { get; }

        public PlayerAggregate Apply(PlayerAggregate state) => _apply(state ?? throw new ArgumentNullException(nameof(state)));

        static IReadOnlyList<string> FreezeKeys(IReadOnlyList<string>? keys)
        {
            if (keys == null || keys.Count == 0)
                return Array.Empty<string>();
            var arr = new string[keys.Count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                string k = keys[i] ?? throw new ArgumentException("owned field key null");
                if (!seen.Add(k))
                    throw new ArgumentException("duplicate owned field: " + k);
                arr[i] = k;
            }
            return Array.AsReadOnly(arr);
        }
    }

    /// <summary>可组合日切策略；T024/T054 再注册业务实现。</summary>
    public interface IDailyResetPolicy
    {
        string ModuleKey { get; }
        /// <summary>模块要求的 InitVersion；同日新注册且 InitVersion 落后仍须初始化。</summary>
        int RequiredInitVersion { get; }

        DailyPatch Evaluate(
            PlayerAggregate state,
            DailyModuleMarker previous,
            int toDay,
            DailyCommandContext ctx);
    }

    /// <summary>一次日切评估结果；空参与者 → IsNoOp。暴露集合均为拷贝+只读。</summary>
    public sealed class DailyCandidate
    {
        public DailyCandidate(
            int targetDay,
            bool isNoOp,
            PlayerAggregate aggregate,
            IReadOnlyDictionary<string, DailyModuleMarker> nextMarkers,
            IReadOnlyList<DailyPatch> patches)
        {
            if (nextMarkers == null) throw new ArgumentNullException(nameof(nextMarkers));
            if (patches == null) throw new ArgumentNullException(nameof(patches));
            if (aggregate == null) throw new ArgumentNullException(nameof(aggregate));
            TargetDay = targetDay;
            IsNoOp = isNoOp;
            Aggregate = aggregate;
            NextMarkers = FreezeMarkers(nextMarkers);
            Patches = FreezePatches(patches);
        }

        public int TargetDay { get; }
        public bool IsNoOp { get; }
        public PlayerAggregate Aggregate { get; }
        public IReadOnlyDictionary<string, DailyModuleMarker> NextMarkers { get; }
        public IReadOnlyList<DailyPatch> Patches { get; }
        public bool HasAssetOrMarkerChange => !IsNoOp;

        static IReadOnlyDictionary<string, DailyModuleMarker> FreezeMarkers(
            IReadOnlyDictionary<string, DailyModuleMarker> source)
        {
            var copy = new Dictionary<string, DailyModuleMarker>(source.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, DailyModuleMarker> kv in source)
            {
                if (kv.Value == null)
                    throw new ArgumentException("null marker in candidate");
                copy[kv.Key] = kv.Value;
            }
            return new ReadOnlyDictionary<string, DailyModuleMarker>(copy);
        }

        static IReadOnlyList<DailyPatch> FreezePatches(IReadOnlyList<DailyPatch> source)
        {
            if (source.Count == 0)
                return Array.Empty<DailyPatch>();
            var arr = new DailyPatch[source.Count];
            for (int i = 0; i < source.Count; i++)
                arr[i] = source[i] ?? throw new ArgumentException("null patch in candidate");
            return Array.AsReadOnly(arr);
        }
    }
}
