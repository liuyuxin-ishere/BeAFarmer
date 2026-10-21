using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using YooAsset;

namespace Game.Framework.Boot
{
    /// <summary>
    /// AOT 差异补丁：按 VersionManifest 初始化远端包、加载清单、仅下载差异资源（YooAsset 3.0.6）。
    /// PrepareAsync 不提交活动索引；CommitVersion 仅在配置/模块/桥接/入口就绪后由 Launcher 调用。
    /// 可配置多包名共享一次候选事务，要求各包远端 PackageVersion 与清单一致。
    /// 不宣称字节级断点续传；取消调用 CancelDownload 并等清理；失败保留旧索引与已校验缓存。
    /// </summary>
    public sealed class PatchService
    {
        /// <summary>活动版本索引文件名（相对 IndexRoot）。</summary>
        public const string ActiveIndexFileName = "active-version.json";

        /// <summary>候选准备成功但尚未 Commit 时的状态（持有 Manifest 快照，不共享外部可变引用）。</summary>
        public sealed class CandidateState
        {
            /// <summary>本次准备使用的清单快照。</summary>
            public VersionManifest Manifest;

            /// <summary>各包 Downloader.TotalDownloadCount 合计（真实差异文件数）。</summary>
            public int TotalDownloadFiles;

            /// <summary>各包 Downloader.TotalDownloadBytes 合计。</summary>
            public long TotalDownloadBytes;

            /// <summary>参与事务且已准备完成的包名快照。</summary>
            public string[] PackageNames;

            /// <summary>与清单对齐的 Yoo 包版本。</summary>
            public string PackageVersion;
        }

        readonly string _indexRoot;
        readonly string _cacheRoot;
        readonly string[] _packageNames;
        readonly int _downloadConcurrency;
        readonly int _downloadRetry;
        CandidateState _candidate;
        bool _prepared;
        int _prepareGate;

        /// <summary>
        /// 创建补丁服务。
        /// </summary>
        /// <param name="indexRoot">活动索引目录。失败/取消不删已有好索引；无索引=首次未就绪。</param>
        /// <param name="cacheRoot">Yoo 沙箱缓存根。隔离验收应指向 StageB 专用目录。</param>
        /// <param name="packageNames">同一候选事务的包名。null 时默认 DefaultPackage+GameLua。</param>
        /// <param name="downloadConcurrency">下载最大并发。</param>
        /// <param name="downloadRetry">失败重试次数（插件选项）。</param>
        public PatchService(
            string indexRoot,
            string cacheRoot,
            string[] packageNames = null,
            int downloadConcurrency = 4,
            int downloadRetry = 2)
        {
            if (string.IsNullOrWhiteSpace(indexRoot))
                throw new ArgumentException("indexRoot 不能为空", nameof(indexRoot));
            if (string.IsNullOrWhiteSpace(cacheRoot))
                throw new ArgumentException("cacheRoot 不能为空", nameof(cacheRoot));
            _indexRoot = indexRoot;
            _cacheRoot = cacheRoot;
            _packageNames = packageNames != null && packageNames.Length > 0
                ? (string[])packageNames.Clone()
                : new[] { ResMgr.PackageName, HotfixLoader.LuaPackageName };
            if (downloadConcurrency <= 0)
                throw new ArgumentOutOfRangeException(nameof(downloadConcurrency));
            if (downloadRetry < 0)
                throw new ArgumentOutOfRangeException(nameof(downloadRetry));
            _downloadConcurrency = downloadConcurrency;
            _downloadRetry = downloadRetry;
        }

        /// <summary>最近一次 Prepare 成功的候选；未准备或失败后为 null。</summary>
        public CandidateState Candidate
        {
            get
            {
                if (_candidate == null)
                    return null;
                // 对外仅提供快照，调用方改字段/数组也不能修改待提交的内部候选。
                return new CandidateState
                {
                    Manifest = CloneManifest(_candidate.Manifest),
                    TotalDownloadFiles = _candidate.TotalDownloadFiles,
                    TotalDownloadBytes = _candidate.TotalDownloadBytes,
                    PackageNames = (string[])_candidate.PackageNames.Clone(),
                    PackageVersion = _candidate.PackageVersion
                };
            }
        }

        /// <summary>磁盘是否已有活动索引。</summary>
        public bool HasActiveIndex => File.Exists(ActiveIndexPath);

        /// <summary>活动索引绝对路径。</summary>
        public string ActiveIndexPath => Path.Combine(_indexRoot, ActiveIndexFileName);

        /// <summary>Prepare 是否占用中（防并发）。</summary>
        public bool IsPrepareInFlight => _prepareGate != 0;

        /// <summary>
        /// 初始化 Host 包 → 请求版本 → 加载清单 → CreateResourceDownloader 仅拉差异。
        /// TotalDownloadCount==0 时跳过下载（零文件更新）。不写活动索引。
        /// </summary>
        /// <param name="manifest">已校验清单；BaseUrl/PackageVersion 必填。</param>
        /// <param name="progress">进度；字节/文件数来自 Downloader 真实字段。</param>
        /// <param name="ct">取消后 CancelDownload 并清理本轮；缓存保留。</param>
        public async UniTask PrepareAsync(
            VersionManifest manifest,
            IProgress<PatchProgress> progress,
            CancellationToken ct)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrWhiteSpace(manifest.BaseUrl))
                throw new ArgumentException("BaseUrl 不能为空", nameof(manifest));
            if (string.IsNullOrWhiteSpace(manifest.PackageVersion))
                throw new ArgumentException("PackageVersion 不能为空", nameof(manifest));

            if (Interlocked.CompareExchange(ref _prepareGate, 1, 0) != 0)
                throw new InvalidOperationException("Prepare 正在进行，拒绝并发");

            try
            {
                ct.ThrowIfCancellationRequested();
                _prepared = false;
                _candidate = null;

                Directory.CreateDirectory(_indexRoot);
                Directory.CreateDirectory(_cacheRoot);
                if (!YooAssets.IsInitialized)
                    YooAssets.Initialize();

                Report(progress, "Initialize", 0, 0, 0, 0);

                int sessionFiles = 0;
                long sessionBytes = 0;
                long downloadedBytes = 0;
                int completedFiles = 0;
                var preparedPackages = new List<string>();
                VersionManifest snapshot = CloneManifest(manifest);

                try
                {
                    for (int i = 0; i < _packageNames.Length; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        string packageName = _packageNames[i];
                        ResourcePackage package = EnsurePackage(packageName);

                        Report(progress, "Initialize:" + packageName,
                            downloadedBytes, sessionBytes, completedFiles, sessionFiles);
                        await InitializeHostAsync(package, snapshot.BaseUrl, ct);

                        Report(progress, "RequestVersion:" + packageName,
                            downloadedBytes, sessionBytes, completedFiles, sessionFiles);
                        RequestPackageVersionOperation versionOp =
                            package.RequestPackageVersionAsync(new RequestPackageVersionOptions(false, 30));
                        await versionOp.ToUniTask(cancellationToken: ct);
                        if (versionOp.Status != EOperationStatus.Succeeded)
                        {
                            throw new InvalidOperationException(
                                "RequestPackageVersion 失败 [" + packageName + "]: " + versionOp.Error);
                        }

                        if (!string.Equals(versionOp.PackageVersion, snapshot.PackageVersion, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "包版本不一致 package=" + packageName
                                + " remote=" + versionOp.PackageVersion
                                + " manifest=" + snapshot.PackageVersion);
                        }

                        Report(progress, "LoadManifest:" + packageName,
                            downloadedBytes, sessionBytes, completedFiles, sessionFiles);
                        LoadPackageManifestOperation manifestOp = package.LoadPackageManifestAsync(
                            new LoadPackageManifestOptions(versionOp.PackageVersion, 30));
                        await manifestOp.ToUniTask(cancellationToken: ct);
                        if (manifestOp.Status != EOperationStatus.Succeeded)
                        {
                            throw new InvalidOperationException(
                                "LoadPackageManifest 失败 [" + packageName + "]: " + manifestOp.Error);
                        }

                        ResourceDownloaderOperation downloader = package.CreateResourceDownloader(
                            new ResourceDownloaderOptions(_downloadConcurrency, _downloadRetry));
                        int fileCount = downloader.TotalDownloadCount;
                        long byteCount = downloader.TotalDownloadBytes;
                        sessionFiles += fileCount;
                        sessionBytes += byteCount;

                        if (fileCount > 0)
                        {
                            downloader.StartDownload();
                            await WaitDownloadOrCancelAsync(
                                downloader,
                                progress,
                                packageName,
                                downloadedBytes,
                                sessionBytes,
                                completedFiles,
                                sessionFiles,
                                ct);

                            if (downloader.Status != EOperationStatus.Succeeded)
                            {
                                throw new InvalidOperationException(
                                    "下载失败 [" + packageName + "]: " + downloader.Error);
                            }

                            downloadedBytes += downloader.TotalDownloadBytes;
                            completedFiles += downloader.TotalDownloadCount;
                        }

                        preparedPackages.Add(packageName);
                        Report(progress, "PackageReady:" + packageName,
                            downloadedBytes, sessionBytes, completedFiles, sessionFiles);
                    }

                    ct.ThrowIfCancellationRequested();
                    _candidate = new CandidateState
                    {
                        Manifest = snapshot,
                        TotalDownloadFiles = sessionFiles,
                        TotalDownloadBytes = sessionBytes,
                        PackageNames = preparedPackages.ToArray(),
                        PackageVersion = snapshot.PackageVersion
                    };
                    _prepared = true;
                    Report(progress, "Ready", downloadedBytes, sessionBytes, completedFiles, sessionFiles);
                }
                catch (OperationCanceledException)
                {
                    Report(progress, "Cancelled", downloadedBytes, sessionBytes, completedFiles, sessionFiles);
                    _prepared = false;
                    _candidate = null;
                    throw;
                }
                catch (Exception)
                {
                    Report(progress, "Failed", downloadedBytes, sessionBytes, completedFiles, sessionFiles);
                    _prepared = false;
                    _candidate = null;
                    throw;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _prepareGate, 0);
            }
        }

        /// <summary>
        /// 原子提交活动版本索引。已有索引时同卷 File.Replace，禁止先删旧文件。
        /// 比对候选快照的 Res/Config/Proto/Package/BaseUrl，拒绝外部后改字段。
        /// </summary>
        /// <param name="manifest">须与 Candidate 快照全字段一致；正式链路应在入口 ready 后调用。</param>
        public void CommitVersion(VersionManifest manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (!_prepared || _candidate == null || _candidate.Manifest == null)
                throw new InvalidOperationException("没有可提交的候选；请先 PrepareAsync 成功");
            if (!ManifestEquals(_candidate.Manifest, manifest))
                throw new InvalidOperationException("提交清单与候选快照不一致");

            Directory.CreateDirectory(_indexRoot);
            string finalPath = ActiveIndexPath;
            string tempPath = finalPath + ".tmp";
            string backupPath = finalPath + ".bak";
            VersionManifest snap = _candidate.Manifest;
            var json = new StringBuilder(256);
            json.Append("{\"resVersion\":").Append(snap.ResVersion)
                .Append(",\"configVersion\":").Append(snap.ConfigVersion)
                .Append(",\"protoVersion\":").Append(snap.ProtoVersion)
                .Append(",\"packageVersion\":\"").Append(EscapeJson(snap.PackageVersion)).Append('"')
                .Append(",\"baseUrl\":\"").Append(EscapeJson(snap.BaseUrl)).Append('"')
                .Append(",\"packages\":[");
            for (int i = 0; i < _candidate.PackageNames.Length; i++)
            {
                if (i > 0)
                    json.Append(',');
                json.Append('"').Append(EscapeJson(_candidate.PackageNames[i])).Append('"');
            }

            json.Append("]}\n");
            File.WriteAllText(tempPath, json.ToString(), new UTF8Encoding(false));
            try
            {
                if (File.Exists(finalPath))
                {
                    File.Replace(tempPath, finalPath, backupPath);
                    try
                    {
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);
                    }
                    catch (IOException)
                    {
                    }
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (IOException)
                {
                }

                throw;
            }
        }

        /// <summary>读取活动索引；不存在返回 null。</summary>
        public string TryReadActiveIndex()
        {
            if (!File.Exists(ActiveIndexPath))
                return null;
            return File.ReadAllText(ActiveIndexPath, Encoding.UTF8);
        }

        /// <summary>丢弃内存候选；不删磁盘索引与沙箱缓存。</summary>
        public void DiscardCandidate()
        {
            _prepared = false;
            _candidate = null;
        }

        /// <summary>
        /// 将活动索引恢复为快照字节。snapshot 为 null/空表示应回到「无索引」：若文件存在则删除。
        /// 用于 Commit 之后业务失败的磁盘回滚；直接写文件字节，不依赖布尔字段。
        /// </summary>
        public void RestoreActiveIndexSnapshot(byte[] snapshot)
        {
            Directory.CreateDirectory(_indexRoot);
            string finalPath = ActiveIndexPath;
            if (snapshot == null || snapshot.Length == 0)
            {
                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                return;
            }

            string tempPath = finalPath + ".restore.tmp";
            string backupPath = finalPath + ".restore.bak";
            File.WriteAllBytes(tempPath, snapshot);
            try
            {
                if (File.Exists(finalPath))
                {
                    File.Replace(tempPath, finalPath, backupPath);
                    try
                    {
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);
                    }
                    catch (IOException)
                    {
                    }
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (IOException)
                {
                }

                throw;
            }
        }

        /// <summary>读取活动索引原始字节；不存在返回 null。</summary>
        public byte[] TryReadActiveIndexBytes()
        {
            if (!File.Exists(ActiveIndexPath))
                return null;
            return File.ReadAllBytes(ActiveIndexPath);
        }

        static ResourcePackage EnsurePackage(string packageName)
        {
            ResourcePackage package;
            if (!YooAssets.TryGetPackage(packageName, out package) || package == null)
                package = YooAssets.CreatePackage(packageName);
            return package;
        }

        /// <summary>
        /// HostPlayMode 初始化。已成功时校验所有权指纹后复用；冲突明确拒绝。
        /// </summary>
        async UniTask InitializeHostAsync(ResourcePackage package, string baseUrl, CancellationToken ct)
        {
            string root = baseUrl.TrimEnd('/');
            string sandbox = Path.Combine(_cacheRoot, package.PackageName);
            const string mode = "HostHttp";

            if (package.InitializeStatus == EOperationStatus.Succeeded)
            {
                YooPackageOwnership.ClaimOrValidate(
                    package.PackageName, mode, root, sandbox, "PatchService");
                return;
            }

            YooPackageOwnership.ClaimOrValidate(
                package.PackageName, mode, root, sandbox, "PatchService");

            Directory.CreateDirectory(sandbox);
            var options = new HostPlayModeOptions();
            options.BuiltinFileSystemParameters = null;
            options.CacheFileSystemParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(
                new PatchRemoteService(root),
                sandbox);
            InitializePackageOperation init = package.InitializePackageAsync(options);
            await init.ToUniTask(cancellationToken: ct);
            if (init.Status != EOperationStatus.Succeeded)
            {
                YooPackageOwnership.Clear(package.PackageName);
                throw new InvalidOperationException("InitializePackage 失败: " + init.Error);
            }
        }

        /// <summary>下载循环：取消时调用 CancelDownload 并等 IsDone，再抛 OCE。</summary>
        static async UniTask WaitDownloadOrCancelAsync(
            ResourceDownloaderOperation downloader,
            IProgress<PatchProgress> progress,
            string packageName,
            long downloadedBytes,
            long sessionBytes,
            int completedFiles,
            int sessionFiles,
            CancellationToken ct)
        {
            try
            {
                while (!downloader.IsDone)
                {
                    ct.ThrowIfCancellationRequested();
                    Report(progress, "Download:" + packageName,
                        downloadedBytes + downloader.CurrentDownloadBytes,
                        sessionBytes,
                        completedFiles + downloader.CurrentDownloadCount,
                        sessionFiles);
                    await UniTask.Yield(PlayerLoopTiming.Update, ct);
                }
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                // 取消既可能在检查处发生，也可能由 Yield(ct) 或进度回调抛出。
                // 在退出等待、释放 Prepare 门闸前，必须先结束真正的下载操作。
                downloader.CancelDownload();
                while (!downloader.IsDone)
                    await UniTask.Yield(PlayerLoopTiming.Update);
                throw;
            }
        }

        static void Report(
            IProgress<PatchProgress> progress,
            string stage,
            long downloaded,
            long totalBytes,
            int completed,
            int totalFiles)
        {
            if (progress == null)
                return;
            progress.Report(new PatchProgress
            {
                Stage = stage,
                DownloadedBytes = downloaded,
                TotalBytes = totalBytes,
                CompletedFiles = completed,
                TotalFiles = totalFiles
            });
        }

        /// <summary>深拷贝清单字段，避免外部后改绕过提交比对。</summary>
        public static VersionManifest CloneManifest(VersionManifest source)
        {
            if (source == null)
                return null;
            return new VersionManifest
            {
                MinAppVersion = source.MinAppVersion,
                ResVersion = source.ResVersion,
                ProtoVersion = source.ProtoVersion,
                ConfigVersion = source.ConfigVersion,
                PackageVersion = source.PackageVersion,
                BaseUrl = source.BaseUrl
            };
        }

        /// <summary>候选提交比对：Res/Config/Proto/Package/BaseUrl。</summary>
        public static bool ManifestEquals(VersionManifest a, VersionManifest b)
        {
            if (a == null || b == null)
                return false;
            return a.ResVersion == b.ResVersion
                   && a.ConfigVersion == b.ConfigVersion
                   && a.ProtoVersion == b.ProtoVersion
                   && string.Equals(a.PackageVersion, b.PackageVersion, StringComparison.Ordinal)
                   && string.Equals(
                       (a.BaseUrl ?? "").TrimEnd('/'),
                       (b.BaseUrl ?? "").TrimEnd('/'),
                       StringComparison.Ordinal);
        }

        static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>BaseUrl + 文件名 → 远端 URL 列表。</summary>
        sealed class PatchRemoteService : IRemoteService
        {
            readonly string _root;

            /// <param name="root">不带尾斜杠的 HTTP 根。</param>
            public PatchRemoteService(string root)
            {
                _root = root;
            }

            /// <param name="fileName">Yoo 请求文件名。</param>
            public IReadOnlyList<string> GetRemoteUrls(string fileName)
            {
                return new[] { _root + "/" + fileName };
            }
        }
    }
}
