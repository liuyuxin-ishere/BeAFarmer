using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Resource;
using UnityEngine;

namespace Game.Framework.Scenes
{
    /// <summary>
    /// 经 ResMgr/Yoo 互斥切换场景。取消时等待底层 AsyncOperation 完成再清理，不把取消当完成。
    /// 不调用 SceneManager.LoadScene 绕过资源适配。
    /// </summary>
    public sealed class SceneService
    {
        readonly ResMgr _resources;
        readonly LoadingView _loading;
        readonly string _fallbackAddress;
        SceneLease _current;
        bool _busy;

        /// <summary>当前已加载业务场景地址；无则为空。</summary>
        public string CurrentAddress => _current != null && _current.IsLoaded ? _current.Address : "";

        /// <summary>是否正在切换。</summary>
        public bool IsBusy => _busy;

        /// <param name="resources">已初始化的 ResMgr。</param>
        /// <param name="loading">常驻 Loading。</param>
        /// <param name="fallbackAddress">失败时可回退的场景地址（如 M1_Login）。</param>
        public SceneService(ResMgr resources, LoadingView loading, string fallbackAddress = "M1_Login")
        {
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
            _loading = loading ?? throw new ArgumentNullException(nameof(loading));
            _fallbackAddress = string.IsNullOrEmpty(fallbackAddress) ? "M1_Login" : fallbackAddress;
        }

        /// <summary>
        /// 互斥切换：先 Show Loading → 卸载旧 → 加载新 → Hide。
        /// 失败时尝试回退 fallback；仍失败则抛出并 Hide Loading。
        /// </summary>
        public async UniTask SwitchAsync(string sceneAddress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sceneAddress))
                throw new ArgumentException("sceneAddress 空白", nameof(sceneAddress));
            if (_busy)
                throw new InvalidOperationException("scene-switch-busy");

            _busy = true;
            _loading.Show("切换场景…");
            _loading.SetProgress(0.05f);
            SceneLease loaded = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (_current != null && _current.IsLoaded)
                {
                    _loading.SetProgress(0.2f);
                    // 卸载不可因 ct 中途放弃句柄；若已取消则仍等底层完成。
                    await UnloadLeaseSafeAsync(_current);
                    _current = null;
                }

                _loading.SetProgress(0.4f);
                try
                {
                    loaded = await _resources.LoadSceneAsync(sceneAddress, ct);
                }
                catch (OperationCanceledException)
                {
                    // ResMgr 已在取消路径等待底层并释放；不把取消当完成。
                    throw;
                }

                _loading.SetProgress(0.9f);
                _current = loaded;
                loaded = null;
                _loading.SetProgress(1f);
            }
            catch (OperationCanceledException)
            {
                if (loaded != null)
                {
                    await UnloadLeaseSafeAsync(loaded);
                }

                throw;
            }
            catch (Exception)
            {
                if (loaded != null)
                    await UnloadLeaseSafeAsync(loaded);

                if (!string.Equals(sceneAddress, _fallbackAddress, StringComparison.Ordinal)
                    && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await SwitchFallbackAsync(ct);
                        return;
                    }
                    catch (Exception fallbackEx)
                    {
                        Debug.LogWarning("[SceneService] fallback failed: " + fallbackEx.Message);
                    }
                }

                throw;
            }
            finally
            {
                _loading.Hide();
                _busy = false;
            }
        }

        /// <summary>卸载当前业务场景；保留常驻框架根。</summary>
        public async UniTask UnloadCurrentAsync(CancellationToken ct)
        {
            if (_busy)
                throw new InvalidOperationException("scene-switch-busy");
            if (_current == null || !_current.IsLoaded)
                return;

            _busy = true;
            try
            {
                ct.ThrowIfCancellationRequested();
                await UnloadLeaseSafeAsync(_current);
                _current = null;
            }
            finally
            {
                _busy = false;
            }
        }

        async UniTask SwitchFallbackAsync(CancellationToken ct)
        {
            if (_current != null && _current.IsLoaded)
            {
                await UnloadLeaseSafeAsync(_current);
                _current = null;
            }

            _current = await _resources.LoadSceneAsync(_fallbackAddress, ct);
        }

        async UniTask UnloadLeaseSafeAsync(SceneLease lease)
        {
            if (lease == null || !lease.IsLoaded)
                return;
            try
            {
                await _resources.UnloadSceneAsync(lease, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SceneService] unload: " + ex.Message);
            }
        }
    }
}

