using System;
using System.Collections;
using System.Text;
using CloudIsle.Net;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Demo.Greybox
{
    /// <summary>
    /// 灰盒入口：联网登录 + 世界 + GuideUI 逐步验收 T01~T13。
    /// </summary>
    public sealed class GreyboxBootstrap : MonoBehaviour
    {
        [SerializeField] string _host = "127.0.0.1";
        [SerializeField] int _port = 17777;
        [SerializeField] string _user = "greybox_tester";
        [SerializeField] string _pass = "poc";

        GreyboxNetClient _net;
        GreyboxPlayerMirror _mirror;
        GreyboxWorldView _world;
        Canvas _canvas;
        Text _guideText;
        Text _hudText;
        Text _toastText;
        Button _btnLogin;
        Button _btnSubmit;

        int _stepIndex;
        float _t02Time = -1f;
        float _t10Time = -1f;
        bool _busy;
        string _toast = "";
        Coroutine _routine;

        static readonly StepDef[] Steps =
        {
            new StepDef("T01", "确认场景与引导 UI", "观察 Game 视图，无需点击", "—", "Plot_0", StepKind.Assert),
            new StepDef("T02", "连接并登录", "点击 HUD「登录」按钮", "屏幕右上 Btn_Login", "Btn_Login", StepKind.HudLogin),
            new StepDef("T03", "播种第 1 块地", "鼠标左键点击", "场景中央偏左 · Plot_0（灰色空地）", "Plot_0", StepKind.Plant),
            new StepDef("T04", "等待作物成熟", "等待倒计时（勿点其他地块）", "—", "Plot_0", StepKind.WaitRipe0),
            new StepDef("T05", "收获", "鼠标左键点击", "Plot_0（绿色成熟）", "Plot_0", StepKind.Harvest),
            new StepDef("T06a", "播种 Plot_1", "鼠标左键点击", "Plot_1", "Plot_1", StepKind.Plant),
            new StepDef("T06b", "播种 Plot_2", "鼠标左键点击", "Plot_2", "Plot_2", StepKind.Plant),
            new StepDef("T06c", "播种 Plot_3", "鼠标左键点击", "Plot_3", "Plot_3", StepKind.Plant),
            new StepDef("T07a", "等待并收获 Plot_1", "等待成熟后左键点击", "Plot_1", "Plot_1", StepKind.WaitHarvest),
            new StepDef("T07b", "等待并收获 Plot_2", "等待成熟后左键点击", "Plot_2", "Plot_2", StepKind.WaitHarvest),
            new StepDef("T07c", "等待并收获 Plot_3", "等待成熟后左键点击", "Plot_3", "Plot_3", StepKind.WaitHarvest),
            new StepDef("T08", "开始磨坊加工", "鼠标左键点击", "场景右侧蓝色 Facility_Mill", "Facility_Mill", StepKind.CraftStart),
            new StepDef("T09", "等待完成并领取", "完成后左键点击磨坊", "Facility_Mill", "Facility_Mill", StepKind.CraftCollect),
            new StepDef("T10", "交付订单", "左键点订单板 或 点「交付」", "OrderBoard / Btn_SubmitOrder", "OrderBoard", StepKind.OrderSubmit),
            new StepDef("T11", "重复交单应失败", "再次点击订单板或交付", "OrderBoard", "OrderBoard", StepKind.OrderFail),
            new StepDef("T12", "材料不足加工应失败", "左键点磨坊", "Facility_Mill", "Facility_Mill", StepKind.CraftFail),
            new StepDef("T13", "闭环计时", "自动判定", "—", "", StepKind.AssertDone),
        };

        enum StepKind
        {
            Assert, HudLogin, Plant, WaitRipe0, Harvest, WaitHarvest,
            CraftStart, CraftCollect, OrderSubmit, OrderFail, CraftFail, AssertDone
        }

        struct StepDef
        {
            public string Id, Title, How, Where, Target;
            public StepKind Kind;
            public StepDef(string id, string title, string how, string where, string target, StepKind kind)
            {
                Id = id; Title = title; How = how; Where = where; Target = target; Kind = kind;
            }
        }

        void Start()
        {
            _mirror = new GreyboxPlayerMirror();
            _net = new GreyboxNetClient();
            _world = gameObject.AddComponent<GreyboxWorldView>();
            _world.Build(_mirror);
            BuildUi();
            _stepIndex = 0;
            // T01 auto-pass after 0.5s if world exists
            _routine = StartCoroutine(AutoPassT01());
            RefreshUi();
        }

        IEnumerator AutoPassT01()
        {
            yield return new WaitForSeconds(0.3f);
            if (_stepIndex == 0 && GameObject.Find("Plot_0") != null && GameObject.Find("Facility_Mill") != null
                && GameObject.Find("OrderBoard") != null && GameObject.Find("GreyboxWorldMarker") != null)
            {
                Advance("T01 场景物体齐全");
            }
        }

        void Update()
        {
            if (_net != null && _net.TryTakeSnapshot(out var snap))
            {
                _mirror.ApplySnapshot(snap);
                _world.RefreshVisuals();
                RefreshUi();
            }

            var step = Current;
            if (!_busy && step.Kind == StepKind.WaitRipe0)
            {
                RefreshRipeLocal();
                if (_mirror.PlotStates[0] == PlotVisualState.Ripe)
                    Advance("Plot_0 Ripe");
            }

            if (Input.GetMouseButtonDown(0))
                HandleClick();

            RefreshUi();
        }

        void RefreshRipeLocal()
        {
            long now = _mirror.NowServerMs();
            for (int i = 0; i < _mirror.PlotStates.Length; i++)
            {
                if (_mirror.PlotStates[i] == PlotVisualState.Growing && now >= _mirror.PlotReadyMs[i])
                    _mirror.PlotStates[i] = PlotVisualState.Ripe;
            }
            if (_mirror.SlotState == SlotVisualState.Working && now >= _mirror.SlotFinishMs)
                _mirror.SlotState = SlotVisualState.Done;
            _world.RefreshVisuals();
        }

        void HandleClick()
        {
            var step = Current;
            Vector3 mp = Input.mousePosition;
            Debug.Log($"[GreyboxClick] mouseDown screen=({mp.x:0},{mp.y:0}) step={step.Id} busy={_busy}");

            if (_busy)
            {
                Debug.Log("[GreyboxClick] ignore: busy");
                return;
            }
            if (step.Kind == StepKind.HudLogin || step.Kind == StepKind.Assert || step.Kind == StepKind.AssertDone
                || step.Kind == StepKind.WaitRipe0)
            {
                Debug.Log($"[GreyboxClick] ignore: stepKind={step.Kind}（请用登录按钮或等待）");
                return;
            }

            var cam = Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[GreyboxClick] Camera.main 为空");
                return;
            }
            var ray = cam.ScreenPointToRay(mp);
            if (!Physics.Raycast(ray, out var hit, 200f))
            {
                Debug.Log("[GreyboxClick] raycast 未命中任何 Collider");
                return;
            }
            Debug.Log($"[GreyboxClick] hit={hit.collider.name}");

            var click = hit.collider.GetComponent<GreyboxClickable>();
            if (click == null)
            {
                Debug.Log($"[GreyboxClick] {hit.collider.name} 无 GreyboxClickable（可能点到了 Ground）");
                return;
            }
            Debug.Log($"[GreyboxClick] 地块/目标已点击 target={click.TargetId} expect={step.Target}");

            if (!string.IsNullOrEmpty(step.Target) && click.TargetId != step.Target
                && !(step.Kind == StepKind.OrderSubmit && click.TargetId == "OrderBoard"))
            {
                Toast($"请先完成 {step.Id}：请点击 {step.Target}");
                Debug.LogWarning($"[GreyboxClick] 目标不符 got={click.TargetId} need={step.Target}");
                return;
            }

            Toast($"已点 {click.TargetId}，发包中…");
            StartCoroutine(RunStepAction(step, click.TargetId));
        }

        IEnumerator RunStepAction(StepDef step, string targetId)
        {
            _busy = true;
            // 不可在 try/catch 内 yield；用 IsFaulted 代替 catch
            try
            {
                if (!_net.IsConnected && step.Kind != StepKind.HudLogin)
                {
                    Toast("连接断开，请先登录");
                    yield break;
                }

                switch (step.Kind)
                {
                    case StepKind.Plant:
                    {
                        int plotId = int.Parse(targetId.Substring(5));
                        var before = _mirror.SeedCount;
                        Debug.Log($"[Greybox] Plant 开始 plot={plotId}");
                        var task = _net.PlantAsync(plotId, GreyboxIds.WheatSeedId);
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { Toast("Plant 异常: " + task.Exception?.GetBaseException().Message); yield break; }
                        var r = task.Result;
                        Debug.Log($"[Greybox] Plant 回包 code={r.Code} seeds={r.SeedCount}");
                        if (r.Code != ErrorCodes.Ok) { Toast($"Plant 失败 code={r.Code}"); yield break; }
                        _mirror.ApplyPlant(r);
                        if (_mirror.SeedCount != before - 1) { Toast("种子未 -1"); yield break; }
                        Advance($"{step.Id} Plant OK seeds={_mirror.SeedCount}");
                        break;
                    }
                    case StepKind.Harvest:
                    case StepKind.WaitHarvest:
                    {
                        RefreshRipeLocal();
                        int plotId = int.Parse(targetId.Substring(5));
                        if (_mirror.PlotStates[plotId] != PlotVisualState.Ripe)
                        {
                            Toast("尚未成熟，请等待");
                            yield break;
                        }
                        int before = _mirror.WheatCount;
                        var task = _net.HarvestAsync(plotId);
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { Toast("Harvest 异常: " + task.Exception?.GetBaseException().Message); yield break; }
                        var r = task.Result;
                        if (r.Code != ErrorCodes.Ok) { Toast($"Harvest 失败 code={r.Code}"); yield break; }
                        _mirror.ApplyHarvest(r);
                        if (_mirror.WheatCount < before + GreyboxIds.HarvestYield)
                        { Toast("小麦增量不足"); yield break; }
                        Advance($"{step.Id} Harvest OK wheat={_mirror.WheatCount}");
                        break;
                    }
                    case StepKind.CraftStart:
                    {
                        var task = _net.CraftStartAsync();
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { Toast("CraftStart 异常: " + task.Exception?.GetBaseException().Message); yield break; }
                        var r = task.Result;
                        if (r.Code != ErrorCodes.Ok) { Toast($"CraftStart 失败 code={r.Code}"); yield break; }
                        _mirror.ApplyCraftStart(r);
                        Advance($"T08 CraftStart OK wheat={_mirror.WheatCount}");
                        break;
                    }
                    case StepKind.CraftCollect:
                    {
                        RefreshRipeLocal();
                        if (_mirror.SlotState != SlotVisualState.Done)
                        {
                            Toast("加工未完成，请等待");
                            yield break;
                        }
                        int before = _mirror.FlourCount;
                        var task = _net.CraftCollectAsync();
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { Toast("Collect 异常: " + task.Exception?.GetBaseException().Message); yield break; }
                        var r = task.Result;
                        if (r.Code != ErrorCodes.Ok) { Toast($"Collect 失败 code={r.Code}"); yield break; }
                        _mirror.ApplyCraftCollect(r);
                        if (_mirror.FlourCount != before + 1) { Toast("面粉未 +1"); yield break; }
                        Advance($"T09 Collect OK flour={_mirror.FlourCount}");
                        break;
                    }
                    case StepKind.OrderSubmit:
                    {
                        var task = _net.OrderSubmitAsync();
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { Toast("Order 异常: " + task.Exception?.GetBaseException().Message); yield break; }
                        var r = task.Result;
                        if (r.Code != ErrorCodes.Ok) { Toast($"Order 失败 code={r.Code}"); yield break; }
                        _mirror.ApplyOrder(r);
                        if (_mirror.Gold != 120 || _mirror.Exp != 10)
                        { Toast($"奖励不符 gold={_mirror.Gold} exp={_mirror.Exp}"); yield break; }
                        _t10Time = Time.realtimeSinceStartup;
                        Advance("T10 Order OK gold=120 exp=10");
                        break;
                    }
                    case StepKind.OrderFail:
                    {
                        long goldBefore = _mirror.Gold;
                        var task = _net.OrderSubmitAsync();
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { Toast("OrderFail 异常: " + task.Exception?.GetBaseException().Message); yield break; }
                        var r = task.Result;
                        if (r.Code == ErrorCodes.Ok) { Toast("重复交单不应成功"); yield break; }
                        if (_mirror.Gold != goldBefore) { Toast("金币被错误修改"); yield break; }
                        Advance($"T11 重复交单失败 code={r.Code}");
                        break;
                    }
                    case StepKind.CraftFail:
                    {
                        while (_mirror.WheatCount >= GreyboxIds.RecipeWheatCost)
                        {
                            var burnStart = _net.CraftStartAsync();
                            while (!burnStart.IsCompleted) yield return null;
                            if (burnStart.IsFaulted) { Toast("T12 预消耗异常"); yield break; }
                            var bs = burnStart.Result;
                            if (bs.Code != ErrorCodes.Ok) { Toast($"T12 预消耗 CraftStart 失败 code={bs.Code}"); yield break; }
                            _mirror.ApplyCraftStart(bs);
                            while (_mirror.SlotState != SlotVisualState.Done)
                            {
                                yield return new WaitForSeconds(0.5f);
                                var snapTask = _net.RefreshSnapshotAsync();
                                while (!snapTask.IsCompleted) yield return null;
                                if (!snapTask.IsFaulted)
                                    _mirror.ApplySnapshot(snapTask.Result);
                                else
                                    RefreshRipeLocal();
                            }
                            var burnCollect = _net.CraftCollectAsync();
                            while (!burnCollect.IsCompleted) yield return null;
                            if (burnCollect.IsFaulted) { Toast("T12 Collect 异常"); yield break; }
                            var bc = burnCollect.Result;
                            if (bc.Code != ErrorCodes.Ok) { Toast($"T12 预消耗 Collect 失败 code={bc.Code}"); yield break; }
                            _mirror.ApplyCraftCollect(bc);
                        }

                        int wheatBefore = _mirror.WheatCount;
                        var task = _net.CraftStartAsync();
                        while (!task.IsCompleted) yield return null;
                        if (task.IsFaulted) { Toast("T12 异常"); yield break; }
                        var r = task.Result;
                        if (r.Code == ErrorCodes.Ok) { Toast("材料不足时应失败"); yield break; }
                        _mirror.WheatCount = wheatBefore;
                        Advance($"T12 CraftFail code={r.Code} wheat={_mirror.WheatCount}");
                        TryFinishT13();
                        break;
                    }
                }
            }
            finally
            {
                _busy = false;
                _world.RefreshVisuals();
                RefreshUi();
            }
        }

        public void OnClickLogin()
        {
            if (Current.Kind != StepKind.HudLogin || _busy) return;
            StartCoroutine(DoLogin());
        }

        public void OnClickSubmit()
        {
            if (_busy) return;
            if (Current.Kind == StepKind.OrderSubmit || Current.Kind == StepKind.OrderFail)
                StartCoroutine(RunStepAction(Current, "OrderBoard"));
            else
                Toast($"请先完成 {Current.Id}");
        }

        IEnumerator DoLogin()
        {
            _busy = true;
            try
            {
                Toast($"连接 {_host}:{_port}…");
                var connect = _net.ConnectAsync(_host, _port);
                while (!connect.IsCompleted)
                {
                    Toast(_net.DebugPhase);
                    yield return null;
                }
                if (connect.IsFaulted)
                {
                    Toast("连接失败: " + connect.Exception?.GetBaseException().Message);
                    yield break;
                }
                Toast("已连接，等待 LoginResp…");
                var login = _net.LoginAsync(_user, _pass);
                while (!login.IsCompleted)
                {
                    Toast(_net.DebugPhase);
                    yield return null;
                }
                if (login.IsFaulted)
                {
                    Toast("登录异常: " + login.Exception?.GetBaseException().Message);
                    Debug.LogError(login.Exception);
                    yield break;
                }
                var resp = login.Result;
                if (resp.Code != ErrorCodes.Ok || resp.Data == null)
                {
                    Toast($"登录失败 code={resp.Code}");
                    yield break;
                }
                _mirror.ApplyLogin(resp);
                // 登录后服务端会推 Snapshot(seq=0)；再 pump 一次收齐
                var pump = _net.PumpAsync(0.3f);
                while (!pump.IsCompleted) yield return null;
                if (_net.TryTakeSnapshot(out var snap))
                    _mirror.ApplySnapshot(snap);
                if (_mirror.PlayerId <= 0 || _mirror.SeedCount != 9 || _mirror.Gold != 100)
                {
                    Toast($"初始态不符 id={_mirror.PlayerId} seeds={_mirror.SeedCount} gold={_mirror.Gold}");
                    yield break;
                }
                _t02Time = Time.realtimeSinceStartup;
                Advance("T02 Login OK");
            }
            finally
            {
                _busy = false;
                _world.RefreshVisuals();
                RefreshUi();
            }
        }

        void TryFinishT13()
        {
            if (_stepIndex >= Steps.Length) return;
            if (Current.Kind != StepKind.AssertDone && _stepIndex < Steps.Length - 1) return;
            // move to T13 if needed
            while (_stepIndex < Steps.Length - 1)
                _stepIndex++;
            float dur = (_t10Time > 0 && _t02Time > 0) ? (_t10Time - _t02Time) : 9999f;
            if (dur <= 300f)
                Advance($"T13 PASS 闭环 {dur:0.0}s");
            else
                Toast($"T13 失败：闭环 {dur:0.0}s > 300s");
        }

        void Advance(string note)
        {
            Debug.Log("[Greybox] " + note);
            Toast(note);
            if (_stepIndex < Steps.Length - 1)
                _stepIndex++;
            else
                _stepIndex = Steps.Length; // done
            _world.SetHighlight(Current.Target);
            RefreshUi();
            if (Current.Kind == StepKind.AssertDone)
                TryFinishT13();
        }

        StepDef Current =>
            _stepIndex >= 0 && _stepIndex < Steps.Length
                ? Steps[_stepIndex]
                : new StepDef("DONE", "全部完成", "可停止 Play", "—", "", StepKind.AssertDone);

        void Toast(string msg)
        {
            _toast = msg;
            if (_toastText != null) _toastText.text = msg;
        }

        void RefreshUi()
        {
            var s = Current;
            _world?.SetHighlight(s.Target);

            if (_guideText != null)
            {
                float elapsed = _t02Time > 0 ? Time.realtimeSinceStartup - _t02Time : 0f;
                var sb = new StringBuilder();
                sb.AppendLine($"<b>{s.Id}</b>  {s.Title}");
                sb.AppendLine($"操作：{s.How}");
                sb.AppendLine($"位置：{s.Where}");
                sb.AppendLine($"通过条件：见操作说明 {s.Id}");
                sb.AppendLine($"进度：{Mathf.Min(_stepIndex + 1, Steps.Length)}/{Steps.Length}  用时 {elapsed:0}s");
                if (!string.IsNullOrEmpty(_toast)) sb.AppendLine($"提示：{_toast}");
                _guideText.text = sb.ToString();
            }

            if (_hudText != null)
            {
                float remainPlot = 0, remainCraft = 0;
                long now = _mirror.NowServerMs();
                if (_mirror.PlotStates[0] == PlotVisualState.Growing)
                    remainPlot = Mathf.Max(0, (_mirror.PlotReadyMs[0] - now) / 1000f);
                if (_mirror.SlotState == SlotVisualState.Working)
                    remainCraft = Mathf.Max(0, (_mirror.SlotFinishMs - now) / 1000f);
                _hudText.text =
                    $"playerId={_mirror.PlayerId}\n" +
                    $"种子={_mirror.SeedCount} 小麦={_mirror.WheatCount} 面粉={_mirror.FlourCount}\n" +
                    $"金币={_mirror.Gold} 经验={_mirror.Exp} 订单完成={_mirror.OrderCompleted}\n" +
                    $"地块0剩余={remainPlot:0.0}s 加工剩余={remainCraft:0.0}s\n" +
                    $"连接={(_net != null && _net.IsConnected)} {_host}:{_port}";
            }
        }

        void BuildUi()
        {
            var canvasGo = new GameObject("GreyboxCanvas");
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            _guideText = CreateText(canvasGo.transform, "GuidePanel", new Vector2(10, -10), new Vector2(520, 220),
                TextAnchor.UpperLeft, 18);
            _hudText = CreateText(canvasGo.transform, "HudPanel", new Vector2(-10, -10), new Vector2(360, 140),
                TextAnchor.UpperRight, 16);
            _toastText = CreateText(canvasGo.transform, "Toast", new Vector2(0, 20), new Vector2(700, 40),
                TextAnchor.LowerCenter, 18);

            _btnLogin = CreateButton(canvasGo.transform, "Btn_Login", "登录", new Vector2(-20, -160), () => OnClickLogin());
            _btnSubmit = CreateButton(canvasGo.transform, "Btn_SubmitOrder", "交付", new Vector2(-20, -220), () => OnClickSubmit());

            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }
        }

        static Text CreateText(Transform parent, string name, Vector2 anchored, Vector2 size, TextAnchor anchor, int fontSize)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor == TextAnchor.UpperLeft ? new Vector2(0, 1)
                : anchor == TextAnchor.UpperRight ? new Vector2(1, 1)
                : new Vector2(0.5f, 0);
            rt.pivot = rt.anchorMin;
            rt.anchoredPosition = anchored;
            rt.sizeDelta = size;
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.65f);
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8, 8);
            trt.offsetMax = new Vector2(-8, -8);
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (text.font == null) text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = fontSize;
            text.color = Color.white;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        static Button CreateButton(Transform parent, string name, string label, Vector2 anchored, Action onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = anchored;
            rt.sizeDelta = new Vector2(140, 44);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.55f, 0.3f, 0.95f);
            var btn = go.AddComponent<Button>();
            btn.onClick.AddListener(() => onClick());
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var trt = textGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                        ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.fontSize = 20;
            return btn;
        }

        void OnDestroy()
        {
            _net?.Dispose();
        }
    }
}
