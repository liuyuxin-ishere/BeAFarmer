using System;
using System.Collections.Generic;
using System.Numerics;
using CloudIsle.Net;

namespace CloudIsle.Domain.Inventory
{
    /// <summary>
    /// D01 纯库存策略：同 ID 精确聚合后验最终非负/容量；不修改 current/deltas/rules；不 Save/不发 Ntf/不增 revision。
    /// </summary>
    public static class InventoryPolicy
    {
        /// <summary>
        /// 应用候选变化。失败时源对象全字段指纹不变；返回集合不别名源对象。
        /// 同 ID 用 BigInteger 精确有界求和，避免中间 long 前缀溢出导致排列依赖；最终 checked 转 long/int。
        /// </summary>
        public static InventoryResult TryApply(
            InventoryState current,
            IReadOnlyList<ItemDelta> deltas,
            InventoryRules rules)
        {
            if (current == null)
                throw new ArgumentNullException(nameof(current));
            if (deltas == null)
                throw new ArgumentNullException(nameof(deltas));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            if (current.BagSlots <= 0 || current.BagSlots > rules.MaxBagSlots)
                return InventoryResult.Fail(ErrorCodes.BadPacket);

            if (deltas.Count > rules.MaxDeltaEntries)
                return InventoryResult.Fail(ErrorCodes.BadPacket);

            var working = new Dictionary<int, long>();
            var legacyIds = new HashSet<int>();
            var diagnostics = new List<string>();

            if (!TryLoadCurrent(current, rules, working, legacyIds, diagnostics, out int loadCode))
                return InventoryResult.Fail(loadCode, diagnostics);

            var beforeCounts = new Dictionary<int, long>(working);
            var aggregated = new Dictionary<int, BigInteger>();

            for (int i = 0; i < deltas.Count; i++)
            {
                ItemDelta d = deltas[i];
                if (d.ItemId <= 0)
                    return InventoryResult.Fail(ErrorCodes.ItemInvalid, diagnostics);
                if (d.Delta == 0)
                    continue;

                if (aggregated.TryGetValue(d.ItemId, out BigInteger sum))
                    aggregated[d.ItemId] = sum + d.Delta;
                else
                    aggregated[d.ItemId] = d.Delta;
            }

            foreach (KeyValuePair<int, BigInteger> pair in aggregated)
            {
                int itemId = pair.Key;
                BigInteger net = pair.Value;
                if (net.IsZero)
                    continue;

                // 未知新 ID / Currency（无物理 stackMax）/ 对 legacy 的任何增减：306；未变化 legacy 不进入本循环。
                if (!rules.IsKnownItem(itemId) || legacyIds.Contains(itemId))
                    return InventoryResult.Fail(ErrorCodes.ItemInvalid, diagnostics);

                if (!rules.TryGetStackMax(itemId, out int maxStack) || maxStack <= 0)
                    return InventoryResult.Fail(ErrorCodes.BadPacket, diagnostics);

                BigInteger prior = working.TryGetValue(itemId, out long cur) ? cur : BigInteger.Zero;
                BigInteger next = prior + net;
                if (next < BigInteger.Zero)
                    return InventoryResult.Fail(ErrorCodes.ItemNotEnough, diagnostics);
                if (next > int.MaxValue)
                    return InventoryResult.Fail(ErrorCodes.BadPacket, diagnostics);

                working[itemId] = (long)next;
            }

            // 移除零数量；负最终数 → 302；超 int 最终数 → 103
            var removeKeys = new List<int>();
            foreach (KeyValuePair<int, long> pair in working)
            {
                if (pair.Value < 0)
                    return InventoryResult.Fail(ErrorCodes.ItemNotEnough, diagnostics);
                if (pair.Value > int.MaxValue)
                    return InventoryResult.Fail(ErrorCodes.BadPacket, diagnostics);
                if (pair.Value == 0)
                    removeKeys.Add(pair.Key);
            }
            for (int i = 0; i < removeKeys.Count; i++)
                working.Remove(removeKeys[i]);

            if (!TryCountOccupied(working, rules, legacyIds, out int occupied, out int slotCode))
                return InventoryResult.Fail(slotCode, diagnostics);

            if (occupied > current.BagSlots)
                return InventoryResult.Fail(ErrorCodes.BagFull, diagnostics);

            InventoryState candidate = BuildCandidate(current.BagSlots, working);
            IReadOnlyList<InventoryStackView> diffs = BuildDiffs(beforeCounts, working);
            return InventoryResult.Ok(candidate, diffs, diagnostics);
        }

        /// <summary>
        /// 仅改容量、保留堆叠：目标格须落在 (0, MaxBagSlots]；占格超目标 → 301；失败源不变。
        /// 不 Save/Ntf/revision；不别名源集合。
        /// </summary>
        public static InventoryResult TrySetBagSlots(
            InventoryState current,
            int targetBagSlots,
            InventoryRules rules)
        {
            if (current == null)
                throw new ArgumentNullException(nameof(current));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));

            var diagnostics = new List<string>();
            if (current.BagSlots <= 0 || current.BagSlots > rules.MaxBagSlots)
                return InventoryResult.Fail(ErrorCodes.BadPacket, diagnostics);
            if (targetBagSlots <= 0 || targetBagSlots > rules.MaxBagSlots)
                return InventoryResult.Fail(ErrorCodes.BadPacket, diagnostics);

            var working = new Dictionary<int, long>();
            var legacyIds = new HashSet<int>();
            if (!TryLoadCurrent(current, rules, working, legacyIds, diagnostics, out int loadCode))
                return InventoryResult.Fail(loadCode, diagnostics);

            if (!TryCountOccupied(working, rules, legacyIds, out int occupied, out int slotCode))
                return InventoryResult.Fail(slotCode, diagnostics);

            if (occupied > targetBagSlots)
                return InventoryResult.Fail(ErrorCodes.BagFull, diagnostics);

            InventoryState candidate = BuildCandidate(targetBagSlots, working);
            return InventoryResult.Ok(candidate, Array.AsReadOnly(Array.Empty<InventoryStackView>()), diagnostics);
        }

        /// <summary>
        /// 按每物品 ceil(count/maxStack) 计占格；legacy 用 stack_default。非法配置/负存量显式抛错。
        /// </summary>
        public static int CountOccupiedSlots(InventoryState state, InventoryRules rules)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (rules == null)
                throw new ArgumentNullException(nameof(rules));
            if (state.BagSlots <= 0 || state.BagSlots > rules.MaxBagSlots)
                throw new ArgumentOutOfRangeException(nameof(state), "bagSlots 超限或非法");

            var working = new Dictionary<int, long>();
            var legacyIds = new HashSet<int>();
            var diagnostics = new List<string>();
            if (!TryLoadCurrent(state, rules, working, legacyIds, diagnostics, out _))
                throw new InvalidOperationException("库存状态非法（负数量/重复 ID/非法条目）");

            if (!TryCountOccupied(working, rules, legacyIds, out int occupied, out _))
                throw new InvalidOperationException("堆叠配置非法或占格溢出");
            return occupied;
        }

        /// <summary>
        /// checked 宽整数 ceil(count/maxStack)，避免 count+maxStack-1 溢出。
        /// </summary>
        public static long CeilDivChecked(long count, int maxStack)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (maxStack <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxStack));
            if (count == 0)
                return 0;
            long q = count / maxStack;
            long r = count % maxStack;
            return r == 0 ? q : checked(q + 1);
        }

        static bool TryLoadCurrent(
            InventoryState current,
            InventoryRules rules,
            Dictionary<int, long> working,
            HashSet<int> legacyIds,
            List<string> diagnostics,
            out int code)
        {
            code = ErrorCodes.Ok;
            var seen = new HashSet<int>();
            for (int i = 0; i < current.Stacks.Count; i++)
            {
                InventoryStackView stack = current.Stacks[i];
                if (stack.ItemId <= 0 || stack.Count <= 0)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
                if (!seen.Add(stack.ItemId))
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }

                if (rules.IsKnownItem(stack.ItemId))
                {
                    if (!rules.TryGetStackMax(stack.ItemId, out int maxStack) || maxStack <= 0)
                    {
                        code = ErrorCodes.BadPacket;
                        return false;
                    }
                }
                else
                {
                    // O06：未知旧条目保留；stack_default 占格；不可操作。
                    legacyIds.Add(stack.ItemId);
                    diagnostics.Add(
                        "legacy-item:" + stack.ItemId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " retained stack_default");
                }

                working[stack.ItemId] = stack.Count;
            }
            return true;
        }

        static bool TryCountOccupied(
            Dictionary<int, long> working,
            InventoryRules rules,
            HashSet<int> legacyIds,
            out int occupied,
            out int code)
        {
            occupied = 0;
            code = ErrorCodes.Ok;
            long total = 0;
            foreach (KeyValuePair<int, long> pair in working)
            {
                if (pair.Value <= 0)
                    continue;

                int maxStack;
                if (legacyIds.Contains(pair.Key) || !rules.IsKnownItem(pair.Key))
                {
                    maxStack = rules.StackDefault;
                }
                else if (!rules.TryGetStackMax(pair.Key, out maxStack) || maxStack <= 0)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }

                try
                {
                    long slots = CeilDivChecked(pair.Value, maxStack);
                    total = checked(total + slots);
                }
                catch (OverflowException)
                {
                    code = ErrorCodes.BadPacket;
                    return false;
                }
            }

            if (total > int.MaxValue)
            {
                code = ErrorCodes.BadPacket;
                return false;
            }

            occupied = (int)total;
            return true;
        }

        static InventoryState BuildCandidate(int bagSlots, Dictionary<int, long> working)
        {
            var ids = new List<int>(working.Keys);
            ids.Sort();
            var stacks = new List<InventoryStackView>(ids.Count);
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                long count = working[id];
                if (count <= 0)
                    continue;
                if (count > int.MaxValue)
                    throw new OverflowException("final count exceeds int");
                stacks.Add(new InventoryStackView(id, (int)count));
            }
            return new InventoryState(bagSlots, stacks);
        }

        static IReadOnlyList<InventoryStackView> BuildDiffs(
            Dictionary<int, long> before,
            Dictionary<int, long> after)
        {
            var ids = new HashSet<int>(before.Keys);
            foreach (int id in after.Keys)
                ids.Add(id);

            var sorted = new List<int>(ids);
            sorted.Sort();
            var diffs = new List<InventoryStackView>();
            for (int i = 0; i < sorted.Count; i++)
            {
                int id = sorted[i];
                long b = before.TryGetValue(id, out long bv) ? bv : 0L;
                long a = after.TryGetValue(id, out long av) ? av : 0L;
                if (b == a)
                    continue;
                if (a > int.MaxValue)
                    throw new OverflowException("diff count exceeds int");
                diffs.Add(new InventoryStackView(id, (int)a));
            }
            return diffs;
        }
    }
}
