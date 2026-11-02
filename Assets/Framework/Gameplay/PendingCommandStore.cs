using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CloudIsle.Commands;

namespace Game.Framework.Gameplay
{
    /// <summary>装载结果：损坏/缺载荷显式锁写，不静默删除。</summary>
    public sealed class PendingLoadResult
    {
        public PendingLoadResult(PendingLoadKind kind, PendingCommand command, string message)
        {
            Kind = kind;
            Command = command;
            Message = message ?? "";
        }

        public PendingLoadKind Kind { get; }
        public PendingCommand Command { get; }
        public string Message { get; }
    }

    /// <summary>
    /// 单槽原子持久化：唯一 temp + flush + File.Replace；不透明 SHA256 路径；跨进程排他锁。
    /// 格式 v1：magic+version+crc32+body。
    /// </summary>
    public class PendingCommandStore
    {
        public const int FormatVersion = 1;
        public const int MaxWireBytes = 64 * 1024;
        public const int MaxBusinessBytes = 64 * 1024;
        public const int MaxFileBytes = 256 * 1024;
        public const int MaxStringBytes = 4 * 1024;

        static readonly byte[] Magic = Encoding.ASCII.GetBytes("M2PC");
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        readonly string _root;
        readonly object _gate = new object();

        public PendingCommandStore(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("root required", nameof(rootDirectory));
            _root = Path.GetFullPath(rootDirectory);
        }

        public string RootDirectory => _root;

        /// <summary>测试/子类：temp flush 后、Replace/Move 前注入故障。</summary>
        protected virtual void OnAfterTempFlushedBeforeCommit(string tempPath, string targetPath)
        {
        }

        public IReadOnlyList<PendingCommand> LoadPendingForAccount(string environmentKey, string accountKey)
        {
            PendingLoadResult r = TryLoad(environmentKey, accountKey);
            if (r.Kind == PendingLoadKind.Ok && r.Command != null)
                return new[] { r.Command };
            if (r.Kind == PendingLoadKind.CorruptLocked || r.Kind == PendingLoadKind.MissingPayloadLocked)
                throw new InvalidOperationException("pending-locked:" + r.Message);
            return Array.Empty<PendingCommand>();
        }

        public PendingLoadResult TryLoad(string environmentKey, string accountKey)
        {
            using (SlotLock.Acquire(_root, environmentKey, accountKey))
            {
                lock (_gate)
                    return TryLoadUnlocked(environmentKey, accountKey);
            }
        }

        /// <summary>原子保存；Replace 失败则保留旧文件（fail-closed）。</summary>
        public virtual void SaveAtomic(PendingCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (string.IsNullOrWhiteSpace(command.EnvironmentKey) || string.IsNullOrWhiteSpace(command.AccountKey))
                throw new ArgumentException("environment/account required");
            if (string.IsNullOrWhiteSpace(command.PlayerId))
                throw new ArgumentException("player required");
            if (!command.HasWireBytes)
                throw new InvalidOperationException("wire required before save");
            command.EnsureDurableHash();

            using (SlotLock.Acquire(_root, command.EnvironmentKey, command.AccountKey))
            {
                lock (_gate)
                {
                    PendingLoadResult existing = TryLoadUnlocked(command.EnvironmentKey, command.AccountKey);
                    if (existing.Kind == PendingLoadKind.Ok && existing.Command != null)
                    {
                        if (!SameSlotKey(existing.Command, command))
                            throw new InvalidOperationException("busy-different-key");
                    }
                    else if (existing.Kind == PendingLoadKind.CorruptLocked
                             || existing.Kind == PendingLoadKind.MissingPayloadLocked)
                    {
                        throw new InvalidOperationException("slot-locked:" + existing.Message);
                    }

                    string dir = AccountDir(command.EnvironmentKey, command.AccountKey);
                    Directory.CreateDirectory(dir);
                    string path = EnsureInsideRoot(Path.Combine(dir, "slot.pending"));
                    string temp = EnsureInsideRoot(Path.Combine(
                        dir, "slot." + Guid.NewGuid().ToString("N") + ".tmp"));
                    byte[] body = Encode(command);
                    if (body.Length > MaxFileBytes)
                        throw new InvalidOperationException("file-too-large");

                    try
                    {
                        File.WriteAllBytes(temp, body);
                        using (var fs = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            fs.Flush(flushToDisk: true);
                        }

                        OnAfterTempFlushedBeforeCommit(temp, path);

                        if (File.Exists(path))
                        {
                            try
                            {
                                File.Replace(temp, path, null);
                            }
                            catch (Exception ex)
                            {
                                TryDeleteQuiet(temp);
                                throw new IOException("atomic-replace-failed;old-preserved", ex);
                            }
                        }
                        else
                        {
                            File.Move(temp, path);
                        }
                    }
                    catch
                    {
                        TryDeleteQuiet(temp);
                        throw;
                    }
                }
            }
        }

        public void Complete(string environmentKey, string accountKey, long scopeId, long opId)
        {
            using (SlotLock.Acquire(_root, environmentKey, accountKey))
            {
                lock (_gate)
                {
                    PendingLoadResult existing = TryLoadUnlocked(environmentKey, accountKey);
                    if (existing.Kind != PendingLoadKind.Ok || existing.Command == null)
                        return;
                    if (existing.Command.OperationScopeId != scopeId || existing.Command.OpId != opId)
                        throw new InvalidOperationException("complete-key-mismatch");
                    DeleteSlot(environmentKey, accountKey);
                }
            }
        }

        public void Archive(string environmentKey, string accountKey, string reason)
        {
            using (SlotLock.Acquire(_root, environmentKey, accountKey))
            {
                lock (_gate)
                {
                    _ = reason;
                    string dir = AccountDir(environmentKey, accountKey);
                    string path = Path.Combine(dir, "slot.pending");
                    if (File.Exists(path))
                    {
                        string archive = EnsureInsideRoot(Path.Combine(
                            dir, "slot.archived-" + Guid.NewGuid().ToString("N")));
                        try
                        {
                            File.Move(path, archive);
                        }
                        catch
                        {
                            // fail-closed：保留原 pending，调用方视为锁
                            throw;
                        }
                    }

                    QuarantineOrphans(dir);
                }
            }
        }

        void DeleteSlot(string environmentKey, string accountKey)
        {
            string path = SlotPath(environmentKey, accountKey);
            if (File.Exists(path))
                File.Delete(path);
            QuarantineOrphans(Path.GetDirectoryName(path));
        }

        PendingLoadResult TryLoadUnlocked(string environmentKey, string accountKey)
        {
            RejectBlankIdentity(environmentKey, accountKey);
            string dir = AccountDir(environmentKey, accountKey);
            string path = SlotPath(environmentKey, accountKey);

            // 仅有 orphan tmp：已 prepared 未 commit → 隔离后 Empty（不可作权威未发送）
            if (!File.Exists(path))
            {
                bool hadOrphan = false;
                if (Directory.Exists(dir))
                {
                    string[] tmps = Directory.GetFiles(dir, "*.tmp");
                    hadOrphan = tmps != null && tmps.Length > 0;
                    QuarantineOrphans(dir);
                }
                return new PendingLoadResult(
                    PendingLoadKind.Empty,
                    null,
                    hadOrphan ? "orphan-quarantined" : "empty");
            }

            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Length > MaxFileBytes)
                    return new PendingLoadResult(PendingLoadKind.CorruptLocked, null, "file-too-large");

                byte[] all = File.ReadAllBytes(path);
                PendingCommand cmd = Decode(all, environmentKey, accountKey, out string err);
                if (cmd == null)
                    return new PendingLoadResult(PendingLoadKind.CorruptLocked, null, err);
                if (!string.Equals(cmd.EnvironmentKey, environmentKey, StringComparison.Ordinal)
                    || !string.Equals(cmd.AccountKey, accountKey, StringComparison.Ordinal))
                {
                    return new PendingLoadResult(PendingLoadKind.CorruptLocked, null, "identity-mismatch");
                }

                if (!cmd.HasWireBytes || string.IsNullOrEmpty(cmd.PayloadHash))
                    return new PendingLoadResult(PendingLoadKind.MissingPayloadLocked, null, "missing-payload");
                return new PendingLoadResult(PendingLoadKind.Ok, cmd, "ok");
            }
            catch (Exception ex)
            {
                return new PendingLoadResult(PendingLoadKind.CorruptLocked, null, ex.GetType().Name);
            }
        }

        void QuarantineOrphans(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            foreach (string tmp in Directory.GetFiles(dir, "*.tmp"))
            {
                try
                {
                    string q = EnsureInsideRoot(tmp + ".quarantine-" + Guid.NewGuid().ToString("N"));
                    if (File.Exists(tmp))
                        File.Move(tmp, q);
                }
                catch
                {
                    /* best-effort */
                }
            }
        }

        static bool SameSlotKey(PendingCommand a, PendingCommand b)
        {
            return a.OperationScopeId == b.OperationScopeId
                   && a.OpId == b.OpId
                   && string.Equals(a.Intent.CommandKey, b.Intent.CommandKey, StringComparison.Ordinal)
                   && string.Equals(a.PayloadHash, b.PayloadHash, StringComparison.Ordinal);
        }

        string AccountDir(string environmentKey, string accountKey)
        {
            RejectBlankIdentity(environmentKey, accountKey);
            string envSeg = OpaqueSegment("env", environmentKey);
            string accSeg = OpaqueSegment("account", accountKey);
            // 两层完整摘要再加唯一临时文件会超过Windows/Mono常见路径上限。
            // 两个固定64字符摘要组成无歧义身份，再域分隔摘要成单层目录。
            string slotSeg = OpaqueSegment("slot", envSeg + accSeg);
            return EnsureInsideRoot(Path.Combine(_root, slotSeg));
        }

        string SlotPath(string environmentKey, string accountKey)
        {
            return EnsureInsideRoot(Path.Combine(AccountDir(environmentKey, accountKey), "slot.pending"));
        }

        string EnsureInsideRoot(string candidate)
        {
            string full = Path.GetFullPath(candidate);
            string root = _root;
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                && !root.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                root += Path.DirectorySeparatorChar;
            }

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFullPath(_root), full, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("path-escapes-root");
            }

            return full;
        }

        static void RejectBlankIdentity(string environmentKey, string accountKey)
        {
            if (string.IsNullOrWhiteSpace(environmentKey))
                throw new ArgumentException("environment blank");
            if (string.IsNullOrWhiteSpace(accountKey))
                throw new ArgumentException("account blank");
        }

        /// <summary>不透明路径段：SHA256(UTF8(domain + NUL + value)) hex；不含原始 key。</summary>
        public static string OpaqueSegment(string domain, string value)
        {
            if (string.IsNullOrEmpty(domain))
                throw new ArgumentException("domain required", nameof(domain));
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("value blank", nameof(value));
            byte[] domainBytes = StrictUtf8.GetBytes(domain);
            byte[] valueBytes = StrictUtf8.GetBytes(value);
            byte[] material = new byte[domainBytes.Length + 1 + valueBytes.Length];
            Buffer.BlockCopy(domainBytes, 0, material, 0, domainBytes.Length);
            material[domainBytes.Length] = 0;
            Buffer.BlockCopy(valueBytes, 0, material, domainBytes.Length + 1, valueBytes.Length);
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(material));
            }
        }

        static string ToLowerHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        static void TryDeleteQuiet(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                /* ignore */
            }
        }

        static byte[] Encode(PendingCommand cmd)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, StrictUtf8, leaveOpen: true))
            {
                WriteString(w, cmd.EnvironmentKey);
                WriteString(w, cmd.AccountKey);
                WriteString(w, cmd.PlayerId);
                WriteString(w, cmd.Intent.CommandKey);
                w.Write(cmd.OpId);
                w.Write(cmd.OperationScopeId);
                w.Write(cmd.ExpectedRevision);
                w.Write(cmd.ConfigVersion);
                WriteString(w, cmd.PayloadHash ?? "");
                w.Write((int)cmd.State);
                w.Write(cmd.CreatedAtMs);
                w.Write(cmd.AuthAttempt);
                w.Write(cmd.Epoch);
                w.Write(cmd.ConnectionGeneration);
                w.Write(cmd.HasConfirmedRevision ? (byte)1 : (byte)0);
                w.Write(cmd.ConfirmedRevision);
                byte[] wire = cmd.PeekWireBytes() ?? Array.Empty<byte>();
                if (wire.Length > MaxWireBytes)
                    throw new InvalidOperationException("wire-too-large");
                w.Write(wire.Length);
                w.Write(wire);
                byte[] business = cmd.PeekBusinessBytes() ?? Array.Empty<byte>();
                if (business.Length > MaxBusinessBytes)
                    throw new InvalidOperationException("business-too-large");
                w.Write(business.Length);
                w.Write(business);
                w.Flush();
                byte[] body = ms.ToArray();
                uint crc = Crc32(body);

                using (var outMs = new MemoryStream(16 + body.Length))
                using (var outW = new BinaryWriter(outMs, StrictUtf8, leaveOpen: true))
                {
                    outW.Write(Magic);
                    outW.Write(FormatVersion);
                    outW.Write(crc);
                    outW.Write(body.Length);
                    outW.Write(body);
                    outW.Flush();
                    return outMs.ToArray();
                }
            }
        }

        static PendingCommand Decode(byte[] all, string expectedEnv, string expectedAccount, out string error)
        {
            error = null;
            if (all == null || all.Length < 16)
            {
                error = "truncated";
                return null;
            }

            if (all.Length > MaxFileBytes)
            {
                error = "file-too-large";
                return null;
            }

            using (var ms = new MemoryStream(all))
            using (var r = new BinaryReader(ms, StrictUtf8, leaveOpen: true))
            {
                byte[] magic = r.ReadBytes(4);
                if (magic.Length != 4
                    || magic[0] != Magic[0] || magic[1] != Magic[1]
                    || magic[2] != Magic[2] || magic[3] != Magic[3])
                {
                    error = "bad-magic";
                    return null;
                }

                int ver = r.ReadInt32();
                if (ver != FormatVersion)
                {
                    error = "bad-version:" + ver.ToString(CultureInfo.InvariantCulture);
                    return null;
                }

                uint crc = r.ReadUInt32();
                int len = r.ReadInt32();
                if (len < 0 || len > MaxFileBytes)
                {
                    error = "bad-length";
                    return null;
                }

                byte[] body = r.ReadBytes(len);
                if (body.Length != len || Crc32(body) != crc)
                {
                    error = "crc-mismatch";
                    return null;
                }

                if (ms.Position != ms.Length)
                {
                    error = "trailing-bytes";
                    return null;
                }

                using (var bms = new MemoryStream(body))
                using (var br = new BinaryReader(bms, StrictUtf8, leaveOpen: true))
                {
                    string env = ReadString(br);
                    string account = ReadString(br);
                    string player = ReadString(br);
                    string key = ReadString(br);
                    long op = br.ReadInt64();
                    long scope = br.ReadInt64();
                    long rev = br.ReadInt64();
                    int cfg = br.ReadInt32();
                    string hash = ReadString(br);
                    int stateRaw = br.ReadInt32();
                    long created = br.ReadInt64();
                    int attempt = br.ReadInt32();
                    long epoch = br.ReadInt64();
                    long gen = br.ReadInt64();
                    bool hasConfirmed = br.ReadByte() != 0;
                    long confirmed = br.ReadInt64();
                    int wireLen = br.ReadInt32();
                    if (wireLen < 0 || wireLen > MaxWireBytes)
                    {
                        error = "wire-len";
                        return null;
                    }

                    byte[] wire = br.ReadBytes(wireLen);
                    if (wire.Length != wireLen)
                    {
                        error = "wire-truncated";
                        return null;
                    }

                    int bizLen = br.ReadInt32();
                    if (bizLen < 0 || bizLen > MaxBusinessBytes)
                    {
                        error = "business-len";
                        return null;
                    }

                    byte[] business = br.ReadBytes(bizLen);
                    if (business.Length != bizLen)
                    {
                        error = "business-truncated";
                        return null;
                    }

                    if (bms.Position != bms.Length)
                    {
                        error = "body-trailing";
                        return null;
                    }

                    if (!string.Equals(env, expectedEnv, StringComparison.Ordinal)
                        || !string.Equals(account, expectedAccount, StringComparison.Ordinal))
                    {
                        error = "identity-mismatch";
                        return null;
                    }

                    if (string.IsNullOrWhiteSpace(player) || op <= 0 || scope <= 0 || rev < 0 || cfg < 1
                        || created < 0 || attempt < 0 || !Enum.IsDefined(typeof(PendingCommandState), stateRaw))
                    {
                        error = "field-bounds";
                        return null;
                    }

                    if (!GameplayCommandAllowlist.TryGet(key, out CommandDescriptor desc))
                    {
                        error = "unknown-command:" + key;
                        return null;
                    }

                    if (!CanonicalBusinessEncoder.IsWriteKind(key))
                    {
                        error = "not-write-kind:" + key;
                        return null;
                    }

                    try
                    {
                        var intent = new CommandIntent(key, desc.RequestParser, business);
                        var state = (PendingCommandState)stateRaw;
                        // 构造会校验 wire 元数据与规范 hash；供应 hash 不一致则抛
                        return new PendingCommand(
                            intent, env, account, player, op, scope, rev, cfg,
                            hash, wire, state, created, attempt, epoch, gen,
                            hasConfirmed, confirmed);
                    }
                    catch (Exception ex)
                    {
                        error = "decode-reject:" + ex.GetType().Name;
                        return null;
                    }
                }
            }
        }

        static void WriteString(BinaryWriter w, string s)
        {
            byte[] utf8 = StrictUtf8.GetBytes(s ?? "");
            if (utf8.Length > MaxStringBytes)
                throw new InvalidOperationException("string-too-large");
            w.Write(utf8.Length);
            w.Write(utf8);
        }

        static string ReadString(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > MaxStringBytes)
                throw new InvalidDataException("string length");
            byte[] utf8 = r.ReadBytes(n);
            if (utf8.Length != n)
                throw new InvalidDataException("string truncated");
            return StrictUtf8.GetString(utf8);
        }

        static uint Crc32(byte[] data)
        {
            uint crc = 0xffffffffu;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int b = 0; b < 8; b++)
                {
                    uint mask = (uint)-(crc & 1);
                    crc = (crc >> 1) ^ (0xedb88320u & mask);
                }
            }
            return ~crc;
        }

        /// <summary>跨进程槽锁：同一 env/account 的 Read/Replace/Complete/Archive 互斥。</summary>
        sealed class SlotLock : IDisposable
        {
            readonly FileStream _fs;

            SlotLock(FileStream fs) { _fs = fs; }

            public static SlotLock Acquire(string root, string environmentKey, string accountKey)
            {
                RejectBlankIdentity(environmentKey, accountKey);
                // 与 AccountDir 同一单层不透明路径，避免锁与 pending 分家。
                string envSeg = OpaqueSegment("env", environmentKey);
                string accSeg = OpaqueSegment("account", accountKey);
                string slotSeg = OpaqueSegment("slot", envSeg + accSeg);
                string dir = Path.GetFullPath(Path.Combine(root, slotSeg));
                Directory.CreateDirectory(dir);
                string lockPath = Path.Combine(dir, "slot.lock");
                string rootFull = Path.GetFullPath(root);
                if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    rootFull += Path.DirectorySeparatorChar;
                string lockFull = Path.GetFullPath(lockPath);
                if (!lockFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("path-escapes-root");

                FileStream fs = null;
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        fs = new FileStream(
                            lockFull, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                        break;
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(20);
                    }
                }

                if (fs == null)
                    throw new IOException("slot-lock-timeout");
                return new SlotLock(fs);
            }

            public void Dispose()
            {
                try { _fs?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
