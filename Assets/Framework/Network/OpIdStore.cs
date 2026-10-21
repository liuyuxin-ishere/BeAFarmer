using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace Game.Framework.Network
{
    /// <summary>
    /// 按玩家持久化操作号。先把新值刷到临时文件并原子提交，成功后才返回。
    /// 同一进程里指向同一目录的实例共用锁，并以磁盘上的成功值为准。
    /// </summary>
    public sealed class OpIdStore
    {
        static readonly ConcurrentDictionary<string, object> Gates =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        readonly string _directory;

        /// <summary>使用这个目录保存各玩家的最后一次成功操作号。</summary>
        /// <param name="directory">持久化目录。空白时抛出异常。</param>
        public OpIdStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("操作号目录为空", nameof(directory));

            _directory = Normalize(directory);
            Directory.CreateDirectory(_directory);
        }

        /// <summary>为玩家分配下一个正数操作号。保存失败时不返回新号。</summary>
        /// <param name="playerId">玩家编号。</param>
        public long Next(long playerId)
        {
            lock (Gate(playerId))
            {
                long last = ReadCore(playerId);
                if (last == long.MaxValue)
                    throw new InvalidOperationException("操作号已到 long 上限，拒绝回绕");

                long next = last + 1;
                Commit(PlayerPath(playerId), next);
                return next;
            }
        }

        /// <summary>读取该玩家上次成功保存的操作号。没有记录时返回 0，不分配新号。</summary>
        /// <param name="playerId">玩家编号。</param>
        public long ReadLast(long playerId)
        {
            lock (Gate(playerId))
                return ReadCore(playerId);
        }

        /// <summary>同一目录、同一玩家共用一把锁。</summary>
        /// <param name="playerId">玩家编号。</param>
        object Gate(long playerId)
        {
            string key = _directory + "\n" + playerId.ToString(CultureInfo.InvariantCulture);
            return Gates.GetOrAdd(key, _ => new object());
        }

        /// <summary>返回该玩家正式操作号文件的路径。</summary>
        /// <param name="playerId">玩家编号。</param>
        string PlayerPath(long playerId)
        {
            string name = playerId.ToString(CultureInfo.InvariantCulture) + ".opid";
            return Path.Combine(_directory, name);
        }

        /// <summary>读取磁盘上的成功值。没有文件返回 0，文件损坏则抛异常。</summary>
        /// <param name="playerId">玩家编号。</param>
        long ReadCore(long playerId)
        {
            string path = PlayerPath(playerId);
            if (!File.Exists(path))
                return 0;

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 64, false))
            {
                text = reader.ReadToEnd();
            }

            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0)
                throw new InvalidDataException("操作号文件无法读取，拒绝从 0 重新分配");

            return value;
        }

        /// <summary>先把新值刷进临时文件，再原子替换正式文件。</summary>
        /// <param name="finalPath">正式文件路径。</param>
        /// <param name="value">要保存的操作号。</param>
        static void Commit(string finalPath, long value)
        {
            string tempPath = finalPath + ".tmp";
            byte[] payload = Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));
            using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
            }

            if (!File.Exists(finalPath))
                File.Move(tempPath, finalPath);
            else
                File.Replace(tempPath, finalPath, null);
        }

        /// <summary>把目录收成绝对路径，并去掉末尾分隔符。</summary>
        /// <param name="directory">调用方传入的目录。</param>
        static string Normalize(string directory)
        {
            string full = Path.GetFullPath(directory);
            string root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && full.Length > root.Length)
                full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }
    }
}
