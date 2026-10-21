using System;
using System.IO;
using System.Text;

namespace Game.Framework.Account
{
    /// <summary>
    /// 本地持久化登录 Token（不含密码）。仅在登录/重连成功且快照应用确认后写入。
    /// </summary>
    public sealed class TokenStore
    {
        readonly string _path;

        /// <param name="directory">存放 token 文件的目录；空白抛异常。</param>
        public TokenStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("token directory empty", nameof(directory));
            Directory.CreateDirectory(directory);
            _path = Path.Combine(Path.GetFullPath(directory), "m1-token.txt");
        }

        /// <summary>磁盘上是否已有非空 Token。</summary>
        public bool HasToken()
        {
            if (!File.Exists(_path))
                return false;
            string text = File.ReadAllText(_path, Encoding.UTF8);
            return !string.IsNullOrWhiteSpace(text);
        }

        /// <summary>读取 Token；无文件或空白返回空串。</summary>
        public string Read()
        {
            if (!File.Exists(_path))
                return "";
            return (File.ReadAllText(_path, Encoding.UTF8) ?? "").Trim();
        }

        /// <summary>
        /// 原子写入 Token。已有文件用同目录 File.Replace；初次 Move。
        /// 写/替换失败保留旧值并返回 false（可观察），不假装已提交。
        /// token 空白视为 Clear（Clear 失败同样返回 false）。
        /// </summary>
        public bool Save(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return Clear();

            string dir = Path.GetDirectoryName(_path) ?? ".";
            string temp = Path.Combine(dir, "m1-token.tmp");
            string backup = Path.Combine(dir, "m1-token.bak");
            try
            {
                File.WriteAllText(temp, token.Trim(), new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    File.Replace(temp, _path, backup);
                    try
                    {
                        if (File.Exists(backup))
                            File.Delete(backup);
                    }
                    catch (IOException)
                    {
                    }
                }
                else
                {
                    File.Move(temp, _path);
                }

                return true;
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch (IOException)
                {
                }

                return false;
            }
        }

        /// <summary>删除 Token 文件；不存在则成功。删除失败返回 false。</summary>
        public bool Clear()
        {
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
                string temp = Path.Combine(Path.GetDirectoryName(_path) ?? ".", "m1-token.tmp");
                if (File.Exists(temp))
                    File.Delete(temp);
                string backup = Path.Combine(Path.GetDirectoryName(_path) ?? ".", "m1-token.bak");
                if (File.Exists(backup))
                    File.Delete(backup);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当前 Token 文件绝对路径（测试用；勿写入公开日志）。</summary>
        public string PathForTests => _path;
    }
}
