using System;
using Game.Framework.Lua;

namespace Game.Framework.Gameplay
{
    /// <summary>
    /// Lua 可见玩法门面（m2-bridge-0.4）。命令/同步、只读状态与显示时钟；无 Bind/MarkReady。
    /// </summary>
    public static class LuaGameplayFacade
    {
        public static CommandObservation SendCommand(string key, string intentJson, LuaCallback done)
        {
            if (done == null)
                throw new ArgumentNullException(nameof(done));

            if (GameplayBridgeBinding.TryGetRuntime(
                    out IMainThreadDispatcher dispatcher,
                    out GameplaySessionContext context,
                    out IGameplayCommandPort port,
                    out GameplayCallbackRegistry registry,
                    out string reason))
            {
                return SendOnMain(key, intentJson, done, dispatcher, context, port, registry);
            }

            return CompleteNotReady(done, reason);
        }

        /// <summary>
        /// 只读会话/写门状态 JSON：authReady、writeFrozen、busy、writable、reason。
        /// 不导出 Bind/Unbind/MarkReady。
        /// </summary>
        public static string GetStatusJson()
        {
            return GameplayBridgeBinding.BuildStatusJson();
        }

        /// <summary>是否可发新写意图；reason 说明挡写原因。</summary>
        public static bool IsWritable(out string reason)
        {
            return GameplayBridgeBinding.TryIsWritable(out reason);
        }

        /// <summary>显示估算 now（规范十进制毫秒串）；未同步返回空串。</summary>
        public static string EstimateNowMs()
        {
            return GameplayBridgeBinding.EstimateNowMs();
        }

        /// <summary>
        /// 显示剩余毫秒的规范十进制字符串（Lua int64 面，&gt;2^53 仍精确）；未同步/已过期为 "0"。
        /// 内部时钟仍用 long 计算。
        /// </summary>
        public static string RemainingMs(string deadlineMs)
        {
            return GameplayInt64.Format(GameplayBridgeBinding.RemainingMs(deadlineMs));
        }

        /// <summary>显示时钟是否已校时。</summary>
        public static bool IsClockSynchronized()
        {
            return GameplayBridgeBinding.IsClockSynchronized();
        }

        public static CommandObservation SyncState(LuaCallback done)
        {
            if (done == null)
                throw new ArgumentNullException(nameof(done));

            if (GameplayBridgeBinding.TryGetRuntime(
                    out IMainThreadDispatcher dispatcher,
                    out GameplaySessionContext context,
                    out IGameplayCommandPort port,
                    out GameplayCallbackRegistry registry,
                    out string reason))
            {
                SessionStamp stamp = context.Capture();
                long epoch = context.Epoch;
                CommandObservation obs = registry.RegisterCompletion(stamp, epoch, done);
                try
                {
                    port.SyncState((code, payload) =>
                    {
                        dispatcher.Post(() =>
                        {
                            if (!context.Matches(stamp) || context.Epoch != epoch)
                                return;
                            registry.PostCompletion(obs.Id, epoch, code, payload ?? "");
                        });
                    });
                }
                catch (Exception ex)
                {
                    ScheduleDeliver(
                        dispatcher, registry, obs, epoch, -2,
                        GameplayCompletionFormatter.FormatLocal(-2, ex.GetType().Name + ":" + ex.Message));
                }

                return obs;
            }

            return CompleteNotReady(done, reason);
        }

        /// <summary>比较两个十进制 int64 字符串（不经 double / Lua number）。</summary>
        public static int CompareInt64Decimal(string left, string right)
        {
            return GameplayInt64.CompareDecimal(left, right);
        }

        /// <summary>探测常量 &gt;2^53 的十进制字符串（不接收 Lua numeric long）。</summary>
        public static string BigInt64Decimal()
        {
            return GameplayInt64.Format(GameplayInt64.BigBeyondDouble);
        }

        /// <summary>long.MaxValue 十进制探测串。</summary>
        public static string MaxInt64Decimal()
        {
            return GameplayInt64.Format(long.MaxValue);
        }

        static CommandObservation SendOnMain(
            string key,
            string intentJson,
            LuaCallback done,
            IMainThreadDispatcher dispatcher,
            GameplaySessionContext context,
            IGameplayCommandPort port,
            GameplayCallbackRegistry registry)
        {
            SessionStamp stamp = context.Capture();
            long epoch = context.Epoch;
            CommandObservation obs = registry.RegisterCompletion(stamp, epoch, done);

            if (!GameplayCommandAllowlist.IsAllowed(key))
            {
                ScheduleDeliver(
                    dispatcher, registry, obs, epoch,
                    GameplayBridgeBinding.CodeUnknownCommand,
                    GameplayCompletionFormatter.FormatLocal(
                        GameplayBridgeBinding.CodeUnknownCommand, "unknown-command:" + key));
                return obs;
            }

            if (!GameplayDtoCodec.TryValidateIntent(key, intentJson, out CommandIntent intent, out string err))
            {
                ScheduleDeliver(
                    dispatcher, registry, obs, epoch,
                    GameplayBridgeBinding.CodeInvalidDto,
                    GameplayCompletionFormatter.FormatLocal(
                        GameplayBridgeBinding.CodeInvalidDto, err ?? "invalid-dto"));
                return obs;
            }

            // 写命令：忙/冻写/未 Ready 时零发送（读 Sync 另走 SyncState）
            if (GameplayCommandAllowlist.TryGet(key, out CommandDescriptor desc) && desc.IsWriteCommand)
            {
                if (!GameplayBridgeBinding.TryIsWritable(out string blockReason))
                {
                    ScheduleDeliver(
                        dispatcher, registry, obs, epoch,
                        GameplayBridgeBinding.CodeNotReady,
                        GameplayCompletionFormatter.FormatLocal(
                            GameplayBridgeBinding.CodeNotReady, blockReason ?? "not-writable"));
                    return obs;
                }
            }

            try
            {
                port.Send(intent, (code, payload) =>
                {
                    dispatcher.Post(() =>
                    {
                        if (!context.Matches(stamp) || context.Epoch != epoch)
                            return;
                        registry.PostCompletion(obs.Id, epoch, code, payload ?? "");
                    });
                });
            }
            catch (Exception ex)
            {
                ScheduleDeliver(
                    dispatcher, registry, obs, epoch, -2,
                    GameplayCompletionFormatter.FormatLocal(-2, ex.GetType().Name + ":" + ex.Message));
            }

            return obs;
        }

        static CommandObservation CompleteNotReady(LuaCallback done, string reason)
        {
            string payload = GameplayCompletionFormatter.FormatLocal(
                GameplayBridgeBinding.CodeNotReady, reason ?? "not-ready");

            if (!GameplayBridgeBinding.TryGetHost(
                    out IMainThreadDispatcher dispatcher,
                    out GameplaySessionContext context,
                    out GameplayCallbackRegistry registry))
            {
                // 无 dispatcher/host：明确 Rejected，不触 Lua
                return CommandObservation.CreateRejected(GameplayBridgeBinding.CodeNotReady, payload);
            }

            if (dispatcher.IsMainThread)
            {
                // 配置主线程但无 port（或其它主线程 NotReady）
                CommandObservation obs = registry.RegisterCompletion(context.Capture(), context.Epoch, done);
                ScheduleDeliver(dispatcher, registry, obs, context.Epoch, GameplayBridgeBinding.CodeNotReady, payload);
                return obs;
            }

            // worker 有 host：Post 主线程再 Retain/登记/回调；此处不 Retain
            CommandObservation deferred = CommandObservation.CreateDeferred(done);
            GameplaySessionContext ctx = context;
            GameplayCallbackRegistry reg = registry;
            dispatcher.Post(() =>
            {
                if (!GameplayBridgeBinding.TryGetHost(out _, out GameplaySessionContext liveCtx, out GameplayCallbackRegistry liveReg))
                    return;
                if (!ReferenceEquals(liveReg, reg) || !ReferenceEquals(liveCtx, ctx))
                    return;
                reg.AdoptDeferred(deferred, ctx.Capture(), ctx.Epoch);
                reg.PostCompletion(deferred.Id, ctx.Epoch, GameplayBridgeBinding.CodeNotReady, payload);
            });
            return deferred;
        }

        static void ScheduleDeliver(
            IMainThreadDispatcher dispatcher,
            GameplayCallbackRegistry registry,
            CommandObservation obs,
            long epoch,
            int code,
            string payload)
        {
            if (obs == null || registry == null || dispatcher == null)
                return;
            if (dispatcher.IsMainThread)
            {
                registry.PostCompletion(obs.Id, epoch, code, payload);
                return;
            }

            dispatcher.Post(() => registry.PostCompletion(obs.Id, epoch, code, payload));
        }
    }
}
