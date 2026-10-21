using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CloudIsle.Server.Hosting
{
    /// <summary>
    /// 补丁静态 HTTP：提供 contentRoot 下的 version.json 与版本目录文件。
    /// 仅服务静态根内合法文件；路径穿越、目录、缺失一律非成功空体。
    /// 单请求任意异常必须关闭/Abort response，避免客户端悬挂。
    /// </summary>
    public sealed class PatchHttpService
    {
        /// <summary>M1 约定的默认 loopback HTTP 端口。</summary>
        public const int DefaultPort = 18080;

        /// <summary>
        /// 可选：每个成功定位到的文件在写出前额外等待。用于慢资源/中途取消回归；生产默认 0。
        /// </summary>
        public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// 在 listenUrl 上服务 contentRoot，直到 ct 取消。
        /// </summary>
        /// <param name="listenUrl">例如 http://127.0.0.1:18080/。必须是带尾斜杠的绝对 HTTP 前缀。</param>
        /// <param name="contentRoot">静态根目录。请求路径映射到此目录之下。</param>
        /// <param name="ct">取消后停止监听并结束任务。</param>
        public async Task RunAsync(string listenUrl, string contentRoot, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(listenUrl))
                throw new ArgumentException("listenUrl 不能为空", nameof(listenUrl));
            if (string.IsNullOrWhiteSpace(contentRoot))
                throw new ArgumentException("contentRoot 不能为空", nameof(contentRoot));

            string root = Path.GetFullPath(contentRoot);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("补丁内容根不存在: " + contentRoot);

            if (!listenUrl.EndsWith("/", StringComparison.Ordinal))
                listenUrl += "/";

            var listener = new HttpListener();
            listener.Prefixes.Add(listenUrl);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException(
                    "无法绑定补丁 HTTP 端口: " + listenUrl + " — " + ex.Message,
                    ex);
            }

            using (ct.Register(() =>
                   {
                       try
                       {
                           if (listener.IsListening)
                               listener.Stop();
                       }
                       catch (ObjectDisposedException)
                       {
                       }
                   }))
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        HttpListenerContext context;
                        try
                        {
                            context = await listener.GetContextAsync().ConfigureAwait(false);
                        }
                        catch (HttpListenerException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            await ServeAsync(context, root, ResponseDelay, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            // 单请求失败不影响监听循环；保证悬挂连接被 Abort。
                            TryAbort(context);
                        }
                    }
                }
                finally
                {
                    try
                    {
                        listener.Close();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// 解析请求路径并写响应。成功返回文件字节；目录/缺失/越界/非法路径返回明确 4xx。
        /// </summary>
        /// <param name="context">当前 HttpListener 上下文。</param>
        /// <param name="rootFull">已规范化的静态根绝对路径。</param>
        /// <param name="delay">写出前可选延迟；用于慢资源测试。</param>
        /// <param name="ct">监听级取消；延迟等待期间可提前结束。</param>
        static async Task ServeAsync(
            HttpListenerContext context,
            string rootFull,
            TimeSpan delay,
            CancellationToken ct)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            bool responseClosed = false;

            try
            {
                if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(response, 405).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }

                string relative;
                try
                {
                    relative = Uri.UnescapeDataString(request.Url!.AbsolutePath);
                }
                catch (UriFormatException)
                {
                    await WriteStatusAsync(response, 400).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }

                if (relative.IndexOf('\0') >= 0)
                {
                    await WriteStatusAsync(response, 400).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }

                if (relative.StartsWith("/", StringComparison.Ordinal))
                    relative = relative.Substring(1);

                // 拒绝显式穿越段与点段伪装。
                if (relative.IndexOf('\\') >= 0 || HasUnsafePathSegment(relative))
                {
                    await WriteStatusAsync(response, 404).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }

                string candidate;
                try
                {
                    candidate = Path.GetFullPath(
                        Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (ArgumentException)
                {
                    // %00 等非法路径段：明确 400，并保证响应关闭。
                    await WriteStatusAsync(response, 400).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }
                catch (NotSupportedException)
                {
                    await WriteStatusAsync(response, 400).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }

                string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
                    ? rootFull
                    : rootFull + Path.DirectorySeparatorChar;

                if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(response, 404).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }

                if (Directory.Exists(candidate) || !File.Exists(candidate))
                {
                    await WriteStatusAsync(response, 404).ConfigureAwait(false);
                    responseClosed = true;
                    return;
                }

                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct).ConfigureAwait(false);

                byte[] bytes = await File.ReadAllBytesAsync(candidate).ConfigureAwait(false);
                response.StatusCode = 200;
                response.ContentType = GuessContentType(candidate);
                response.ContentLength64 = bytes.Length;
                if (!string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                    await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                response.OutputStream.Close();
                responseClosed = true;
            }
            finally
            {
                if (!responseClosed)
                    TryAbort(context);
            }
        }

        /// <summary>写空体状态码并关闭输出流。</summary>
        /// <param name="response">当前响应。</param>
        /// <param name="status">HTTP 状态码。</param>
        static async Task WriteStatusAsync(HttpListenerResponse response, int status)
        {
            response.StatusCode = status;
            response.ContentLength64 = 0;
            response.OutputStream.Close();
            await Task.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>尽力 Abort 未完成响应，避免客户端永久等待。</summary>
        /// <param name="context">可能尚未写出完毕的请求上下文。</param>
        static void TryAbort(HttpListenerContext context)
        {
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (HttpListenerException)
            {
            }
        }

        /// <summary>按扩展名猜测 Content-Type。</summary>
        /// <param name="path">本地文件绝对路径。</param>
        static string GuessContentType(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                return "application/json; charset=utf-8";
            if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase))
                return "text/plain; charset=utf-8";
            return "application/octet-stream";
        }

        /// <summary>路径段是否含 "." / ".."。</summary>
        /// <param name="relative">已去掉前导斜杠的相对路径。</param>
        static bool HasUnsafePathSegment(string relative)
        {
            string[] segments = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i] == ".." || segments[i] == ".")
                    return true;
            }

            return false;
        }
    }
}
