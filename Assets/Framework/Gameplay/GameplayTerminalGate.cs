using System;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// 204/106 终止通知恰好一次/epoch；旧 epoch 不得终止新会话。
    /// </summary>
    internal sealed class GameplayTerminalGate
    {
        readonly object _gate = new object();
        long _claimedEpoch = -1;
        int _claimedCode;

        /// <summary>
        /// 若本 epoch 尚未委托且 stampEpoch 仍为当前会话 epoch，则认领并返回 true。
        /// </summary>
        public bool TryClaim(long stampEpoch, long liveEpoch, int code)
        {
            if (code != 204 && code != 106)
                return false;
            if (stampEpoch != liveEpoch)
                return false;

            lock (_gate)
            {
                if (_claimedEpoch == stampEpoch)
                    return false;
                _claimedEpoch = stampEpoch;
                _claimedCode = code;
                return true;
            }
        }

        public int LastClaimedCode
        {
            get { lock (_gate) return _claimedCode; }
        }

        public long LastClaimedEpoch
        {
            get { lock (_gate) return _claimedEpoch; }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _claimedEpoch = -1;
                _claimedCode = 0;
            }
        }
    }
}
