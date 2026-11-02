using System;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 显示用单调时钟。仅估算倒计时；不参与成熟/发奖/经济结算。
    /// 失去同步、会话重置、单调回退或溢出时进入未同步态。
    /// </summary>
    public sealed class ClientGameplayClock
    {
        readonly object _gate = new object();
        long _serverAnchorMs;
        long _localAnchorMs;
        /// <summary>上一次已成功观察的单调读数；回退对比此值，而非仅对比初始锚点。</summary>
        long _lastObservedMs;
        bool _synced;

        /// <summary>用已校验 server_time 与当时单调读数锚定。serverTimeMs 为规范十进制字符串。</summary>
        public void Synchronize(string serverTimeMs, long localMonotonicMs)
        {
            if (!GameplayInt64.TryParse(serverTimeMs, out long server) || server < 0)
            {
                Reset();
                return;
            }

            if (localMonotonicMs < 0)
                localMonotonicMs = 0;

            lock (_gate)
            {
                _serverAnchorMs = server;
                _localAnchorMs = localMonotonicMs;
                _lastObservedMs = localMonotonicMs;
                _synced = true;
            }
        }

        /// <summary>估算当前服务器毫秒的规范十进制串；未同步返回空串。</summary>
        public string EstimateNowMs(long localMonotonicMs)
        {
            lock (_gate)
            {
                if (!_synced)
                    return "";

                // 相对已观察读数回退（例：200→150）即失同步，即使仍高于初始锚点
                if (localMonotonicMs < _lastObservedMs)
                {
                    Clear_NoLock();
                    return "";
                }

                long delta = localMonotonicMs - _localAnchorMs;
                if (delta < 0)
                {
                    Clear_NoLock();
                    return "";
                }

                if (_serverAnchorMs > long.MaxValue - delta)
                {
                    Clear_NoLock();
                    return "";
                }

                _lastObservedMs = localMonotonicMs;
                return GameplayInt64.Format(_serverAnchorMs + delta);
            }
        }

        /// <summary>距 deadline 剩余毫秒；未同步或已过期返回 0（不负）。</summary>
        public long RemainingMs(string deadlineMs, long localMonotonicMs)
        {
            if (!GameplayInt64.TryParse(deadlineMs, out long deadline))
                return 0;

            string nowText = EstimateNowMs(localMonotonicMs);
            if (string.IsNullOrEmpty(nowText)
                || !GameplayInt64.TryParse(nowText, out long now))
            {
                return 0;
            }

            // 负 deadline（含 long.MinValue）不得做减法以免溢出成巨大正数
            if (deadline <= now)
                return 0;

            long remain;
            try
            {
                remain = checked(deadline - now);
            }
            catch (OverflowException)
            {
                return 0;
            }

            return remain < 0 ? 0 : remain;
        }

        public bool IsSynchronized
        {
            get { lock (_gate) return _synced; }
        }

        /// <summary>会话结束/重置：丢弃锚点。</summary>
        public void Reset()
        {
            lock (_gate) Clear_NoLock();
        }

        void Clear_NoLock()
        {
            _synced = false;
            _serverAnchorMs = 0;
            _localAnchorMs = 0;
            _lastObservedMs = 0;
        }
    }
}
