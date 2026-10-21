using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Scenes;
using Game.Framework.UI;
using UnityEngine;

namespace Game.Framework.Account
{
    /// <summary>
    /// Login↔Home 场景切换的固定 C# 钩子，供 Lua 在无业务类窗口的前提下调用。
    /// 串行：等旧切换取消清理完成后再启动最新目标；按真实 CurrentAddress 打开 Login/HomeHud。
    /// 失败/取消不得展示错误目标窗口；最新目标获胜。
    /// </summary>
    public static class AccountFlow
    {
        static SceneService _scenes;
        static CancellationTokenSource _switchCts;
        static int _epoch;
        static int _inflight;
        static string _lastError = "";
        static string _desiredAddress = "";

        /// <summary>最近一次切换错误（空=无）；测试可观察，不把 busy 当成功。</summary>
        public static string LastError => _lastError;

        /// <summary>是否仍有切换在飞。</summary>
        public static bool IsSwitching => Volatile.Read(ref _inflight) > 0;

        /// <summary>最新期望场景地址（测试可读）。</summary>
        public static string DesiredAddress => _desiredAddress ?? "";

        public static void Bind(SceneService scenes) => _scenes = scenes;

        /// <summary>关闭绑定与进行中切换；Kick/Logout 路径应调用以免静态泄漏。</summary>
        public static void Shutdown()
        {
            Interlocked.Increment(ref _epoch);
            try { _switchCts?.Cancel(); } catch { }
            try { _switchCts?.Dispose(); } catch { }
            _switchCts = null;
            _scenes = null;
            _desiredAddress = "";
            _lastError = "";
        }

        /// <summary>切到正式 M1_Home（空家园），成功后再开 HomeHud。</summary>
        public static void EnterHome() => Switch("M1_Home");

        /// <summary>切到正式 M1_Login，成功后再开 Login（关 HomeHud）。</summary>
        public static void EnterLogin() => Switch("M1_Login");

        static void Switch(string address)
        {
            if (_scenes == null)
            {
                _lastError = "flow-unbound";
                return;
            }

            _desiredAddress = address ?? "";
            int epoch = Interlocked.Increment(ref _epoch);
            try { _switchCts?.Cancel(); } catch { }
            try { _switchCts?.Dispose(); } catch { }
            _switchCts = new CancellationTokenSource();
            CancellationToken ct = _switchCts.Token;
            Interlocked.Increment(ref _inflight);
            RunSwitchAsync(address, epoch, ct).Forget();
        }

        static async UniTaskVoid RunSwitchAsync(string address, int epoch, CancellationToken ct)
        {
            try
            {
                // 等旧切换释放 _busy，避免立刻撞 scene-switch-busy 并被吞掉。
                SceneService scenes = _scenes;
                float waitUntil = Time.realtimeSinceStartup + 30f;
                while (scenes != null && scenes.IsBusy)
                {
                    if (epoch != Volatile.Read(ref _epoch) || ct.IsCancellationRequested)
                        return;
                    if (Time.realtimeSinceStartup > waitUntil)
                    {
                        _lastError = "wait-busy-timeout";
                        return;
                    }

                    await UniTask.Yield(PlayerLoopTiming.Update);
                    scenes = _scenes;
                }

                if (epoch != Volatile.Read(ref _epoch) || ct.IsCancellationRequested)
                    return;
                scenes = _scenes;
                if (scenes == null)
                {
                    _lastError = "flow-unbound";
                    return;
                }

                try
                {
                    await scenes.SwitchAsync(address, ct);
                    _lastError = "";
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetBaseException().Message ?? "switch-failed";
                    Debug.LogWarning("[AccountFlow] switch failed: " + _lastError);
                }

                if (epoch != Volatile.Read(ref _epoch) || ct.IsCancellationRequested)
                    return;

                scenes = _scenes;
                if (scenes == null)
                    return;

                string cur = scenes.CurrentAddress ?? "";
                if (!string.Equals(cur, address, StringComparison.Ordinal))
                {
                    // 失败回退不得同时展示错误 Home：目标非 Login 且当前也不是目标时，再切 Login。
                    if (!string.Equals(address, "M1_Login", StringComparison.Ordinal)
                        && !string.Equals(cur, "M1_Login", StringComparison.Ordinal)
                        && epoch == Volatile.Read(ref _epoch)
                        && !ct.IsCancellationRequested)
                    {
                        try
                        {
                            await scenes.SwitchAsync("M1_Login", ct);
                            cur = scenes.CurrentAddress ?? "";
                        }
                        catch (Exception ex)
                        {
                            _lastError = "fallback-login:" + (ex.GetBaseException().Message ?? "");
                        }
                    }
                }

                if (epoch != Volatile.Read(ref _epoch) || ct.IsCancellationRequested)
                    return;

                // 仅按真实 CurrentAddress 开窗；失败/取消不得开错误目标。
                ApplyWindowsForCurrentAddress(scenes.CurrentAddress ?? "");
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
            }
        }

        /// <summary>按真实场景地址统一 Login/HomeHud；无 Manager 时仅记场景。</summary>
        public static void ApplyWindowsForCurrentAddress(string currentAddress)
        {
            if (UiBridge.Manager == null)
                return;

            if (string.Equals(currentAddress, "M1_Home", StringComparison.Ordinal))
            {
                try { UiBridge.Close("Login", (_, __) => { }); } catch { }
                try
                {
                    UiBridge.Open("HomeHud", "", (code, _) =>
                    {
                        if (code == 0)
                        {
                            try { AccountBridge.HideDisconnectMask(); } catch { }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[AccountFlow] open HomeHud failed: " + ex.Message);
                }

                return;
            }

            if (string.Equals(currentAddress, "M1_Login", StringComparison.Ordinal))
            {
                try { UiBridge.Close("HomeHud", (_, __) => { }); } catch { }
                try { UiBridge.Open("Login", "", (_, __) => { }); } catch { }
                try { AccountBridge.HideDisconnectMask(); } catch { }
            }
        }
    }
}
