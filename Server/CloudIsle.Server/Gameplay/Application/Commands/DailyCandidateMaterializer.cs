using System;
using System.Collections.Generic;
using CloudIsle.Application.Init;
using CloudIsle.Domain.Daily;
using CloudIsle.Domain.Init;
using CloudIsle.Proto;

namespace CloudIsle.Server.Gameplay.Application.Commands;

/// <summary>
/// 将已验证的 <see cref="DailyCandidate.Aggregate"/> 投影到 PlayerFullData：
/// 只写声明字段，保留无关/未知字段；兼容回调仅可映射已校验补丁且须对齐候选。
/// </summary>
internal static class DailyCandidateMaterializer
{
    public static PlayerFullData Materialize(
        PlayerFullData observed,
        DailyCandidate candidate,
        IReadOnlyDictionary<string, DailyModuleRegistration> registrations)
    {
        if (observed == null) throw new ArgumentNullException(nameof(observed));
        if (candidate == null) throw new ArgumentNullException(nameof(candidate));
        if (registrations == null) throw new ArgumentNullException(nameof(registrations));

        var owned = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < candidate.Patches.Count; i++)
        {
            IReadOnlyList<string> keys = candidate.Patches[i].OwnedFieldKeys;
            for (int k = 0; k < keys.Count; k++)
                owned.Add(keys[k]);
        }

        PlayerFullData next = ProjectDeclaredFields(observed.Clone(), candidate.Aggregate, owned);

        for (int i = 0; i < candidate.Patches.Count; i++)
        {
            DailyPatch patch = candidate.Patches[i];
            if (!registrations.TryGetValue(patch.ModuleKey, out DailyModuleRegistration? reg)
                || reg.ApplyFullData == null)
                continue;

            PlayerFullData trial = next.Clone();
            reg.ApplyFullData(trial, patch, candidate.TargetDay);
            ValidateCallbackAgainstCandidate(next, trial, candidate, patch.OwnedFieldKeys);
            next = trial;
        }

        ValidateFinalAgainstCandidate(observed, next, candidate, owned);
        return next;
    }

    static PlayerFullData ProjectDeclaredFields(
        PlayerFullData data,
        PlayerAggregate aggregate,
        HashSet<string> owned)
    {
        if (data.Info == null)
            data.Info = new PlayerInfo();

        if (Covers(owned, "gold") || Covers(owned, "assets.gold") || Covers(owned, "assets"))
            data.Info.Gold = aggregate.Gold;
        if (Covers(owned, "gem") || Covers(owned, "assets.gem") || Covers(owned, "assets"))
            data.Info.Gem = aggregate.Gem;
        if (Covers(owned, "ticket") || Covers(owned, "assets.ticket") || Covers(owned, "assets"))
            data.Info.Ticket = aggregate.Ticket;
        if (Covers(owned, "shard") || Covers(owned, "assets.shard") || Covers(owned, "assets"))
            data.Info.Shard = aggregate.Shard;
        if (Covers(owned, "level") || Covers(owned, "assets.level") || Covers(owned, "assets"))
            data.Info.Level = aggregate.Level;
        if (Covers(owned, "exp") || Covers(owned, "assets.exp") || Covers(owned, "assets"))
            data.Info.Exp = aggregate.Exp;
        if (Covers(owned, "bagSlots") || Covers(owned, "assets.bagSlots") || Covers(owned, "assets"))
            data.Info.BagSlots = aggregate.BagSlots;
        if (Covers(owned, "guideStep") || Covers(owned, "assets.guideStep") || Covers(owned, "assets"))
            data.Info.GuideStep = aggregate.GuideStep;
        if (Covers(owned, "nickname") || Covers(owned, "assets.nickname") || Covers(owned, "assets"))
            data.Info.Nickname = aggregate.Nickname;

        // 集合域：仅在声明所有权时用领域聚合整表替换（经 ToFullData 保留 unknown 模板行语义）。
        bool projectCollections = Covers(owned, "plots") || Covers(owned, "assets.plots")
                                  || Covers(owned, "nodes") || Covers(owned, "assets.nodes")
                                  || Covers(owned, "tools") || Covers(owned, "assets.tools")
                                  || Covers(owned, "facilities") || Covers(owned, "assets.facilities")
                                  || Covers(owned, "bag") || Covers(owned, "assets.bag")
                                  || Covers(owned, "assets");
        if (projectCollections)
        {
            PlayerFullData projected = PlayerFullDataInitMapper.ToFullData(aggregate, data);
            if (Covers(owned, "plots") || Covers(owned, "assets.plots") || Covers(owned, "assets"))
            {
                data.Plots.Clear();
                data.Plots.AddRange(projected.Plots);
            }
            if (Covers(owned, "nodes") || Covers(owned, "assets.nodes") || Covers(owned, "assets"))
            {
                data.Nodes.Clear();
                data.Nodes.AddRange(projected.Nodes);
            }
            if (Covers(owned, "tools") || Covers(owned, "assets.tools") || Covers(owned, "assets"))
            {
                data.Tools.Clear();
                data.Tools.AddRange(projected.Tools);
            }
            if (Covers(owned, "facilities") || Covers(owned, "assets.facilities") || Covers(owned, "assets"))
            {
                data.Facilities.Clear();
                data.Facilities.AddRange(projected.Facilities);
            }
            if (Covers(owned, "bag") || Covers(owned, "assets.bag") || Covers(owned, "assets"))
            {
                data.Bag.Clear();
                data.Bag.AddRange(projected.Bag);
            }
        }

        return data;
    }

    static void ValidateCallbackAgainstCandidate(
        PlayerFullData observed,
        PlayerFullData trial,
        DailyCandidate candidate,
        IReadOnlyList<string> patchOwned)
    {
        PlayerAggregate trialAgg = PlayerFullDataInitMapper.ToAggregate(trial);
        PlayerAggregate want = candidate.Aggregate;
        PlayerAggregate obsAgg = PlayerFullDataInitMapper.ToAggregate(observed);

        // 回调触及的声明字段必须等于领域候选；未声明字段必须保持观察值。
        AssertScalarAligned(trialAgg, want, obsAgg, patchOwned, requireOwnedMatch: true);
        AssertNoUndeclaredMutation(observed, trial, patchOwned);
    }

    static void ValidateFinalAgainstCandidate(
        PlayerFullData observed,
        PlayerFullData next,
        DailyCandidate candidate,
        HashSet<string> allOwned)
    {
        PlayerAggregate nextAgg = PlayerFullDataInitMapper.ToAggregate(next);
        PlayerAggregate want = candidate.Aggregate;
        PlayerAggregate obsAgg = PlayerFullDataInitMapper.ToAggregate(observed);

        var ownedList = new List<string>(allOwned);
        AssertScalarAligned(nextAgg, want, obsAgg, ownedList, requireOwnedMatch: true);

        // 领域候选相对观察值的差分必须全部落在声明所有权内，且已写入 next。
        if (want.Gold != obsAgg.Gold && nextAgg.Gold != want.Gold)
            throw new InvalidOperationException("daily commit gold diverges from domain candidate");
        if (want.Gem != obsAgg.Gem && nextAgg.Gem != want.Gem)
            throw new InvalidOperationException("daily commit gem diverges from domain candidate");
        if (want.Ticket != obsAgg.Ticket && nextAgg.Ticket != want.Ticket)
            throw new InvalidOperationException("daily commit ticket diverges from domain candidate");
        if (want.Shard != obsAgg.Shard && nextAgg.Shard != want.Shard)
            throw new InvalidOperationException("daily commit shard diverges from domain candidate");

        // 未声明字段相对观察值不得被改写（保留无关 PlayerFullData）。
        AssertNoUndeclaredMutation(observed, next, ownedList);
    }

    static void AssertScalarAligned(
        PlayerAggregate actual,
        PlayerAggregate want,
        PlayerAggregate observed,
        IReadOnlyList<string> owned,
        bool requireOwnedMatch)
    {
        void Check(string name, string assetsName, long actualV, long wantV, long observedV)
        {
            bool declared = Covers(owned, name) || Covers(owned, assetsName) || Covers(owned, "assets");
            if (declared)
            {
                if (requireOwnedMatch && actualV != wantV)
                    throw new InvalidOperationException(
                        "projection callback/domain mismatch on " + name);
            }
            else if (actualV != observedV)
            {
                throw new InvalidOperationException(
                    "undeclared asset mutated during daily projection: " + name);
            }
        }

        Check("gold", "assets.gold", actual.Gold, want.Gold, observed.Gold);
        Check("gem", "assets.gem", actual.Gem, want.Gem, observed.Gem);
        Check("ticket", "assets.ticket", actual.Ticket, want.Ticket, observed.Ticket);
        Check("shard", "assets.shard", actual.Shard, want.Shard, observed.Shard);
        Check("level", "assets.level", actual.Level, want.Level, observed.Level);
        Check("exp", "assets.exp", actual.Exp, want.Exp, observed.Exp);
        Check("bagSlots", "assets.bagSlots", actual.BagSlots, want.BagSlots, observed.BagSlots);
        Check("guideStep", "assets.guideStep", actual.GuideStep, want.GuideStep, observed.GuideStep);
    }

    static void AssertNoUndeclaredMutation(
        PlayerFullData observed,
        PlayerFullData actual,
        IReadOnlyList<string> owned)
    {
        PlayerFullData obsNorm = observed.Clone();
        PlayerFullData actNorm = actual.Clone();
        obsNorm.Revision = 0;
        actNorm.Revision = 0;

        // 对声明字段先对齐到 actual，再要求剩余字节等价于观察值（保留 unknown/orders 等）。
        PlayerAggregate actAgg = PlayerFullDataInitMapper.ToAggregate(actual);
        ProjectDeclaredFields(obsNorm, actAgg, ToSet(owned));
        obsNorm.Revision = 0;
        actNorm.Revision = 0;
        if (!obsNorm.Equals(actNorm))
            throw new InvalidOperationException(
                "daily projection mutated undeclared PlayerFullData fields");
    }

    static HashSet<string> ToSet(IReadOnlyList<string> owned)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < owned.Count; i++)
            set.Add(owned[i]);
        return set;
    }

    static bool Covers(IReadOnlyList<string> owned, string path)
    {
        for (int i = 0; i < owned.Count; i++)
        {
            if (CoversOne(owned[i], path))
                return true;
        }
        return false;
    }

    static bool Covers(HashSet<string> owned, string path)
    {
        foreach (string o in owned)
        {
            if (CoversOne(o, path))
                return true;
        }
        return false;
    }

    static bool CoversOne(string owned, string path)
        => DailyFieldOwnership.Covers(owned, path);
}
