using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace CloudIsle.Config
{
    /// <summary>
    /// 不可变物品堆叠规格。用于材料/奖励字符串解析结果；不含可变集合。
    /// </summary>
    public readonly struct ItemStackSpec : IEquatable<ItemStackSpec>
    {
        /// <param name="itemId">物品 ID，必须为正。</param>
        /// <param name="count">数量，必须为正且不超过 int.MaxValue。</param>
        public ItemStackSpec(int itemId, int count)
        {
            if (itemId <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemId), "itemId 必须为正");
            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count), "count 必须为正");
            ItemId = itemId;
            Count = count;
        }

        /// <summary>物品 ID。</summary>
        public int ItemId { get; }

        /// <summary>数量。</summary>
        public int Count { get; }

        /// <inheritdoc />
        public bool Equals(ItemStackSpec other)
        {
            return ItemId == other.ItemId && Count == other.Count;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is ItemStackSpec other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return (ItemId * 397) ^ Count;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return ItemId.ToString(CultureInfo.InvariantCulture) + ":" + Count.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 材料/奖励列表：解析 <c>itemId:count;itemId:count</c> 为真只读集合。
    /// 空或空白串视为空列表；非法格式/空段/数量≤0/溢出/重复 itemId 失败。
    /// 与 <c>gen_config.ps1</c> Parse-MaterialList 同严：不丢弃空段。
    /// </summary>
    public static class MaterialList
    {
        static readonly IReadOnlyList<ItemStackSpec> Empty =
            Array.AsReadOnly(Array.Empty<ItemStackSpec>());

        /// <summary>解析材料字符串。null/空白 → 空只读列表。</summary>
        /// <param name="raw">例如 <c>3009:5;3010:10</c>。</param>
        /// <returns>拥有副本的 <see cref="ReadOnlyCollection{T}"/>，不可经 IList 写入。</returns>
        public static IReadOnlyList<ItemStackSpec> Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Empty;

            // 禁止 RemoveEmptyEntries：双分隔/尾部分隔等空段必须失败（与 PS 导出校验一致）。
            string[] parts = raw.Split(new[] { ';' }, StringSplitOptions.None);
            var list = new List<ItemStackSpec>(parts.Length);
            var seen = new HashSet<int>();
            for (int i = 0; i < parts.Length; i++)
            {
                string piece = parts[i].Trim();
                if (piece.Length == 0)
                    throw new FormatException("材料串含空段");

                int colon = piece.IndexOf(':');
                if (colon <= 0 || colon >= piece.Length - 1)
                    throw new FormatException("材料段必须是 itemId:count，实际: " + piece);

                string idText = piece.Substring(0, colon).Trim();
                string countText = piece.Substring(colon + 1).Trim();
                if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int itemId))
                    throw new FormatException("itemId 不是整数: " + idText);
                if (!long.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long countLong))
                    throw new FormatException("count 不是整数: " + countText);
                if (countLong <= 0)
                    throw new FormatException("count 必须为正: " + countText);
                if (countLong > int.MaxValue)
                    throw new OverflowException("count 溢出: " + countText);
                if (itemId <= 0)
                    throw new FormatException("itemId 必须为正: " + idText);
                if (!seen.Add(itemId))
                    throw new FormatException("材料列表重复 itemId: " + itemId.ToString(CultureInfo.InvariantCulture));

                list.Add(new ItemStackSpec(itemId, checked((int)countLong)));
            }

            return Array.AsReadOnly(list.ToArray());
        }

        /// <summary>校验每个 itemId 均存在于给定集合；缺失则抛出。</summary>
        /// <param name="stacks">已解析堆叠。</param>
        /// <param name="knownItemIds">合法物品 ID 集合。</param>
        /// <param name="context">错误上下文，例如表名与行。</param>
        public static void EnsureItemRefs(IReadOnlyList<ItemStackSpec> stacks, ISet<int> knownItemIds, string context)
        {
            if (stacks == null)
                throw new ArgumentNullException(nameof(stacks));
            if (knownItemIds == null)
                throw new ArgumentNullException(nameof(knownItemIds));
            for (int i = 0; i < stacks.Count; i++)
            {
                int id = stacks[i].ItemId;
                if (!knownItemIds.Contains(id))
                    throw new InvalidOperationException((context ?? "材料") + " 缺少物品引用: " + id.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
