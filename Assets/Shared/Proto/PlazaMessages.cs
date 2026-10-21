using System;
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;

namespace CloudIsle.Proto
{
    /// <summary>
    /// PoC #10 广场消息（手写 protobuf wire，字段号对齐 plaza.proto / 05§4.7）。
    /// 避免本机无 protoc 时阻塞；后续可用官方生成代码替换。
    /// </summary>
    public static class ProtoWire
    {
        public static byte[] Encode(Action<CodedOutputStream> write, int sizeHint = 64)
        {
            using var ms = new MemoryStream(sizeHint);
            var output = new CodedOutputStream(ms);
            write(output);
            output.Flush();
            return ms.ToArray();
        }

        public static void WriteInt32(CodedOutputStream o, int field, int value)
        {
            if (value == 0) return;
            o.WriteTag(field, WireFormat.WireType.Varint);
            o.WriteInt32(value);
        }

        public static void WriteInt64(CodedOutputStream o, int field, long value)
        {
            if (value == 0L) return;
            o.WriteTag(field, WireFormat.WireType.Varint);
            o.WriteInt64(value);
        }

        public static void WriteString(CodedOutputStream o, int field, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            o.WriteTag(field, WireFormat.WireType.LengthDelimited);
            o.WriteString(value);
        }

        public static void WriteMessage(CodedOutputStream o, int field, byte[] body)
        {
            if (body == null || body.Length == 0) return;
            o.WriteTag(field, WireFormat.WireType.LengthDelimited);
            o.WriteBytes(ByteString.CopyFrom(body));
        }

        public static void WriteRepeatedInt32(CodedOutputStream o, int field, IList<int> values)
        {
            if (values == null) return;
            for (int i = 0; i < values.Count; i++)
            {
                o.WriteTag(field, WireFormat.WireType.Varint);
                o.WriteInt32(values[i]);
            }
        }

        public static void WriteRepeatedInt64(CodedOutputStream o, int field, IList<long> values)
        {
            if (values == null) return;
            for (int i = 0; i < values.Count; i++)
            {
                o.WriteTag(field, WireFormat.WireType.Varint);
                o.WriteInt64(values[i]);
            }
        }
    }

    public sealed class PlazaEntity
    {
        public long PlayerId;
        public string Nickname = "";
        public int Level;
        public List<int> AvatarIds = new List<int>();
        public int X;
        public int Y;
        public int Dir;
        public int State;

        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, PlayerId);
            ProtoWire.WriteString(o, 2, Nickname);
            ProtoWire.WriteInt32(o, 3, Level);
            ProtoWire.WriteRepeatedInt32(o, 4, AvatarIds);
            ProtoWire.WriteInt32(o, 5, X);
            ProtoWire.WriteInt32(o, 6, Y);
            ProtoWire.WriteInt32(o, 7, Dir);
            ProtoWire.WriteInt32(o, 8, State);
        }, 128);

        public static PlazaEntity ParseFrom(byte[] data)
        {
            var m = new PlazaEntity();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.PlayerId = input.ReadInt64(); break;
                    case 2: m.Nickname = input.ReadString(); break;
                    case 3: m.Level = input.ReadInt32(); break;
                    case 4: m.AvatarIds.Add(input.ReadInt32()); break;
                    case 5: m.X = input.ReadInt32(); break;
                    case 6: m.Y = input.ReadInt32(); break;
                    case 7: m.Dir = input.ReadInt32(); break;
                    case 8: m.State = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class PlazaEnterReq
    {
        public int PreferLineId;
        public byte[] ToByteArray() => ProtoWire.Encode(o => ProtoWire.WriteInt32(o, 1, PreferLineId));
        public static PlazaEnterReq ParseFrom(byte[] data)
        {
            var m = new PlazaEnterReq();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 1) m.PreferLineId = input.ReadInt32();
                else input.SkipLastField();
            }
            return m;
        }
    }

    public sealed class PlazaEnterResp
    {
        public int Code;
        public int LineId;
        public PlazaEntity Self;
        public List<PlazaEntity> Entities = new List<PlazaEntity>();

        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, Code);
            ProtoWire.WriteInt32(o, 2, LineId);
            if (Self != null) ProtoWire.WriteMessage(o, 3, Self.ToByteArray());
            for (int i = 0; i < Entities.Count; i++)
                ProtoWire.WriteMessage(o, 4, Entities[i].ToByteArray());
        }, 1024);

        public static PlazaEnterResp ParseFrom(byte[] data)
        {
            var m = new PlazaEnterResp();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.Code = input.ReadInt32(); break;
                    case 2: m.LineId = input.ReadInt32(); break;
                    case 3: m.Self = PlazaEntity.ParseFrom(input.ReadBytes().ToByteArray()); break;
                    case 4: m.Entities.Add(PlazaEntity.ParseFrom(input.ReadBytes().ToByteArray())); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class PlazaLeaveReq
    {
        public byte[] ToByteArray() => Array.Empty<byte>();
        public static PlazaLeaveReq ParseFrom(byte[] data) => new PlazaLeaveReq();
    }

    public sealed class PlazaLeaveResp
    {
        public int Code;
        public byte[] ToByteArray() => ProtoWire.Encode(o => ProtoWire.WriteInt32(o, 1, Code));
        public static PlazaLeaveResp ParseFrom(byte[] data)
        {
            var m = new PlazaLeaveResp();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 1) m.Code = input.ReadInt32();
                else input.SkipLastField();
            }
            return m;
        }
    }

    public sealed class PlazaMoveReq
    {
        public int X;
        public int Y;
        public int Dir;
        public int State;
        public long ClientTime;

        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, X);
            ProtoWire.WriteInt32(o, 2, Y);
            ProtoWire.WriteInt32(o, 3, Dir);
            ProtoWire.WriteInt32(o, 4, State);
            ProtoWire.WriteInt64(o, 5, ClientTime);
        });

        public static PlazaMoveReq ParseFrom(byte[] data)
        {
            var m = new PlazaMoveReq();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.X = input.ReadInt32(); break;
                    case 2: m.Y = input.ReadInt32(); break;
                    case 3: m.Dir = input.ReadInt32(); break;
                    case 4: m.State = input.ReadInt32(); break;
                    case 5: m.ClientTime = input.ReadInt64(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class EntityMove
    {
        public long PlayerId;
        public int X;
        public int Y;
        public int Dir;
        public int State;

        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, PlayerId);
            ProtoWire.WriteInt32(o, 2, X);
            ProtoWire.WriteInt32(o, 3, Y);
            ProtoWire.WriteInt32(o, 4, Dir);
            ProtoWire.WriteInt32(o, 5, State);
        });

        public static EntityMove ParseFrom(byte[] data)
        {
            var m = new EntityMove();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.PlayerId = input.ReadInt64(); break;
                    case 2: m.X = input.ReadInt32(); break;
                    case 3: m.Y = input.ReadInt32(); break;
                    case 4: m.Dir = input.ReadInt32(); break;
                    case 5: m.State = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class PlazaSyncNtf
    {
        public long ServerTime;
        public List<EntityMove> Moves = new List<EntityMove>();

        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt64(o, 1, ServerTime);
            for (int i = 0; i < Moves.Count; i++)
                ProtoWire.WriteMessage(o, 2, Moves[i].ToByteArray());
        }, 2048);

        public static PlazaSyncNtf ParseFrom(byte[] data)
        {
            var m = new PlazaSyncNtf();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.ServerTime = input.ReadInt64(); break;
                    case 2: m.Moves.Add(EntityMove.ParseFrom(input.ReadBytes().ToByteArray())); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }

    public sealed class PlazaCorrectNtf
    {
        public int X;
        public int Y;
        public int Reason;

        public byte[] ToByteArray() => ProtoWire.Encode(o =>
        {
            ProtoWire.WriteInt32(o, 1, X);
            ProtoWire.WriteInt32(o, 2, Y);
            ProtoWire.WriteInt32(o, 3, Reason);
        });

        public static PlazaCorrectNtf ParseFrom(byte[] data)
        {
            var m = new PlazaCorrectNtf();
            var input = new CodedInputStream(data);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (WireFormat.GetTagFieldNumber(tag))
                {
                    case 1: m.X = input.ReadInt32(); break;
                    case 2: m.Y = input.ReadInt32(); break;
                    case 3: m.Reason = input.ReadInt32(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return m;
        }
    }
}
