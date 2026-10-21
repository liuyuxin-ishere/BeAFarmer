using System.Collections.Generic;
using Google.Protobuf;

namespace CloudIsle.Proto
{
    /// <summary>灰盒经营消息（手写 wire，对齐 05 MsgId）。</summary>
    public sealed class PlantReq
    {
        public long OpId;
        public int PlotId;
        public int SeedId;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, OpId);
            ProtoWire.WriteInt32(o, 2, PlotId);
            ProtoWire.WriteInt32(o, 3, SeedId);
        });
        public static PlantReq ParseFrom(byte[] data)
        {
            var m = new PlantReq();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.OpId = input.ReadInt64(); break;
                    case 2: m.PlotId = input.ReadInt32(); break;
                    case 3: m.SeedId = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class PlantResp
    {
        public int Code;
        public int PlotId;
        public int State;
        public long ReadyTimeMs;
        public int SeedCount;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, Code);
            ProtoWire.WriteInt32(o, 2, PlotId);
            ProtoWire.WriteInt32(o, 3, State);
            ProtoWire.WriteInt64(o, 4, ReadyTimeMs);
            ProtoWire.WriteInt32(o, 5, SeedCount);
        });
        public static PlantResp ParseFrom(byte[] data)
        {
            var m = new PlantResp();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.Code = input.ReadInt32(); break;
                    case 2: m.PlotId = input.ReadInt32(); break;
                    case 3: m.State = input.ReadInt32(); break;
                    case 4: m.ReadyTimeMs = input.ReadInt64(); break;
                    case 5: m.SeedCount = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class HarvestReq
    {
        public long OpId;
        public int PlotId;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, OpId);
            ProtoWire.WriteInt32(o, 2, PlotId);
        });
        public static HarvestReq ParseFrom(byte[] data)
        {
            var m = new HarvestReq();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.OpId = input.ReadInt64(); break;
                    case 2: m.PlotId = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class HarvestResp
    {
        public int Code;
        public int PlotId;
        public int State;
        public int WheatGained;
        public int WheatCount;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, Code);
            ProtoWire.WriteInt32(o, 2, PlotId);
            ProtoWire.WriteInt32(o, 3, State);
            ProtoWire.WriteInt32(o, 4, WheatGained);
            ProtoWire.WriteInt32(o, 5, WheatCount);
        });
        public static HarvestResp ParseFrom(byte[] data)
        {
            var m = new HarvestResp();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.Code = input.ReadInt32(); break;
                    case 2: m.PlotId = input.ReadInt32(); break;
                    case 3: m.State = input.ReadInt32(); break;
                    case 4: m.WheatGained = input.ReadInt32(); break;
                    case 5: m.WheatCount = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class CraftStartReq
    {
        public long OpId;
        public int FacilityId;
        public int SlotId;
        public int RecipeId;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, OpId);
            ProtoWire.WriteInt32(o, 2, FacilityId);
            ProtoWire.WriteInt32(o, 3, SlotId);
            ProtoWire.WriteInt32(o, 4, RecipeId);
        });
        public static CraftStartReq ParseFrom(byte[] data)
        {
            var m = new CraftStartReq();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.OpId = input.ReadInt64(); break;
                    case 2: m.FacilityId = input.ReadInt32(); break;
                    case 3: m.SlotId = input.ReadInt32(); break;
                    case 4: m.RecipeId = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class CraftStartResp
    {
        public int Code;
        public int State;
        public long FinishTimeMs;
        public int WheatCount;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, Code);
            ProtoWire.WriteInt32(o, 2, State);
            ProtoWire.WriteInt64(o, 3, FinishTimeMs);
            ProtoWire.WriteInt32(o, 4, WheatCount);
        });
        public static CraftStartResp ParseFrom(byte[] data)
        {
            var m = new CraftStartResp();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.Code = input.ReadInt32(); break;
                    case 2: m.State = input.ReadInt32(); break;
                    case 3: m.FinishTimeMs = input.ReadInt64(); break;
                    case 4: m.WheatCount = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class CraftCollectReq
    {
        public long OpId;
        public int FacilityId;
        public int SlotId;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, OpId);
            ProtoWire.WriteInt32(o, 2, FacilityId);
            ProtoWire.WriteInt32(o, 3, SlotId);
        });
        public static CraftCollectReq ParseFrom(byte[] data)
        {
            var m = new CraftCollectReq();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.OpId = input.ReadInt64(); break;
                    case 2: m.FacilityId = input.ReadInt32(); break;
                    case 3: m.SlotId = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class CraftCollectResp
    {
        public int Code;
        public int FlourGained;
        public int FlourCount;
        public int State;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, Code);
            ProtoWire.WriteInt32(o, 2, FlourGained);
            ProtoWire.WriteInt32(o, 3, FlourCount);
            ProtoWire.WriteInt32(o, 4, State);
        });
        public static CraftCollectResp ParseFrom(byte[] data)
        {
            var m = new CraftCollectResp();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.Code = input.ReadInt32(); break;
                    case 2: m.FlourGained = input.ReadInt32(); break;
                    case 3: m.FlourCount = input.ReadInt32(); break;
                    case 4: m.State = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class OrderSubmitReq
    {
        public long OpId;
        public int OrderId;
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, OpId);
            ProtoWire.WriteInt32(o, 2, OrderId);
        });
        public static OrderSubmitReq ParseFrom(byte[] data)
        {
            var m = new OrderSubmitReq();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.OpId = input.ReadInt64(); break;
                    case 2: m.OrderId = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class OrderSubmitResp
    {
        public int Code;
        public long Gold;
        public int Exp;
        public int FlourCount;
        public int OrderCompleted; // 1=true
        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, Code);
            ProtoWire.WriteInt64(o, 2, Gold);
            ProtoWire.WriteInt32(o, 3, Exp);
            ProtoWire.WriteInt32(o, 4, FlourCount);
            ProtoWire.WriteInt32(o, 5, OrderCompleted);
        });
        public static OrderSubmitResp ParseFrom(byte[] data)
        {
            var m = new OrderSubmitResp();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.Code = input.ReadInt32(); break;
                    case 2: m.Gold = input.ReadInt64(); break;
                    case 3: m.Exp = input.ReadInt32(); break;
                    case 4: m.FlourCount = input.ReadInt32(); break;
                    case 5: m.OrderCompleted = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    /// <summary>登录后可选下发的灰盒快照（简化：用 flags + bag 已够；本消息供刷新）。</summary>
    public sealed class GreyboxSnapshotNtf
    {
        public long ServerTimeMs;
        public int SeedCount;
        public int WheatCount;
        public int FlourCount;
        public long Gold;
        public int Exp;
        public List<int> PlotStates = new List<int>();
        public List<long> PlotReadyMs = new List<long>();
        public int SlotState;
        public long SlotFinishMs;
        public int OrderCompleted;

        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, ServerTimeMs);
            ProtoWire.WriteInt32(o, 2, SeedCount);
            ProtoWire.WriteInt32(o, 3, WheatCount);
            ProtoWire.WriteInt32(o, 4, FlourCount);
            ProtoWire.WriteInt64(o, 5, Gold);
            ProtoWire.WriteInt32(o, 6, Exp);
            ProtoWire.WriteRepeatedInt32(o, 7, PlotStates);
            for (int i = 0; i < PlotReadyMs.Count; i++)
                ProtoWire.WriteInt64(o, 8, PlotReadyMs[i]);
            ProtoWire.WriteInt32(o, 9, SlotState);
            ProtoWire.WriteInt64(o, 10, SlotFinishMs);
            ProtoWire.WriteInt32(o, 11, OrderCompleted);
        }, 256);

        public static GreyboxSnapshotNtf ParseFrom(byte[] data)
        {
            var m = new GreyboxSnapshotNtf();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.ServerTimeMs = input.ReadInt64(); break;
                    case 2: m.SeedCount = input.ReadInt32(); break;
                    case 3: m.WheatCount = input.ReadInt32(); break;
                    case 4: m.FlourCount = input.ReadInt32(); break;
                    case 5: m.Gold = input.ReadInt64(); break;
                    case 6: m.Exp = input.ReadInt32(); break;
                    case 7: m.PlotStates.Add(input.ReadInt32()); break;
                    case 8: m.PlotReadyMs.Add(input.ReadInt64()); break;
                    case 9: m.SlotState = input.ReadInt32(); break;
                    case 10: m.SlotFinishMs = input.ReadInt64(); break;
                    case 11: m.OrderCompleted = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }
}
