using System;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace Game.Framework.Boot
{
    /// <summary>
    /// AOT 版本清单拉取与最低包版本判断。不耦合 UI / Launcher。
    /// </summary>
    public sealed class VersionService
    {
        /// <summary>宿主要求的协议版本。与 BridgeApi / VersionManifestParser 一致。</summary>
        public const int HostProtoVersion = VersionManifestParser.ExpectedProtoVersion;

        /// <summary>默认请求超时（秒）。已连接却无响应时结束，避免 Launcher 永久停在版本检查。</summary>
        public const int DefaultTimeoutSeconds = 15;

        /// <summary>
        /// UnityWebRequest.timeout（秒）。可在测试中设为较短值；必须 &gt; 0。
        /// </summary>
        public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

        /// <summary>
        /// 从 HTTP(S) 地址拉取并校验 version.json。
        /// 非 200、超限、坏 JSON、缺字段、非法版本或 Proto 不兼容均抛 VersionManifestException / 取消异常。
        /// 超时与主动取消均结束本次请求；下一次 FetchAsync 可成功。
        /// </summary>
        /// <param name="versionUri">清单绝对地址，例如 http://127.0.0.1:18080/version.json。</param>
        /// <param name="ct">取消本次下载与等待。</param>
        /// <returns>已验证的清单，可供 T45 PatchService 使用。</returns>
        public async UniTask<VersionManifest> FetchAsync(Uri versionUri, CancellationToken ct)
        {
            if (versionUri == null)
                throw new ArgumentNullException(nameof(versionUri));
            if (!versionUri.IsAbsoluteUri)
                throw new VersionManifestException("版本地址必须是绝对 URI");
            if (versionUri.Scheme != Uri.UriSchemeHttp && versionUri.Scheme != Uri.UriSchemeHttps)
                throw new VersionManifestException("版本地址只允许 http 或 https");
            if (TimeoutSeconds <= 0)
                throw new InvalidOperationException("TimeoutSeconds 必须大于 0");

            ct.ThrowIfCancellationRequested();

            // UnityWebRequest.Get 已自带 DownloadHandlerBuffer，不要再 new 覆盖。
            using (UnityWebRequest request = UnityWebRequest.Get(versionUri))
            {
                request.timeout = TimeoutSeconds;
                UnityWebRequestAsyncOperation op = request.SendWebRequest();
                try
                {
                    // UniTask 在非 Success 时抛 UnityWebRequestException；统一转成 VersionManifestException。
                    await op.ToUniTask(cancellationToken: ct);
                }
                catch (OperationCanceledException)
                {
                    request.Abort();
                    throw;
                }
                catch (UnityWebRequestException ex)
                {
                    request.Abort();
                    // Unity 超时也会走非 Success；映射为契约异常，便于 Launcher 展示。
                    if (request.result == UnityWebRequest.Result.ConnectionError &&
                        !string.IsNullOrEmpty(request.error) &&
                        request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw new VersionManifestException(
                            "拉取版本清单超时 (" + TimeoutSeconds + "s): " + ex.Message);
                    }

                    long code = request.responseCode;
                    throw new VersionManifestException(
                        "拉取版本清单失败 HTTP " + code + ": " + ex.Message);
                }

                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                {
                    throw new VersionManifestException(
                        "拉取版本清单非 200: " + request.responseCode + " " + request.error);
                }

                byte[] bytes = request.downloadHandler != null ? request.downloadHandler.data : null;
                if (bytes == null)
                    throw new VersionManifestException("版本清单响应体为空");
                if (bytes.Length > VersionManifestParser.MaxBytes)
                {
                    throw new VersionManifestException(
                        "版本清单超过大小限制 " + VersionManifestParser.MaxBytes);
                }

                return VersionManifestParser.Parse(bytes, HostProtoVersion);
            }
        }

        /// <summary>
        /// 安装包版本是否低于清单要求的最低版本。true 表示必须重装，不能靠热更。
        /// </summary>
        /// <param name="installedAppVersion">当前安装的 AppVersion，例如 0.2.0。</param>
        /// <param name="minAppVersion">清单 MinAppVersion。</param>
        /// <returns>installed &lt; min 时为 true。</returns>
        public bool RequiresAppUpdate(string installedAppVersion, string minAppVersion)
        {
            return AppVersionComparer.Compare(installedAppVersion, minAppVersion) < 0;
        }
    }
}
