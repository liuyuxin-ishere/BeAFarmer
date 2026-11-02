using System;
using System.Globalization;
using CloudIsle.Proto;
using Google.Protobuf;
using XLua;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 经唯一 Lua ModelStore 写模型；仅主线程同步执行；Inbox/Runtime 负责异步投递。
    /// 禁止 off-thread 阻塞等待（无 ManualResetEventSlim）。
    /// </summary>
    internal sealed class LuaModelStorePort : IGameplayModelPort
    {
        static readonly JsonParser Parser = new JsonParser(JsonParser.Settings.Default);

        readonly LuaEnv _env;
        readonly IMainThreadDispatcher _dispatcher;
        readonly object _gate = new object();
        long _revision;
        string _playerId = "";
        bool _installed;

        public LuaModelStorePort(LuaEnv env, IMainThreadDispatcher dispatcher)
        {
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public void Clear(SessionStamp stamp, string reason)
        {
            _ = stamp;
            if (!EnsureMainThread())
            {
                lock (_gate)
                {
                    _revision = 0;
                    _playerId = "";
                    _installed = false;
                }
                return;
            }

            try
            {
                _env.Global.Set("__m2_clear_reason", reason ?? "");
                _env.DoString(
                    "local ok, Store = pcall(require, 'Models/ModelStore')\n" +
                    "if ok and Store and Store.clear then Store.clear(__m2_clear_reason) end\n" +
                    "__m2_clear_reason = nil",
                    "m2-modelstore-clear");
            }
            catch
            {
                try { _env.Global.Set("__m2_clear_reason", (string)null); } catch { /* ignore */ }
            }

            lock (_gate)
            {
                _revision = 0;
                _playerId = "";
                _installed = false;
            }
        }

        public ModelApplyResult ReplaceSnapshot(SnapshotEnvelope snapshot, SessionStamp stamp)
        {
            if (snapshot == null)
                return ModelApplyResult.Invalid(GetRevision(stamp), "snapshot-null");
            if (string.IsNullOrWhiteSpace(snapshot.SnapshotJson))
                return ModelApplyResult.Invalid(GetRevision(stamp), "snapshot-empty");
            if (!EnsureMainThread())
                return ModelApplyResult.Invalid(GetRevision(stamp), "off-main-thread");

            if (!string.IsNullOrEmpty(stamp.PlayerId))
            {
                try
                {
                    PlayerFullData data = Parser.Parse<PlayerFullData>(snapshot.SnapshotJson);
                    if (data?.Info != null)
                    {
                        string pid = data.Info.PlayerId.ToString(CultureInfo.InvariantCulture);
                        if (!string.Equals(pid, stamp.PlayerId, StringComparison.Ordinal))
                            return ModelApplyResult.Invalid(GetRevision(stamp), "player-stamp-mismatch");
                    }
                }
                catch (Exception ex)
                {
                    return ModelApplyResult.Invalid(GetRevision(stamp), "snapshot-parse:" + ex.GetType().Name);
                }
            }

            try
            {
                _env.Global.Set("__m2_snap_json", snapshot.SnapshotJson);
                object[] ret = _env.DoString(
                    "local Store = require('Models/ModelStore')\n" +
                    "local Json = require('Json')\n" +
                    "local raw = __m2_snap_json\n" +
                    "__m2_snap_json = nil\n" +
                    "local r = Store.replaceSnapshot(Json.decode(raw), nil)\n" +
                    "return r.kind, r.appliedRevision, r.error",
                    "m2-modelstore-replace");
                ModelApplyResult luaResult = MapLuaResult(ret, GetRevision(stamp));
                RememberIfApplied(luaResult, stamp);
                return luaResult;
            }
            catch (Exception ex)
            {
                try { _env.Global.Set("__m2_snap_json", (string)null); } catch { /* ignore */ }
                return ModelApplyResult.Invalid(GetRevision(stamp), "lua-replace:" + ex.GetType().Name);
            }
        }

        public ModelApplyResult ApplyBatch(DeltaBatchDto batch, SessionStamp stamp)
        {
            if (batch == null)
                return ModelApplyResult.Invalid(GetRevision(stamp), "batch-null");
            if (string.IsNullOrWhiteSpace(batch.BatchJson))
                return ModelApplyResult.Invalid(GetRevision(stamp), "batch-empty");
            if (!EnsureMainThread())
                return ModelApplyResult.Invalid(GetRevision(stamp), "off-main-thread");

            try
            {
                _env.Global.Set("__m2_batch_json", batch.BatchJson);
                object[] ret = _env.DoString(
                    "local Store = require('Models/ModelStore')\n" +
                    "local Json = require('Json')\n" +
                    "local raw = __m2_batch_json\n" +
                    "__m2_batch_json = nil\n" +
                    "local r = Store.applyBatch(Json.decode(raw), nil)\n" +
                    "return r.kind, r.appliedRevision, r.error",
                    "m2-modelstore-batch");
                ModelApplyResult luaResult = MapLuaResult(ret, GetRevision(stamp));
                RememberIfApplied(luaResult, stamp);
                return luaResult;
            }
            catch (Exception ex)
            {
                try { _env.Global.Set("__m2_batch_json", (string)null); } catch { /* ignore */ }
                return ModelApplyResult.Invalid(GetRevision(stamp), "lua-batch:" + ex.GetType().Name);
            }
        }

        public bool TryInstallSnapshot(SessionStamp stamp, string snapshotJson, out long revision, out string error)
        {
            revision = 0;
            error = null;
            if (!EnsureMainThread())
            {
                error = "off-main-thread";
                return false;
            }

            long revHint = 0;
            try
            {
                PlayerFullData data = Parser.Parse<PlayerFullData>(snapshotJson ?? "");
                if (data != null) revHint = data.Revision;
            }
            catch (Exception ex)
            {
                error = "snapshot-parse:" + ex.GetType().Name;
                return false;
            }

            var envelope = new SnapshotEnvelope(
                snapshotJson,
                revHint,
                stamp.Epoch,
                stamp.ConnectionGeneration);
            ModelApplyResult result = ReplaceSnapshot(envelope, stamp);
            if (result.Kind == ModelApplyKind.Applied || result.Kind == ModelApplyKind.Duplicate)
            {
                revision = result.AppliedRevision;
                return true;
            }

            error = result.Error;
            Clear(stamp, "install-failed");
            return false;
        }

        public long GetRevision(SessionStamp stamp)
        {
            lock (_gate)
            {
                if (!_installed)
                    return 0;
                if (!string.IsNullOrEmpty(stamp.PlayerId)
                    && !string.Equals(stamp.PlayerId, _playerId, StringComparison.Ordinal))
                    return 0;
                return _revision;
            }
        }

        bool EnsureMainThread()
        {
            return _dispatcher.IsMainThread;
        }

        void RememberIfApplied(ModelApplyResult result, SessionStamp stamp)
        {
            if (result.Kind != ModelApplyKind.Applied && result.Kind != ModelApplyKind.Duplicate)
                return;
            if (!EnsureMainThread())
                return;

            string player = stamp.PlayerId ?? "";
            try
            {
                object[] ret = _env.DoString(
                    "local ok, Store = pcall(require, 'Models/ModelStore')\n" +
                    "if not ok or not Store then return '', '0' end\n" +
                    "local s = Store.getSnapshot()\n" +
                    "local pid = s and tostring(s.playerId or '') or ''\n" +
                    "return pid, Store.getRevision()",
                    "m2-modelstore-probe");
                if (ret != null && ret.Length >= 2)
                {
                    player = ret[0] != null ? ret[0].ToString() : player;
                    if (GameplayInt64.TryParse(ret[1] != null ? ret[1].ToString() : "0", out long live))
                    {
                        lock (_gate)
                        {
                            _revision = live;
                            _playerId = player;
                            _installed = live > 0 || !string.IsNullOrEmpty(player);
                        }
                        return;
                    }
                }
            }
            catch
            {
                /* fallthrough */
            }

            lock (_gate)
            {
                _revision = result.AppliedRevision;
                if (!string.IsNullOrEmpty(stamp.PlayerId))
                    _playerId = stamp.PlayerId;
                _installed = true;
            }
        }

        static ModelApplyResult MapLuaResult(object[] ret, long fallbackRevision)
        {
            string kind = ret != null && ret.Length > 0 && ret[0] != null ? ret[0].ToString() : "Invalid";
            long rev = fallbackRevision;
            if (ret != null && ret.Length > 1 && ret[1] != null)
                GameplayInt64.TryParse(ret[1].ToString(), out rev);
            string err = ret != null && ret.Length > 2 && ret[2] != null ? ret[2].ToString() : "";

            if (string.Equals(kind, "Applied", StringComparison.Ordinal))
                return ModelApplyResult.Applied(rev);
            if (string.Equals(kind, "Duplicate", StringComparison.Ordinal))
                return ModelApplyResult.Duplicate(rev);
            if (string.Equals(kind, "Gap", StringComparison.Ordinal))
                return ModelApplyResult.Gap(rev, err);
            return ModelApplyResult.Invalid(rev, string.IsNullOrEmpty(err) ? kind : err);
        }
    }
}
