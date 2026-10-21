using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CloudIsle.Config;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using UnityEngine;

namespace Game.Framework.Config
{
    /// <summary>
    /// AOT 配置入口。通过 ResMgr 租约读取与服务端同一份导出产物。
    /// 自 Game.Hotfix 迁出；T42 于 2026-10-20 xLua StageA 正式验收。
    /// </summary>
    public sealed class ConfigService
    {
        /// <summary>租约持有者名称。加载结束后 CaptureLeaks 里不应再有这个名字。</summary>
        public const string LeaseOwner = "ConfigService";

        readonly ResMgr _resources;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        ConfigSnapshot _snapshot;

        /// <summary>
        /// 测试钩子：每张表加载前额外等待的毫秒数。生产保持 0。
        /// 用于在 EditorSimulate 下稳定插入“等待中取消”；真实慢 HTTP 路径另见 StageB PatchHttp ResponseDelay。
        /// </summary>
        public static int TestInterTableDelayMs;

        /// <summary>绑定已经能加载 DefaultPackage 的资源管理器。</summary>
        /// <param name="resources">生产资源入口。不能为 null。</param>
        public ConfigService(ResMgr resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));
            _resources = resources;
        }

        /// <summary>
        /// 按地址加载清单和全部登记表。全部校验通过后才替换快照。
        /// 并发调用在锁内串行。取消或失败会释放本次租约，并保留上一份有效快照。
        /// </summary>
        /// <param name="configAddress">清单 TextAsset 的 YooAsset 地址。表地址由 ConfigAddresses.Table 推出。</param>
        /// <param name="expectedVersion">要求的配置版本。</param>
        /// <param name="ct">取消本次等待。取消时不替换快照。</param>
        /// <returns>加载完成的任务。</returns>
        public async UniTask LoadAsync(string configAddress, int expectedVersion, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(configAddress))
                throw new ArgumentException("清单地址不能为空白", nameof(configAddress));

            await _gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                ConfigSnapshot next = await ReadAsync(configAddress, expectedVersion, ct);
                ct.ThrowIfCancellationRequested();
                _snapshot = next;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>读取当前快照里的 Global 字符串。</summary>
        /// <param name="key">Global 键。未知键抛出 KeyNotFoundException，不返回空字符串。</param>
        /// <returns>该键对应的规范化字符串。</returns>
        public string GetGlobal(string key)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("配置键不能为空", nameof(key));

            ConfigSnapshot snapshot = _snapshot;
            if (snapshot == null)
                throw new InvalidOperationException("配置尚未加载");
            if (!snapshot.Globals.TryGetValue(key, out string value))
                throw new KeyNotFoundException("未知配置键: " + key);
            return value;
        }

        async UniTask<ConfigSnapshot> ReadAsync(string configAddress, int expectedVersion, CancellationToken ct)
        {
            ResourceLease<TextAsset> manifestLease = null;
            var tableLeases = new List<ResourceLease<TextAsset>>();
            try
            {
                manifestLease = await _resources.LoadAsync<TextAsset>(configAddress, LeaseOwner, ct);
                byte[] manifestBytes = CopyBytes(manifestLease.Asset.bytes);
                List<string> names = ListFileNames(manifestBytes);
                var payloads = new Dictionary<string, byte[]>(names.Count, StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TestInterTableDelayMs > 0)
                        await UniTask.Delay(TestInterTableDelayMs, cancellationToken: ct);
                    string address = ConfigAddresses.Table(configAddress, names[i]);
                    ResourceLease<TextAsset> lease = await _resources.LoadAsync<TextAsset>(address, LeaseOwner, ct);
                    tableLeases.Add(lease);
                    payloads.Add(names[i], CopyBytes(lease.Asset.bytes));
                }

                return ConfigSnapshotReader.Read(
                    manifestBytes,
                    name =>
                    {
                        byte[] bytes;
                        if (!payloads.TryGetValue(name, out bytes))
                            throw new ConfigLoadException("缺少配置文件: " + name);
                        return bytes;
                    },
                    expectedVersion);
            }
            finally
            {
                if (manifestLease != null)
                    manifestLease.Dispose();
                for (int i = 0; i < tableLeases.Count; i++)
                    tableLeases[i].Dispose();
            }
        }

        static List<string> ListFileNames(byte[] manifestBytes)
        {
            SimpleJson.Obj manifest;
            try
            {
                manifest = SimpleJson.ParseObject(Encoding.UTF8.GetString(manifestBytes));
            }
            catch (FormatException ex)
            {
                throw new ConfigLoadException("配置清单不是合法 JSON: " + ex.Message);
            }

            List<SimpleJson.Obj> files = manifest.GetObjectArray("files");
            var names = new List<string>(files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                string name = files[i].RequireString("name");
                if (!ConfigSnapshotReader.IsSafeFileName(name))
                    throw new ConfigLoadException("非法配置文件名: " + name);
                if (names.Contains(name))
                    throw new ConfigLoadException("配置文件名重复: " + name);
                names.Add(name);
            }

            return names;
        }

        /// <summary>复制 TextAsset 字节，避免释放租约后缓冲失效。</summary>
        /// <param name="source">资源上的字节。为空表示资源没有字节。</param>
        /// <returns>独立副本。来源为空时返回 null，交给后面的校验报缺文件。</returns>
        static byte[] CopyBytes(byte[] source)
        {
            if (source == null)
                return null;
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }
}
