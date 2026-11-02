using System;
using System.Collections.Generic;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 农场批量逐项展示结果可用性（仅显示；不写资产）。
    /// </summary>
    public enum FarmResultsAvailability
    {
        /// <summary>非农场批量 / 不适用。</summary>
        None = 0,
        /// <summary>权威逐项结果可用。</summary>
        Present = 1,
        /// <summary>旧 v1/v2/空/不支持摘要；不得发明逐项成功。</summary>
        Unavailable = 2,
        /// <summary>可识别归档/摘要畸形；可观察错误，不得假全成功。</summary>
        Error = 3
    }

    /// <summary>单地块展示码（防御拷贝用值类型）。</summary>
    public readonly struct FarmPlotDisplayResult : IEquatable<FarmPlotDisplayResult>
    {
        public FarmPlotDisplayResult(int plotId, int code)
        {
            PlotId = plotId;
            Code = code;
        }

        public int PlotId { get; }
        public int Code { get; }

        public bool Equals(FarmPlotDisplayResult other)
            => PlotId == other.PlotId && Code == other.Code;

        public override bool Equals(object obj)
            => obj is FarmPlotDisplayResult other && Equals(other);

        public override int GetHashCode()
            => unchecked((PlotId * 397) ^ Code);
    }

    /// <summary>
    /// 槽位私有不可变展示结果信封；getter 返回新数组拷贝。
    /// </summary>
    public sealed class FarmCommandDisplayResults
    {
        public const int MaxItems = 24;

        static readonly FarmPlotDisplayResult[] EmptyItems = Array.Empty<FarmPlotDisplayResult>();
        static readonly FarmCommandDisplayResults NoneInstance =
            new FarmCommandDisplayResults(FarmResultsAvailability.None, "", EmptyItems);
        static readonly FarmCommandDisplayResults UnavailableInstance =
            new FarmCommandDisplayResults(FarmResultsAvailability.Unavailable, "", EmptyItems);
        static readonly FarmCommandDisplayResults ErrorInstance =
            new FarmCommandDisplayResults(FarmResultsAvailability.Error, "", EmptyItems);

        readonly FarmPlotDisplayResult[] _items;

        FarmCommandDisplayResults(
            FarmResultsAvailability availability,
            string archiveKind,
            FarmPlotDisplayResult[] items)
        {
            Availability = availability;
            ArchiveKind = archiveKind ?? "";
            _items = items ?? EmptyItems;
        }

        public static FarmCommandDisplayResults None => NoneInstance;
        public static FarmCommandDisplayResults Unavailable => UnavailableInstance;
        public static FarmCommandDisplayResults Error => ErrorInstance;

        public FarmResultsAvailability Availability { get; }
        /// <summary>归档头 kind（Plant/Harvest）；直连 Resp 时与命令键一致。</summary>
        public string ArchiveKind { get; }

        /// <summary>每次新拷贝；不可变快照。</summary>
        public IReadOnlyList<FarmPlotDisplayResult> Items
        {
            get
            {
                if (_items.Length == 0)
                    return EmptyItems;
                var copy = new FarmPlotDisplayResult[_items.Length];
                Array.Copy(_items, copy, _items.Length);
                return copy;
            }
        }

        public int Count => _items.Length;

        public static FarmCommandDisplayResults Present(string archiveKind, FarmPlotDisplayResult[] items)
        {
            if (items == null || items.Length == 0)
                return Unavailable;
            if (items.Length > MaxItems)
                return Error;
            var copy = new FarmPlotDisplayResult[items.Length];
            Array.Copy(items, copy, items.Length);
            return new FarmCommandDisplayResults(
                FarmResultsAvailability.Present,
                archiveKind ?? "",
                copy);
        }

        public static FarmCommandDisplayResults ErrorWithKind(string archiveKind)
            => new FarmCommandDisplayResults(FarmResultsAvailability.Error, archiveKind ?? "", EmptyItems);

        public static FarmCommandDisplayResults UnavailableWithKind(string archiveKind)
            => new FarmCommandDisplayResults(FarmResultsAvailability.Unavailable, archiveKind ?? "", EmptyItems);

        /// <summary>槽位私有克隆；调用方不得共享可变数组。</summary>
        public FarmCommandDisplayResults Clone()
        {
            if (Availability == FarmResultsAvailability.None)
                return None;
            if (Availability == FarmResultsAvailability.Unavailable && _items.Length == 0
                && string.IsNullOrEmpty(ArchiveKind))
                return Unavailable;
            if (Availability == FarmResultsAvailability.Error && _items.Length == 0
                && string.IsNullOrEmpty(ArchiveKind))
                return Error;
            var copy = new FarmPlotDisplayResult[_items.Length];
            if (_items.Length > 0)
                Array.Copy(_items, copy, _items.Length);
            return new FarmCommandDisplayResults(Availability, ArchiveKind, copy);
        }
    }
}
