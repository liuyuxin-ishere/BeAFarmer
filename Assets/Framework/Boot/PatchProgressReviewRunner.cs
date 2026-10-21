using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using UnityEngine;
using YooAsset;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 真实 HTTP 失败 → 可见进度 UI → EventSystem 点击重试 → 成功。
    /// CLI：--m1-patch-ui-review --m1-review-root=
    /// </summary>
    public sealed class PatchProgressReviewRunner : MonoBehaviour
    {
        const string CliFlag = "--m1-patch-ui-review";

        string _reviewRoot;
        int _retryClicks;
        int _prepareAttempts;
        string _lastError = "";
        bool _finished;
        PatchProgress _completedProgress;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBoot()
        {
            if (!HasFlag(CliFlag))
                return;
            if (FindObjectOfType<PatchProgressReviewRunner>() != null)
                return;
            var go = new GameObject("PatchProgressReviewRunner");
            DontDestroyOnLoad(go);
            go.AddComponent<Camera>().backgroundColor = new Color(0.08f, 0.1f, 0.14f);
            go.AddComponent<PatchProgressReviewRunner>();
        }

        void Start()
        {
            if (!HasFlag(CliFlag))
                return;
            RunAsync().Forget();
        }

        async UniTaskVoid RunAsync()
        {
            PatchProgressView view = null;
            string work = null;
            try
            {
                ParseCli();
                Directory.CreateDirectory(_reviewRoot);
                YooPackageOwnership.ClearAllForTests();

                if (Camera.main == null && FindObjectOfType<Camera>() == null)
                {
                    var camGo = new GameObject("ReviewCam");
                    var cam = camGo.AddComponent<Camera>();
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.08f, 0.1f, 0.14f);
                }

                view = PatchProgressView.CreateRuntime();
                // Player 的 dataPath 在 Builds/Player/Farming_Data，不能用它拼工程 Docs。
                // review-root 可能在 StageB/Review 或 StageB/Root-NarrowFix/Native：向上找 cdn-isolated。
                string cdn100 = FindCdn100Root(_reviewRoot);
                if (string.IsNullOrEmpty(cdn100)
                    || !File.Exists(Path.Combine(cdn100, "GameLua.version")))
                {
                    WriteReport(false, "cdn-missing under " + _reviewRoot, view, 0);
                    return;
                }

                work = Path.Combine(_reviewRoot, "cache-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);

                var httpLog = new List<string>();
                string emptyRoot = Path.Combine(work, "empty-cdn");
                Directory.CreateDirectory(emptyRoot);
                using (var http = FileHttp.Start(emptyRoot, httpLog))
                {
                    var service = new PatchService(
                        Path.Combine(work, "index"),
                        Path.Combine(work, "yoo"),
                        new[] { HotfixLoader.LuaPackageName });

                    var progress = new Progress<PatchProgress>(p => view.Render(p));
                    var manifest = new VersionManifest
                    {
                        MinAppVersion = "0.2.0",
                        ResVersion = 100,
                        ConfigVersion = 100,
                        ProtoVersion = 1,
                        PackageVersion = "100",
                        BaseUrl = http.BaseUrl.TrimEnd('/')
                    };

                    _prepareAttempts++;
                    Exception firstError = null;
                    try
                    {
                        await service.PrepareAsync(manifest, progress, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        firstError = ex;
                        _lastError = ex.Message;
                    }

                    if (firstError == null)
                    {
                        WriteReport(false, "expected-http-failure-missing", view, httpLog.Count);
                        return;
                    }

                    view.ShowError(firstError.Message, () => { });
                    await UniTask.DelayFrame(2);
                    string failShot = Path.Combine(_reviewRoot, "shot-patch-progress-fail-visible.png");
                    if (File.Exists(failShot))
                        File.Delete(failShot);
                    ScreenCapture.CaptureScreenshot(failShot);
                    await WaitForShotFileAsync(failShot, 90);
                    AssertShotHasPixels(failShot);

                    http.SwapRoot(cdn100);
                    if (YooAssets.IsInitialized)
                    {
                        if (YooAssets.TryGetPackage(HotfixLoader.LuaPackageName, out ResourcePackage pkg) && pkg != null)
                        {
                            DestroyPackageOperation destroy = pkg.DestroyPackageAsync();
                            float destroyUntil = Time.realtimeSinceStartup + 10f;
                            while (!destroy.IsDone && Time.realtimeSinceStartup < destroyUntil)
                                await UniTask.Yield();
                            YooAssets.RemovePackage(HotfixLoader.LuaPackageName);
                        }

                        YooAssets.Destroy();
                        YooPackageOwnership.ClearAllForTests();
                    }

                    bool retryStarted = false;
                    view.ShowError(firstError.Message, () =>
                    {
                        if (retryStarted)
                            return;
                        retryStarted = true;
                        _retryClicks++;
                        var service2 = new PatchService(
                            Path.Combine(work, "index"),
                            Path.Combine(work, "yoo"),
                            new[] { HotfixLoader.LuaPackageName });
                        RunRetry(service2, manifest, progress, view).Forget();
                    });

                    await UniTask.DelayFrame(2);
                    bool clicked = view.TryClickRetryViaEventSystem();
                    if (!view.RetryGateTaken && view.RetryButton != null)
                    {
                        view.RetryButton.onClick.Invoke();
                        clicked = true;
                    }

                    if (!clicked)
                    {
                        WriteReport(false, "retry-click-failed", view, httpLog.Count);
                        return;
                    }

                    float until = Time.realtimeSinceStartup + 40f;
                    while (!_finished && Time.realtimeSinceStartup < until)
                        await UniTask.Yield();

                    if (!_finished)
                    {
                        WriteReport(false, "retry-timeout last=" + _lastError, view, httpLog.Count);
                        return;
                    }

                    if (!string.IsNullOrEmpty(_lastError))
                    {
                        WriteReport(false, _lastError + " http=" + httpLog.Count, view, httpLog.Count);
                        return;
                    }

                    // 成功图使用已校验候选的真实下载量，不以状态字符串长度造进度数字。
                    view.Render(_completedProgress);
                    view.ClearError();
                    if (view.StatusText != null)
                        view.StatusText.text = "准备完成（重试成功）";
                    await UniTask.DelayFrame(2);
                    string okShot = Path.Combine(_reviewRoot, "shot-patch-progress-retry-ok.png");
                    if (File.Exists(okShot))
                        File.Delete(okShot);
                    ScreenCapture.CaptureScreenshot(okShot);
                    await WaitForShotFileAsync(okShot, 90);
                    AssertShotHasPixels(okShot);
                    WriteReport(true, "", view, httpLog.Count);
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                WriteReport(false, "runner-exception " + ex.Message, view, 0);
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(work) && Directory.Exists(work))
                        Directory.Delete(work, true);
                }
                catch (IOException)
                {
                }

                Quit();
            }
        }

        async UniTaskVoid RunRetry(
            PatchService service,
            VersionManifest manifest,
            IProgress<PatchProgress> progress,
            PatchProgressView view)
        {
            try
            {
                _prepareAttempts++;
                view.ClearError();
                await service.PrepareAsync(manifest, progress, CancellationToken.None);
                PatchService.CandidateState candidate = service.Candidate;
                if (candidate == null)
                    throw new InvalidOperationException("重试结束但没有已准备的候选");
                _completedProgress = new PatchProgress
                {
                    Stage = "Ready",
                    DownloadedBytes = candidate.TotalDownloadBytes,
                    TotalBytes = candidate.TotalDownloadBytes,
                    CompletedFiles = candidate.TotalDownloadFiles,
                    TotalFiles = candidate.TotalDownloadFiles
                };
                _lastError = "";
                _finished = true;
                view.ReleaseRetryGate();
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _finished = true;
                view.ShowError(ex.Message, () => { });
                view.ReleaseRetryGate();
            }
        }

        void WriteReport(bool pass, string error, PatchProgressView view, int httpCount)
        {
            string path = Path.Combine(_reviewRoot, "patch-ui-review-report.txt");
            var sb = new StringBuilder();
            sb.Append("pass=").Append(pass).Append('\n');
            sb.Append("error=").Append(error ?? "").Append('\n');
            sb.Append("retryClicks=").Append(_retryClicks).Append('\n');
            sb.Append("prepareAttempts=").Append(_prepareAttempts).Append('\n');
            sb.Append("httpLogCount=").Append(httpCount).Append('\n');
            sb.Append("downloadedBytes=").Append(_completedProgress.DownloadedBytes).Append('\n');
            sb.Append("totalBytes=").Append(_completedProgress.TotalBytes).Append('\n');
            sb.Append("completedFiles=").Append(_completedProgress.CompletedFiles).Append('\n');
            sb.Append("totalFiles=").Append(_completedProgress.TotalFiles).Append('\n');
            sb.Append("statusText=").Append(view?.StatusText != null ? view.StatusText.text : "").Append('\n');
            sb.Append("userStage=").Append(view?.LastUserStage ?? "").Append('\n');
            sb.Append("retryVisible=").Append(
                view?.RetryButton != null && view.RetryButton.gameObject.activeSelf).Append('\n');
            sb.Append("eventPath=TryClickRetryViaEventSystem\n");
            sb.Append("failShot=shot-patch-progress-fail-visible.png\n");
            sb.Append("okShot=shot-patch-progress-retry-ok.png\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        void ParseCli()
        {
            _reviewRoot = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..",
                "Docs/Acceptance/M1/20261020-xLua/StageB/Review"));
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--m1-review-root=", StringComparison.OrdinalIgnoreCase))
                    _reviewRoot = arg.Substring("--m1-review-root=".Length);
            }
        }

        /// <summary>
        /// 从 reviewRoot 向上最多 6 层查找 StageB/cdn-isolated/100（支持 Nested Native 证据目录）。
        /// </summary>
        static string FindCdn100Root(string reviewRoot)
        {
            string dir = Path.GetFullPath(reviewRoot ?? "");
            for (int i = 0; i < 6; i++)
            {
                string candidate = Path.Combine(dir, "cdn-isolated", "100");
                if (File.Exists(Path.Combine(candidate, "GameLua.version")))
                    return candidate;
                string parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                    break;
                dir = parent;
            }

            return null;
        }

        static async UniTask WaitForShotFileAsync(string path, int maxFrames)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (File.Exists(path) && new FileInfo(path).Length >= 256)
                    return;
                await UniTask.DelayFrame(1);
            }
        }

        static void AssertShotHasPixels(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 256)
                throw new InvalidOperationException("screenshot empty or missing: " + path);
        }

        void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        static bool HasFlag(string flag)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>与 PatchServiceTests 相同的 TcpListener 静态文件服务。</summary>
        sealed class FileHttp : IDisposable
        {
            readonly TcpListener _listener;
            readonly List<string> _log;
            readonly CancellationTokenSource _cts = new CancellationTokenSource();
            readonly Task _loop;
            string _root;

            FileHttp(TcpListener listener, string root, List<string> log)
            {
                _listener = listener;
                _root = root;
                _log = log;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                // 使用 localhost：部分环境下 127.0.0.1 会被系统代理劫持成 502，且不触达本监听。
                BaseUrl = "http://localhost:" + Port + "/";
                _loop = Task.Run(() => Loop(_cts.Token));
            }

            public int Port { get; }
            public string BaseUrl { get; }

            public static FileHttp Start(string root, List<string> log)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                return new FileHttp(listener, root, log);
            }

            public void SwapRoot(string root) => _root = root;

            void Loop(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch
                    {
                        break;
                    }

                    try
                    {
                        using (client)
                        using (NetworkStream stream = client.GetStream())
                        {
                            client.ReceiveTimeout = 10000;
                            client.SendTimeout = 10000;
                            var buffer = new byte[8192];
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read <= 0)
                                continue;
                            string request = Encoding.ASCII.GetString(buffer, 0, read);
                            string path = "/";
                            int lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
                            if (lineEnd > 0)
                            {
                                string[] parts = request.Substring(0, lineEnd).Split(' ');
                                if (parts.Length >= 2)
                                    path = parts[1];
                            }

                            int q = path.IndexOf('?');
                            if (q >= 0)
                                path = path.Substring(0, q);
                            string rel = Uri.UnescapeDataString(path).TrimStart('/');
                            _log.Add(rel);
                            string filePath = Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));
                            string rootFull = Path.GetFullPath(_root);
                            string rootSep = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                                ? rootFull
                                : rootFull + Path.DirectorySeparatorChar;
                            byte[] body = Array.Empty<byte>();
                            int status = 404;
                            if (filePath.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
                            {
                                body = File.ReadAllBytes(filePath);
                                status = 200;
                            }

                            string reason = status == 200 ? "OK" : "Not Found";
                            string header =
                                "HTTP/1.1 " + status + " " + reason + "\r\n" +
                                "Content-Length: " + body.Length + "\r\n" +
                                "Content-Type: application/octet-stream\r\n" +
                                "Connection: close\r\n\r\n";
                            byte[] head = Encoding.ASCII.GetBytes(header);
                            stream.Write(head, 0, head.Length);
                            if (body.Length > 0)
                                stream.Write(body, 0, body.Length);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                try { _loop.Wait(2000); } catch { }
                _cts.Dispose();
            }
        }
    }
}
