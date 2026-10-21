using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using CloudIsle.Contracts;
using Cysharp.Threading.Tasks;
using Game.Framework.Lua;
using UnityEngine;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 正式 LuaLoader 的 MonoBehaviour 宿主，供 Player / Launcher 复用。
    /// CLI：--m1-lua-host --m1-lua-report= --m1-lua-shot= --m1-lua-quit
    /// 调用路径：LuaHostBehaviour → LuaModulePreloader.TryLoadBuiltin → LuaLoader.Prepare/Start/Tick/Dispose。
    /// </summary>
    public sealed class LuaHostBehaviour : MonoBehaviour
    {
        const string CliFlag = "--m1-lua-host";

        LuaLoader _loader;
        string _reportPath = "";
        string _shotPath = "";
        bool _quitWhenDone;
        string _flowError = "";
        bool _finished;

        /// <summary>底层正式服务实例。</summary>
        public LuaLoader Loader => _loader;

        /// <summary>流程是否结束。</summary>
        public bool Finished => _finished;

        /// <summary>流程错误。</summary>
        public string FlowError => _flowError;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoBootFromCli()
        {
            if (!HasCliFlag(CliFlag))
                return;
            if (UnityEngine.Object.FindObjectOfType<LuaHostBehaviour>() != null)
                return;
            var go = new GameObject("LuaHostBehaviour");
            DontDestroyOnLoad(go);
            go.AddComponent<LuaHostBehaviour>();
        }

        void Start()
        {
            if (!HasCliFlag(CliFlag))
                return;
            RunFormalAsync().Forget();
        }

        /// <summary>公开入口：内置资源 Prepare→Start→异步推进→Dispose→写报告。</summary>
        public async UniTask RunFormalAsync(CancellationToken ct = default)
        {
            ParseCli();
            const string servicePath =
                "LuaHostBehaviour>LuaModulePreloader.TryLoadBuiltin>LuaLoader.PrepareAsync>StartEntryAsync>AdvanceAsync>Tick>Dispose";
            _loader = new LuaLoader();
            bool pass = false;
            try
            {
                if (!LuaModulePreloader.TryLoadBuiltin(
                        out LuaIndex index,
                        out Dictionary<string, byte[]> modules,
                        out byte[] config))
                {
                    throw new InvalidOperationException("builtin Lua assets missing");
                }

                await _loader.PrepareAsync(index, modules, config, ct);
                var ctx = new BootContext
                {
                    Versions = new VersionManifest
                    {
                        ResVersion = 100,
                        ConfigVersion = 100,
                        ProtoVersion = BridgeApi.RequiredProtoVersion,
                        PackageVersion = "100"
                    }
                };
                await _loader.StartEntryAsync(ctx, "boot", ct);
                _loader.Tick();
                if (!_loader.IsReady)
                    throw new InvalidOperationException("EntryReady 未置位");
                if (_loader.EntryInvocations != 1)
                    throw new InvalidOperationException("entryInvocations=" + _loader.EntryInvocations);
                if (!_loader.CancelRegistered || _loader.AsyncFrames < 1)
                    throw new InvalidOperationException("async producers not advanced");

                pass = true;
                // 覆盖场景旧 Hotfix 等待文案，便于实机截图证明新宿主。
                ShowOverlayStatus("[LuaLoader] EntryReady " + _loader.EntrySummary);
                if (!string.IsNullOrEmpty(_shotPath))
                {
                    string dir = Path.GetDirectoryName(_shotPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    await UniTask.DelayFrame(1);
                    ScreenCapture.CaptureScreenshot(_shotPath);
                    await UniTask.DelayFrame(3);
                }
            }
            catch (Exception ex)
            {
                _flowError = ex.Message;
                pass = false;
            }
            finally
            {
                string disposeErrorBefore = _loader?.DisposeError ?? "";
                int entryInv = _loader?.EntryInvocations ?? 0;
                int asyncFrames = _loader?.AsyncFrames ?? 0;
                int cancelWait = _loader?.CancelWaitFrames ?? 0;
                bool cancelReg = _loader != null && _loader.CancelRegistered;
                string liveId = _loader?.LiveProducerId ?? "";
                string summary = _loader?.EntrySummary ?? "";
                bool wasReady = _loader != null && _loader.IsReady;
                bool entryExecutedBeforeDispose = BridgeApi.EntryExecuted;

                if (_loader != null && _loader.State != LuaLoader.HostState.Disposed)
                    _loader.Dispose();

                WriteReport(
                    pass,
                    servicePath,
                    wasReady,
                    entryExecutedBeforeDispose,
                    entryInv,
                    summary,
                    asyncFrames,
                    cancelReg,
                    cancelWait,
                    liveId,
                    disposeErrorBefore);

                _finished = true;
                if (_quitWhenDone)
                {
                    await UniTask.DelayFrame(1);
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit(pass && string.IsNullOrEmpty(_loader?.DisposeError) ? 0 : 4);
#endif
                }
            }
        }

        void ParseCli()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--m1-lua-report=", StringComparison.OrdinalIgnoreCase))
                    _reportPath = arg.Substring("--m1-lua-report=".Length);
                else if (arg.StartsWith("--m1-lua-shot=", StringComparison.OrdinalIgnoreCase))
                    _shotPath = arg.Substring("--m1-lua-shot=".Length);
                else if (string.Equals(arg, "--m1-lua-quit", StringComparison.OrdinalIgnoreCase))
                    _quitWhenDone = true;
            }

            if (string.IsNullOrEmpty(_reportPath))
            {
                _reportPath = Path.GetFullPath(Path.Combine(
                    Application.dataPath, "..",
                    "Docs/Acceptance/M1/20261020-xLua/StageB/Review/player-lua-loader-host.txt"));
            }
        }

        void WriteReport(
            bool pass,
            string servicePath,
            bool entryReadyBeforeDispose,
            bool entryExecutedBeforeDispose,
            int entryInvocations,
            string entrySummary,
            int asyncFrames,
            bool cancelRegistered,
            int cancelWaitFrames,
            string liveProducer,
            string disposeErrorBefore)
        {
            var sb = new StringBuilder(640);
            sb.Append("pass=").Append(pass && string.IsNullOrEmpty(_loader?.DisposeError)).Append('\n');
            sb.Append("error=").Append(_flowError ?? "").Append('\n');
            sb.Append("servicePath=").Append(servicePath).Append('\n');
            sb.Append("loaderType=").Append(nameof(LuaLoader)).Append('\n');
            sb.Append("state=").Append(_loader != null ? _loader.State.ToString() : "null").Append('\n');
            sb.Append("entryReadyBeforeDispose=").Append(entryReadyBeforeDispose).Append('\n');
            sb.Append("entry=").Append(entrySummary).Append('\n');
            sb.Append("entryInvocations=").Append(entryInvocations).Append('\n');
            // Dispose 现在会回收面板并清除活动标记，历史执行状态必须在关闭前取快照。
            sb.Append("entryExecuted=").Append(entryExecutedBeforeDispose).Append('\n');
            sb.Append("entryExecutedAfterDispose=").Append(BridgeApi.EntryExecuted).Append('\n');
            sb.Append("asyncFrames=").Append(asyncFrames).Append('\n');
            sb.Append("cancelRegistered=").Append(cancelRegistered).Append('\n');
            sb.Append("cancelWaitFrames=").Append(cancelWaitFrames).Append('\n');
            sb.Append("asyncBeforeDispose=").Append(_loader?.AsyncBeforeDispose ?? 0).Append('\n');
            sb.Append("asyncAfterDispose=").Append(_loader?.AsyncAfterDispose ?? 0).Append('\n');
            sb.Append("liveSubscriptions=").Append(AsyncLease.LiveSubscriptions).Append('\n');
            sb.Append("liveProducer=").Append(liveProducer).Append('\n');
            sb.Append("postDisposeLiveId=").Append(_loader?.PostDisposeLiveId ?? "").Append('\n');
            sb.Append("disposeError=").Append(_loader?.DisposeError ?? disposeErrorBefore ?? "").Append('\n');
            sb.Append("sameProducerAfterDispose=").Append(
                !string.IsNullOrEmpty(liveProducer)
                && string.Equals(liveProducer, _loader?.PostDisposeLiveId, StringComparison.Ordinal)).Append('\n');
            sb.Append("appVersion=").Append(Application.version).Append('\n');
            sb.Append("bridgeContract=").Append(BridgeApi.BridgeContractValue).Append('\n');
            sb.Append("protoVersion=").Append(BridgeApi.RequiredProtoVersion).Append('\n');
            sb.Append("gcForced=0\n");
            sb.Append("ENABLE_IL2CPP=")
#if ENABLE_IL2CPP
                .Append('1')
#else
                .Append('0')
#endif
                .Append('\n');

            string dir = Path.GetDirectoryName(_reportPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_reportPath, sb.ToString(), new UTF8Encoding(false));
        }

        static void ShowOverlayStatus(string text)
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Text>())
            {
                if (t != null && t.text != null && t.text.IndexOf("Hotfix", StringComparison.Ordinal) >= 0)
                    t.text = text;
            }

            var go = new GameObject("LuaLoaderStatusOverlay");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            go.AddComponent<UnityEngine.UI.CanvasScaler>();
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var rect = labelGo.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(1100, 80);
            var label = labelGo.AddComponent<UnityEngine.UI.Text>();
            label.font = PatchProgressView.ResolveUiFont();
            label.fontSize = 26;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = text;
        }

        static bool HasCliFlag(string flag)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
