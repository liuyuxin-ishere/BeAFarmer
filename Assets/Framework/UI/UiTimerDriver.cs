using Game.Framework.Core;
using UnityEngine;

namespace Game.Framework.UI
{
    /// <summary>每帧用 unscaledDeltaTime 推进 TimerService（Toast 等）。</summary>
    public sealed class UiTimerDriver : MonoBehaviour
    {
        TimerService _timers;

        public TimerService Timers => _timers ??= new TimerService();

        public static UiTimerDriver Ensure()
        {
            var existing = FindObjectOfType<UiTimerDriver>();
            if (existing != null)
                return existing;
            var go = new GameObject("M1_UiTimerDriver");
            DontDestroyOnLoad(go);
            return go.AddComponent<UiTimerDriver>();
        }

        void Update()
        {
            Timers.Tick(Time.unscaledDeltaTime);
        }
    }
}
