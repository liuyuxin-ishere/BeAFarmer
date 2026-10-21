<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 原任务步骤、源码、命令、问题和实测结果完整保留；服务端、协议、资源与公共 C# 能力可按新边界复用，旧热更 DLL／补元数据链的完成记录不构成 xLua 验收。旧自动推进在 T42 部分实现阶段停止，T42 尚未验收；后续按新方案重新安排。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](../07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# M1 开发日志 · 5.2 公共能力与资源

记录方式对齐 `06_开发计划.md` 第 6.1 节：每项写成可勾选的文档、命令或方法。目录索引见 `M1_Logs.md`。

本文件对应 `06_M1_基础框架任务拆解.md` 第 5.2 节，任务 T09–T17。

## T09：实现 EventBus.Subscribe/Publish

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T09，依赖 T02
- 需求：R-C04
- 依据：拆解第 4.3 节 `EventBus` 签名；`04_技术方案与架构.md` 第 5.5 节的强类型事件；现有空类 `Assets/Framework/Core/EventBus.cs`
- 通过条件：订阅一次收到一次；`Dispose` 之后收不到；重复 `Dispose` 不抛异常
- 日期：2026-10-18

### 任务、流程与验证

这一步把 `BootContext.Events` 上的空壳做成可用的事件总线。界面、对象池、计时器和网络收发留在后面的任务。热更模块以后用它发通知，这样农场模块和界面可以互不引用对方的类。

两个方法按拆解 4.3：

```csharp
IDisposable Subscribe<TEvent>(Action<TEvent> handler)
void Publish<TEvent>(TEvent eventData)
```

`TEvent` 是事件自己的类型。发布一颗 `CropHarvestedEvent` 时，只有 `Subscribe<CropHarvestedEvent>` 注册的函数会收到它。结构体事件走泛型参数，调用过程中保持原类型，列表里存的是 `Action<TEvent>`。

`Subscribe` 返回的对象负责退订。调用一次 `Dispose`，这份订阅从列表移除。再对同一个对象 `Dispose`，方法直接返回，不抛异常。之后 `Publish` 不再调用这个处理函数。同一事件上还有别的订阅时，那些订阅继续收到。

“订阅一次收到一次”指：一个处理函数只 `Subscribe` 一次，`Publish` 一次，这个函数被调用一次，参数就是这次发布的数据。再 `Publish` 一次，它再被调用一次。同一次 `Publish` 里它只出现一次。

`EventBus` 在构造时记下当前线程。`Subscribe` 和 `Publish` 都要在这个线程上调用，否则抛 `InvalidOperationException`。游戏启动时在主线程 `new EventBus()`，因此允许调用的线程就是主线程。回调就在 `Publish` 里同步执行。把网络线程上的包送回主线程，是 T19 的队列，本步不实现那条队列。

发布开始时先复制当前处理函数列表，然后按订阅顺序调用这份副本。某个回调里退订自己，或再订阅新的函数，只影响下一次 `Publish`。这样遍历列表时从列表里删除，不会把后面的订阅跳过。

`handler` 为空引用时，`Subscribe` 抛 `ArgumentNullException`。事件数据允许是默认值，例如字段全是 0 的结构体。

`BootContext` 里 `Events` 的注释仍写着 T09 之前为空壳。实现后把这句改成已提供 `Subscribe` 和 `Publish`。

验证用 Unity Test Framework 1.6.0 的 Edit Mode 测试。测试程序集只进编辑器，引用 `Game.Framework`。命令行跑测试的参数见 [Test Framework 命令行](https://docs.unity3d.com/Packages/com.unity.test-framework@1.6/manual/reference-command-line.html)。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 订阅一次收到一次 | 对一个结构体事件 `Subscribe` 一次，`Publish` 两次 | 处理函数被调用两次；两次参数分别等于两次发布的值 | PASS。`SubscribeOnce_ReceivesEachPublish` |
| 退订 | `Dispose` 之后再 `Publish` | 该处理函数调用次数不再增加；同一事件的另一个订阅仍收到 | PASS。`Dispose_StopsOnlyThatSubscription` |
| 重复 Dispose | 对同一个退订句柄连续 `Dispose` 两次 | 第二次不抛异常 | PASS。`Dispose_Twice_DoesNotThrow` |
| 调用线程 | 在另一线程调用 `Publish` | 抛 `InvalidOperationException`；处理函数不被调用 | PASS。`Publish_OnAnotherThread_Throws` |
| Edit Mode | `Unity.exe -runTests -batchmode -projectPath <仓库> -testPlatform EditMode -assemblyNames Game.Framework.EditModeTests` | 退出码 0；上述用例失败数为 0 | PASS。已打开的编辑器里 Test Runner 结果为 Passed，pass=4，fail=0 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。编辑器路径沿用基线：`D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe`。

#### 1. 实现 `Assets/Framework/Core/EventBus.cs`

命名空间保持 `Game.Framework.Core`。类保持 `public sealed class EventBus`，供 `BootContext` 继续引用。

```csharp
/// <summary>
/// 按事件类型分发通知。Subscribe 与 Publish 都在构造时所在的线程上调用。
/// </summary>
public sealed class EventBus
{
    /// <summary>
    /// 注册处理函数。返回的对象 Dispose 后，该函数不再收到新的 Publish。
    /// </summary>
    /// <typeparam name="TEvent">事件类型。结构体和类都可以。</typeparam>
    /// <param name="handler">收到事件时同步调用的函数。空引用会抛异常。</param>
    /// <returns>只退订这一次注册的句柄。重复 Dispose 不抛异常。</returns>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)

    /// <summary>
    /// 按订阅顺序调用当前已注册的 TEvent 处理函数。
    /// </summary>
    /// <typeparam name="TEvent">要发布的事件类型。</typeparam>
    /// <param name="eventData">传给每个处理函数的数据。</param>
    public void Publish<TEvent>(TEvent eventData)
}
```

内部用 `Dictionary<Type, object>` 保存每种事件的 `List<Action<TEvent>>`。列表里的元素类型就是 `Action<TEvent>`，由一个私有泛型通道类持有。`Publish` 调用前把列表复制到数组。退订句柄记下总线、事件类型和处理函数；第一次 `Dispose` 从列表移除一次，并把句柄内部引用清空，第二次看到已清空就返回。

- [x] 已写入磁盘

#### 2. 更新 `BootContext` 注释

把 `Assets/Framework/Boot/BootContext.cs` 里 `Events` 的说明改为：事件总线，`Subscribe` 与 `Publish` 已在 T09 实现。字段类型仍是 `EventBus`。

- [x] 已写入磁盘

#### 3. 添加 Edit Mode 测试

新建 `Assets/Tests/EditMode/Game.Framework.EditModeTests.asmdef`，内容对齐 Test Framework 1.6 自带示例：

- `name`：`Game.Framework.EditModeTests`
- `references`：`Game.Framework`、`UnityEngine.TestRunner`、`UnityEditor.TestRunner`
- `includePlatforms`：`Editor`
- `overrideReferences`：`true`
- `precompiledReferences`：`nunit.framework.dll`
- `defineConstraints`：`UNITY_INCLUDE_TESTS`

新建 `Assets/Tests/EditMode/EventBusTests.cs`。测试里定义一个只含 `int Value` 的结构体，覆盖上表前四行：两次发布、退订后另一订阅仍收到、重复 `Dispose`、另一线程 `Publish` 抛异常。

- [x] 已写入磁盘

#### 4. 用编辑器跑 Edit Mode 测试

先等脚本编译完成，再执行：

```powershell
$unity = 'D:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
$root = 'E:\Workbench\UnityWorkBench\Farming'
$results = Join-Path $root 'Docs\Acceptance\M1\20261018-T09\EventBusTests.xml'
& $unity -batchmode -nographics -projectPath $root -runTests -testPlatform EditMode -assemblyNames Game.Framework.EditModeTests -testResults $results -logFile (Join-Path $root 'Docs\Acceptance\M1\20261018-T09\Unity.log')
```

证据写到 `Docs/Acceptance/M1/<run-id>/T09-events.txt`，记下退出码、失败数，以及四条行为断言的结果。若编辑器因工程已打开而无法进入 batchmode，改用当前已打开编辑器的 Test Runner 跑同一程序集，并把 Test Runner 的结果抄进证据。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T09/T09-events.txt`

本步范围：`EventBus` 的订阅、发布、退订，以及 Edit Mode 断言。对象池、计时器、日志和 UI 订阅留在后续任务。

### 遇到的问题和解决方案

#### 测试程序集一开始没有被编译

- 现象：只写 `defineConstraints: UNITY_INCLUDE_TESTS` 时，编辑器里没有生成 `Game.Framework.EditModeTests`。随后补上 `optionalUnityReferences: TestAssemblies`，控制台报程序集引用重复：`UnityEngine.TestRunner`、`UnityEditor.TestRunner`。
- 原因：`TestAssemblies` 会自己引用这两个测试程序集，并定义 `UNITY_INCLUDE_TESTS`。计划里又把它们写进 `references`，名字出现两次。
- 解决：`references` 只保留 `Game.Framework`。`TestAssemblies`、`nunit.framework.dll` 和 `UNITY_INCLUDE_TESTS` 仍留在 asmdef 里。
- 教训：测试程序集勾上 Test Assemblies 之后，不要再手工填写同名的 Test Runner 引用。

#### 命令行 batchmode 无法打开同一工程

- 现象：`Unity.exe -batchmode -runTests` 马上退出，日志写着已经有一个 Unity 实例打开了这个工程。
- 原因：验收时编辑器正开着 `E:\Workbench\UnityWorkBench\Farming`。
- 解决：在当前编辑器里用 Test Runner API 跑 `Game.Framework.EditModeTests`。结果 `status=Passed`，`pass=4`，`fail=0`。
- 教训：工程已打开时，用当前编辑器的 Test Runner 跑 Edit Mode；batchmode 留给编辑器关闭之后。

## T10：实现 ObjectPool.Rent/Return

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T10，依赖 T02
- 需求：R-C04
- 依据：拆解第 4.3 节 `ObjectPool<T>` 的签名；[Unity ObjectPool](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Pool.ObjectPool_1.html) 里“归还时调用重置、超过容量丢弃、重复归还可被发现”的行为
- 通过条件：归还时执行 `reset`；下一次 `Rent` 看不到上一次留下的脏数据；重复 `Return` 被发现
- 日期：2026-10-18

### 任务、流程与验证

这一步在 `Assets/Framework/Core/ObjectPool.cs` 做一个小型对象池。界面上的 Toast、子弹、飘字以后可以反复借用同一批对象，少做 `new`。计时器、日志和 Toast 服务本身留在 T11、T12、T50。

#### 对象池是什么

对象池是一组已经创建好、可以反复借出的同类对象。调用方需要一个对象时向池子要，用完还回来。池子把它洗干净，留给下一次使用。

普通写法是每次 `new` 一个对象，用完交给垃圾回收。对象池把“创建”和“销毁”换成“借出”和“归还”。对象的身份可以变，内存块尽量留着。

本步的池只管理普通 C# 类，约束是 `where T : class`。`GameObject`、贴图、网络连接的专门池以后再按各自的生命周期做。

#### 为什么要用

Unity 里频繁创建和销毁会在某一帧集中产生垃圾。垃圾回收一运行，帧时间就会抖一下。收获飘字、伤害数字、Toast、子弹若每秒新建几十个再丢掉，玩家会看到偶尔的卡顿。

对象池把这些短命对象留在池里。下一颗子弹直接拿走上一颗还回来的对象，少一次分配，也少一次回收。数量有上限 `maxRetained`，峰值过去后多出来的对象可以不再保留，内存不会随着游玩时间一直涨。

#### 怎么用

使用顺序固定为三步：

1. 启动时创建一个池。`factory` 负责第一次出现时怎么 `new`。`reset` 负责归还时把血量、文本、坐标等写回默认值。`maxRetained` 写明最多囤多少个闲置对象。
2. 需要对象时调用 `Rent`。拿到后由调用方设置这一次的位置、文字和回调。
3. 这一次的表演结束时调用 `Return`。池子先执行 `reset`，再决定放回闲置栈，或因为已经达到上限而丢掉。

一段典型调用：

```csharp
ObjectPool<DamageText> texts = new ObjectPool<DamageText>(
    () => new DamageText(),
    text => text.Value = 0,
    maxRetained: 32);

DamageText text = texts.Rent();
text.Value = 15;
// 播放结束后
texts.Return(text);
```

换场景或退出玩法时调用 `Clear`，丢掉还堆在池里的闲置对象。已经借出去的对象仍要各自 `Return`。

#### 开发时容易遇到的问题

- 借了不还。对象一直留在外借表里，池子只好不断 `factory` 新对象，内存和个数一起涨。
- 还了还用。`Return` 之后调用方仍改这个对象，下一位借用人会看到半成品。归还之后就把本地变量当成失效。
- `reset` 漏字段。只清了数字，没清列表、事件或子节点，下一次 `Rent` 带着上一轮的数据。本步的测试专门查这一点。
- 同一对象 `Return` 两次。第二次会抛 `InvalidOperationException`。常见原因是正常结束和取消逻辑各还了一次。
- 把池外 `new` 出来的对象拿去 `Return`。它不在外借表里，同样会抛异常。
- `maxRetained` 过大。闲置对象占着内存，峰值场景过去后仍不释放。过小则会在高峰反复 `new`。
- 本步的池没有加锁。后台线程和主线程一起 `Rent` / `Return` 会把栈和集合弄乱。网络线程只负责收包，借对象放在主线程。

#### 以后可以改进的方向

这些改进留到真正接 UI 和特效时再做，本步先把借还和脏数据做对。

- 预热：加载场景时先 `Rent` 再 `Return` 一批，把第一次卡顿提前到加载阶段。
- 借出句柄：`Rent` 返回一个可 `Dispose` 的租约，关闭窗口时统一归还，降低“忘记 Return”的概率。资源租约在 T14。
- 统计：记录当前外借数量和历史峰值，方便看出谁借了不还。
- 按种类分池：飘字一个池，子弹一个池。不同预制体混在同一个池里，`reset` 无法表达它们的差异。
- `GameObject` 池：归还时 `SetActive(false)` 并移到池根节点下，借出时再激活。Unity 自带的 [ObjectPool](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Pool.ObjectPool_1.html) 可以作对照，本工程的方法名仍按拆解使用 `Rent` / `Return`。

#### 适合的游戏场景

适合“同类、短命、数量会突然变多”的东西：

- 收获、升级时的飘字和飘图标
- Toast、战斗跳字
- 子弹、命中特效、点击尘土
- 背包和广场列表里反复出现的格子，滚动时复用格子对象
- 地块上暂时显示的成熟标记

不适合只有一个、活得很久的对象，例如玩家数据、登录窗口、数据库连接。那些对象创建一次就一直用，放进池里只是多一次借还。

类按拆解 4.3：

```csharp
public sealed class ObjectPool<T> where T : class
{
    public ObjectPool(Func<T> factory, Action<T> reset, int maxRetained)
    public T Rent()
    public void Return(T item)
    public void Clear()
}
```

构造时传入三样东西：

- `factory`：池里没有闲置对象时，用它新建一个。
- `reset`：对象还回来时调用，把字段清回默认状态。
- `maxRetained`：池里最多留多少个闲置对象。多出来的还回来之后执行 `reset`，然后不再留在池里。

`Rent` 先看闲置栈。有对象就取出并交给调用方；没有就调用 `factory`。借出的对象记在一张“正在外借”的表里。

`Return` 先确认这个对象当前确实在外借表中。在表里就从表中移除，调用 `reset`，闲置数量还没到 `maxRetained` 时放回栈。第二次 `Return` 同一个对象时，它已经不在外借表里，抛 `InvalidOperationException`。从未借出的对象也走这条路径。`item` 为空引用时抛 `ArgumentNullException`。

这样下一次 `Rent` 拿到的是已经执行过 `reset` 的对象。测试里用一个带 `int Value` 的类：借出后把 `Value` 改成 7，归还时 `reset` 把 `Value` 写成 0，再次 `Rent` 到的同一个对象 `Value` 必须是 0。

`Clear` 丢掉全部闲置对象。已经借出的对象仍算外借，之后仍要 `Return`。`factory` 或 `reset` 为空、`maxRetained` 小于 0 时，构造函数抛异常。`factory` 若返回空引用，`Rent` 抛 `InvalidOperationException`，空引用不进入外借表。

命名空间用 `Game.Framework.Core`，和 `EventBus` 放在一起。方法名保持 `Rent` / `Return`，与拆解 4.3 一致。

测试加在 T09 已经建好的 `Game.Framework.EditModeTests` 里。工程仍由编辑器打开时，用当前编辑器的 Test Runner 跑这个程序集；batchmode 留给编辑器关闭之后。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 归还时 reset | 借出对象，改脏字段，`Return` | `reset` 被调用一次，字段回到 `reset` 写的值 | PASS。`Return_RunsReset` |
| 再次 Rent | `maxRetained` 至少为 1，归还后再 `Rent` | 得到刚才还回的那个实例，脏字段已清除 | PASS。`Rent_AfterReturn_HasNoDirtyState` |
| 重复 Return | 对同一对象连续 `Return` 两次 | 第二次抛 `InvalidOperationException` | PASS。`Return_Twice_Throws` |
| 超过容量 | `maxRetained = 1`，归还第二个对象后再 `Rent` 两次 | 第一次 `Rent` 复用池里留下的那个；第二次调用 `factory` 新建 | PASS。`Return_BeyondMaxRetained_DropsExtraInstance`，`factory` 共调用 3 次 |
| Edit Mode | Test Runner 跑 `Game.Framework.EditModeTests` 中的 `ObjectPoolTests` | 失败数 0；T09 的 `EventBusTests` 仍通过 | PASS。程序集 pass=8，fail=0，其中 EventBus 4 条、ObjectPool 4 条 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。

#### 1. 新建 `Assets/Framework/Core/ObjectPool.cs`

```csharp
using System;
using System.Collections.Generic;

namespace Game.Framework.Core
{
    /// <summary>
    /// 可复用的 class 对象池。归还时调用 reset，重复归还会抛异常。
    /// </summary>
    public sealed class ObjectPool<T> where T : class
    {
        readonly Func<T> _factory;
        readonly Action<T> _reset;
        readonly int _maxRetained;
        readonly Stack<T> _idle = new Stack<T>();
        readonly HashSet<T> _rented = new HashSet<T>();

        /// <summary>
        /// 创建对象池。
        /// </summary>
        /// <param name="factory">没有闲置对象时用来新建。空引用会抛异常。</param>
        /// <param name="reset">归还时调用，负责清掉脏字段。空引用会抛异常。</param>
        /// <param name="maxRetained">闲置对象的保留上限。小于 0 会抛异常。等于 0 时归还后不保留。</param>
        public ObjectPool(Func<T> factory, Action<T> reset, int maxRetained)

        /// <summary>取出一个对象。池里有闲置对象就复用，否则调用 factory。</summary>
        public T Rent()

        /// <summary>
        /// 归还对象。先执行 reset，再按上限决定是否留下。
        /// </summary>
        /// <param name="item">必须是当前借出的对象。空引用或重复归还会抛异常。</param>
        public void Return(T item)

        /// <summary>丢弃全部闲置对象。已经借出的对象保持外借。</summary>
        public void Clear()
    }
}
```

- [x] 已写入磁盘

#### 2. 在现有 Edit Mode 程序集里加测试

新建 `Assets/Tests/EditMode/ObjectPoolTests.cs`，程序集仍是 `Game.Framework.EditModeTests`。用一个只有 `int Value` 的测试类覆盖上表前四行。`reset` 把 `Value` 写成 0。

- [x] 已写入磁盘

#### 3. 用当前编辑器的 Test Runner 运行

编辑器已打开本工程时，用 Test Runner API 跑 `Game.Framework.EditModeTests`，过滤 `ObjectPoolTests`。同时确认 `EventBusTests` 仍在同一程序集里并且通过。证据写到 `Docs/Acceptance/M1/<run-id>/T10-pool.txt`，记下每个用例的通过状态和失败数。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T10/T10-pool.txt`

本步范围：`ObjectPool` 的借出、归还、容量和重复归还。Toast 自动回收留在 T50。

### 遇到的问题和解决方案

本步实现和 8 条 Edit Mode 断言一次通过，没有需要改 `ObjectPool` 的问题。工程仍由编辑器打开，测试在当前编辑器的 Test Runner 中运行。

## T11：实现 TimerService.Schedule/Cancel/Tick

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T11，依赖 T02
- 需求：R-C04
- 依据：拆解第 3.1 节 `TimerHandle`；第 4.3 节 `TimerService` 签名；[Time.timeScale](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Time-timeScale.html)
- 通过条件：`Time.timeScale` 暂停后仍触发；取消后不触发；循环间隔非法时拒绝
- 日期：2026-10-18

### 任务、流程与验证

这一步做游戏里的倒计时。Toast 自动消失、按钮冷却、重连等待以后都来登记一个计时器。对象池已经完成，日志和 Toast 服务留在 T12、T50。本步只提供登记、取消和往前走一秒的方法，不在 `Update` 里自己读时间。

三个方法按拆解 4.3：

```csharp
TimerHandle Schedule(TimeSpan delay, Action callback, bool repeat)
bool Cancel(TimerHandle handle)
void Tick(double unscaledDeltaSeconds)
```

`Schedule` 登记一个回调。`delay` 是距离第一次触发还要多少时间。`repeat` 为 true 时，触发后按同一个间隔再等下一次。返回的 `TimerHandle` 含 `Id` 和 `Generation`，定义在 `Assets/Framework/Core/TimerHandle.cs`。

`Tick` 由以后的主循环每次调用，参数是这段时间里流过的秒数，类型用 `double`。参数名是 `unscaledDeltaSeconds`：调用方应传入 `Time.unscaledDeltaTime` 一类不受缩放影响的值。服务内部只把这个参数累加到倒计时上，不读取 `Time.deltaTime`，也不把秒数乘上 `Time.timeScale`。

[Time.timeScale](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Time-timeScale.html) 为 0 时，Unity 的缩放时间停住，`deltaTime` 变成 0，菜单暂停时角色动画会停。`unscaledDeltaTime` 仍按真实时间往前走。所以暂停菜单里的 Toast 和冷却，只要主循环继续把非缩放秒数送进 `Tick`，到点仍会触发。测试里把 `timeScale` 设为 0，再 `Tick` 一段足够的秒数，回调仍执行一次，最后把 `timeScale` 恢复成原来的值。

时间规则：

- 单次计时的 `delay` 必须大于或等于 0。0 表示下一次收到非负 `Tick` 时触发，触发后这条计时器移除。
- 循环计时的 `delay` 必须大于 0。0 或负数会在 `Schedule` 时抛 `ArgumentOutOfRangeException`，不登记。
- 单次和循环的负数间隔都拒绝。
- `callback` 为空引用时抛 `ArgumentNullException`。
- `Tick` 收到负数秒数时抛 `ArgumentOutOfRangeException`，已有计时器不动。0 秒的 `Tick` 只让 `delay` 为 0 的单次计时器触发。

`Cancel` 只取消句柄上 `Id` 与 `Generation` 都对得上、并且还在等待的计时器，返回 true。已经触发完的单次计时器、已经取消过的句柄、以及 `default` 句柄，返回 false。计时器完成后槽位可以交给新的 `Schedule`，新计时器使用同一个 `Id` 时把 `Generation` 加一。旧句柄再 `Cancel` 返回 false，新计时器继续等待。

一次 `Tick` 开始时只检查当时已经登记的计时器。回调里再 `Schedule` 的新计时器，从下一次 `Tick` 开始计时。回调里 `Cancel` 自己之后，同一次 `Tick` 不再调用它。循环计时器若一次 `Tick` 跨过多个间隔，按间隔逐次触发，并把余数留下；单次计时器只触发一次。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 暂停后仍触发 | `Time.timeScale = 0`，登记 0.5 秒单次回调，`Tick(0.5)` | 回调执行一次；测试结束时 `timeScale` 恢复 | PASS。`Tick_FiresWhileTimeScaleIsZero` |
| 取消 | `Schedule` 之后立刻 `Cancel`，再 `Tick` 超过间隔 | `Cancel` 返回 true；回调次数仍是 0 | PASS。`Cancel_PreventsCallback` |
| 非法循环间隔 | `Schedule(TimeSpan.Zero, callback, repeat: true)` 和负的 `TimeSpan` | 抛 `ArgumentOutOfRangeException`；随后 `Tick` 不调用该回调 | PASS。`Schedule_RejectsNonPositiveRepeatDelay` |
| 句柄世代 | 单次计时器触发后，用旧句柄 `Cancel`；同一槽位再登记一个新计时器 | 旧句柄 `Cancel` 返回 false；新计时器到点仍触发 | PASS。`Cancel_OldGenerationDoesNotStopReplacement` |
| Edit Mode | Test Runner 跑 `Game.Framework.EditModeTests` | 失败数 0；`EventBusTests` 与 `ObjectPoolTests` 仍通过 | PASS。pass=12，fail=0，含 EventBus 4 条、ObjectPool 4 条、Timer 4 条 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。命名空间用 `Game.Framework.Core`。

#### 1. 新建 `TimerHandle` 与 `TimerService`

`Assets/Framework/Core/TimerHandle.cs`：

```csharp
namespace Game.Framework.Core
{
    /// <summary>
    /// 一次 Schedule 的取消凭证。Id 标出槽位，Generation 防止槽位复用后误取消。
    /// </summary>
    public readonly struct TimerHandle
    {
        /// <summary>槽位编号。0 表示还没有登记过的空句柄。</summary>
        public int Id { get; }

        /// <summary>该槽位当前这一轮登记的世代。取消时必须与槽位里的世代相同。</summary>
        public int Generation { get; }

        /// <summary>
        /// 记录一次成功的 Schedule。
        /// </summary>
        /// <param name="id">槽位编号，从 1 起。</param>
        /// <param name="generation">该槽位本次登记的世代，从 1 起，每次回收后加一。</param>
        public TimerHandle(int id, int generation)
        {
            Id = id;
            Generation = generation;
        }
    }
}
```

`Assets/Framework/Core/TimerService.cs` 用一组槽位保存倒计时。每个槽位记下剩余秒数、间隔、是否循环、回调和世代。空闲槽位放进一条可复用列表。

```csharp
/// <summary>
/// 用调用方传入的非缩放秒数推进倒计时。
/// </summary>
public sealed class TimerService
{
    /// <summary>
    /// 登记一个倒计时。
    /// </summary>
    /// <param name="delay">第一次触发前等待的时间。循环时必须大于 0。</param>
    /// <param name="callback">到点时在 Tick 的调用线程上执行。空引用会抛异常。</param>
    /// <param name="repeat">true 表示按 delay 重复。false 表示只触发一次。</param>
    /// <returns>用于 Cancel 的句柄。</returns>
    public TimerHandle Schedule(TimeSpan delay, Action callback, bool repeat)

    /// <summary>
    /// 取消还在等待的计时器。
    /// </summary>
    /// <param name="handle">Schedule 返回的句柄。</param>
    /// <returns>确实取消了一条尚未触发完的计时器时为 true。</returns>
    public bool Cancel(TimerHandle handle)

    /// <summary>
    /// 把倒计时向前推进一段非缩放时间。
    /// </summary>
    /// <param name="unscaledDeltaSeconds">这段时间的真实秒数，不受 timeScale 影响。负数会抛异常。</param>
    public void Tick(double unscaledDeltaSeconds)
}
```

- [x] 已写入磁盘

#### 2. 在现有 Edit Mode 程序集里加测试

新建 `Assets/Tests/EditMode/TimerServiceTests.cs`。覆盖上表前四行。暂停用例放在 `try/finally` 里，无论成败都把 `Time.timeScale` 写回进入测试前的值。

- [x] 已写入磁盘

#### 3. 用当前编辑器的 Test Runner 运行

编辑器已打开本工程时，用 Test Runner API 跑整个 `Game.Framework.EditModeTests`。证据写到 `Docs/Acceptance/M1/<run-id>/T11-timer.txt`，记下 `timeScale` 暂停用例、取消用例、非法间隔用例和全部失败数。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T11/T11-timer.txt`

本步范围：计时器的登记、取消、非缩放推进和句柄世代。谁在每一帧调用 `Tick`，留到接入主循环的任务。

### 遇到的问题和解决方案

#### 测试程序集看不到 `Time.timeScale`

- 现象：暂停用例需要读写 `UnityEngine.Time.timeScale`。`Game.Framework.EditModeTests` 打开了 `overrideReferences`，默认的引擎程序集不会自动加进来。
- 原因：T09 为了只引用 NUnit，把引擎引用改成手工列表，当时列表里只有 `nunit.framework.dll`。
- 解决：在 `precompiledReferences` 中补上 `UnityEngine.CoreModule.dll`。Test Runner 随后 12 条断言全部通过。
- 教训：测试要碰 Unity API 时，在这份 asmdef 里显式加上对应模块。

## T12：实现 FileLogger 滚动日志

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T12，依赖 T02
- 需求：R-C04
- 依据：拆解第 4.3 节 `FileLogger` 签名；第 3.1 节 `LogContext`；`04_技术方案与架构.md` 第 7.8 节客户端文件日志
- 通过条件：超过大小阈值就滚动，并限制保留文件数量；`Dispose` 之后文件能读；日志正文不含密码和 Token
- 日期：2026-10-18

### 任务、流程与验证

这一步给客户端做一个写到磁盘上的日志。玩家遇到闪退时，可以把最近的日志文件交出来排查。服务端的 Serilog、FPS 覆盖层留在后面的任务。本步只实现 `Assets/Framework/Core/FileLogger.cs`。

三个方法按拆解 4.3：

```csharp
void Initialize(string directory, long maxFileBytes, int retainedFiles)
void Write(LogLevel level, string message, LogContext context)
void Dispose()
```

`Initialize` 创建目录，并打开当前日志 `client.log`。`maxFileBytes` 是单个文件的大小上限，必须大于 0。`retainedFiles` 是目录里最多留下几个日志文件，必须大于或等于 1。当前文件算在这一个数里面。上限为 3 时，目录里最多是 `client.log`、`client.1.log`、`client.2.log`。

`Write` 先把一行放进内存队列，队列最多 256 条。已经满了就丢掉最旧的、还没写进文件的那一条，再放入新的一行。接着在同一把锁里把队列写进文件流，此时先不 `Flush`。`Dispose` 负责把文件流里剩下的缓冲刷到磁盘并关闭文件。关闭之后，测试用普通的 `File.ReadAllText` 就能读到内容。

一行的格式是：

```text
2026-10-18T12:00:00.0000000Z INFO playerId=1 msgId=1005 seq=3 opId=9 elapsedMs=12.5 正文
```

`LogContext` 按拆解 3.1 放在 `Assets/Shared/Diagnostics/LogContext.cs`，字段只有 `PlayerId`、`MsgId`、`Seq`、`OpId`、`ElapsedMs`。这五个都是数字，结构里不放密码，也不放 Token。

正文里如果出现 `password` 或 `token` 后面跟着 `=` 或 `:`，把等号或冒号后面的连续非空白字符换成 `***`。大小写都认。例如 `password=secret123 token:abc.def` 落盘后是 `password=*** token:***`。文件里不能再出现 `secret123` 和 `abc.def`。

滚动发生在一行已经写入、文件长度达到或超过 `maxFileBytes` 的时候：

1. 关闭当前 `client.log`。
2. 若 `client.{retainedFiles-1}.log` 已存在，删掉它。
3. 从编号大的一边往小的一边改名，`client.1.log` 变成 `client.2.log`，当前 `client.log` 变成 `client.1.log`。
4. 新建一个空的 `client.log`。

`retainedFiles` 为 1 时没有历史文件可留，滚动就是删掉旧的 `client.log` 再新建。单行本身长过上限时，这一行仍然写入，写完立刻滚动，避免把这条日志丢掉。

`Write` 在 `Initialize` 之前调用，或 `Dispose` 之后调用，抛 `InvalidOperationException`。`directory` 为空、`maxFileBytes` 小于 1、`retainedFiles` 小于 1，在 `Initialize` 时抛 `ArgumentOutOfRangeException` 或 `ArgumentException`。重复 `Initialize` 抛 `InvalidOperationException`。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 滚动与保留数量 | `maxFileBytes` 设得很小，`retainedFiles = 2`，连续写入直到滚动 | 目录里同时存在的 `client*.log` 不超过 2 个；最旧的一份已被删除 | PASS。`Roll_KeepsOnlyRetainedFiles`，没有 `client.2.log`，最早一行已不在 |
| 退出可读 | `Write` 一行后 `Dispose`，再 `File.ReadAllText` | 能打开文件；内容含这条日志的级别和 `playerId` | PASS。`Dispose_LeavesReadableFile`，含 `INFO` 和 `playerId=7` |
| 脱敏 | 正文写入 `password=secret123 token:abc.def` | 文件含 `password=***` 和 `token:***`；不含 `secret123`、`abc.def` | PASS。`Write_RedactsPasswordAndToken` |
| 上下文 | 读落盘的一行 | 出现 `playerId`、`msgId`、`seq`、`opId`、`elapsedMs`；没有密码字段名以外的秘密值 | PASS。`Write_IncludesNumericContextOnly` |
| Edit Mode | Test Runner 跑 `Game.Framework.EditModeTests` | 失败数 0；已有的 EventBus、对象池、计时器测试仍通过 | PASS。pass=16，fail=0 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。日志类放在 `Game.Framework.Core`。`LogContext` 放在 `CloudIsle.Diagnostics`，让共享层以后也能带上这五个数字。

#### 1. 新建 `LogContext` 与 `FileLogger`

`Assets/Shared/Diagnostics/LogContext.cs`：

```csharp
namespace CloudIsle.Diagnostics
{
    /// <summary>
    /// 附在一条日志上的数字上下文。不放密码，也不放 Token。
    /// </summary>
    public readonly struct LogContext
    {
        /// <summary>玩家编号。没有玩家时为 0。</summary>
        public long PlayerId { get; }
        /// <summary>相关消息 ID。没有消息时为 0。</summary>
        public ushort MsgId { get; }
        /// <summary>请求序号。</summary>
        public uint Seq { get; }
        /// <summary>幂等操作编号。没有操作时为 0。</summary>
        public long OpId { get; }
        /// <summary>这步耗时，单位毫秒。</summary>
        public double ElapsedMs { get; }

        /// <summary>
        /// 记录一次日志要带上的数字。
        /// </summary>
        public LogContext(long playerId, ushort msgId, uint seq, long opId, double elapsedMs)
        {
            PlayerId = playerId;
            MsgId = msgId;
            Seq = seq;
            OpId = opId;
            ElapsedMs = elapsedMs;
        }
    }
}
```

`Assets/Framework/Core/FileLogger.cs` 定义 `enum LogLevel { Debug, Info, Warn, Error }`，以及 `sealed class FileLogger : IDisposable`。脱敏用正则 `(?i)\b(password|token)\b(\s*[:=]\s*)\S+`，替换成 `$1$2***`。文件流用 `FileStream`，编码 UTF-8。

```csharp
/// <summary>打开日志目录和当前的 client.log。</summary>
/// <param name="directory">日志目录。不存在就创建。空字符串会抛异常。</param>
/// <param name="maxFileBytes">单个文件达到该字节数就滚动。必须大于 0。</param>
/// <param name="retainedFiles">目录中最多保留的日志文件数，含当前文件。必须大于或等于 1。</param>
public void Initialize(string directory, long maxFileBytes, int retainedFiles)

/// <summary>格式化、脱敏，并写入有界队列，再落入当前文件。</summary>
/// <param name="level">Debug、Info、Warn 或 Error。</param>
/// <param name="message">正文。password 和 token 的值会变成 ***。空引用按空字符串处理。</param>
/// <param name="context">五个数字字段。</param>
public void Write(LogLevel level, string message, LogContext context)

/// <summary>把缓冲刷到磁盘并关闭文件。之后文件可以被其他读者打开。</summary>
public void Dispose()
```

- [x] 已写入磁盘

#### 2. 在现有 Edit Mode 程序集里加测试

新建 `Assets/Tests/EditMode/FileLoggerTests.cs`。临时目录用 `Path.Combine(Path.GetTempPath(), "CloudIsle.T12." + Guid)`，`finally` 里删除。覆盖上表前四行。测试程序集已经引用 `Game.Framework`，可以间接使用共享层里的 `LogContext`。

- [x] 已写入磁盘

#### 3. 用当前编辑器的 Test Runner 运行

编辑器已打开本工程时，用 Test Runner API 跑整个 `Game.Framework.EditModeTests`。证据写到 `Docs/Acceptance/M1/<run-id>/T12-log.txt`，附上滚动后的文件名列表，以及脱敏后的那一行。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T12/T12-log.txt`

本步范围：客户端滚动文件日志和脱敏。服务端 Serilog 留在 T40。

### 遇到的问题和解决方案

#### 测试程序集不能直接看见 `LogContext`

- 现象：`FileLoggerTests` 要构造 `CloudIsle.Diagnostics.LogContext`。它定义在 `Game.Shared`，测试程序集原先只引用 `Game.Framework`。
- 原因：Unity 的 asmdef 引用不会把被引用程序集的依赖再传给测试程序集。
- 解决：在 `Game.Framework.EditModeTests.asmdef` 的 `references` 里加上 `Game.Shared`。随后 16 条断言全部通过。
- 教训：测试代码里出现共享层类型时，要给测试程序集单独加上 `Game.Shared`。

## T13：从已验证 YooAsset 调用提炼 ResMgr.InitializeAsync

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T13，依赖 T01、T02
- 需求：R-C02
- 依据：拆解第 4.1 节 `ResMgr.InitializeAsync`；已安装的 YooAsset 3.0.6；现有 `Assets/Framework/Resource/YooAssetPoCBoot.cs`；`VersionManifest.BaseUrl`
- 通过条件：编辑器模拟模式和 HTTP 模式都能初始化成功，实际用哪种模式由设置决定
- 日期：2026-10-18

### 任务、流程与验证

这一步把 PoC 里已经跑通的 YooAsset 初始化收成 `ResMgr.InitializeAsync`。加载资源、租约、下载补丁留在 T14 和 T45。`YooAssetPoCBoot` 继续用它原来的 Offline 流程，本步不改那份 PoC。

已安装的是 YooAsset 3.0.6。初始化入口是 `YooAssets.Initialize`、`CreatePackage("DefaultPackage")`，然后 `package.InitializePackageAsync(options)`。选项类型以包内源码为准：

- 编辑器模拟：`EditorSimulateModeOptions`，文件系统用 `FileSystemParameters.CreateDefaultEditorFileSystemParameters(packageRoot)`。`packageRoot` 来自 `EditorSimulateBuildInvoker.Build("DefaultPackage", (int)EBundleType.VirtualAssetBundle)` 的 `PackageRootDirectory`。这个 Build 只在 `UNITY_EDITOR` 下可用。
- HTTP 远端：`HostPlayModeOptions`。内置文件用 `CreateDefaultBuiltinFileSystemParameters()`，缓存文件用 `CreateDefaultSandboxFileSystemParameters(remoteService)`。`remoteService` 实现 `IRemoteService.GetRemoteUrls`，把 `VersionManifest.BaseUrl` 和文件名拼成地址。

`InitializeAsync` 的签名保持拆解 4.1：

```csharp
UniTask InitializeAsync(VersionManifest manifest, CancellationToken ct)
```

模式不放进这个参数列表。`ResMgr` 上增加一个可先赋值的 `PlayMode`，类型是本工程自己的枚举 `ResourcePlayMode`，取值 `EditorSimulate` 和 `HostHttp`。调用前先设置它，再调用 `InitializeAsync`。成功后能读到 `ActiveMode` 和 `RootPath`：编辑器模式的根是模拟构建目录，HTTP 模式的根是 `manifest.BaseUrl`。

流程是：

1. `manifest` 为空就抛 `ArgumentNullException`。HTTP 模式还要求 `BaseUrl` 不是空白，否则抛 `ArgumentException`。
2. 若还没调用过，执行一次 `YooAssets.Initialize()`。
3. `TryGetPackage("DefaultPackage")`，没有就 `CreatePackage`。
4. 按 `PlayMode` 组装上面的 options，等待 `InitializePackageAsync`。`ct` 取消时停止等待。操作的 `Status` 必须是 `EOperationStatus.Succeeded`，否则抛异常并带上 `Error`。
5. 本步不调用 `RequestPackageVersionAsync`，也不加载清单。PoC 里这两步是为了接着实例化 `PatchCube`。正式的下载和清单比较在 T45。

YooAsset 3.0.6 的 `HostPlayMode` 初始化只是建好文件系统和远端地址。测试用 `http://127.0.0.1/CloudIsle` 作为 `BaseUrl`，不要求这个地址上已经有资源文件。若执行时发现初始化立刻访问网络并失败，把失败信息写入问题记录，再补一个只应答的本地端口。

编辑器模拟必须在 Unity 编辑器的 Play Mode 里跑，因为 `EditorSimulateBuildInvoker` 在玩家包里会抛 `PlatformNotSupportedException`。HTTP 模式的初始化代码本身不依赖编辑器，验收时同样放在 Play Mode 测试里，两种设置各跑一次。每次测试结束调用 `DestroyPackageAsync`，再 `YooAssets.Destroy()`，避免下一个测试看见上一个包。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 编辑器模式 | `PlayMode = EditorSimulate`，传入一份 `VersionManifest`，调用 `InitializeAsync` | 成功；`ActiveMode` 为 `EditorSimulateMode`；`RootPath` 是模拟构建目录且目录存在 | Passed。`EditorSimulate_InitializesPackage`，0.412 秒。目录存在断言通过 |
| HTTP 模式 | `PlayMode = HostHttp`，`BaseUrl` 为 `http://127.0.0.1/CloudIsle` | 成功；`ActiveMode` 为 `HostPlayMode`；`RootPath` 等于这份 `BaseUrl` | Passed。`HostHttp_InitializesPackage`，0.032 秒。`RootPath` 为 `http://127.0.0.1/CloudIsle` |
| 设置决定模式 | 两次测试使用同一个 `InitializeAsync` | 第一次走到编辑器 options，第二次走到 Host options；代码里没有写死唯一模式 | 两次都调用 `ResMgr.InitializeAsync`。控制台分别出现 `EditorFileSystem` 与 `BuiltinFileSystem` + `SandboxFileSystem` |
| Play Mode | Test Runner 的 PlayMode 平台跑 `Game.Framework.PlayModeTests` | 失败数 0 | `TestResults.xml` result=Passed，passed=2，failed=0，耗时 0.548 秒 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。命名空间用 `Game.Framework.Resource`。`InitializePackageOperation` 实现的是 `IEnumerator`，等待使用 UniTask 的 `ToUniTask(this IEnumerator, cancellationToken)`。

#### 1. 实现 `ResMgr.InitializeAsync`

在 `Assets/Framework/Resource/ResMgr.cs` 中替换空类。保留类名 `ResMgr`，供 `BootContext.Resources` 继续使用。

```csharp
/// <summary>正式资源初始化使用的两种模式。由启动设置赋值，不写死在 InitializeAsync 里。</summary>
public enum ResourcePlayMode
{
    /// <summary>编辑器里直接读模拟构建目录。</summary>
    EditorSimulate,

    /// <summary>通过 HTTP 访问 VersionManifest.BaseUrl。</summary>
    HostHttp
}

/// <summary>
/// 按 PlayMode 初始化 DefaultPackage。
/// </summary>
/// <param name="manifest">版本信息。HTTP 模式读取其中的 BaseUrl。</param>
/// <param name="ct">取消等待。取消后不再把失败当成成功。</param>
public async UniTask InitializeAsync(VersionManifest manifest, CancellationToken ct)
```

另外提供只读属性 `ActiveMode` 和 `RootPath`，以及可写属性 `PlayMode`。包名常量是 `DefaultPackage`。

- [x] 已写入磁盘

#### 2. 增加 Play Mode 测试

新建 `Assets/Tests/PlayMode/Game.Framework.PlayModeTests.asmdef`。`optionalUnityReferences` 使用 `TestAssemblies`，`references` 包含 `Game.Framework` 和 `Game.Shared`，`precompiledReferences` 包含 `nunit.framework.dll` 与 `UnityEngine.CoreModule.dll`，`defineConstraints` 为 `UNITY_INCLUDE_TESTS`。不要把 `UnityEngine.TestRunner` 再写进 `references`，T09 里那样会和 `TestAssemblies` 重复。

新建 `Assets/Tests/PlayMode/ResMgrInitializeTests.cs`。两个 `[UnityTest]` 分别覆盖编辑器模式和 HTTP 模式。用 `yield return mgr.InitializeAsync(...).ToCoroutine()` 等待。`TearDown` 里销毁 DefaultPackage 并调用 `YooAssets.Destroy()`。

- [x] 已写入磁盘

#### 3. 用当前编辑器的 Test Runner 跑 Play Mode

编辑器已打开本工程时，用 Test Runner API，`testMode` 设为 PlayMode，程序集名为 `Game.Framework.PlayModeTests`。证据写到 `Docs/Acceptance/M1/<run-id>/T13-res.txt`，记下两种模式的 `ActiveMode`、`RootPath` 和失败数。

- [x] 已执行并写入证据

本步范围：DefaultPackage 的初始化、模式开关和根路径。资源加载与下载留在后续任务。

### 遇到的问题和解决方案

- `AsyncOperationBase` 是 `IEnumerator`，工程里的 UniTask 没有 `CustomYieldInstruction.ToUniTask`。`InitializeAsync` 使用 `operation.ToUniTask(cancellationToken: ct)`。
- 测试程序集引用不传递。Play Mode 测试要直接使用 `YooAssets`、`EPlayMode` 和 `ToCoroutine`，所以 `Game.Framework.PlayModeTests.asmdef` 另外引用了 `YooAsset` 和 `UniTask`。
- Play Mode 进入时会域重载，`TestRunnerApi` 注册的内存回调没有写完 `ResMgrTests.txt`。官方结果在 `C:/Users/Lenovo/AppData/LocalLow/DefaultCompany/Farming/TestResults.xml`，passed=2，failed=0。证据已按这份结果重写。
- 控制台有一条 `YooAssetSettings not found, using default configuration`。初始化仍然成功，沿用 YooAsset 默认配置。
- 销毁包时 YooAsset 打出 `DownloadSchedulerOperation has been aborted`。这是 `DestroyPackageAsync` 停掉内部下载调度，两次测试都已通过。
- HTTP 模式用 `http://127.0.0.1/CloudIsle` 初始化成功，没有另起本地监听。本步只建文件系统，不请求版本和清单。

## T14：实现 ResourceLease 与 ResMgr.LoadAsync

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T14，依赖 T13
- 需求：R-C02
- 依据：拆解第 3.1 节 `ResourceLease<T>`；第 4.1 节 `LoadAsync` 与 `Dispose`；YooAsset 3.0.6 的 `ResourcePackage.LoadAssetAsync<T>` 与 `HandleBase.Release`；现有 `YooAssetPoCBoot` 对 `PatchCube` 的加载顺序
- 通过条件：相同地址两次加载得到两份逻辑租约；释放一份后另一份仍可使用
- 日期：2026-10-18

### 任务、流程与验证

这一步给已经初始化的 `DefaultPackage` 加上按地址加载。调用方拿到 `ResourceLease<T>`，用完后自己 `Dispose`。泄漏报告留在 T15，场景加载留在 T16，清单下载与版本比较留在 T45。`YooAssetPoCBoot` 继续直接调用 YooAsset，本步不改那份 PoC。

`LoadAssetAsync` 会检查活动清单。T13 的 `InitializeAsync` 只建立文件系统，包上还没有活动清单。正式的版本请求和清单提交在 T45。本步的测试夹具在 `InitializeAsync` 成功后，按 PoC 已跑通的两步把清单激活，再调用 `LoadAsync`：

1. `package.RequestPackageVersionAsync()`
2. `package.LoadPackageManifestAsync(new LoadPackageManifestOptions(version, 60))`

测试使用 T13 的编辑器模拟模式，地址用 PoC 已打进 `DefaultPackage` 的 `PatchCube`，类型是 `GameObject`。

YooAsset 每次 `LoadAssetAsync` 返回自己的 `AssetHandle`，并让底层 Provider 的引用计数加一。`Release` 只归还这一份句柄；引用计数到 0 时才卸载资源包。两份租约因此可以指向同一份已加载资源，释放其中一份后，另一份的 `Asset` 仍然有效。

`LoadAsync` 的签名保持拆解 4.1：

```csharp
UniTask<ResourceLease<T>> LoadAsync<T>(string address, string owner, CancellationToken ct) where T : UnityEngine.Object
```

`ResourceLease<T>` 放在 `Assets/Framework/Resource/ResourceLease.cs`，约束 `T : UnityEngine.Object`。对外字段是 `Asset`、`Address`、`Owner`、`IsDisposed`。构造函数设为 `internal`，测试程序集和其他业务程序集不能自己 new 一份租约。

流程是：

1. `address` 或 `owner` 为空白时抛 `ArgumentException`。包还没初始化时抛 `InvalidOperationException`，提示先调用 `InitializeAsync`。
2. 调用前若 `ct` 已取消，直接抛 `OperationCanceledException`，此时不创建 YooAsset 句柄。
3. `package.LoadAssetAsync<T>(address)`，用 `handle.ToUniTask(cancellationToken: ct)` 等待。`Status` 必须是 `Succeeded`，`GetAssetObject<T>()` 必须得到对象。
4. 成功时把句柄交给 `ResourceLease<T>` 并返回。失败或等待被取消时调用 `handle.Release()`，再把原来的异常抛出。成功路径不能在返回前释放句柄。
5. `Dispose` 第一次调用 `handle.Release()` 并把 `IsDisposed` 设为 true，之后 `Asset` 读出来是 null。第二次 `Dispose` 直接返回。
6. 本步加载的是资源本体。Prefab 的实例化留给窗口和场景。调用方若已经 `Instantiate`，要先销毁实例，再 `Dispose` 这份 Prefab 租约。这个顺序写在 `Dispose` 的注释里，本步不自动销毁场景里的实例。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 两份租约 | 编辑器模拟模式初始化并激活清单后，对 `PatchCube` 连续调用两次 `LoadAsync<GameObject>`，owner 分别为 `owner-a` 和 `owner-b` | 两个租约对象不同；`Address` 都是 `PatchCube`；`Owner` 与传入一致；两份 `Asset` 都不是 null | Passed。`Load_Twice_KeepsTheOtherLease` |
| 释放一份 | `Dispose` 第一份，再读第二份 | 第一份 `IsDisposed` 为 true，`Asset` 为 null；第二份 `IsDisposed` 为 false，`Asset` 仍是释放前的同一个对象 | 同一条用例通过。释放后第二份 `Asset` 与释放前是同一个对象 |
| 重复释放 | 对第一份再 `Dispose` 一次 | 不抛异常 | 同一条用例通过 |
| 失败释放句柄 | 加载一个清单里没有的地址 | 抛异常；随后再次加载 `PatchCube` 仍然成功 | Passed。`Load_MissingAddress_ThenPatchCubeStillLoads`。异常为 `InvalidOperationException`，随后 `PatchCube` 加载成功 |
| Play Mode | Test Runner 的 PlayMode 平台跑新增用例 | 失败数 0 | `TestResults.xml` result=Passed，passed=2，failed=0，耗时 0.631 秒。start 2026-10-18 12:38:22Z |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。命名空间用 `Game.Framework.Resource`。等待句柄沿用 T13 的 `ToUniTask(this IEnumerator)`。

#### 1. 实现 `ResourceLease<T>`

新建 `Assets/Framework/Resource/ResourceLease.cs`。

```csharp
/// <summary>
/// 一份已加载资源的持有记录。由 ResMgr.LoadAsync 创建。
/// 若调用方已经 Instantiate，先销毁实例，再 Dispose 这份 Prefab 租约。
/// </summary>
public sealed class ResourceLease<T> : IDisposable where T : UnityEngine.Object
{
    /// <summary>加载到的资源。Dispose 之后为 null。</summary>
    public T Asset { get; }

    /// <summary>加载时传入的地址。</summary>
    public string Address { get; }

    /// <summary>持有者。泄漏报告用它区分是谁没释放。</summary>
    public string Owner { get; }

    /// <summary>是否已经释放。</summary>
    public bool IsDisposed { get; }

    /// <summary>释放这一份句柄。重复调用直接返回。</summary>
    public void Dispose()
}
```

- [x] 已写入磁盘

#### 2. 实现 `ResMgr.LoadAsync`

在现有 `Assets/Framework/Resource/ResMgr.cs` 上增加方法，保留 T13 的初始化逻辑。

```csharp
/// <summary>
/// 从已有活动清单的 DefaultPackage 加载资源，并返回一份独立租约。
/// </summary>
/// <param name="address">YooAsset 定位地址，例如 PatchCube。</param>
/// <param name="owner">持有者名称。空白会拒绝。</param>
/// <param name="ct">取消等待。取消或失败时释放本次创建的句柄。</param>
public async UniTask<ResourceLease<T>> LoadAsync<T>(string address, string owner, CancellationToken ct)
    where T : UnityEngine.Object
```

- [x] 已写入磁盘

#### 3. 增加 Play Mode 测试

在 `Assets/Tests/PlayMode/ResMgrLoadTests.cs` 增加用例，程序集继续用 `Game.Framework.PlayModeTests`。`SetUp` 使用 `ResourcePlayMode.EditorSimulate` 调 `InitializeAsync`，再执行上面的版本请求和清单加载。`UnityTearDown` 释放测试中还未释放的租约，然后 `DestroyPackageAsync` 和 `YooAssets.Destroy()`。

用例覆盖验证表里的四项：两份租约、释放一份、重复释放、失败地址后再加载 `PatchCube`。

- [x] 已写入磁盘

#### 4. 用当前编辑器的 Test Runner 跑 Play Mode

`testMode` 为 PlayMode，程序集名为 `Game.Framework.PlayModeTests`，只筛 `ResMgrLoadTests`。证据写到 `Docs/Acceptance/M1/<run-id>/T14-lease.txt`。Play Mode 域重载会丢掉内存回调时，以 Test Runner 写出的 `TestResults.xml` 为准。

- [x] 已执行并写入证据

本步范围：资源租约和 `LoadAsync`。`CaptureLeaks`、场景租约、补丁下载留在后续任务。

### 遇到的问题和解决方案

- `where T : Object` 在同时看见 `UnityEngine.Object` 和 `object` 时编译失败。约束和测试里的资源引用都写成 `UnityEngine.Object`。
- 清单里没有的地址会先打 Warning，再打 Error：`Failed to load asset. Error: Location is invalid`。测试用 `LogAssert.Expect` 接住这两条日志，否则 Play Mode 把 Error 当成失败。
- 用 `ToCoroutine` 的异常回调接住失败时，UniTask 仍在析构里报了未观察异常。测试改成在 `async UniTask` 里 catch，再把异常作为正常结果返回。
- 第一次正式跑测时两份租约用例已经通过，缺地址用例因未声明的 Error 日志失败。补上日志期望后重跑，passed=2，failed=0。

## T15：实现 CaptureLeaks

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T15，依赖 T14
- 需求：R-C02
- 依据：拆解第 3.1 节 `ResourceLeakInfo`；第 4.1 节 `CaptureLeaks`；04 第 5.2 节“持有者负责释放，开发期提供资源泄漏报告”
- 通过条件：故意保留一份租约可报告 address、owner、count；释放后从报告中消失
- 日期：2026-10-18

### 任务、流程与验证

这一步在 T14 的租约上补一份只读报告。`ResMgr` 记下尚未 `Dispose` 的租约。调用方传入持有者名称，拿回该持有者还占着的资源。报告生成后不释放句柄，也不卸载资源包。场景加载留在 T16，清单下载留在 T45。

`ResourceLeakInfo` 放在 `Assets/Framework/Resource/ResourceLeakInfo.cs`。字段与拆解 3.1 一致：`Address`、`Owner`、`RefCount`、`CreatedAtMs`。`RefCount` 是同一地址、同一持有者上还活着的租约份数。`CreatedAtMs` 是这组里最早一份登记时的 UTC Unix 毫秒。

常驻资源和临时资源靠持有者名称分开。Loading、框架根节点这类会一直拿着的资源，使用自己的持有者，例如 `resident`。场景或窗口使用自己的持有者，例如 `scene-home`。`CaptureLeaks("scene-home")` 只列出 `scene-home` 还没释放的租约。`resident` 的租约留在报告外面，`Asset` 仍然可用。本方法不销毁任何租约。

签名保持拆解 4.1：

```csharp
IReadOnlyList<ResourceLeakInfo> CaptureLeaks(string owner)
```

登记发生在 `LoadAsync` 成功、句柄交给租约的那一刻。失败或取消的加载不会进入名单。`Dispose` 第一次执行时先把这一份从名单去掉，再 `Release` 句柄。第二次 `Dispose` 仍然直接返回，名单里不会多减一次。

`CaptureLeaks` 的流程：

1. `owner` 为空白时抛 `ArgumentException`。
2. 在该 `ResMgr` 实例的未释放名单里，筛出 `Owner` 与参数完全相同的记录。
3. 按地址合并。同一地址多份租约合成一行，`RefCount` 为份数，`CreatedAtMs` 取最早的一份。不同地址各占一行，顺序按该地址第一次登记的先后。
4. 返回只读列表。没有任何记录时返回空列表，返回值本身是 null 的情况不出现。
5. 查询过程中不调用 `Dispose`，不调用 YooAsset 的卸载。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 保留一份 | 编辑器模拟模式初始化并激活清单后，`LoadAsync<GameObject>("PatchCube", "owner-a")`，不释放，调用 `CaptureLeaks("owner-a")` | 一行；`Address` 为 `PatchCube`；`Owner` 为 `owner-a`；`RefCount` 为 1；`CreatedAtMs` 大于 0 | Passed。`KeepOne_ReportsThenDisappears` |
| 释放后消失 | `Dispose` 这一份，再调用 `CaptureLeaks("owner-a")` | 空列表；该租约 `IsDisposed` 为 true | 同一条用例通过。释放后 `CaptureLeaks("owner-a")` 为空 |
| 同一持有者两份 | 同一地址、同一 `owner-a` 再加载一份 | 仍是一行，`RefCount` 为 2，`CreatedAtMs` 仍是第一份的时间 | Passed。`SameOwner_RefCountDropsAsLeasesRelease` |
| 释放其中一份 | `Dispose` 两份中的一份后再查询 | `RefCount` 为 1；再释放另一份后列表为空 | 同一条用例通过 |
| 常驻与临时分开 | 同时保留 `resident` 与 `scene-home` 各一份 `PatchCube`，查询 `CaptureLeaks("scene-home")` | 只有 `scene-home` 一行，`RefCount` 为 1；`resident` 那份 `Asset` 仍是查询前的同一个对象 | Passed。`Resident_StaysOutOfSceneReport` |
| 其他持有者 | 只持有 `owner-a` 时查询 `CaptureLeaks("owner-b")` | 空列表；`owner-a` 的租约仍未释放 | Passed。`OtherOwner_IsEmptyAndQueryKeepsLease` |
| 报告不释放 | 查询前后比较租约的 `IsDisposed` 和 `Asset` | 查询后仍未释放，`Asset` 仍是同一个对象 | 保留一份与其他持有者两条用例都通过 |
| Play Mode | Test Runner 的 PlayMode 平台跑新增用例 | 失败数 0 | `TestResults.xml` result=Passed，passed=4，failed=0，耗时 1.618 秒。start 2026-10-18 13:09:22Z |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。命名空间用 `Game.Framework.Resource`。测试继续用 `Game.Framework.PlayModeTests`，准备包的方式和 T14 相同：`EditorSimulate`、`InitializeAsync`，然后 `RequestPackageVersionAsync` 与 `LoadPackageManifestAsync`。

#### 1. 增加 `ResourceLeakInfo`

新建 `Assets/Framework/Resource/ResourceLeakInfo.cs`。

```csharp
/// <summary>
/// 某一持有者在某一地址上尚未释放的租约汇总。只描述现状，不触发释放。
/// </summary>
public sealed class ResourceLeakInfo
{
    /// <summary>资源地址。</summary>
    public string Address { get; }

    /// <summary>持有者。</summary>
    public string Owner { get; }

    /// <summary>该地址上此持有者尚未释放的租约份数。</summary>
    public int RefCount { get; }

    /// <summary>这组租约里最早一份的登记时间，UTC Unix 毫秒。</summary>
    public long CreatedAtMs { get; }
}
```

- [x] 已写入磁盘

#### 2. 让租约在释放时通知 `ResMgr`

`ResourceLease<T>` 增加一个仅内部使用的释放回调。`LoadAsync` 成功时把对应记录放进该 `ResMgr` 的名单，并把“从名单移除这一条”传给租约。`Dispose` 第一次先调用回调，再 `Release`。回调只执行一次。

名单挂在 `ResMgr` 实例上。两个 `ResMgr` 对象的报告互不包含对方加载的租约。

- [x] 已写入磁盘

#### 3. 实现 `CaptureLeaks`

在现有 `ResMgr` 上增加方法，保留 T13 初始化与 T14 加载。

```csharp
/// <summary>
/// 列出该持有者尚未释放的租约。不释放、不卸载。
/// </summary>
/// <param name="owner">持有者名称，与 LoadAsync 传入的 owner 全字匹配。</param>
public IReadOnlyList<ResourceLeakInfo> CaptureLeaks(string owner)
```

- [x] 已写入磁盘

#### 4. 增加 Play Mode 测试

新建 `Assets/Tests/PlayMode/ResMgrLeakTests.cs`。`SetUp` 与 T14 一样激活清单。`UnityTearDown` 释放测试里还开着的租约，然后 `DestroyPackageAsync`、`RemovePackage`、`YooAssets.Destroy()`。

用例覆盖验证表：保留一份、释放后消失、同一持有者计数、常驻持有者留在本次查询之外、其他持有者为空、查询本身不释放。

- [x] 已写入磁盘

#### 5. 用当前编辑器的 Test Runner 跑 Play Mode

`testMode` 为 PlayMode，程序集名为 `Game.Framework.PlayModeTests`，只筛 `ResMgrLeakTests`。证据写到 `Docs/Acceptance/M1/<run-id>/T15-leak.txt`。Play Mode 域重载会丢掉内存回调时，以 Test Runner 写出的 `TestResults.xml` 为准。

- [x] 已执行并写入证据

本步范围：泄漏报告。场景租约、补丁下载留在后续任务。

### 遇到的问题和解决方案

- 第一次连接编辑器时发现文件还没刷新，短等后重试，导入和 Test Runner 都执行成功。
- 第一次正式跑测即通过，passed=4，failed=0。拆包日志里的 `YooAssetSettings not found` 和卸载时的 `DownloadSchedulerOperation has been aborted` 与 T13 相同，用例结果仍是 Passed。
- Play Mode 进入时会域重载。结果以 `C:/Users/Lenovo/AppData/LocalLow/DefaultCompany/Farming/TestResults.xml` 为准，证据在 `Docs/Acceptance/M1/20261018-T15/T15-leak.txt`。

## T16：实现资源场景加载与卸载适配

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T16，依赖 T14
- 需求：R-C02 / R-C06
- 依据：拆解第 3.1 节 `SceneLease`；第 4.1 节 `LoadSceneAsync` / `UnloadSceneAsync`；第 4.3 节“取消时禁止继续业务推进，仍等待底层完成并做清理”
- 通过条件：Home 场景加载后可卸载；取消或失败后没有遗留的临时句柄
- 日期：2026-10-18

### 任务、流程与验证

这一步给 `ResMgr` 加上资源包场景的加载和卸载。调用方拿到 `SceneLease`，用完后调用 `UnloadSceneAsync`。`SceneService` 的切换流程留在 T52。泄漏报告仍只统计 T15 的资源租约，场景句柄由这次的加载和卸载自己配对释放。

`DefaultPackage` 目前收集 `Assets/Res/PoC`，地址规则是文件名。在该目录放一个空场景 `Home.unity`，里面有一个名为 `HomeMarker` 的物体。模拟构建之后，地址就是 `Home`。

加载使用 `LoadSceneMode.Additive`。Play Mode 测试自己占着一个场景，附加加载才能在卸掉 Home 之后让测试继续。`LoadSceneAsync` 的签名没有模式参数，适配层固定用附加模式。

取消发生在句柄已经创建之后时，等待 YooAsset 把这次加载跑完。场景已经出现就调用 `UnloadSceneAsync`，然后 `Release`。失败的地址没有场景对象，只 `Release`。成功路径把句柄交给 `SceneLease`，此时 `IsLoaded` 为 true。`UnloadSceneAsync` 成功后把 `IsLoaded` 设为 false。对同一份租约再卸一次时直接返回。

```csharp
UniTask<SceneLease> LoadSceneAsync(string address, CancellationToken ct)
UniTask UnloadSceneAsync(SceneLease lease, CancellationToken ct)
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 加载 Home | 编辑器模拟模式初始化并激活清单后，`LoadSceneAsync("Home")` | `Address` 为 `Home`；`IsLoaded` 为 true；`SceneManager` 里名为 Home 的场景已加载；能找到 `HomeMarker` | Passed。`Home_LoadsThenUnloads` |
| 卸载 Home | `UnloadSceneAsync` | `IsLoaded` 为 false；Home 场景不再处于加载状态；场景数量回到加载前 | 同一条用例通过 |
| 重复卸载 | 对同一份租约再调用一次 | 直接返回，`IsLoaded` 仍为 false | 同一条用例通过 |
| 取消 | 传入已取消的 `CancellationToken` | 抛出 `OperationCanceledException`；Home 未留在已加载场景里；随后再次加载 Home 成功 | Passed。`Cancel_RemovesHomeAndLaterLoadWorks` |
| 失败地址 | 加载 `MissingScene-T16` | 抛出 `InvalidOperationException`；场景数量不变；随后加载 Home 成功 | Passed。`MissingScene_ReleasesHandleAndHomeStillLoads` |
| Play Mode | Test Runner 的 PlayMode 平台跑 `ResMgrSceneTests` | 失败数 0 | `TestResults.xml` result=Passed，passed=3，failed=0，耗时 1.288 秒。start 2026-10-18 13:17:54Z |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。命名空间用 `Game.Framework.Resource`。测试继续用 `Game.Framework.PlayModeTests`。`SetUp` 与 T14 相同。

#### 1. 准备 Home 场景

在 `Assets/Res/PoC/Home.unity` 保存一个空场景，并放上名为 `HomeMarker` 的物体。地址由收集规则按文件名生成。

- [x] 已写入磁盘

#### 2. 实现 `SceneLease`

新建 `Assets/Framework/Resource/SceneLease.cs`。对外是 `Address` 和 `IsLoaded`。句柄只给 `ResMgr` 卸载时使用。

- [x] 已写入磁盘

#### 3. 实现加载与卸载

在 `ResMgr` 上增加 `LoadSceneAsync` 和 `UnloadSceneAsync`。空白地址抛 `ArgumentException`。包未初始化抛 `InvalidOperationException`。卸载时空租约抛 `ArgumentNullException`。

- [x] 已写入磁盘

#### 4. 增加 Play Mode 测试

新建 `Assets/Tests/PlayMode/ResMgrSceneTests.cs`。测试程序集补上 `UnityEngine.SceneManagementModule.dll`，才能访问 `SceneManager`。`UnityTearDown` 卸掉还开着的场景，然后销毁包。

- [x] 已写入磁盘

#### 5. 用当前编辑器的 Test Runner 跑 Play Mode

`testMode` 为 PlayMode，只筛 `ResMgrSceneTests`。证据写到 `Docs/Acceptance/M1/20261018-T16/T16-scene.txt`。以 `TestResults.xml` 为准。

- [x] 已执行并写入证据

本步范围：资源包场景的加载和卸载。`SceneService` 与 Loading 界面留在 T52。

### 遇到的问题和解决方案

- `ResMgr` 同时引用了 `UnityEngine.SceneManagement` 和 YooAsset。`SceneHandle` 在两边都有定义，编译报 CS0104。场景句柄参数写成 `YooAsset.SceneHandle` 后编译通过。
- 第一次正式跑测即通过，passed=3，failed=0。证据在 `Docs/Acceptance/M1/20261018-T16/T16-scene.txt`。

## T17：实现 IInputProvider 与 MouseKeyboardInput

- 来源：`06_M1_基础框架任务拆解.md` 第 5.2 节 T17，依赖 T01/T02
- 需求：R-C07
- 依据：拆解第 4.3 节 `IInputProvider` 与 `MouseKeyboardInput`；04 第 5.8 节输入抽象；基线里的 Input System 1.20.0，`activeInputHandler` 为 2
- 通过条件：移动、点击、拖动三阶段、滚轮都有调试输出；Disable 之后没有输出
- 日期：2026-10-18

### 任务、流程与验证

这一步提供玩法层用的输入接口，并接上工程里已经启用的 Input System。触屏实现留在后续，模态界面挡住点击的规则留在 E09。

工程里已有一份模板资源 `Assets/InputSystem_Actions.inputactions`，动作是 Player 地图的 Move、Look、Attack。家园需要的指针位置、点击和滚轮不在这份模板里。本步新增 `Assets/Framework/Platform/MouseKeyboard.inputactions`，动作地图名 `MouseKeyboard`，四个动作：

| 动作 | 类型 | 绑定 |
|---|---|---|
| Move | Vector2 | 键盘 WASD 与方向键，2D 合成 |
| Point | Vector2 | `<Mouse>/position`，屏幕像素 |
| Press | Button | `<Mouse>/leftButton` |
| Zoom | Axis | `<Mouse>/scroll/y` |

`MouseKeyboardInput` 按这四个名字取动作。名字对不上时 `Enable` 抛 `InvalidOperationException`，并带上缺少的动作名。模板那份 Player 地图保持不动。

拖拽和点击共用一条规则，阈值是 8 像素：

1. 按下时记下起点，本帧不发事件。
2. 按住期间，指针离起点超过 8 像素：这一帧先发 `DragStarted`（起点像素），再发 `Dragged`（当前像素）。之后每一帧位置有变化就再发 `Dragged`。
3. 松开时如果已经进入拖拽，发 `DragEnded`（松开时的像素），`WasTapPressed` 为 false。
4. 松开时如果没超过 8 像素，这一帧 `WasTapPressed` 为 true，不发拖拽事件。
5. 同一次按下只走点击或拖拽中的一条。

`Tick` 采样动作并写调试日志。读数接口返回最近一次 `Tick` 的结果。日志前缀固定为 `[Input]`，数值用不变区域性，方便测试对上整行：

- 移动长度大于 0：`[Input] move=(x,y)`
- 本帧识别为点击：`[Input] tap=(x,y)`
- 拖拽开始：`[Input] drag-start=(x,y)`
- 拖拽中：`[Input] drag=(x,y)`
- 拖拽结束：`[Input] drag-end=(x,y)`
- 滚轮不为 0：`[Input] zoom=value`

`ReadMove` 在长度大于 1 时归一化，斜向按住不会超过单位长度。`ReadPointer` 保持屏幕像素。`ReadZoom` 返回本帧滚轮的 y。

`Enable` 启用动作。重复 `Enable` 抛 `InvalidOperationException`。`Disable` 关掉动作、清掉按下和拖拽状态，读数回到 0，`WasTapPressed` 为 false。重复 `Disable` 直接返回。`Disable` 之后再 `Tick`，不采样、不发事件、不写日志。

签名：

```csharp
Vector2 ReadMove();
Vector2 ReadPointer();
bool WasTapPressed();
float ReadZoom();
event Action<Vector2> DragStarted;
event Action<Vector2> Dragged;
event Action<Vector2> DragEnded;
void Enable();
void Disable();
void Tick();
```

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 移动 | Play Mode 里按下 W，调用 `Tick` | `ReadMove` 的 y 大于 0 且长度不超过 1；日志含 `[Input] move=` | Passed。`Move_LogsNormalizedVector`。日志为 `[Input] move=(0,1)` |
| 点击 | 按下左键再在 8 像素内松开，各 `Tick` 一次 | 松开那一帧 `WasTapPressed` 为 true；日志含 `[Input] tap=`；拖拽事件次数为 0 | Passed。`Tap_LogsWithoutDrag` |
| 拖拽三阶段 | 按下后把指针移过 8 像素，再松开 | 依次出现 `DragStarted`、`Dragged`、`DragEnded`；三行日志分别为 `drag-start`、`drag`、`drag-end`；`WasTapPressed` 为 false | Passed。`Drag_LogsThreePhases` |
| 滚轮 | 给出非 0 的 scroll y，调用 `Tick` | `ReadZoom` 等于该值；日志含 `[Input] zoom=` | Passed。`Zoom_LogsScroll`。`ReadZoom` 为 2.5 |
| Disable | `Disable` 后再按下 W 并 `Tick` | 没有 `[Input]` 日志；`ReadMove` 为 0；不发拖拽事件 | Passed。`Disable_SuppressesOutput` |
| Play Mode | Test Runner 的 PlayMode 平台跑 `MouseKeyboardInputTests` | 失败数 0 | `TestResults.xml` result=Passed，passed=5，failed=0，耗时 0.145 秒。start 2026-10-18 13:33:22Z |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。命名空间用 `Game.Framework.Platform`。`Game.Framework` 和 `Game.Framework.PlayModeTests` 都要在 asmdef 的 references 里写上 `Unity.InputSystem`。

#### 1. 增加输入配置

新建 `Assets/Framework/Platform/MouseKeyboard.inputactions`，内容是上面的地图和四条绑定。不生成 C# 包装类，代码按动作名查找。

- [x] 已写入磁盘

#### 2. 实现接口和鼠标键盘输入

新建 `IInputProvider.cs` 与 `MouseKeyboardInput.cs`。构造函数接收 `InputActionAsset`。空资源抛 `ArgumentNullException`。

- [x] 已写入磁盘

#### 3. 增加 Play Mode 测试

新建 `Assets/Tests/PlayMode/MouseKeyboardInputTests.cs`。用 `InputSystem.AddDevice` 加入键盘和鼠标，用状态事件喂入按键、指针和滚轮，再调用 `Tick`。`UnityTearDown` 里 `Disable` 并 `RemoveDevice`。用例覆盖验证表的五项。日志用 `LogAssert.Expect` 接住。

- [x] 已写入磁盘

#### 4. 用当前编辑器的 Test Runner 跑 Play Mode

`testMode` 为 PlayMode，只筛 `MouseKeyboardInputTests`。证据写到 `Docs/Acceptance/M1/<run-id>/T17-input.txt`。以 `TestResults.xml` 为准。

- [x] 已执行并写入证据

本步范围：鼠标键盘输入。触屏、虚拟摇杆和界面拦截留在后续任务。

### 遇到的问题和解决方案

- `autoReferenced` 只作用于预定义程序集。`Game.Framework.asmdef` 要显式引用 `Unity.InputSystem`，测试程序集同样把 `Unity.InputSystem` 写进 references。预编译项 `Unity.InputSystem.dll` 没有被测试程序集解析到。
- `InputActionMap.AddAction` 的布局参数名是 `expectedControlLayout`。测试里创建动作时不传这个参数，类型由绑定决定。
- 编辑器里原有的鼠标会抢走指针位置和左键，点击和拖拽读不到测试喂入的状态。测试在加入自己的键盘和鼠标之前，先移开已有的键盘和鼠标，结束时再加回去。第一次正式跑测因此有 2 项失败；隔离设备后重跑，passed=5，failed=0。证据在 `Docs/Acceptance/M1/20261018-T17/T17-input.txt`。

