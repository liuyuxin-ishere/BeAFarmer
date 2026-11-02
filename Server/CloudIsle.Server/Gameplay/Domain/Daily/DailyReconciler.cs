using System;
using System.Collections.Generic;
using CloudIsle.Domain.Init;

namespace CloudIsle.Domain.Daily
{
    /// <summary>
    /// 可组合日切：只评估显式注册的策略；跨多天一次到 today，不循环补奖。
    /// 空参与者集合合法（NoOp）。同日新模块仍可按自身 InitVersion 初始化。
    /// Unavailable 模块同日仍重试，不阻塞其他模块合并；不保留调用方可变集合。
    /// </summary>
    public sealed class DailyReconciler
    {
        readonly IReadOnlyList<IDailyResetPolicy> _policies;

        public DailyReconciler(IEnumerable<IDailyResetPolicy>? policies = null)
        {
            if (policies == null)
            {
                _policies = Array.Empty<IDailyResetPolicy>();
                return;
            }

            var list = new List<IDailyResetPolicy>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (IDailyResetPolicy p in policies)
            {
                if (p == null) throw new ArgumentException("null policy");
                if (string.IsNullOrWhiteSpace(p.ModuleKey))
                    throw new ArgumentException("policy ModuleKey required");
                if (p.RequiredInitVersion < 0)
                    throw new ArgumentOutOfRangeException(nameof(policies), "RequiredInitVersion < 0");
                if (!keys.Add(p.ModuleKey))
                    throw new ArgumentException("duplicate policy module: " + p.ModuleKey);
                list.Add(p);
            }
            _policies = list;
        }

        public IReadOnlyList<IDailyResetPolicy> Policies => _policies;

        public DailyCandidate Evaluate(
            PlayerAggregate state,
            IReadOnlyDictionary<string, DailyModuleMarker> markers,
            DailyCommandContext ctx)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (markers == null) throw new ArgumentNullException(nameof(markers));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));

            int targetDay = DailyCalendar.GetDayIndex(ctx.NowMs, ctx.ResetHour);
            if (_policies.Count == 0)
            {
                return new DailyCandidate(
                    targetDay, isNoOp: true, state,
                    SnapshotMarkers(markers),
                    Array.Empty<DailyPatch>());
            }

            var patches = new List<DailyPatch>();
            var owned = new HashSet<string>(StringComparer.Ordinal);
            PlayerAggregate current = state;

            for (int i = 0; i < _policies.Count; i++)
            {
                IDailyResetPolicy policy = _policies[i];
                markers.TryGetValue(policy.ModuleKey, out DailyModuleMarker? previous);
                previous ??= DailyModuleMarker.Default(policy.ModuleKey);

                bool needsInit = previous.InitVersion < policy.RequiredInitVersion;
                bool needsDay = previous.LastResetDay < targetDay;
                // 同日 Unavailable：前置条件可能已变，必须重试；不永久跳过。
                bool needsUnavailableRetry = previous.Availability == ModuleAvailability.Unavailable;
                if (!needsInit && !needsDay && !needsUnavailableRetry)
                    continue;

                DailyPatch patch = policy.Evaluate(current, previous, targetDay, ctx);
                if (patch == null)
                    throw new InvalidOperationException("policy returned null patch: " + policy.ModuleKey);
                if (!string.Equals(patch.ModuleKey, policy.ModuleKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("policy patch key mismatch: " + policy.ModuleKey);

                ValidateMarkerTransition(previous, patch.NextMarker, targetDay, policy);

                for (int k = 0; k < patch.OwnedFieldKeys.Count; k++)
                {
                    string field = patch.OwnedFieldKeys[k];
                    RejectOwnershipCollision(owned, field);
                    owned.Add(field);
                }

                PlayerAggregate beforeApply = current;
                current = patch.Apply(current);
                ValidateOwnedAssetDiff(beforeApply, current, patch.OwnedFieldKeys, policy.ModuleKey);
                patches.Add(patch);
            }

            if (patches.Count == 0)
            {
                return new DailyCandidate(
                    targetDay, isNoOp: true, state,
                    SnapshotMarkers(markers),
                    Array.Empty<DailyPatch>());
            }

            var next = new Dictionary<string, DailyModuleMarker>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DailyModuleMarker> kv in markers)
                next[kv.Key] = kv.Value;
            for (int i = 0; i < patches.Count; i++)
                next[patches[i].ModuleKey] = patches[i].NextMarker;

            // 完全相同的资产+markers → NoOp（不抬 revision）；marker 变化仍算变更。
            if (SameAggregateAssets(current, state) && SameMarkerMaps(next, markers))
            {
                return new DailyCandidate(
                    targetDay, isNoOp: true, state,
                    SnapshotMarkers(markers),
                    Array.Empty<DailyPatch>());
            }

            return new DailyCandidate(targetDay, isNoOp: false, current, next, patches);
        }

        static void ValidateMarkerTransition(
            DailyModuleMarker previous,
            DailyModuleMarker next,
            int toDay,
            IDailyResetPolicy policy)
        {
            if (!string.Equals(next.ModuleKey, policy.ModuleKey, StringComparison.Ordinal))
                throw new InvalidOperationException("marker moduleKey mismatch: " + policy.ModuleKey);
            if (next.LastResetDay != toDay)
                throw new InvalidOperationException(
                    "marker LastResetDay must equal target day: " + policy.ModuleKey);
            if (next.InitVersion < policy.RequiredInitVersion)
                throw new InvalidOperationException(
                    "marker InitVersion below required: " + policy.ModuleKey);
            if (next.InitVersion < previous.InitVersion)
                throw new InvalidOperationException(
                    "marker InitVersion must not decrease: " + policy.ModuleKey);
        }

        static void RejectOwnershipCollision(HashSet<string> owned, string field)
        {
            if (owned.Contains(field))
                throw new InvalidOperationException(
                    "overlapping daily field patch rejected atomically: " + field);
            foreach (string existing in owned)
            {
                if (IsAncestorOrDescendant(existing, field))
                    throw new InvalidOperationException(
                        "overlapping daily field patch rejected atomically: " + field
                        + " conflicts with " + existing);
            }
        }

        static bool IsAncestorOrDescendant(string a, string b)
        {
            return DailyFieldOwnership.Overlaps(a, b);
        }

        static void ValidateOwnedAssetDiff(
            PlayerAggregate before,
            PlayerAggregate after,
            IReadOnlyList<string> ownedFieldKeys,
            string moduleKey)
        {
            if (after == null)
                throw new InvalidOperationException("daily policy returned null aggregate");
            if (before.PlayerId != after.PlayerId || before.Revision != after.Revision
                || before.GrantVersion != after.GrantVersion)
                throw new InvalidOperationException("daily policy cannot change identity, revision or global grant version");
            if (ReferenceEquals(before, after))
                return;

            List<string> changed = DiffAssetPaths(before, after);
            for (int i = 0; i < changed.Count; i++)
            {
                string path = changed[i];
                if (!IsCoveredByOwnership(ownedFieldKeys, path))
                    throw new InvalidOperationException(
                        "daily patch changed undeclared asset field '" + path
                        + "' in module " + moduleKey);
            }
        }

        static bool IsCoveredByOwnership(IReadOnlyList<string> ownedFieldKeys, string path)
        {
            for (int i = 0; i < ownedFieldKeys.Count; i++)
            {
                string owned = ownedFieldKeys[i];
                if (DailyFieldOwnership.Covers(owned, path))
                    return true;
            }
            return false;
        }

        /// <summary>实际资产差分路径（含短名与 assets.* 别名，供所有权覆盖检查）。</summary>
        static List<string> DiffAssetPaths(PlayerAggregate before, PlayerAggregate after)
        {
            var paths = new List<string>();
            void Add(string shortName, bool changed)
            {
                if (!changed) return;
                paths.Add(shortName);
                paths.Add("assets." + shortName);
            }

            Add("gold", before.Gold != after.Gold);
            Add("gem", before.Gem != after.Gem);
            Add("ticket", before.Ticket != after.Ticket);
            Add("shard", before.Shard != after.Shard);
            Add("level", before.Level != after.Level);
            Add("exp", before.Exp != after.Exp);
            Add("bagSlots", before.BagSlots != after.BagSlots);
            Add("guideStep", before.GuideStep != after.GuideStep);
            Add("nickname", !string.Equals(before.Nickname, after.Nickname, StringComparison.Ordinal));
            Add("grantVersion", before.GrantVersion != after.GrantVersion);

            if (!SamePlots(before.Plots, after.Plots))
            {
                paths.Add("plots");
                paths.Add("assets.plots");
            }
            if (!SameNodes(before.Nodes, after.Nodes))
            {
                paths.Add("nodes");
                paths.Add("assets.nodes");
            }
            if (!SameTools(before.Tools, after.Tools))
            {
                paths.Add("tools");
                paths.Add("assets.tools");
            }
            if (!SameFacilities(before.Facilities, after.Facilities))
            {
                paths.Add("facilities");
                paths.Add("assets.facilities");
            }
            if (!SameBag(before.Bag, after.Bag))
            {
                paths.Add("bag");
                paths.Add("assets.bag");
            }
            if (before.OrdersInitializedEmpty != after.OrdersInitializedEmpty)
            {
                paths.Add("order.list");
                paths.Add("orders");
            }

            if (!CloudIsle.Domain.Trade.ShopPurchaseCounts.Same(before.ShopPurchases, after.ShopPurchases))
            {
                paths.Add("shop.purchaseCounts");
                paths.Add("assets.shop.purchaseCounts");
            }

            if (!CloudIsle.Domain.Farm.AcceptedTaskState.Same(before.AcceptedTasks, after.AcceptedTasks))
            {
                paths.Add("farm.acceptedTasks");
                paths.Add("assets.farm.acceptedTasks");
            }

            return paths;
        }

        static bool SameAggregateAssets(PlayerAggregate a, PlayerAggregate b)
        {
            if (ReferenceEquals(a, b)) return true;
            return a.PlayerId == b.PlayerId
                   && string.Equals(a.Nickname, b.Nickname, StringComparison.Ordinal)
                   && a.Level == b.Level
                   && a.Exp == b.Exp
                   && a.Gold == b.Gold
                   && a.Gem == b.Gem
                   && a.Ticket == b.Ticket
                   && a.Shard == b.Shard
                   && a.BagSlots == b.BagSlots
                   && a.GuideStep == b.GuideStep
                   && a.GrantVersion == b.GrantVersion
                   && a.OrdersInitializedEmpty == b.OrdersInitializedEmpty
                   && CloudIsle.Domain.Trade.ShopPurchaseCounts.Same(a.ShopPurchases, b.ShopPurchases)
                   && CloudIsle.Domain.Farm.AcceptedTaskState.Same(a.AcceptedTasks, b.AcceptedTasks)
                   && SamePlots(a.Plots, b.Plots)
                   && SameNodes(a.Nodes, b.Nodes)
                   && SameTools(a.Tools, b.Tools)
                   && SameFacilities(a.Facilities, b.Facilities)
                   && SameBag(a.Bag, b.Bag);
        }

        static bool SameMarkerMaps(
            IReadOnlyDictionary<string, DailyModuleMarker> a,
            IReadOnlyDictionary<string, DailyModuleMarker> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, DailyModuleMarker> kv in a)
            {
                if (!b.TryGetValue(kv.Key, out DailyModuleMarker? other))
                    return false;
                if (!kv.Value.Equals(other))
                    return false;
            }
            return true;
        }

        static bool SamePlots(IReadOnlyList<PlotStateView> a, IReadOnlyList<PlotStateView> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                PlotStateView x = a[i], y = b[i];
                if (x.PlotId != y.PlotId || x.State != y.State || x.CropId != y.CropId
                    || x.PlantTime != y.PlantTime || x.ReadyTime != y.ReadyTime
                    || x.Watered != y.Watered || x.Fertilized != y.Fertilized)
                    return false;
            }
            return true;
        }

        static bool SameNodes(IReadOnlyList<NodeStateView> a, IReadOnlyList<NodeStateView> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].NodeId != b[i].NodeId || a[i].ConfigNodeId != b[i].ConfigNodeId
                    || a[i].NextReadyTime != b[i].NextReadyTime)
                    return false;
            }
            return true;
        }

        static bool SameTools(IReadOnlyList<ToolStateView> a, IReadOnlyList<ToolStateView> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].ToolId != b[i].ToolId || a[i].Level != b[i].Level)
                    return false;
            }
            return true;
        }

        static bool SameFacilities(IReadOnlyList<FacilityStateView> a, IReadOnlyList<FacilityStateView> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].FacilityId != b[i].FacilityId || a[i].Level != b[i].Level)
                    return false;
            }
            return true;
        }

        static bool SameBag(IReadOnlyList<BagItemView> a, IReadOnlyList<BagItemView> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].ItemId != b[i].ItemId || a[i].Count != b[i].Count)
                    return false;
            }
            return true;
        }

        static IReadOnlyDictionary<string, DailyModuleMarker> SnapshotMarkers(
            IReadOnlyDictionary<string, DailyModuleMarker> markers)
        {
            var copy = new Dictionary<string, DailyModuleMarker>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, DailyModuleMarker> kv in markers)
                copy[kv.Key] = kv.Value;
            return copy;
        }
    }
}
