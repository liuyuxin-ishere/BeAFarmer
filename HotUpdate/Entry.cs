using System;
using System.Collections.Generic;
using CloudIsle.Proto;
using Google.Protobuf;
using Google.Protobuf.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Game.HotUpdate
{
    /// <summary>
    /// HybridCLR 热更入口；PoC #4 在此验证 Protobuf 序列化 / 反序列化。
    /// </summary>
    public static class Entry
    {
        public const string HotUpdateVersion = "v1";

        /// <summary>
        /// 由 AOT HotfixLoader 反射调用；返回值会写到 UI Text。
        /// </summary>
        public static string Start(Text statusText)
        {
            string hotfixMsg = $"[Hotfix] Entry.Start OK — patch {HotUpdateVersion}";
            Debug.Log(hotfixMsg);

            string protoMsg = RunProtobufPoC();
            string combined = hotfixMsg + " | " + protoMsg;

            if (statusText != null)
                statusText.text = combined;

            return combined;
        }

        public static string RunProtobufPoC()
        {
            try
            {
                var req = new LoginReq
                {
                    Username = "farmer_poc",
                    Password = "secret",
                    AppVersion = "0.1.0",
                    ResVersion = 2,
                    ProtoVersion = 1,
                    ConfigVersion = 1,
                    DeviceId = SystemInfo.deviceUniqueIdentifier
                };

                byte[] reqBytes = req.ToByteArray();
                var req2 = LoginReq.Parser.ParseFrom(reqBytes);
                if (req2.Username != req.Username || req2.ResVersion != req.ResVersion)
                    throw new Exception("LoginReq roundtrip mismatch");

                var resp = new LoginResp
                {
                    Code = 0,
                    Token = "poc-token-001",
                    ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    NeedResVersion = 2,
                    NeedConfigVersion = 1,
                    Data = new PlayerSnapshot
                    {
                        PlayerId = 10001,
                        Nickname = "云岛试玩",
                        Level = 3,
                        Gold = 1280
                    }
                };

                // repeated + map：HybridCLR / IL2CPP 常见 AOT 泛型压力点
                resp.Data.Bag.Add(new ItemStack { ItemId = 1001, Count = 5 });
                resp.Data.Bag.Add(new ItemStack { ItemId = 2003, Count = 12 });
                resp.Data.Flags["tutorial_done"] = 1;
                resp.Data.Flags["daily_claim"] = 0;

                byte[] respBytes = resp.ToByteArray();
                var resp2 = LoginResp.Parser.ParseFrom(respBytes);

                if (resp2.Code != 0 || resp2.Token != resp.Token)
                    throw new Exception("LoginResp scalar mismatch");
                if (resp2.Data == null || resp2.Data.Bag.Count != 2)
                    throw new Exception($"Bag roundtrip failed count={resp2.Data?.Bag.Count}");
                if (resp2.Data.Bag[0].ItemId != 1001 || resp2.Data.Bag[1].Count != 12)
                    throw new Exception("Bag item mismatch");
                if (!resp2.Data.Flags.TryGetValue("tutorial_done", out var flag) || flag != 1)
                    throw new Exception("MapField roundtrip failed");

                TouchAotGenerics(resp2.Data.Bag, resp2.Data.Flags);

                string msg =
                    $"[Protobuf PoC] PASS req={reqBytes.Length}B resp={respBytes.Length}B " +
                    $"player={resp2.Data.Nickname} bag={resp2.Data.Bag.Count} flags={resp2.Data.Flags.Count}";
                Debug.Log(msg);
                return msg;
            }
            catch (Exception ex)
            {
                string err = $"[Protobuf PoC] FAIL: {ex.GetType().Name}: {ex.Message}";
                Debug.LogError(err);
                Debug.LogException(ex);
                return err;
            }
        }

        /// <summary>
        /// 显式触碰 repeated / map 常用泛型方法，便于 HybridCLR 扫描 AOT 补充元数据。
        /// </summary>
        static void TouchAotGenerics(RepeatedField<ItemStack> bag, MapField<string, int> flags)
        {
            var list = new List<ItemStack>(bag.Count);
            foreach (var item in bag)
                list.Add(item);

            var cloneBag = new RepeatedField<ItemStack>();
            cloneBag.AddRange(list);

            var cloneFlags = new MapField<string, int>();
            foreach (var kv in flags)
                cloneFlags[kv.Key] = kv.Value;

            if (cloneBag.Count != bag.Count || cloneFlags.Count != flags.Count)
                throw new Exception("TouchAotGenerics clone mismatch");
        }
    }
}
