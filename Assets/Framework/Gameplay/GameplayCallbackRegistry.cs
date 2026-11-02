using System;
using System.Collections.Generic;
using Game.Framework.Lua;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 完成观察与增量订阅登记。无 static active；每 lease 持有本实例注销闭包。
    /// </summary>
    internal sealed class GameplayCallbackRegistry
    {
        /// <summary>单观察者故障诊断（可选；测试可挂接）。</summary>
        internal static Action<Exception> ObserverFault;

        readonly object _gate = new object();
        readonly Dictionary<long, CommandObservation> _completions = new Dictionary<long, CommandObservation>();
        readonly Dictionary<long, GameplaySubscriptionLease> _subs = new Dictionary<long, GameplaySubscriptionLease>();
        long _nextId = 1;

        internal GameplayCallbackRegistry()
        {
        }

        public int LiveCompletionCount
        {
            get { lock (_gate) return _completions.Count; }
        }

        public int LiveSubscriptionCount
        {
            get { lock (_gate) return _subs.Count; }
        }

        public CommandObservation RegisterCompletion(SessionStamp stamp, long epoch, LuaCallback done)
        {
            long id;
            lock (_gate)
                id = _nextId++;

            // 注销闭包捕获本 registry + id，绝不查全局
            Action unregister = () => RemoveCompletion(id, silent: true);
            var obs = CommandObservation.CreateArmed(id, stamp, epoch, done, unregister);
            lock (_gate)
                _completions[id] = obs;
            return obs;
        }

        /// <summary>主线程：接纳 Deferred 壳并 Retain+登记。</summary>
        public CommandObservation AdoptDeferred(CommandObservation deferred, SessionStamp stamp, long epoch)
        {
            if (deferred == null)
                throw new ArgumentNullException(nameof(deferred));
            long id;
            lock (_gate)
                id = _nextId++;
            Action unregister = () => RemoveCompletion(id, silent: true);
            if (!deferred.ArmDeferredOnMain(id, stamp, epoch, unregister))
                return deferred;
            lock (_gate)
                _completions[id] = deferred;
            return deferred;
        }

        public GameplaySubscriptionLease Subscribe(SessionStamp stamp, long epoch, LuaCallback handler)
        {
            long id;
            lock (_gate)
                id = _nextId++;

            Action unregister = () => RemoveSubscription(id);
            var lease = new GameplaySubscriptionLease(id, stamp, epoch, handler, unregister);
            lock (_gate)
                _subs[id] = lease;
            return lease;
        }

        /// <summary>主线程交付。无论成功或回调抛错，finally 移除登记。</summary>
        public void PostCompletion(long callbackId, long currentEpoch, int code, string json)
        {
            CommandObservation obs;
            lock (_gate)
            {
                if (!_completions.TryGetValue(callbackId, out obs))
                    return;
            }

            try
            {
                obs.TryDeliver(currentEpoch, code, json);
            }
            catch (Exception ex)
            {
                try { ObserverFault?.Invoke(ex); } catch { /* ignore */ }
            }
            finally
            {
                lock (_gate)
                    _completions.Remove(callbackId);
                // A stale-epoch result is dropped without delivery, but its
                // retained callback must still be released.
                obs.InvalidateSilent();
            }
        }

        public void BroadcastDelta(long currentEpoch, int code, string json)
        {
            GameplaySubscriptionLease[] snap;
            lock (_gate)
            {
                snap = new GameplaySubscriptionLease[_subs.Count];
                _subs.Values.CopyTo(snap, 0);
            }

            for (int i = 0; i < snap.Length; i++)
            {
                try
                {
                    snap[i].TryNotify(currentEpoch, code, json);
                }
                catch (Exception ex)
                {
                    try { ObserverFault?.Invoke(ex); } catch { /* ignore */ }
                }
            }
        }

        public void InvalidateEpoch(long epoch)
        {
            CommandObservation[] comps;
            GameplaySubscriptionLease[] subs;
            lock (_gate)
            {
                comps = new CommandObservation[_completions.Count];
                _completions.Values.CopyTo(comps, 0);
                _completions.Clear();
                subs = new GameplaySubscriptionLease[_subs.Count];
                _subs.Values.CopyTo(subs, 0);
                _subs.Clear();
            }

            for (int i = 0; i < comps.Length; i++)
                comps[i].InvalidateSilent();
            for (int i = 0; i < subs.Length; i++)
                subs[i].InvalidateSilent();
        }

        void RemoveCompletion(long id, bool silent)
        {
            CommandObservation obs;
            lock (_gate)
            {
                if (!_completions.TryGetValue(id, out obs))
                    return;
                _completions.Remove(id);
            }

            if (silent)
                obs.InvalidateSilent();
        }

        void RemoveSubscription(long id)
        {
            lock (_gate)
                _subs.Remove(id);
        }
    }
}
