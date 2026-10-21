using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CloudIsle.Diagnostics;

namespace Game.Framework.Core
{
    /// <summary>一条客户端日志的级别。</summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// 写入滚动文件的客户端日志。Dispose 时把缓冲刷到磁盘并关闭文件。
    /// </summary>
    public sealed class FileLogger : IDisposable
    {
        const int MaxPending = 256;
        static readonly Regex SecretPattern = new Regex(
            @"(?i)\b(password|token)\b(\s*[:=]\s*)\S+",
            RegexOptions.Compiled);

        readonly object _gate = new object();
        readonly Queue<string> _pending = new Queue<string>();
        string _directory;
        long _maxFileBytes;
        int _retainedFiles;
        FileStream _stream;
        long _currentBytes;
        bool _disposed;

        /// <summary>打开日志目录和当前的 client.log。</summary>
        /// <param name="directory">日志目录。不存在就创建。空字符串会抛异常。</param>
        /// <param name="maxFileBytes">单个文件达到该字节数就滚动。必须大于 0。</param>
        /// <param name="retainedFiles">目录中最多保留的日志文件数，含当前文件。必须大于或等于 1。</param>
        public void Initialize(string directory, long maxFileBytes, int retainedFiles)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("日志目录不能为空", nameof(directory));
            if (maxFileBytes < 1)
                throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
            if (retainedFiles < 1)
                throw new ArgumentOutOfRangeException(nameof(retainedFiles));

            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(FileLogger));
                if (_stream != null)
                    throw new InvalidOperationException("FileLogger 已经 Initialize");

                Directory.CreateDirectory(directory);
                _directory = directory;
                _maxFileBytes = maxFileBytes;
                _retainedFiles = retainedFiles;
                OpenCurrent();
            }
        }

        /// <summary>格式化、脱敏，并写入有界队列，再落入当前文件。</summary>
        /// <param name="level">Debug、Info、Warn 或 Error。</param>
        /// <param name="message">正文。password 和 token 的值会变成 ***。空引用按空字符串处理。</param>
        /// <param name="context">五个数字字段。</param>
        public void Write(LogLevel level, string message, LogContext context)
        {
            string line = Format(level, message, context);
            lock (_gate)
            {
                EnsureWritable();
                _pending.Enqueue(line);
                if (_pending.Count > MaxPending)
                    _pending.Dequeue();

                while (_pending.Count > 0)
                    WriteLine(_pending.Dequeue());
            }
        }

        /// <summary>把缓冲刷到磁盘并关闭文件。之后文件可以被其他读者打开。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                CloseStream();
            }
        }

        static string Format(LogLevel level, string message, LogContext context)
        {
            string body = SecretPattern.Replace(message ?? string.Empty, "$1$2***");
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:o} {1} playerId={2} msgId={3} seq={4} opId={5} elapsedMs={6} {7}\n",
                DateTime.UtcNow,
                level.ToString().ToUpperInvariant(),
                context.PlayerId,
                context.MsgId,
                context.Seq,
                context.OpId,
                context.ElapsedMs,
                body);
        }

        void EnsureWritable()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FileLogger));
            if (_stream == null)
                throw new InvalidOperationException("先调用 Initialize");
        }

        void WriteLine(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);
            _currentBytes += bytes.Length;
            if (_currentBytes >= _maxFileBytes)
                Roll();
        }

        void Roll()
        {
            CloseStream();
            if (_retainedFiles == 1)
            {
                File.Delete(CurrentPath());
            }
            else
            {
                string oldest = ArchivedPath(_retainedFiles - 1);
                if (File.Exists(oldest))
                    File.Delete(oldest);

                for (int index = _retainedFiles - 2; index >= 1; index--)
                {
                    string from = ArchivedPath(index);
                    if (File.Exists(from))
                        File.Move(from, ArchivedPath(index + 1));
                }

                if (File.Exists(CurrentPath()))
                    File.Move(CurrentPath(), ArchivedPath(1));
            }

            OpenCurrent();
        }

        void OpenCurrent()
        {
            _stream = new FileStream(CurrentPath(), FileMode.Append, FileAccess.Write, FileShare.Read);
            _currentBytes = _stream.Length;
        }

        void CloseStream()
        {
            if (_stream == null)
                return;
            _stream.Flush();
            _stream.Dispose();
            _stream = null;
        }

        string CurrentPath()
        {
            return Path.Combine(_directory, "client.log");
        }

        string ArchivedPath(int index)
        {
            return Path.Combine(_directory, "client." + index.ToString(CultureInfo.InvariantCulture) + ".log");
        }
    }
}
