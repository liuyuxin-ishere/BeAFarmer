using System;
using System.Collections.Generic;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

namespace Game.Framework.Resource
{
    /// <summary>正式资源初始化使用的两种模式。由启动设置赋值，不写死在 InitializeAsync 里。</summary>
    public enum ResourcePlayMode
    {
        /// <summary>编辑器里直接读模拟构建目录。</summary>
        EditorSimulate,

        /// <summary>通过 HTTP 访问 VersionManifest.BaseUrl。</summary>
        HostHttp
    }

    /// <summary>
    /// 按 PlayMode 初始化 DefaultPackage。
    /// </summary>
    public sealed class ResMgr
    {
        /// <summary>M1 使用的 YooAsset 包名。</summary>
        public const string PackageName = "DefaultPackage";

        /// <summary>调用 InitializeAsync 之前设置。决定本次使用编辑器模拟还是 HTTP。</summary>
        public ResourcePlayMode PlayMode { get; set; }

        /// <summary>最近一次成功初始化对应的 YooAsset 运行模式。</summary>
        public EPlayMode ActiveMode { get; private set; }

        /// <summary>编辑器模式是模拟构建目录，HTTP 模式是 BaseUrl。</summary>
        public string RootPath { get; private set; }

        readonly List<LiveLease> _live = new List<LiveLease>();

        /// <summary>
        /// 按 PlayMode 初始化 DefaultPackage。
        /// </summary>
        /// <param name="manifest">版本信息。HTTP 模式读取其中的 BaseUrl。</param>
        /// <param name="ct">取消等待。取消后不再把失败当成成功。</param>
        public async UniTask InitializeAsync(VersionManifest manifest, CancellationToken ct)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (PlayMode == ResourcePlayMode.HostHttp && string.IsNullOrWhiteSpace(manifest.BaseUrl))
                throw new ArgumentException("HTTP 模式需要 BaseUrl", nameof(manifest));

            if (!YooAssets.IsInitialized)
                YooAssets.Initialize();

            ResourcePackage package;
            if (!YooAssets.TryGetPackage(PackageName, out package) || package == null)
                package = YooAssets.CreatePackage(PackageName);

            string mode = PlayMode == ResourcePlayMode.EditorSimulate ? "EditorSimulate" : "HostHttp";
            // ResMgr Host 使用插件默认沙箱（空 CacheRoot）；与 PatchService 自定义根冲突时拒绝。
            string cacheRoot = "";

            if (package.InitializeStatus == EOperationStatus.Succeeded)
            {
                YooPackageOwnership.Record existing = YooPackageOwnership.TryGet(PackageName);
                if (existing == null)
                {
                    throw new InvalidOperationException(
                        "DefaultPackage 已初始化但无所有权登记，拒绝静默复用");
                }

                string expectRemote = PlayMode == ResourcePlayMode.HostHttp
                    ? (manifest.BaseUrl ?? "").TrimEnd('/')
                    : existing.RemoteBase;
                // PatchService 可能已用自定义沙箱初始化；校验须沿用其 CacheRoot，不能用空串冒充默认沙箱。
                string cacheForValidate = existing.CacheRoot ?? cacheRoot;
                YooPackageOwnership.ClaimOrValidate(
                    PackageName, mode, expectRemote, cacheForValidate, "ResMgr");
                ActiveMode = PlayMode == ResourcePlayMode.EditorSimulate
                    ? EPlayMode.EditorSimulateMode
                    : EPlayMode.HostPlayMode;
                RootPath = PlayMode == ResourcePlayMode.HostHttp ? expectRemote : existing.RemoteBase;
                return;
            }

            InitializePackageOptions options = CreateOptions(manifest);
            string remoteForClaim = PlayMode == ResourcePlayMode.HostHttp
                ? (manifest.BaseUrl ?? "").TrimEnd('/')
                : (RootPath ?? "");
            YooPackageOwnership.ClaimOrValidate(PackageName, mode, remoteForClaim, cacheRoot, "ResMgr");

            InitializePackageOperation operation = package.InitializePackageAsync(options);
            await operation.ToUniTask(cancellationToken: ct);
            if (operation.Status != EOperationStatus.Succeeded)
            {
                YooPackageOwnership.Clear(PackageName);
                throw new InvalidOperationException(operation.Error);
            }
        }

        /// <summary>
        /// 从已有活动清单的 DefaultPackage 加载资源，并返回一份独立租约。
        /// </summary>
        /// <param name="address">YooAsset 定位地址，例如 PatchCube。</param>
        /// <param name="owner">持有者名称。空白会拒绝。</param>
        /// <param name="ct">取消等待。取消或失败时释放本次创建的句柄。</param>
        public async UniTask<ResourceLease<T>> LoadAsync<T>(string address, string owner, CancellationToken ct)
            where T : UnityEngine.Object
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空白", nameof(address));
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("持有者不能为空白", nameof(owner));
            ct.ThrowIfCancellationRequested();

            ResourcePackage package;
            if (!YooAssets.IsInitialized || !YooAssets.TryGetPackage(PackageName, out package) || package == null)
                throw new InvalidOperationException("先调用 InitializeAsync");

            AssetHandle handle = package.LoadAssetAsync<T>(address);
            bool transferred = false;
            try
            {
                await handle.ToUniTask(cancellationToken: ct);
                if (handle.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException(string.IsNullOrEmpty(handle.Error) ? "加载失败" : handle.Error);

                T asset = handle.GetAssetObject<T>();
                if (asset == null)
                    throw new InvalidOperationException("加载结果为空");

                var record = new LiveLease(address, owner, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _live.Add(record);
                ResourceLease<T> lease = new ResourceLease<T>(handle, asset, address, owner, () => _live.Remove(record));
                transferred = true;
                return lease;
            }
            finally
            {
                if (!transferred)
                    handle.Release();
            }
        }

        /// <summary>
        /// 列出该持有者尚未释放的租约。不释放、不卸载。
        /// </summary>
        /// <param name="owner">持有者名称，与 LoadAsync 传入的 owner 全字匹配。</param>
        public IReadOnlyList<ResourceLeakInfo> CaptureLeaks(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("持有者不能为空白", nameof(owner));

            var grouped = new List<ResourceLeakInfo>();
            var indexByAddress = new Dictionary<string, int>();
            for (int i = 0; i < _live.Count; i++)
            {
                LiveLease record = _live[i];
                if (!string.Equals(record.Owner, owner, StringComparison.Ordinal))
                    continue;

                int index;
                if (!indexByAddress.TryGetValue(record.Address, out index))
                {
                    indexByAddress.Add(record.Address, grouped.Count);
                    grouped.Add(new ResourceLeakInfo(record.Address, record.Owner, 1, record.CreatedAtMs));
                    continue;
                }

                ResourceLeakInfo existing = grouped[index];
                long createdAtMs = existing.CreatedAtMs <= record.CreatedAtMs
                    ? existing.CreatedAtMs
                    : record.CreatedAtMs;
                grouped[index] = new ResourceLeakInfo(existing.Address, existing.Owner, existing.RefCount + 1, createdAtMs);
            }

            return grouped;
        }

        /// <summary>
        /// 以附加方式加载资源包里的场景，并返回一份租约。
        /// 取消或失败时等待底层加载结束，再卸载已出现的场景并释放句柄。
        /// </summary>
        /// <param name="address">YooAsset 定位地址。Home 场景的地址是 Home。</param>
        /// <param name="ct">取消等待。取消后不把租约交给调用方。</param>
        public async UniTask<SceneLease> LoadSceneAsync(string address, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空白", nameof(address));

            ResourcePackage package;
            if (!YooAssets.IsInitialized || !YooAssets.TryGetPackage(PackageName, out package) || package == null)
                throw new InvalidOperationException("先调用 InitializeAsync");

            YooAsset.SceneHandle handle = package.LoadSceneAsync(address, LoadSceneMode.Additive);
            try
            {
                await handle.ToUniTask(cancellationToken: ct);
                if (handle.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException(string.IsNullOrEmpty(handle.Error) ? "场景加载失败" : handle.Error);

                Scene scene = handle.SceneObject;
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new InvalidOperationException("场景未加载");

                return new SceneLease(handle, address);
            }
            catch (Exception)
            {
                await ReleaseSceneHandleAsync(handle);
                throw;
            }
        }

        /// <summary>
        /// 卸载租约持有的场景并释放句柄。重复调用直接返回。
        /// </summary>
        /// <param name="lease">LoadSceneAsync 返回的租约。</param>
        /// <param name="ct">调用前已取消时直接抛出，不开始卸载。</param>
        public async UniTask UnloadSceneAsync(SceneLease lease, CancellationToken ct)
        {
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));
            if (!lease.IsLoaded)
                return;

            ct.ThrowIfCancellationRequested();
            await ReleaseSceneHandleAsync(lease.Handle);
            lease.MarkUnloaded();
        }

        static async UniTask ReleaseSceneHandleAsync(YooAsset.SceneHandle handle)
        {
            if (!handle.IsDone)
                await handle.ToUniTask();

            if (handle.Status == EOperationStatus.Succeeded
                && handle.SceneObject.IsValid()
                && handle.SceneObject.isLoaded)
            {
                UnloadSceneOperation unload = handle.UnloadSceneAsync();
                await unload.ToUniTask();
                if (unload.Status != EOperationStatus.Succeeded)
                    throw new InvalidOperationException(string.IsNullOrEmpty(unload.Error) ? "场景卸载失败" : unload.Error);
            }

            handle.Release();
        }

        InitializePackageOptions CreateOptions(VersionManifest manifest)
        {
            if (PlayMode == ResourcePlayMode.EditorSimulate)
                return CreateEditorOptions();
            if (PlayMode == ResourcePlayMode.HostHttp)
                return CreateHostOptions(manifest.BaseUrl);
            throw new InvalidOperationException("未设置 PlayMode");
        }

        InitializePackageOptions CreateEditorOptions()
        {
#if UNITY_EDITOR
            PackageBuildResult build = EditorSimulateBuildInvoker.Build(
                PackageName,
                (int)EBundleType.VirtualAssetBundle);
            RootPath = build.PackageRootDirectory;
            ActiveMode = EPlayMode.EditorSimulateMode;
            var options = new EditorSimulateModeOptions();
            options.EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(RootPath);
            return options;
#else
            throw new PlatformNotSupportedException("EditorSimulate 只在 Unity 编辑器中可用");
#endif
        }

        InitializePackageOptions CreateHostOptions(string baseUrl)
        {
            RootPath = baseUrl.TrimEnd('/');
            ActiveMode = EPlayMode.HostPlayMode;
            var options = new HostPlayModeOptions();
            options.BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters();
            options.CacheFileSystemParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
                new ManifestRemoteService(RootPath));
            return options;
        }
    }

    /// <summary>一份已成功加载、尚未释放的租约登记。</summary>
    sealed class LiveLease
    {
        /// <param name="address">资源地址。</param>
        /// <param name="owner">持有者名称。</param>
        /// <param name="createdAtMs">登记时间，UTC Unix 毫秒。</param>
        public LiveLease(string address, string owner, long createdAtMs)
        {
            Address = address;
            Owner = owner;
            CreatedAtMs = createdAtMs;
        }

        public string Address { get; }
        public string Owner { get; }
        public long CreatedAtMs { get; }
    }

    /// <summary>把 BaseUrl 和文件名拼成 YooAsset 要访问的 HTTP 地址。</summary>
    sealed class ManifestRemoteService : IRemoteService
    {
        readonly string _root;

        /// <param name="root">VersionManifest.BaseUrl，末尾不带斜杠。</param>
        public ManifestRemoteService(string root)
        {
            _root = root;
        }

        /// <param name="fileName">YooAsset 请求的文件名。</param>
        public IReadOnlyList<string> GetRemoteUrls(string fileName)
        {
            return new[] { _root + "/" + fileName };
        }
    }
}
