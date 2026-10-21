<!-- XLUA-MIGRATION:BEGIN -->
> **迁移说明（2026-10-19）：本文件保留历史原文。**
> 当前热更架构调整为 xLua；旧记录中的技术名称和“已通过”仅描述当时环境，不能解释为 xLua 已验证。
> 原任务步骤、源码、命令、问题和实测结果完整保留；服务端、协议、资源与公共 C# 能力可按新边界复用，旧热更 DLL／补元数据链的完成记录不构成 xLua 验收。旧自动推进在 T42 部分实现阶段停止，T42 尚未验收；后续按新方案重新安排。
> 当前设计、验证范围和证据见 [xLua 迁移方案与 PoC](../07_xLua迁移方案与PoC.md)。
<!-- XLUA-MIGRATION:END -->

# M1 开发日志 · 5.1 工程与协议

记录方式对齐 `06_开发计划.md` 第 6.1 节：每项写成可勾选的文档、命令或方法。目录索引见 `M1_Logs.md`。

本文件对应 `06_M1_基础框架任务拆解.md` 第 5.1 节，任务 T01–T08。T01 的基线记录在 `M1_Baseline.md`。

## T02：整理 asmdef 和生产热更入口清单

- 来源：`06_M1_基础框架任务拆解.md` 第 5.1 节 T02，依赖已完成的 T01
- 需求：R-C01
- 依据：`M1_Baseline.md` 第 2、6 节；`04_技术方案与架构.md` 第 4.2 节；`05_协议与接口.md` 的共享协议由 `Game.Shared` 承载
- 通过条件：`Game.Hotfix` 依赖 `Game.Framework` 与 `Game.Shared`；依赖单向；`Game.Shared` 的 `noEngineReferences` 为 true
- 日期：2026-10-18

### 任务、验证与流程

T02 固定两件事：代码按哪个程序集分层，以及正式热更从哪个方法进入。它不实现登录、补丁下载或正式协议。分层定下来之后，T03 才能把 05 的协议生成进 `Game.Shared`，T09 之后的框架代码才知道该放进 AOT 还是热更。

要验证的是依赖方向和入口契约：热更程序集引用框架与共享层，框架不引用热更，共享层不带 Unity 引擎；HybridCLR 热更名单只有 `Game.Hotfix`；共享工程能按 .NET Standard 2.1 编译；PoC 的 `Start(Text)` 还在；生产入口是 `StartAsync(BootContext, CancellationToken)`，加载器用反射找到它并等待结束。

流程是：先改两个 asmdef，让框架能看见共享层和 UniTask，热更能看见 UniTask。接着在共享层放版本清单，在框架里放资源、网络、事件的空类型和 `BootContext`，这样入口的参数类型现在就能编译，完整功能留给后续任务。然后在热更入口增加 `StartAsync`，只做参数检查和“本进程只启动一次”。框架加载器增加 `InvokeEntryAsync`，用反射调用这个方法并 await。PoC 场景继续走原来的 `Start(Text)`。最后用命令打印引用、热更名单，并编译共享工程，把结果填进验证表。

### 创建文档

- [x] 新建 `Assets/ReferenceDoc/Logs/M1_Logs.md`
- [x] 写入程序集归属（见下表）
- [x] 写入入口清单（见下表）
- [x] 写入协议类型落点（见下表）
- [x] 执行「验证步骤」后，把实际结果写入该表，并把输出保存到 `Docs/Acceptance/M1/20261018-T02/T02-asmdef.txt`

#### 程序集归属

| 程序集 | 归属 | HybridCLR | 说明 |
|---|---|---|---|
| `Game.Framework` | 基包 AOT | 否 | 启动、资源、网络、核心；编译期不引用 Hotfix |
| `Game.Shared` | 基包 AOT | 否 | 协议/合约；`noEngineReferences=true`；服务端同源编译 |
| `Game.Config` | 基包 AOT | 否 | Luban 生成类型；改 C# 需升 AppVersion，不能只发补丁 |
| `Game.Hotfix` | 热更 | 是（名单唯一项） | 玩法与 UI；`Entry.StartAsync` 为生产入口 |

#### 入口清单

| 角色 | 签名 | 谁调用 | 本步是否实现体 |
|---|---|---|---|
| 生产入口 | `Game.Hotfix.Entry.StartAsync(BootContext context, CancellationToken ct)` | 仅 Launcher → `HotfixLoader.InvokeEntryAsync` | 是：只做一次初始化守卫与契约校验；配置/UI/Login 留给 T42/T48/T54/T56 |
| PoC 适配入口 | `Game.Hotfix.Entry.Start(Text statusText)` | 现有 PoC 场景 `HotfixLoader.Start` → `InvokeHotfixEntry` | 保留现有实现，不删 |
| 生产反射调用 | `HotfixLoader.InvokeEntryAsync(Assembly, BootContext, CancellationToken)` | Launcher（T56） | 是：反射唯一 `StartAsync` 并 await |
| PoC 反射调用 | `HotfixLoader.InvokeHotfixEntry(Assembly)` | PoC MonoBehaviour.Start | 保留 |

#### 协议类型落点

| 类型 | 程序集 | 本步动作 |
|---|---|---|
| 05 正式 `PlayerFullData` 等 | `Game.Shared`（T03 生成） | 只登记；本步不写 `.proto` |
| PoC `PlayerSnapshot` / 简化 Login | 现有 `Shared/Proto` PoC 产物 | 标为待隔离；T03 前禁止与正式类型同命名空间并存 |

### 待执行步骤

工作目录为 `E:\Workbench\UnityWorkBench\Farming`。先改程序集与源码，再跑核对命令。

UniTask 已放在 `Assets/Plugins/UniTask`。运行时程序集名以 `Assets/Plugins/UniTask/Runtime/UniTask.asmdef` 为准，为 `UniTask`。`Game.Framework` 与 `Game.Hotfix` 显式引用它；`Game.Shared` 继续不引用 Unity 与 UniTask。

官方依据：

- HybridCLR 加载与反射入口：[加载和运行](https://www.hybridclr.cn/docs/basic/runhotupdatecodes) — `Assembly.Load(bytes)`；`GetType` + `GetMethod` + `Invoke`
- HybridCLR 补充元数据时机：[使用泛型](https://www.hybridclr.cn/docs/beginner/generic) — 执行热更代码前调用 `RuntimeApi.LoadMetadataForAOTAssembly`
- UniTask 反射 await：Cysharp/UniTask [#287](https://github.com/Cysharp/UniTask/issues/287) — 非泛型 `UniTask` 可 `as UniTask` 后直接 `await`
- Unity asmdef 引用：[Assembly definitions](https://docs.unity3d.com/6000.3/Documentation/Manual/assembly-definition-files.html)

#### 1. 修改 `Assets/Framework/Game.Framework.asmdef`

框架要持有 `BootContext`，而版本清单在 `Game.Shared`，异步用 UniTask。把这两项写成显式引用，依赖方向在文件里就能看见，不靠 `autoReferenced` 隐式带进来。框架仍然不引用 `Game.Hotfix`，热更只能被反射调用。

`references` 在现有三项上增加 `Game.Shared` 与 `UniTask`：

```json
{
    "name": "Game.Framework",
    "rootNamespace": "Game.Framework",
    "references": [
        "Game.Shared",
        "UniTask",
        "UnityEngine.UI",
        "YooAsset",
        "HybridCLR.Runtime"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [x] 已写入磁盘

#### 2. 修改 `Assets/Hotfix/Game.Hotfix.asmdef`

`StartAsync` 的返回类型是 `UniTask`，定义在 `UniTask` 程序集里。热更已经引用框架和共享层，这里只补上 UniTask，入口方法才能编译。

在现有 `Game.Framework`、`Game.Shared`、`UnityEngine.UI` 上追加 `UniTask`：

```json
"references": [
    "Game.Framework",
    "Game.Shared",
    "UniTask",
    "UnityEngine.UI"
]
```

- [x] 已写入磁盘

#### 3. 新建 `Assets/Shared/Contracts/VersionManifest.cs`

`BootContext` 要带上当前版本，热更入口才能知道自己跑的是哪一版资源、协议和配置。类型放在 `Game.Shared`，客户端和服务端以后读的是同一份字段，而且这个程序集没有 Unity 依赖。比较 App 版本时按数字比较主次修订号，不能按字符串排序（否则 `0.1.10` 会小于 `0.1.9`）。比较函数在 T44，本步只放数据。

```csharp
namespace CloudIsle.Contracts
{
    /// <summary>
    /// 一次启动要核对的版本数据。放在共享层，AOT 与服务端都能看见。
    /// </summary>
    public sealed class VersionManifest
    {
        /// <summary>允许进入游戏的最低安装包版本，例如 0.1.0。低于它就要重装，不能靠热更。</summary>
        public string MinAppVersion;

        /// <summary>资源与热更代码版本。M1 验收基包为 100，补丁为 101。</summary>
        public int ResVersion;

        /// <summary>协议版本。登录时与服务端比对，不兼容则不能进游戏。</summary>
        public int ProtoVersion;

        /// <summary>配置表版本。与资源版本一起标识这次补丁的配置。</summary>
        public int ConfigVersion;

        /// <summary>YooAsset 包版本字符串，给资源系统定位清单。</summary>
        public string PackageVersion;

        /// <summary>补丁文件的 HTTP 根地址。</summary>
        public string BaseUrl;
    }
}
```

- [x] 已写入磁盘

#### 4. 新建 T02 最小桩（供 `BootContext` 编译；完整实现在后续任务）

拆解里的 `BootContext` 要带资源、网络和事件三个框架对象。这三个类的正式方法分别在 T13、T18、T09。现在先放空类，入口的参数类型就能编译，同时不会把后续任务的实现提前写进来。空类放在 `Game.Framework`，属于基包，热更只使用、不定义它们。

`Assets/Framework/Resource/ResMgr.cs`（完整 API 见 T13–T15）：

```csharp
namespace Game.Framework.Resource
{
    /// <summary>
    /// 资源管理器占位。热更通过 BootContext.Resources 拿到它。
    /// InitializeAsync、LoadAsync 在 T13–T14 实现。
    /// </summary>
    public sealed class ResMgr
    {
    }
}
```

`Assets/Framework/Network/NetClient.cs`（完整 API 见 T18–T21）：

```csharp
namespace Game.Framework.Network
{
    /// <summary>
    /// 客户端网络占位。热更通过 BootContext.Network 发请求。
    /// 连接、收发、超时在 T18–T21 实现。
    /// </summary>
    public sealed class NetClient
    {
    }
}
```

`Assets/Framework/Core/EventBus.cs`（完整 API 见 T09）：

```csharp
namespace Game.Framework.Core
{
    /// <summary>
    /// 事件总线占位。热更通过 BootContext.Events 订阅和发布。
    /// Subscribe、Publish 在 T09 实现。
    /// </summary>
    public sealed class EventBus
    {
    }
}
```

- [x] 已写入磁盘

#### 5. 新建 `Assets/Framework/Boot/BootContext.cs`

热更程序集不能在编译期被框架引用，所以 Launcher 不能直接 new 热更里的窗口。它把基包已经准备好的对象放进 `BootContext`，再交给 `StartAsync`。上下文里只有 AOT 类型，这样换热更 DLL 时不用改框架。

```csharp
using CloudIsle.Contracts;
using Game.Framework.Core;
using Game.Framework.Network;
using Game.Framework.Resource;

namespace Game.Framework.Boot
{
    /// <summary>
    /// 传给生产热更入口的上下文。只含基包类型，不含热更窗口。
    /// </summary>
    public sealed class BootContext
    {
        /// <summary>资源管理器。热更用它加载界面和场景。T13 之前为空壳。</summary>
        public ResMgr Resources;

        /// <summary>网络客户端。热更用它登录和重连。T18 之前为空壳。</summary>
        public NetClient Network;

        /// <summary>事件总线。模块之间用它通知，避免互相硬引用。T09 之前为空壳。</summary>
        public EventBus Events;

        /// <summary>本次启动的版本。StartAsync 要求它非空，用来打日志并在后续任务里校验配置。</summary>
        public VersionManifest Versions;
    }
}
```

- [x] 已写入磁盘

#### 6. 扩展 `Assets/Hotfix/Entry.cs`：新增生产入口，保留 PoC `Start`

正式启动需要取消令牌和基包上下文，所以用 `StartAsync`，不用继续把 `Text` 传进热更。旧的 `Start(Text)` 留给已经验证过的 PoC 场景。HybridCLR 不能在同一个进程里卸掉再加载另一份热更 DLL，因此用静态计数保证 `StartAsync` 只成功进入一次。

在现有 `using` 与类中追加（保留 `Start(Text)` 与 `RunProtobufPoC`）：

```csharp
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Framework.Boot;
using UnityEngine;

/// <summary>0 表示还没进过生产入口，1 表示本进程已经进入。跨线程用 Interlocked 改它。</summary>
static int s_startAsyncGate;

/// <summary>
/// 生产热更入口。本步只检查参数并保证只初始化一次。
/// 配置、窗口和登录界面分别在 T42、T48、T54、T56 接上。
/// </summary>
/// <param name="context">基包准备好的资源、网络、事件和版本。不能为 null，Versions 也不能为 null。</param>
/// <param name="ct">启动取消令牌。玩家退出或流程被取消时，这里会抛出取消异常，热更不再继续初始化。</param>
/// <returns>初始化完成的 UniTask。本步没有后续等待，返回已完成任务，方便加载器 await。</returns>
public static async UniTask StartAsync(BootContext context, CancellationToken ct)
{
    if (context == null)
        throw new ArgumentNullException(nameof(context));
    if (context.Versions == null)
        throw new ArgumentException("BootContext.Versions is required.", nameof(context));

    ct.ThrowIfCancellationRequested();

    // Exchange 把闸门设为 1，并返回设置前的值。返回值不是 0，说明已经启动过。
    if (System.Threading.Interlocked.Exchange(ref s_startAsyncGate, 1) != 0)
        throw new InvalidOperationException("Game.Hotfix.Entry.StartAsync already started in this process.");

    Debug.Log(
        $"[Hotfix] StartAsync OK — patch {HotfixVersion}, " +
        $"Res={context.Versions.ResVersion}, Proto={context.Versions.ProtoVersion}, Config={context.Versions.ConfigVersion}");

    // TODO T42: await ConfigService.LoadAsync(...)
    // TODO T48/T54: UIManager.Register(...); Open Login
    // TODO T56: 由 Launcher 串接完整启动链
    await UniTask.CompletedTask;
}
```

- [x] 已写入磁盘

#### 7. 扩展 `Assets/Framework/Boot/HotfixLoader.cs`：新增 `InvokeEntryAsync`，保留 PoC 路径

框架不能在代码里直接写 `Entry.StartAsync`，否则基包就会依赖热更，补丁就换不掉入口所在的程序集。HybridCLR 文档的做法是：拿到热更 `Assembly` 后，用类型名和方法签名反射调用。这里把签名写死为 `StartAsync(BootContext, CancellationToken)`，避免误调到 PoC 的 `Start(Text)`。`MethodInfo.Invoke` 返回的是装箱后的 `UniTask`；UniTask 作者说明非泛型结果可以拆箱再 `await`（[UniTask #287](https://github.com/Cysharp/UniTask/issues/287)）。

在现有类中追加（保留 `Start` / `InvokeHotfixEntry` 给 PoC）：

```csharp
using System.Threading;
using Cysharp.Threading.Tasks;

/// <summary>生产入口方法名。与 PoC 的 Start 分开，反射时不会调错。</summary>
const string EntryMethodNameAsync = "StartAsync";

/// <summary>
/// 在已加载的热更程序集里调用生产入口，并等到 StartAsync 结束。
/// DLL 字节的加载仍用现有 LoadHotfixAssembly。元数据缺失就中止，留到 T47。
/// </summary>
/// <param name="assembly">已经加载的 Game.Hotfix 程序集。编辑器里是当前域里的程序集，真机上是 Assembly.Load 的结果。</param>
/// <param name="context">交给 StartAsync 的基包上下文。</param>
/// <param name="ct">传给 StartAsync 的取消令牌。调用反射之前若已取消，则直接停止。</param>
/// <returns>入口方法结束后完成的 UniTask。</returns>
public async UniTask InvokeEntryAsync(Assembly assembly, BootContext context, CancellationToken ct)
{
    if (assembly == null)
        throw new ArgumentNullException(nameof(assembly));
    if (context == null)
        throw new ArgumentNullException(nameof(context));

    ct.ThrowIfCancellationRequested();

    // EntryTypeName 是已有常量 "Game.Hotfix.Entry"。
    Type entryType = assembly.GetType(EntryTypeName);
    if (entryType == null)
        throw new InvalidOperationException("Type not found: " + EntryTypeName);

    // types 指定参数列表，只匹配生产签名，不匹配 Start(Text)。
    MethodInfo startAsync = entryType.GetMethod(
        EntryMethodNameAsync,
        BindingFlags.Public | BindingFlags.Static,
        binder: null,
        types: new[] { typeof(BootContext), typeof(CancellationToken) },
        modifiers: null);

    if (startAsync == null)
        throw new InvalidOperationException(
            EntryTypeName + "." + EntryMethodNameAsync + "(BootContext, CancellationToken) not found");

    if (startAsync.ReturnType != typeof(UniTask))
        throw new InvalidOperationException(
            EntryTypeName + "." + EntryMethodNameAsync + " must return UniTask");

    // Invoke 的第一个参数 null 表示静态方法。返回值是装箱的 UniTask 结构体。
    object boxed = startAsync.Invoke(null, new object[] { context, ct });
    if (boxed == null)
        throw new InvalidOperationException("StartAsync returned null UniTask box");

    await (UniTask)boxed;
    SetStatus("[Hotfix] Entry.StartAsync completed");
}
```

说明：

- PoC 的 `void Start()` 仍调用 `InvokeHotfixEntry` → `Entry.Start(Text)`，Launcher 与 PoC 各走各的入口。
- `LoadMetadataForAOTAssembly` / 缺 DLL 失败即停的强化在 T47；本步只固定生产反射契约。
- HybridCLR 官方建议：进程内 `Assembly.Load` 后不要长期保存 dll `byte[]`；编辑器勿重复 Load 已加载程序集（现有 `#if UNITY_EDITOR` 查找逻辑保留）。

- [x] 已写入磁盘

#### 8. 核对命令

改完文件后用命令读回 asmdef 和 HybridCLR 设置，并编译共享工程。这样验证的是磁盘上的依赖，而不是只看日志里的草稿。共享层用 `netstandard2.1` 编译，是为了确认它没有 Unity 类型，服务端还能引用同一份源码。

- [x] 打印四个 asmdef 的引用和 `noEngineReferences`

```powershell
$root = 'E:\Workbench\UnityWorkBench\Farming'
$names = 'Game.Framework.asmdef','Game.Shared.asmdef','Game.Hotfix.asmdef','Game.Config.asmdef'
Get-ChildItem (Join-Path $root 'Assets') -Recurse -Filter *.asmdef |
  Where-Object { $names -contains $_.Name } |
  ForEach-Object {
    $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
    $refs = if ($j.references) { $j.references -join ',' } else { '(none)' }
    '{0} | refs={1} | noEngineReferences={2}' -f $j.name, $refs, $j.noEngineReferences
  }
```

- [x] 读取 HybridCLR 热更程序集名单

```powershell
Select-String -Path 'E:\Workbench\UnityWorkBench\Farming\ProjectSettings\HybridCLRSettings.asset' -Pattern 'hotUpdateAssembl' -Context 0,4
```

- [x] 用 .NET Standard 2.1 编译共享层

```powershell
dotnet build 'E:\Workbench\UnityWorkBench\Farming\Server\CloudIsle.Shared\CloudIsle.Shared.csproj' --framework netstandard2.1
```

- [x] 将以上输出保存到 `Docs/Acceptance/M1/20261018-T02/T02-asmdef.txt`

本步范围：登记程序集与生产入口。正式 `.proto` 在 T03；Config / UI / Login / Launcher 串接在 T42、T48、T54、T56；PoC `Start(Text)` 与 Protobuf PoC 保留。

### 验证步骤

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| `Game.Hotfix.asmdef` | 待执行步骤第 8 条，打印 asmdef | `references` 含 `Game.Framework`、`Game.Shared`、`UniTask` | PASS。`refs=Game.Framework,Game.Shared,UniTask,UnityEngine.UI` |
| `Game.Framework.asmdef` | 同上 | `references` 含 `Game.Shared`、`UniTask`；不含 `Game.Hotfix` | PASS。`refs=Game.Shared,UniTask,UnityEngine.UI,YooAsset,HybridCLR.Runtime` |
| `Game.Shared.asmdef`、`Game.Config.asmdef` | 同上 | `references` 为 `(none)`，`noEngineReferences=True`；不含 `UniTask` | PASS。两者均为 `refs=(none)`，`noEngineReferences=True` |
| `HybridCLRSettings.asset` | 待执行步骤第 8 条，`Select-String` | `hotUpdateAssemblies` 下一项为 `Game.Hotfix` | PASS。名单为 `Game.Hotfix` |
| `CloudIsle.Shared` | `dotnet build ... --framework netstandard2.1` | 退出码 0；工程不引用 Unity DLL | PASS。已成功生成，0 警告 0 错误，退出码 0。csproj 只有 `Google.Protobuf`，没有 Unity 引用 |
| `Entry.Start(Text)` | 对照 `Hotfix/Entry.cs` 与 `HotfixLoader.InvokeHotfixEntry` | PoC 方法仍在；PoC 场景仍反射 `Start` | PASS。`Start(Text)` 仍在；`InvokeHotfixEntry` 仍按方法名 `Start` 反射 |
| `Entry.StartAsync` / `InvokeEntryAsync` | 对照源码签名 | 生产签名为 `StartAsync(BootContext, CancellationToken)`，返回 `UniTask`；`InvokeEntryAsync` 按该签名反射并 await | PASS。两个方法均已按该签名写入 |

证据文件：`Docs/Acceptance/M1/20261018-T02/T02-asmdef.txt`。

### 遇到的问题和解决方案

#### 核对 HybridCLR 名单时输出重复

- 现象：按日志里的 `Select-String -Pattern 'hotUpdateAssembl'` 读取 `HybridCLRSettings.asset`，同一段热更名单在输出里出现多次。
- 原因：`hotUpdateAssembl` 同时命中 `hotUpdateAssemblyDefinitions`、`hotUpdateAssemblies` 和 `preserveHotUpdateAssemblies`，`-Context 0,4` 又把相邻行重复打印。
- 解决：确认名单本身只有 `Game.Hotfix`。证据文件保留可读的核对结果；以后查看这一项时用整行 `hotUpdateAssemblies:` 匹配。
- 教训：核对 YAML 键名时写完整键，避免前缀同时命中多个字段。

## T03：依据 05 建立正式 common/system/msg_id.proto

- 来源：`06_M1_基础框架任务拆解.md` 第 5.1 节 T03，依赖 T02
- 需求：R-E01
- 依据：`05_协议与接口.md` 第 4.1、4.2、4.10 节；拆解 1.2 对 PoC `PlayerSnapshot` 的隔离要求
- 通过条件：1001–1010 的字段和 ID 与 05 一致；PoC 类型与正式类型不冲突
- 日期：2026-10-18

### 任务、流程与验证

T03 把 M1 账号流程要用的协议写成正式 `.proto` 源文件。范围是三份：`common.proto` 放登录快照 `PlayerFullData` 及其字段所依赖的结构，`system.proto` 放心跳、注册、登录、重连、登出和踢下线，`msg_id.proto` 把这些消息编成 1001–1010。生成 C# 和 `MsgRegistry` 是 T04 的 `gen_proto.ps1`，本步不运行 protoc，也不改账号处理逻辑。

现有 `Shared/proto/poc.proto` 与正式协议使用同一个 C# 命名空间 `CloudIsle.Proto`，里面已有 `LoginReq`、`LoginResp`、`ItemStack`。若直接按 05 生成正式代码，两边会在同一命名空间里定义同名类型。本步把 PoC 的 C# 命名空间改到 `CloudIsle.Proto.Poc`，正式文件继续用 `CloudIsle.Proto`。PoC 的 protobuf 包名保持 `cloudisle.poc`，正式包名用 `cloudisle`，两边的消息名因此也可以同时存在。已有的手写 `Assets/Shared/Proto/Poc.cs` 先留着，等 T04 再按新命名空间生成，避免这一步拆掉已经能编译的 PoC。

`system.proto` 的 `LoginResp` 和 `ReconnectResp` 要引用 `PlayerFullData`，所以文件头除了 05 里的消息体外，还要写 `syntax`、`package`、`csharp_namespace`，并 `import "common.proto"`。`RegisterResp` 只有 `code`，没有 Token 和玩家快照；注册成功后仍要再发 `LoginReq`。`LogoutReq` 没有对应响应，`KickNtf` 是服务端推送，这两项不按“请求加 1 等于响应”去配。

流程是：先改 `poc.proto` 的 C# 命名空间，再按 05 写入三份正式 proto，字段号和 1001–1010 的枚举值原样照搬。最后用文本核对字段号、消息 ID 和两个 C# 命名空间，把结果填进下表。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| `Shared/proto/common.proto` | 对照 05 第 4.1 节 | 含 `PlayerFullData` 及其依赖类型；字段号与 05 相同；`package cloudisle`；`csharp_namespace = "CloudIsle.Proto"` | PASS。命名空间为 `CloudIsle.Proto`；`protoc` 已生成 `Common.cs` |
| `Shared/proto/system.proto` | 对照 05 第 4.2 节 | `Heartbeat` 到 `KickNtf` 的字段号与 05 相同；`import "common.proto"`；`LoginResp.data` 与 `ReconnectResp.data` 的类型是 `PlayerFullData` | PASS。两处均为 `PlayerFullData data`，字段号 6 与 3；`protoc` 已生成 `System.cs` |
| `Shared/proto/msg_id.proto` | 对照 05 第 4.10 节 | `MSG_NONE=0`；`HEARTBEAT_REQ=1001` 到 `KICK_NTF=1010` 与 05 相同 | PASS。1001–1010 与 05 一致；`protoc` 已生成 `MsgId.cs` |
| `Shared/proto/poc.proto` | 读 `option csharp_namespace` | 值为 `CloudIsle.Proto.Poc`；包名仍是 `cloudisle.poc`；`LoginReq` 等 PoC 消息还在 | PASS。包名为 `cloudisle.poc`，C# 命名空间为 `CloudIsle.Proto.Poc` |
| 同名类型 | 比较正式包与 PoC 包的 C# 命名空间 | 正式为 `CloudIsle.Proto`，PoC 为 `CloudIsle.Proto.Poc`，生成后不会落在同一个 C# 命名空间 | PASS。`common`/`system`/`msg_id` 为 `CloudIsle.Proto`；`poc.proto` 与 `plaza.proto` 为 `CloudIsle.Proto.Poc` |

### 待实现步骤

工作目录为 `E:\Workbench\UnityWorkBench\Farming`。只改协议源文件。

#### 1. 隔离 `Shared/proto/poc.proto`

把这一行：

```proto
option csharp_namespace = "CloudIsle.Proto";
```

改成：

```proto
// 与正式协议 CloudIsle.Proto 分开，避免 LoginReq、LoginResp、ItemStack 同名。
option csharp_namespace = "CloudIsle.Proto.Poc";
```

`package cloudisle.poc` 和现有消息体保持不变。

- [x] 已写入磁盘

#### 2. 新建 `Shared/proto/common.proto`

这是登录全量快照的数据结构。`PlayerFullData` 被 `LoginResp` 和 `ReconnectResp` 引用，所以它依赖的物品、地块、订单、家园、外观和活动类型一起放在这个文件里。字段号按 05 原样，发布后不能改。

```proto
syntax = "proto3";
package cloudisle;
option csharp_namespace = "CloudIsle.Proto";

// 背包里的一格。item_id 是配置表物品编号，count 是数量。
message ItemStack {
  int32 item_id = 1;
  int32 count = 2;
}

// 一次发放的奖励。items 为物品列表，其余为货币和经验增量。
message Reward {
  repeated ItemStack items = 1;
  int32 gold = 2;
  int32 gem = 3;
  int32 ticket = 4;
  int32 exp = 5;
}

// 玩家基础属性。时间无关；货币用 int64，避免大数溢出。
message PlayerInfo {
  int64 player_id = 1;
  string nickname = 2;
  int32 level = 3;
  int32 exp = 4;
  int64 gold = 5;
  int64 gem = 6;
  int64 ticket = 7;
  int64 shard = 8;
  int32 bag_slots = 9;
  int32 guide_step = 10;
}

// 地块状态。0 未解锁，1 空地，2 生长中，3 可收获。
enum PlotState {
  PLOT_LOCKED = 0;
  PLOT_EMPTY = 1;
  PLOT_GROWING = 2;
  PLOT_RIPE = 3;
}

// 一块田。plant_time、ready_time 为 Unix 毫秒。
message PlotData {
  int32 plot_id = 1;
  PlotState state = 2;
  int32 crop_id = 3;
  int64 plant_time = 4;
  int64 ready_time = 5;
  bool watered = 6;
  bool fertilized = 7;
}

// 批量种植或收获时，单块地的结果。code 为 0 表示该块成功。
message PlotResult {
  int32 plot_id = 1;
  int32 code = 2;
  PlotData plot = 3;
}

// 采集点。next_ready_time 为下次可采集的 Unix 毫秒。
message NodeData {
  int32 node_id = 1;
  int64 next_ready_time = 2;
}

// 工具等级。
message ToolData {
  int32 tool_id = 1;
  int32 level = 2;
}

// 加工槽状态。0 空闲，1 生产中，2 待领取。
enum SlotState {
  SLOT_IDLE = 0;
  SLOT_WORKING = 1;
  SLOT_DONE = 2;
}

// 设施上的一个生产槽。finish_time 为 Unix 毫秒。
message SlotData {
  int32 slot_id = 1;
  int32 recipe_id = 2;
  int64 finish_time = 3;
  SlotState state = 4;
}

// 一座设施及其槽位。
message FacilityData {
  int32 facility_id = 1;
  int32 level = 2;
  repeated SlotData slots = 3;
}

// 一条订单。slot 为 0~5；status 为 0 进行中、1 已完成。
message OrderData {
  int32 order_id = 1;
  int32 slot = 2;
  repeated ItemStack needs = 3;
  Reward reward = 4;
  int32 difficulty = 5;
  bool rare = 6;
  int32 status = 7;
}

// 当日订单进度。next_refresh_time 为 Unix 毫秒。
message OrderProgress {
  int32 day_index = 1;
  int32 done_count = 2;
  int32 reroll_count = 3;
  bool claimed_3 = 4;
  bool claimed_6 = 5;
  int64 next_refresh_time = 6;
}

// 一件家具的摆放。坐标单位是厘米。
message FurniturePlacement {
  int32 furniture_id = 1;
  int32 x = 2;
  int32 y = 3;
  int32 rotation = 4;
}

// 家园布局。version 用于保存时识别客户端提交的是哪一版布局。
message HomeLayout {
  repeated FurniturePlacement items = 1;
  int32 version = 2;
}

// 外观。equipped 的键是部位，值是外观 ID。
message AvatarData {
  repeated int32 owned = 1;
  map<int32, int32> equipped = 2;
}

// 一个卡池的保底计数。
message GachaPoolState {
  int32 pool_id = 1;
  int32 pity_ssr = 2;
  int32 pity_sr = 3;
  bool up_guarantee = 4;
}

// 一条活动。type 目前为 "gacha"；时间为 Unix 毫秒。
message ActivityInfo {
  int32 activity_id = 1;
  string name = 2;
  string type = 3;
  int32 pool_id = 4;
  int64 start_time = 5;
  int64 end_time = 6;
  int64 preload_time = 7;
  string resource_bundle = 8;
}

// 登录和重连下发的全量玩家数据。正式快照用它，不用 PoC 的 PlayerSnapshot。
message PlayerFullData {
  PlayerInfo info = 1;
  repeated ItemStack bag = 2;
  repeated PlotData plots = 3;
  repeated NodeData nodes = 4;
  repeated ToolData tools = 5;
  repeated FacilityData facilities = 6;
  repeated OrderData orders = 7;
  OrderProgress order_progress = 8;
  HomeLayout home = 9;
  AvatarData avatar = 10;
  repeated GachaPoolState gacha_states = 11;
  repeated ActivityInfo activities = 12;
}
```

- [x] 已写入磁盘

#### 3. 新建 `Shared/proto/system.proto`

这是 1001–1010 对应的消息体。`import` 让 `PlayerFullData` 来自 `common.proto`。每个响应的第一个字段都是 `code`，0 表示成功。

```proto
syntax = "proto3";
package cloudisle;
option csharp_namespace = "CloudIsle.Proto";
import "common.proto";

// 1001 心跳请求。client_time 为客户端本地 Unix 毫秒，供服务端观察时钟。
message HeartbeatReq {
  int64 client_time = 1;
}

// 1002 心跳响应。server_time 为服务端 Unix 毫秒，客户端用来计算时间偏移。
message HeartbeatResp {
  int32 code = 1;
  int64 server_time = 2;
}

// 1003 注册请求。成功只表示账号已创建，不表示已经登录。
message RegisterReq {
  string username = 1;
  string password = 2;
  string nickname = 3;
  int32 proto_version = 4;
  string device_id = 5;
}

// 1004 注册响应。只有结果码，没有 Token，也没有玩家快照。
message RegisterResp {
  int32 code = 1;
}

// 1005 登录请求。版本字段用于判断安装包、资源和协议是否兼容。
message LoginReq {
  string username = 1;
  string password = 2;
  string app_version = 3;
  int32 res_version = 4;
  int32 proto_version = 5;
  int32 config_version = 6;
  string device_id = 7;
}

// 1006 登录响应。data 是全量玩家数据；token 只出现在这里，不写入日志。
message LoginResp {
  int32 code = 1;
  string token = 2;
  int64 server_time = 3;
  int32 need_res_version = 4;
  int32 need_config_version = 5;
  PlayerFullData data = 6;
}

// 1007 重连请求。用已保存的 token 恢复会话，不再提交密码。
message ReconnectReq {
  string token = 1;
  int32 proto_version = 2;
}

// 1008 重连响应。成功时 data 覆盖客户端当前玩家数据。
message ReconnectResp {
  int32 code = 1;
  int64 server_time = 2;
  PlayerFullData data = 3;
}

// 1009 登出请求。没有响应消息。
message LogoutReq {}

// 踢线原因。0 顶号，1 维护，2 违规。
enum KickReason {
  KICK_ELSEWHERE = 0;
  KICK_MAINTENANCE = 1;
  KICK_VIOLATION = 2;
}

// 1010 踢下线推送。由服务端主动发送，seq 为 0，不配对请求。
message KickNtf {
  KickReason reason = 1;
}
```

- [x] 已写入磁盘

#### 4. 新建 `Shared/proto/msg_id.proto`

枚举值就是消息 ID。请求为奇数，响应是请求加 1。`LOGOUT_REQ` 没有响应枚举，`KICK_NTF` 单独占用 1010。其余业务 ID 仍按 05 的注释留到后续消息文件一起补，避免本步写出还没有消息体的 ID。

```proto
syntax = "proto3";
package cloudisle;
option csharp_namespace = "CloudIsle.Proto";

// 消息 ID。0 保留给“无消息”，不在线路上当业务包使用。
enum MsgId {
  MSG_NONE = 0;
  HEARTBEAT_REQ = 1001;
  HEARTBEAT_RESP = 1002;
  REGISTER_REQ = 1003;
  REGISTER_RESP = 1004;
  LOGIN_REQ = 1005;
  LOGIN_RESP = 1006;
  RECONNECT_REQ = 1007;
  RECONNECT_RESP = 1008;
  LOGOUT_REQ = 1009;
  KICK_NTF = 1010;
  // 其余 ID 按 05 第 2 节在对应业务 proto 落地时再填。
}
```

- [x] 已写入磁盘

#### 5. 核对命令

在工程根目录执行。结果保存到 `Docs/Acceptance/M1/<run-id>/T03-proto.txt`，并填入验证表的实际结果。

```powershell
$root = 'E:\Workbench\UnityWorkBench\Farming'
$proto = Join-Path $root 'Shared\proto'
Write-Output '=== csharp namespaces ==='
Select-String -Path (Join-Path $proto '*.proto') -Pattern 'csharp_namespace'
Write-Output '=== msg ids 1001-1010 ==='
Select-String -Path (Join-Path $proto 'msg_id.proto') -Pattern ' = 100[0-9]| = 1010'
Write-Output '=== login snapshot field ==='
Select-String -Path (Join-Path $proto 'system.proto') -Pattern 'PlayerFullData data'
Write-Output '=== poc package ==='
Select-String -Path (Join-Path $proto 'poc.proto') -Pattern '^package '
```

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T03/T03-proto.txt`

本步范围：三份正式 proto 和 PoC 命名空间隔离。`gen_proto.ps1`、C# 生成和 ID 重复校验在 T04。

### 遇到的问题和解决方案

#### `plaza.proto` 也会和正式心跳消息重名

- 现象：只改 `poc.proto` 后，`Shared/proto/plaza.proto` 仍使用 `csharp_namespace = "CloudIsle.Proto"`，并且定义了 `HeartbeatReq`、`HeartbeatResp`。
- 原因：广场 PoC 和登录 PoC 一样，把 C# 命名空间设成了正式协议的 `CloudIsle.Proto`。T04 若同时生成，会与 `system.proto` 的同名消息冲突。`plaza.proto` 的 `HeartbeatResp` 还没有 `code` 字段，和 05 的正式心跳响应也不是同一份定义。
- 解决：将 `plaza.proto` 的 C# 命名空间改为 `CloudIsle.Proto.Poc`，包名 `cloudisle.poc` 保持不变。已有的手写 C# 仍留到 T04 再生成。
- 教训：隔离 PoC 时要检查 `Shared/proto` 下所有仍指向 `CloudIsle.Proto` 的文件，不能只看 `poc.proto`。

## T04：实现 gen_proto.ps1 与 ProtoRegistryGenerator

- 来源：`06_M1_基础框架任务拆解.md` 第 5.1 节 T04，依赖 T03
- 需求：R-E01
- 依据：拆解 4.2 的 `MsgRegistry` 与 4.6 的脚本契约；`05_协议与接口.md` 第 4.10 节；`M1_Baseline.md` 第 2.1、6 节的 protoc 与 Protobuf 版本
- 通过条件：同一输入连续生成两次，输出一致；重复 ID 时退出码非 0；`LogoutReq` 与 `KickNtf` 不被配成一对
- 日期：2026-10-18

### 任务、流程与验证

T04 把 T03 的 `.proto` 变成两端共用的 C#，并生成消息 ID 与类型的对照表。脚本是 `Tools/gen_proto.ps1`，参数为 `RepoRoot` 和 `ProtocPath`。生成器是 `Server/CloudIsle.Tools/ProtoRegistryGenerator.cs`。正式 C# 与 `MsgRegistry.cs` 写到 `Assets/Shared/Proto/Generated/`，由现有 `CloudIsle.Shared` 连同 `Assets/Shared/**/*.cs` 一起编译。本步不实现 TCP、账号或 NUnit 工程。

生成分两段，成功后才替换正式目录。先用 protoc 3.20.3 把 `common.proto`、`system.proto`、`msg_id.proto` 生成到临时目录，并导出 descriptor。`ProtoRegistryGenerator.ValidateIds` 读这份 descriptor：ID 必须唯一；`*_REQ` 只有在「值 + 1」的枚举确实是 `*_RESP` 时才配对。`LOGOUT_REQ = 1009` 没有响应，`KICK_NTF = 1010` 不是 1009 的响应。校验失败时删除临时目录、返回非 0，不覆盖上一次成功的 `Generated`。校验通过后，`Generate` 写出 `MsgRegistry.cs`，脚本再把临时 C# 换进 `Generated`。

现有手写代码已经占用正式命名空间里的同名类型。`Assets/Shared/Proto/Poc.cs` 里有 `LoginReq`、`LoginResp`、`ItemStack`；`PlazaMessages.cs` 里有 `HeartbeatReq`、`HeartbeatResp`。正式代码若直接生成进 `CloudIsle.Proto`，共享工程会因重复定义而编译失败。PoC 登录类型改由 `poc.proto` 生成到 `CloudIsle.Proto.Poc`，并替换旧的 `Poc.cs`。广场文件里只删掉这两个心跳类，其余手写广场消息保留。仍在做 PoC 登录往返的代码改为引用 `CloudIsle.Proto.Poc`。服务端心跳改用生成后的正式 `HeartbeatReq` / `HeartbeatResp`。正式心跳响应的 `server_time` 是字段 2，`code` 是字段 1；旧手写心跳把 `server_time` 放在字段 1。这是按 05 对齐，不把手写字段号写回正式 proto。

protoc 是 3.20.3，运行库 `Google.Protobuf` 是 3.21.12。生成器项目引用 3.21.12，与客户端 DLL 一致。版本不同本身不算失败，但要记在证据里。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 连续两次生成 | 对正式工程根目录执行两次 `gen_proto.ps1`，比较 `Generated` 下全部 `.cs` 的 SHA-256 | 两次退出码都是 0；文件集合与哈希相同 | PASS。两次退出码 0，五个 `.cs` 哈希相同 |
| 重复 ID | 把 proto 复制到临时根目录，把两个枚举改成同一个数字后再执行脚本 | 退出码非 0；正式工程的 `Assets/Shared/Proto/Generated` 不被改写 | PASS。退出码 1；正式 `MsgRegistry.cs` 哈希未变 |
| Logout / Kick | 调用生成后的 `MsgRegistry.TryGetResponseId` | `1001→1002`、`1003→1004`、`1005→1006`、`1007→1008` 为 true；`1009` 与 `1010` 为 false | PASS。六项断言均为 true |
| 共享工程 | `dotnet build Server/CloudIsle.Shared/CloudIsle.Shared.csproj --framework netstandard2.1` | 退出码 0；`CloudIsle.Proto` 中不再同时存在手写和生成的 `LoginReq`、`HeartbeatReq` | PASS。0 警告 0 错误。旧 `Poc.cs` 已删除，心跳类已从 `PlazaMessages.cs` 移除 |
| 版本组合 | 记录 `protoc --version` 与 `Google.Protobuf` 包版本 | protoc 3.20.3，运行库 3.21.12 | PASS。`libprotoc 3.20.3`，包版本 3.21.12 |

### 待实现步骤

`RepoRoot` 使用 `E:\Workbench\UnityWorkBench\Farming`。`ProtocPath` 使用 `D:\Users\Lenovo\anaconda3\Library\bin\protoc.exe`。

#### 1. 新建 `Server/CloudIsle.Tools`

新建 `net8.0` 控制台项目，引用 `Google.Protobuf` 3.21.12。`Program.Main` 接收 `validate <descriptor路径>` 或 `generate <proto目录> <输出目录> <descriptor路径>`，分别调用下面两个方法。校验失败抛异常，进程退出码为非 0。

```csharp
public sealed class ProtoRegistryGenerator
{
    /// <summary>
    /// 读取 protoc 导出的 descriptor，检查 MsgId。
    /// 重复数值、缺少同名消息、把 Logout 配到 Kick 都抛 InvalidOperationException。
    /// </summary>
    /// <param name="descriptorPath">protoc --descriptor_set_out 写出的文件。</param>
    public void ValidateIds(string descriptorPath)

    /// <summary>
    /// 按已通过校验的 descriptor 写出 MsgRegistry.cs。
    /// </summary>
    /// <param name="protoDir">Shared/proto，用于确认消息来源。</param>
    /// <param name="outputDir">放置 MsgRegistry.cs 的目录。</param>
    public void Generate(string protoDir, string outputDir)
}
```

配对规则写在 `ValidateIds` 内：

- 枚举数值除 0 以外不可重复。0 只允许 `MSG_NONE`。
- `HEARTBEAT_REQ` 对应消息 `HeartbeatReq`。名字对不上就失败。
- 名字以 `_REQ` 结尾时，只有「当前值 + 1」存在且名字以 `_RESP` 结尾，才记录响应 ID。
- `LOGOUT_REQ` 不记录响应。`KICK_NTF` 不作为任何请求的响应。

`Generate` 写出的 `MsgRegistry` 提供拆解 4.2 的三个方法：

```csharp
/// <summary>由消息 CLR 类型得到线路上的消息 ID。未知类型抛异常。</summary>
/// <param name="messageType">例如 typeof(LoginReq)。</param>
public static ushort GetId(Type messageType)

/// <summary>按消息 ID 把 body 反序列化成 IMessage。未知 ID 抛异常。</summary>
/// <param name="msgId">1001–1010 中的一个。</param>
/// <param name="body">该消息的 protobuf 正文，可以为空数组。</param>
public static IMessage Parse(ushort msgId, byte[] body)

/// <summary>查询请求是否有配对响应。</summary>
/// <param name="requestId">请求消息 ID，例如 1005。</param>
/// <param name="responseId">有配对时得到请求 ID + 1；没有配对时为 0。</param>
/// <returns>1009 和 1010 返回 false。</returns>
public static bool TryGetResponseId(ushort requestId, out ushort responseId)
```

- [x] 已写入磁盘

#### 2. 新建 `Tools/gen_proto.ps1`

```powershell
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$ProtocPath
)
```

脚本依次做这些事：在临时目录生成 C# 和 descriptor；`dotnet run` 调用 `ValidateIds`；通过后再调用 `Generate`；最后用临时目录整体替换 `Assets/Shared/Proto/Generated`。protoc 或校验失败时立刻以非 0 退出，不替换 `Generated`。正式 proto 用 `--csharp_out` 生成 `Common.cs`、`System.cs`、`MsgId.cs`。`poc.proto` 单独生成到 `Poc.cs`，命名空间保持 `CloudIsle.Proto.Poc`。

- [x] 已写入磁盘

#### 3. 去掉会和生成结果重名的旧类型

- 删除 `Assets/Shared/Proto/Poc.cs`，改用生成的 `Generated/Poc.cs`。
- 从 `Assets/Shared/Proto/PlazaMessages.cs` 删除手写 `HeartbeatReq`、`HeartbeatResp`。
- `Assets/Hotfix/Entry.cs` 的 PoC 往返改为 `CloudIsle.Proto.Poc` 的 `LoginReq`、`LoginResp`、`PlayerSnapshot`、`ItemStack`。
- 服务端、灰盒和 PoC 客户端里仍依赖 `PlayerSnapshot` 的登录代码，改为 `CloudIsle.Proto.Poc`。涉及 `ClientSession.cs`、`BotWorker.cs`、`CloudIsle.PocClient/Program.cs`、`CloudIsle.GreyboxSmoke/Program.cs`、`GreyboxNetClient.cs`、`GreyboxPlayerMirror.cs`、`NetworkPoCTools.cs`。
- 心跳收发改为正式 `CloudIsle.Proto.HeartbeatReq` 与 `HeartbeatResp`。

- [x] 已写入磁盘

#### 4. 核对命令

```powershell
$root = 'E:\Workbench\UnityWorkBench\Farming'
$protoc = 'D:\Users\Lenovo\anaconda3\Library\bin\protoc.exe'
$script = Join-Path $root 'Tools\gen_proto.ps1'
& $script -RepoRoot $root -ProtocPath $protoc
& $script -RepoRoot $root -ProtocPath $protoc
dotnet build (Join-Path $root 'Server\CloudIsle.Shared\CloudIsle.Shared.csproj') --framework netstandard2.1
```

重复 ID 使用临时根目录，不使用正式 `RepoRoot`。证据保存到 `Docs/Acceptance/M1/<run-id>/T04-proto.txt`。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T04/T04-proto.txt`

本步范围：生成脚本、ID 校验、正式 `MsgRegistry`，以及为了让共享工程通过编译而移动的 PoC 类型。账号业务仍使用后续任务里的正式 `PlayerFullData`。

### 遇到的问题和解决方案

#### 生成的心跳消息没有静态 `ParseFrom`

- 现象：`CloudIsle.Server` 编译失败，`HeartbeatReq` 没有 `ParseFrom`。
- 原因：手写心跳类提供静态 `ParseFrom`。protoc 3.20.3 生成的 C# 只提供 `Parser.ParseFrom`。
- 解决：`ClientSession` 改为调用 `HeartbeatReq.Parser.ParseFrom(body)`。之后服务端编译通过。
- 教训：替换手写消息后，按生成代码的 `Parser` 入口调用，不沿用旧的静态方法名。

#### 重复枚举值先被 protoc 拒绝

- 现象：把 `HEARTBEAT_RESP` 改成 `1001` 后，脚本在进入 `ValidateIds` 之前就退出，报 enum value 相同。
- 原因：proto3 默认不允许两个枚举名共用一个数值，除非写 `option allow_alias = true`。
- 解决：未加 `allow_alias` 时，protoc 的非 0 退出已经满足“重复 ID 失败且不覆盖正式产物”。补测时加上 `allow_alias`，`ValidateIds` 输出「消息 ID 重复: 1001」并返回 1。两种情况都没有改写正式 `Generated`。
- 教训：重复 ID 可能在 protoc 或生成器任一处失败；脚本必须在替换 `Generated` 之前停住。

## T05：建立 .NET 8 Server/Tests 项目并源文件引用 Shared

- 来源：`06_M1_基础框架任务拆解.md` 第 5.1 节 T05，依赖 T04
- 需求：R-S01、R-E04
- 依据：`04_技术方案与架构.md` 第 4.1、4.2 节；`M1_Baseline.md` 第 2.1、2.2 节；拆解第 4.6 节里测试工程的路径 `Server/CloudIsle.Tests`
- 通过条件：客户端共享代码和服务端都能编译；服务端不引用 Unity DLL
- 日期：2026-10-18

### 任务、流程与验证

这一步是在给已经能编译的服务端补上测试工程，并确认客户端、服务端用的是同一份共享源码。不写 TCP，不写 `FrameCodec`，不写登录。拆包测试是 T06–T08，账号和数据库是更后面的任务。

先分清三个已经存在的东西：

- Unity 里的 `Assets/Shared/Game.Shared.asmdef` 告诉 Unity：把 `Assets/Shared` 下的 `.cs` 编成程序集 `Game.Shared`。它的 `noEngineReferences` 已经是 `true`，表示这些脚本不能使用 `UnityEngine`。
- `Server/CloudIsle.Shared/CloudIsle.Shared.csproj` 做同一件事，只是给 .NET 命令行用。它用 `Compile Include="..\..\Assets\Shared\**\*.cs"` 直接编译那批源文件，程序集名也是 `Game.Shared`，目标框架是 `netstandard2.1`。
- `Server/CloudIsle.Server` 已经是 `net8.0` 主程序，并且已经 `ProjectReference` 了 `CloudIsle.Shared`。本步保留它，不另建一套服务端。

“源文件引用”的意思是：服务端和测试工程都去用 `CloudIsle.Shared` 编出来的那份程序集，而 `CloudIsle.Shared` 编译的是 `Assets/Shared` 里的原文件。不要把 `Assets/Shared` 复制进 `Server`。测试工程里也不要再写一遍 `<Compile Include="Assets/Shared/**/*.cs" />`。同一批 `.cs` 如果被两个工程各自编译，类型会定义两次，后面一引用就冲突。

“两端均编译”里的两端是：

1. 共享端：`dotnet build Server/CloudIsle.Shared/CloudIsle.Shared.csproj --framework netstandard2.1`。它代表 Unity 的 `Game.Shared`，因为源文件是同一批。本步不打开 Unity，也不进 Play Mode。
2. 服务端：`dotnet build Server/CloudIsle.Server/CloudIsle.Server.csproj`。目标框架必须是 `net8.0`。

还要新建 `Server/CloudIsle.Tests`，并加进现有的 `Server/CloudIsle.sln`。这是 NUnit 工程，用 `dotnet test` 在命令行跑，不是 Unity 的 Play Mode 测试。通过条件没有要求本步写出拆包用例，但空的测试工程以后会让 `dotnet test` 出现“没找到测试却显示成功”。所以放一条最小测试：加载 `Game.Shared`，断言它引用的程序集名单里没有 `UnityEngine` 和 `UnityEditor`。`FrameCodecTests` 仍留给 T08。

“服务端不引用 Unity DLL”要看两处。工程文件里不能出现 `UnityEngine`、`UnityEditor` 或指向 `Editor\Data\Managed` 的引用。编译出的 `CloudIsle.Server.dll` 和 `Game.Shared.dll`，用反射看 `GetReferencedAssemblies()`，名单里也不能有这两个名字。服务端跑在 .NET 8 上，机器上没有 Unity 运行时；一旦共享代码调用了 `UnityEngine`，服务端就编不过，或者编过了也带上 Unity 专用 DLL。

本机同时装着 .NET SDK `8.0.404` 和 `10.0.301`，当前默认是 10，工程根目录还没有 `global.json`。不指定的话，`dotnet` 会用最新的 SDK 10 去编译写着 `net8.0` 的工程。在仓库根目录 `E:\Workbench\UnityWorkBench\Farming` 放 `global.json`，把 SDK 固定到 `8.0.404`，`rollForward` 用 `latestFeature`，这样仍停留在 8.0 这一代，补丁号可以向前。官方说明见 [.NET global.json](https://learn.microsoft.com/dotnet/core/tools/global-json)。`dotnet` 是从当前工作目录往上找这份文件的，所以命令要在仓库根目录执行，不能只把文件放进 `Server` 却在根目录运行。

`CloudIsle.Shared` 现在的 `LangVersion` 是 `latest`。SDK 10 下的 `latest` 可能接受 Unity 编译器不接受的新语法。共享代码要能被 Unity 6000.3 的 `Game.Shared` 编译，语言版本按工程基线定为 C# 9，把这项改成 `9.0`。执行前对照 [Unity C# 编译器说明](https://docs.unity3d.com/6000.3/Documentation/Manual/csharp-compiler.html)；若该页写明本编辑器支持的版本高于 9，以该页为准，并在证据里记下页上的版本号。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| SDK | 在仓库根目录执行 `dotnet --version` | 输出以 `8.0.` 开头，来自根目录 `global.json` | PASS。`8.0.404` |
| 共享端 | `dotnet build Server/CloudIsle.Shared/CloudIsle.Shared.csproj --framework netstandard2.1` | 退出码 0；产物仍是 `Game.Shared.dll`；源文件来自 `Assets/Shared`，不是复制品 | PASS。0 警告 0 错误，产物为 `Game.Shared.dll` |
| 服务端 | `dotnet build Server/CloudIsle.Server/CloudIsle.Server.csproj` | 退出码 0；目标框架 `net8.0`；仍引用原来的 `CloudIsle.Shared` | PASS。0 警告 0 错误；`TargetFramework` 为 `net8.0`，并引用 `CloudIsle.Shared` |
| Unity DLL | 搜索 Server 下 `CloudIsle.Server`、`CloudIsle.Shared`、`CloudIsle.Tests` 的 csproj；再对两个 dll 调用 `GetReferencedAssemblies()` | 工程文件和程序集引用里都没有 `UnityEngine`、`UnityEditor` | PASS。三个 csproj 无匹配。`Game.Shared` 引用 `netstandard`、`Google.Protobuf`；`CloudIsle.Server` 引用 `Game.Shared`、`Game.Config` 及 .NET 库 |
| 测试工程 | `dotnet test Server/CloudIsle.Tests/CloudIsle.Tests.csproj --logger "trx;LogFileName=t05.trx"` | 退出码 0；报告里的测试数大于 0；`Game.Shared` 不含 Unity 程序集 | PASS。失败 0，通过 1，总计 1 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。已有的 `CloudIsle.Server`、PoC 客户端、机器人和灰盒工程保持原样，只在需要时确认它们仍引用 `CloudIsle.Shared`。

#### 1. 在仓库根目录固定 SDK 8

新建 `E:\Workbench\UnityWorkBench\Farming\global.json`：

```json
{
  "sdk": {
    "version": "8.0.404",
    "rollForward": "latestFeature"
  }
}
```

`version` 是本机已经安装的 .NET 8 SDK。`rollForward` 为 `latestFeature` 时，允许使用更新的 8.0.x，不会跳到 SDK 10。

- [x] 已写入磁盘

#### 2. 把共享工程的语言版本改成 C# 9

在 `Server/CloudIsle.Shared/CloudIsle.Shared.csproj` 里，把：

```xml
<LangVersion>latest</LangVersion>
```

改成：

```xml
<!-- 与 Unity 6000.3 能编译的共享代码对齐；不用 latest，避免 SDK 10 放进更高语法。 -->
<LangVersion>9.0</LangVersion>
```

`TargetFramework` 保持 `netstandard2.1`。`Compile Include` 保持指向 `..\..\Assets\Shared\**\*.cs`。不要把这批文件再复制到 `Server`。

- [x] 已写入磁盘

#### 3. 新建 NUnit 工程并引用 Shared

在仓库根目录执行：

```powershell
dotnet new nunit -n CloudIsle.Tests -o Server/CloudIsle.Tests -f net8.0
dotnet sln Server/CloudIsle.sln add Server/CloudIsle.Tests/CloudIsle.Tests.csproj
```

模板自带的 NUnit 包版本保留，不手写一套版本号。在生成的 csproj 里增加对共享工程的引用：

```xml
<ItemGroup>
  <!-- 使用 CloudIsle.Shared 已经编好的 Game.Shared，不再次编译 Assets/Shared。 -->
  <ProjectReference Include="..\CloudIsle.Shared\CloudIsle.Shared.csproj" />
</ItemGroup>
```

删掉模板生成的示例测试文件。新建 `Server/CloudIsle.Tests/SharedReferenceTests.cs`：

```csharp
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace CloudIsle.Tests
{
    public sealed class SharedReferenceTests
    {
        /// <summary>
        /// 确认测试工程能加载共享程序集，并且该程序集没有引用 Unity。
        /// </summary>
        [Test]
        public void GameShared_DoesNotReferenceUnityEngine()
        {
            // MsgRegistry 来自 T04 生成代码，位于 Game.Shared。
            Assembly assembly = typeof(CloudIsle.Proto.MsgRegistry).Assembly;
            // AssemblyName.Name 在反射 API 里是 string?，这里保持可空，避免测试工程的 Nullable 警告。
            string?[] names = assembly.GetReferencedAssemblies()
                .Select(item => item.Name)
                .ToArray();

            Assert.That(assembly.GetName().Name, Is.EqualTo("Game.Shared"));
            Assert.That(names, Does.Not.Contain("UnityEngine"));
            Assert.That(names, Does.Not.Contain("UnityEditor"));
        }
    }
}
```

- [x] 已写入磁盘

#### 4. 编译并核对两边都不引用 Unity

```powershell
$root = 'E:\Workbench\UnityWorkBench\Farming'
Set-Location $root
dotnet --version
dotnet build (Join-Path $root 'Server\CloudIsle.Shared\CloudIsle.Shared.csproj') --framework netstandard2.1
dotnet build (Join-Path $root 'Server\CloudIsle.Server\CloudIsle.Server.csproj')
dotnet test (Join-Path $root 'Server\CloudIsle.Tests\CloudIsle.Tests.csproj') --logger "trx;LogFileName=t05.trx"
```

再用文本搜索三个 csproj，并对 `Game.Shared.dll`、`CloudIsle.Server.dll` 打印 `GetReferencedAssemblies()`。证据写到 `Docs/Acceptance/M1/<run-id>/T05-projects.txt`，内容包含 SDK 版本、三个命令的退出码、测试数量，以及两份程序集的引用名单。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T05/T05-projects.txt`

本步范围：SDK 选择、共享工程语言版本、NUnit 工程和“同一份源码”的引用关系。不改协议，不实现网络收发。

### 遇到的问题和解决方案

#### 测试工程开启了可空引用

- 现象：第一次 `dotnet test` 通过，但 `SharedReferenceTests.cs` 出现警告 CS8619。`AssemblyName.Name` 是 `string?`，赋给 `string[]` 时类型不一致。
- 原因：`dotnet new nunit` 生成的工程默认 `<Nullable>enable</Nullable>`。计划里的示例按不可空数组来写。
- 解决：局部变量改成 `string?[]`。再次测试为 0 失败、通过 1，编译输出里不再出现该警告。
- 教训：模板工程的可空设置要跟着生成结果，不按旧工程的 `Nullable=disable` 假设。

#### Unity 6000.3 的语言版本就是 C# 9.0

- 现象：执行前需要确认 `LangVersion` 能不能定为 `9.0`。
- 依据：[Unity 6000.3 C# 编译器说明](https://docs.unity3d.com/6000.3/Documentation/Manual/csharp-compiler.html) 写明本编辑器的语言版本是 C# 9.0。
- 处理：`CloudIsle.Shared` 使用 `9.0`，没有改成更高版本。

## T06：实现 FrameCodec.Encode

- 来源：`06_M1_基础框架任务拆解.md` 第 5.1 节 T06，依赖 T05
- 需求：R-C03、R-S01
- 依据：拆解第 3.2 节；`05_协议与接口.md` 第 1.1 节；拆解第 4.2 节的 `FrameCodec.Encode` 签名
- 通过条件：空 body 帧长度是 10；长度字段和字节序符合 3.2
- 日期：2026-10-18

### 任务、流程与验证

这一步给客户端和服务端共用的 `Assets/Shared` 增加“把一条消息装进 TCP 帧”的方法。方法名是 `FrameCodec.Encode`。解码、拆包、粘包放在 T07，成套测试放在 T08。本步写完后，调用 `Encode` 就能得到可以按字节核对的一帧。

一帧是一段连续字节，从左到右四段：

| 顺序 | 字段 | 宽度 | 含义 |
|---|---|---|---|
| 1 | `len` | 4 字节 | 后面三段一共多少字节 |
| 2 | `msgId` | 2 字节 | 消息 ID，例如心跳请求 1001 |
| 3 | `seq` | 4 字节 | 请求序号 |
| 4 | `body` | `len - 6` 字节 | Protobuf 正文 |

`len` 只统计它后面的 `msgId`、`seq`、`body`。`len` 自己的 4 字节留在帧的最前面，不写进 `len`。所以：

- `len = 6 + body.Length`
- 整帧字节数 = `4 + len`

空 body 时 `body.Length` 为 0，`len` 为 6，整帧为 10 字节。这就是本步的第一条通过条件。

字节序是小端：一个数的低位字节放在前面。1001 的十六进制是 `0x03E9`，放进 2 字节时写成 `E9 03`。数字 6 放进 4 字节时写成 `06 00 00 00`。写入用 `System.Buffers.Binary.BinaryPrimitives` 的小端方法，文档在 [BinaryPrimitives](https://learn.microsoft.com/dotnet/api/system.buffers.binary.binaryprimitives)。

长度上限按拆解 3.2：

- body 最长 65536 字节
- `len` 最大 65542（6 + 65536）
- 整帧最大 65546（4 + 65542）

`Encode` 先看 body 是不是空引用、长度是否超过 65536。超限时抛 `ArgumentOutOfRangeException`，此时还没有为这帧分配那块最大 65546 字节的数组。通过之后再分配 `4 + len` 字节，依次写入 `len`、`msgId`、`seq`，最后把 body 抄到偏移 10 的位置。

签名按拆解 4.2：

```csharp
byte[] Encode(ushort msgId, uint seq, byte[] body)
```

`seq` 用 `uint`。已有的 `Assets/Shared/Net/PacketCodec.cs` 也在做同类封帧，它的 `seq` 参数类型是 `int`，PoC、灰盒、机器人和当前服务端会话都还在调用它。本步新建 `Assets/Shared/Network/FrameCodec.cs`，命名空间用 `CloudIsle.Net`，与 `PacketCodec`、`MsgIds` 放在一起。那些旧调用点先保持原样，等 T07 的 `TryDecode` 完成后，再决定是否把收发改到 `FrameCodec`。

用两个具体例子核对。空 body、`msgId = 1001`、`seq = 0` 的 10 字节是：

```text
06 00 00 00   E9 03   00 00 00 00
len=6         msgId   seq
```

`msgId = 1005`、`seq = 0x01020304`、body 为 `01 02` 时，`len = 8`，整帧 12 字节：

```text
08 00 00 00   ED 03   04 03 02 01   01 02
len=8         msgId   seq           body
```

`body.Length == 65536` 时返回数组长度 65546。`body.Length == 65537` 时抛异常，并且异常发生在分配返回数组之前。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 空 body | `Encode(1001, 0, Array.Empty<byte>())` | 返回长度 10；字节为 `06 00 00 00 E9 03 00 00 00 00` | PASS。断言 `Is.EqualTo` 通过 |
| 非空 body | `Encode(1005, 0x01020304, new byte[] { 0x01, 0x02 })` | 返回长度 12；`len`、`msgId`、`seq`、body 的字节与上表一致 | PASS。12 字节与计划中的例子一致 |
| 最大 body | body 长度 65536 | 返回长度 65546；前 4 字节是 `len = 65542` 的小端 | PASS。长度 65546，前 4 字节为 `06 00 01 00` |
| 超限 body | body 长度 65537，以及 `body == null` | 超限抛 `ArgumentOutOfRangeException`；空引用抛 `ArgumentNullException`；两种情况都不返回数组 | PASS。两种异常均被断言捕获 |
| 共享工程 | `dotnet build Server/CloudIsle.Shared/CloudIsle.Shared.csproj --framework netstandard2.1` | 退出码 0；`Game.Shared` 含 `CloudIsle.Net.FrameCodec` | PASS。0 警告 0 错误 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。新文件会被 `CloudIsle.Shared` 的 `Assets/Shared/**/*.cs` 自动编进去。

#### 1. 新建 `Assets/Shared/Network/FrameCodec.cs`

```csharp
using System;
using System.Buffers.Binary;

namespace CloudIsle.Net
{
    /// <summary>
    /// 把 msgId、seq、body 装成一帧小端字节。
    /// 帧布局：len(4) + msgId(2) + seq(4) + body。
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>msgId 与 seq 的字节数。len = 该值 + body 长度。</summary>
        public const int HeaderSize = 6;

        /// <summary>body 允许的最大字节数，65536。</summary>
        public const int MaxBodyLength = 65536;

        /// <summary>len 的最大值，6 + 65536 = 65542。</summary>
        public const int MaxPayloadLength = HeaderSize + MaxBodyLength;

        /// <summary>整帧最大字节数，4 + 65542 = 65546。</summary>
        public const int MaxFrameLength = 4 + MaxPayloadLength;

        /// <summary>
        /// 校验 body 长度后写出完整帧。
        /// </summary>
        /// <param name="msgId">消息 ID，按小端写入 2 字节。</param>
        /// <param name="seq">请求序号；推送可以用 0。按小端写入 4 字节。</param>
        /// <param name="body">Protobuf 正文。长度 0 时整帧为 10 字节。空引用或超过 65536 时抛异常。</param>
        /// <returns>长度为 4 + 6 + body.Length 的新数组。</returns>
        public static byte[] Encode(ushort msgId, uint seq, byte[] body)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (body.Length > MaxBodyLength)
                throw new ArgumentOutOfRangeException(nameof(body), "body 超过 65536 字节");

            int payloadLength = HeaderSize + body.Length;
            byte[] frame = new byte[4 + payloadLength];
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)payloadLength);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), msgId);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6, 4), seq);
            if (body.Length > 0)
                body.CopyTo(frame.AsSpan(10));
            return frame;
        }
    }
}
```

`TryDecode`、`Packet`、`DecodeStatus` 留到 T07，写在同一个文件里。

- [x] 已写入磁盘

#### 2. 在现有测试工程里核对 Encode

新建 `Server/CloudIsle.Tests/FrameCodecTests.cs`。本步只放 `Encode` 的断言：空 body 的 10 字节、带 body 的字节序、65536 字节的帧长、65537 与空引用的异常。T08 再往这个类里加拆包和粘包用例。

测试工程已经引用 `CloudIsle.Shared`，可以直接调用 `CloudIsle.Net.FrameCodec`。

- [x] 已写入磁盘

#### 3. 编译并运行

在仓库根目录执行，让 `global.json` 继续选择 SDK 8：

```powershell
$root = 'E:\Workbench\UnityWorkBench\Farming'
Set-Location $root
dotnet build (Join-Path $root 'Server\CloudIsle.Shared\CloudIsle.Shared.csproj') --framework netstandard2.1
dotnet test (Join-Path $root 'Server\CloudIsle.Tests\CloudIsle.Tests.csproj') --filter FrameCodecTests --logger "trx;LogFileName=t06.trx"
```

证据写到 `Docs/Acceptance/M1/<run-id>/T06-encode.txt`，附上空 body 的 10 个字节、非空例子的 12 个字节，以及两次 `dotnet` 的退出码。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T06/T06-encode.txt`

本步范围：`FrameCodec.Encode` 和它的长度、字节序断言。`PacketCodec` 的现有调用点保持不动。

### 遇到的问题和解决方案

本步编译和 5 条断言一次通过，没有需要改实现的问题。

## T07：实现 FrameCodec.TryDecode

- 来源：`06_M1_基础框架任务拆解.md` 第 5.1 节 T07，依赖 T06
- 需求：R-C03、R-S01
- 依据：拆解第 3.1 节的 `Packet`、`DecodeStatus`；第 3.2 节帧契约；第 4.2 节 `TryDecode` 签名
- 通过条件：半包时 `consumedBytes = 0`；缓冲里有多帧时只消费第一帧；非法长度返回 `Invalid`
- 日期：2026-10-18

### 任务、流程与验证

这一步给 T06 的 `FrameCodec` 补上读帧方法 `TryDecode`。它从一段已经收到的字节里查看最前面能不能拆出完整的一帧。TCP 发送、粘包的全部分割位置、以及“每个位置都测一遍”留在 T08。本步把半包、只消费一帧、非法长度这三件事做对。

`Encode` 写出的布局是：

| 顺序 | 字段 | 宽度 |
|---|---|---|
| 1 | `len` | 4 字节，小端。数值等于后面 `msgId + seq + body` 的字节数 |
| 2 | `msgId` | 2 字节，小端 |
| 3 | `seq` | 4 字节，小端 |
| 4 | `body` | `len - 6` 字节 |

整帧长度是 `4 + len`。空 body 时 `len = 6`，整帧 10 字节。

方法签名按拆解 4.2：

```csharp
DecodeStatus TryDecode(ReadOnlySpan<byte> buffer, out Packet packet, out int consumedBytes)
```

`DecodeStatus` 有三个值，定义在 `Assets/Shared/Network/DecodeStatus.cs`：

| 值 | 何时返回 | `consumedBytes` |
|---|---|---|
| `NeedMoreData` | 字节还不够一帧 | 0 |
| `Success` | 最前面有一帧完整数据 | 这一帧的字节数，即 `4 + len` |
| `Invalid` | `len` 小于 6，或 `len` 大于 65542 | 0 |

`Packet` 定义在 `Assets/Shared/Network/Packet.cs`，字段是 `ushort MsgId`、`uint Seq`、`byte[] Body`。成功时 `Body` 是新分配的数组，内容从缓冲里抄出来。之后即使调用方改掉原来的缓冲，`Packet.Body` 里的字节保持不变。拆解要求这份内存归这颗包自己，后续接 `System.IO.Pipelines` 时才能在 `AdvanceTo` 之后继续使用它。Pipelines 的收发循环是 T22，本步只保证拷贝。

读取顺序：

1. 缓冲短于 4 字节时，还不知道 `len`，返回 `NeedMoreData`，`consumedBytes = 0`。此时不分配 body。
2. 读出小端 `uint` 的 `len`。`len < 6` 或 `len > 65542` 时返回 `Invalid`，`consumedBytes = 0`。这 4 字节已经足够判断，不必等 body 到齐。同样不按这个非法长度去分配数组。
3. 缓冲短于 `4 + len` 时，长度合法，数据还没到齐，返回 `NeedMoreData`，`consumedBytes = 0`。
4. 数据到齐后读 `msgId`、`seq`，按 `len - 6` 拷贝 body，返回 `Success`。`consumedBytes` 只等于第一帧的长度。缓冲后部若还有第二帧，那些字节留在原处，交给调用方用 `buffer.Slice(consumedBytes)` 再解一次。

`NeedMoreData` 和 `Invalid` 时，`packet` 用 `default`。只有 `Success` 时 `Packet.Body` 才有数组；空 body 用 `Array.Empty<byte>()`。

用 T06 已经对过的字节做例子。`Encode(1001, 0, 空数组)` 得到 10 字节 `06 00 00 00 E9 03 00 00 00 00`。

- 把前 9 字节送进 `TryDecode`：`NeedMoreData`，`consumedBytes = 0`。
- 把 10 字节再拼上同样一帧，共 20 字节：第一次 `Success`，`consumedBytes = 10`，`MsgId = 1001`，`Seq = 0`，`Body` 长度为 0。剩下 10 字节再解，得到第二帧。
- 缓冲以 `05 00 00 00` 开头：`len = 5`，小于 6，返回 `Invalid`。
- 缓冲以 `07 00 01 00` 开头：`len = 65543`，大于 65542，返回 `Invalid`。

已有 `Assets/Shared/Net/PacketCodec.TryDecode` 在非法长度时抛异常，PoC 和当前服务端会话仍调用它。本步只扩展 `FrameCodec`，那些调用点保持原样。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 半包 | 对 10 字节空 body 帧分别传入前 0、3、4、9 字节 | 四次都是 `NeedMoreData`，`consumedBytes = 0` | PASS。四次 `consumedBytes` 都是 0 |
| 只消费一帧 | 两帧 10 字节空 body 帧拼成 20 字节，连续解两次 | 第一次 `consumedBytes = 10`；第二次再得到一帧；两次 `MsgId` 都是 1001 | PASS。两次 `consumedBytes` 都是 10，`MsgId` 都是 1001 |
| 非法长度 | `len = 5` 与 `len = 65543`，缓冲只放这 4 字节 | 两次都是 `Invalid`，`consumedBytes = 0` | PASS。两次都是 `Invalid`，`consumedBytes` 为 0 |
| 正文拷贝 | `Encode` 一帧 body 为 `01 02`，解码后把原缓冲改成 0 | `Packet.Body` 仍是 `01 02` | PASS。改写原缓冲后 `Body` 仍是 `01 02` |
| 共享工程 | `dotnet build Server/CloudIsle.Shared`，再 `dotnet test --filter FrameCodecTests` | 退出码 0；T06 的 5 条 `Encode` 断言仍然通过 | PASS。编译 0 警告 0 错误；9 条断言通过，其中含原有 5 条 `Encode` |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。新文件和修改后的 `FrameCodec.cs` 都在 `Assets/Shared` 下，由 `CloudIsle.Shared` 自动编译。

#### 1. 新建 `DecodeStatus` 与 `Packet`

`Assets/Shared/Network/DecodeStatus.cs`：

```csharp
namespace CloudIsle.Net
{
    /// <summary>TryDecode 的三种结果：数据不够、成功解出一帧、长度非法。</summary>
    public enum DecodeStatus
    {
        /// <summary>缓冲里的字节还不够一整帧。consumedBytes 为 0。</summary>
        NeedMoreData = 0,

        /// <summary>最前面的一帧已经完整。consumedBytes 等于该帧字节数。</summary>
        Success = 1,

        /// <summary>len 小于 6 或大于 65542。consumedBytes 为 0。</summary>
        Invalid = 2
    }
}
```

`Assets/Shared/Network/Packet.cs`：

```csharp
namespace CloudIsle.Net
{
    /// <summary>
    /// 一帧解出的消息。Body 在构造成功时拷贝为独立数组。
    /// </summary>
    public readonly struct Packet
    {
        /// <summary>消息 ID。</summary>
        public ushort MsgId { get; }

        /// <summary>请求序号。推送帧可以是 0。</summary>
        public uint Seq { get; }

        /// <summary>Protobuf 正文的独立副本。空正文是长度为 0 的数组。</summary>
        public byte[] Body { get; }

        /// <summary>
        /// 保存已经拷贝好的正文。
        /// </summary>
        /// <param name="msgId">从帧里读出的消息 ID。</param>
        /// <param name="seq">从帧里读出的序号。</param>
        /// <param name="body">调用方已经从缓冲拷出的正文。空引用会抛异常。</param>
        public Packet(ushort msgId, uint seq, byte[] body)
        {
            if (body == null)
                throw new System.ArgumentNullException(nameof(body));
            MsgId = msgId;
            Seq = seq;
            Body = body;
        }
    }
}
```

- [x] 已写入磁盘

#### 2. 在 `FrameCodec` 中实现 `TryDecode`

在 `Assets/Shared/Network/FrameCodec.cs` 增加：

```csharp
/// <summary>
/// 从缓冲最前面尝试解出一帧。长度非法时返回 Invalid，数据不够时返回 NeedMoreData。
/// </summary>
/// <param name="buffer">当前已经收到的字节，可以含半包，也可以在一帧后面带着下一帧。</param>
/// <param name="packet">Success 时为解出的包；其余状态为 default。</param>
/// <param name="consumedBytes">Success 时为这一帧的字节数；NeedMoreData 和 Invalid 时为 0。</param>
/// <returns>NeedMoreData、Success 或 Invalid。</returns>
public static DecodeStatus TryDecode(ReadOnlySpan<byte> buffer, out Packet packet, out int consumedBytes)
{
    packet = default;
    consumedBytes = 0;

    if (buffer.Length < 4)
        return DecodeStatus.NeedMoreData;

    uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    if (payloadLength < HeaderSize || payloadLength > MaxPayloadLength)
        return DecodeStatus.Invalid;

    int frameLength = 4 + (int)payloadLength;
    if (buffer.Length < frameLength)
        return DecodeStatus.NeedMoreData;

    ushort msgId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(6, 4));
    int bodyLength = (int)payloadLength - HeaderSize;
    byte[] body = bodyLength == 0
        ? Array.Empty<byte>()
        : buffer.Slice(10, bodyLength).ToArray();

    packet = new Packet(msgId, seq, body);
    consumedBytes = frameLength;
    return DecodeStatus.Success;
}
```

`Encode` 保持 T06 的实现。

- [x] 已写入磁盘

#### 3. 把三条通过条件写进 `FrameCodecTests`

在 `Server/CloudIsle.Tests/FrameCodecTests.cs` 增加半包、两帧只消费第一帧、`len = 5`、`len = 65543`、以及正文拷贝后原缓冲被改写的断言。保留 T06 已有的 5 条 `Encode` 测试。T08 再补“每个分割位置”和最大 body 的解码用例。

- [x] 已写入磁盘

#### 4. 编译并运行

在仓库根目录执行：

```powershell
$root = 'E:\Workbench\UnityWorkBench\Farming'
Set-Location $root
dotnet build (Join-Path $root 'Server\CloudIsle.Shared\CloudIsle.Shared.csproj') --framework netstandard2.1
dotnet test (Join-Path $root 'Server\CloudIsle.Tests\CloudIsle.Tests.csproj') --filter FrameCodecTests --logger "trx;LogFileName=t07.trx"
```

证据写到 `Docs/Acceptance/M1/<run-id>/T07-decode.txt`，记下每次调用的 `DecodeStatus` 和 `consumedBytes`，以及两条命令的退出码。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T07/T07-decode.txt`

本步范围：`TryDecode`、`Packet`、`DecodeStatus`，以及半包、单帧消费、非法长度的断言。`PacketCodec` 的调用点保持不动。

### 遇到的问题和解决方案

本步编译和 9 条断言一次通过，没有需要改实现的问题。

## T08：添加并执行 FrameCodecTests

- 来源：`06_M1_基础框架任务拆解.md` 第 5.1 节 T08，依赖 T07
- 需求：R-E04
- 依据：拆解第 3.2 节；第 4.6 节 `ST/FrameCodecTests.cs` 的两个方法名
- 通过条件：每个分割位置、两帧粘包、最大 body 和超限 body 的断言全部通过
- 日期：2026-10-18

### 任务、流程与验证

这一步把 `Server/CloudIsle.Tests/FrameCodecTests.cs` 里的解码断言补全，并实际跑一遍。`FrameCodec.Encode` 和 `TryDecode` 已在 T06、T07 实现。本步以测试为主；只有断言暴露出解码错误时才改 `FrameCodec`。

TCP 一次 `Read` 可能停在一帧的任意字节上，也可能一次带回两帧。分割位置指：一帧共 N 字节时，缓冲里先只有前 `i` 字节，`i` 从 0 取到 `N - 1`。这 N 种情况都还缺数据，`TryDecode` 应返回 `NeedMoreData`，`consumedBytes` 为 0。凑满 N 字节后返回 `Success`，`consumedBytes` 等于 N。

T07 对 10 字节空 body 帧只抽了 0、3、4、9 这四个位置，两帧粘包也只用了两条相同的空 body 帧。T08 按拆解 4.6 增加两个方法：

| 方法 | 要断言的内容 |
|---|---|
| `Decode_HandlesSplitAndCoalescedFrames` | 空 body 帧和带正文帧的每一个分割位置；两帧不同内容粘在一起；第一帧完整、第二帧只到一半 |
| `Decode_RejectsInvalidLength` | body 长度 0 与 65536 能来回编解码；`len` 为 0、5、65543 时返回 `Invalid`，并且后面跟着多余字节也不消费 |

最大 body 是 65536 字节，整帧 65546 字节。对这一帧同样从 0 循环到 65545：每次都是 `NeedMoreData` 且 `consumedBytes = 0`。第 65546 字节到齐后，`consumedBytes = 65546`，`Packet.Body` 长度是 65536，首尾字节与编码前一致。半包路径在长度未到齐时不分配这块正文。

超限有两类：

- `Encode` 传入 65537 字节，抛 `ArgumentOutOfRangeException`。T06 已有这条断言，本步保留。
- 缓冲前 4 字节表示 `len = 65543`（正文将是 65537 字节）。无论这 4 字节后面有没有更多数据，都返回 `Invalid`，`consumedBytes = 0`。`len = 0` 和 `len = 5` 同样处理。`len = 65542` 是允许的最大长度；只收到这 4 字节时返回 `NeedMoreData`，用来和“超限立刻 Invalid”分开。

粘包用两条内容不同的帧拼成一个数组。第一帧 `msgId = 1001`、`seq = 1`、body 为 `0A`。第二帧 `msgId = 1005`、`seq = 2`、body 为 `0B 0C`。第一次 `TryDecode` 的 `consumedBytes` 等于第一帧长度，`MsgId` 为 1001，`Body` 为 `0A`。从 `consumedBytes` 再解，得到第二帧。若缓冲只有第一帧加上第二帧的前 3 字节，第一次仍只消费第一帧；对剩余字节再解，得到 `NeedMoreData`，`consumedBytes = 0`。

已有的 9 条测试留在同一个类里。`PacketCodec` 的调用点保持原样。

| 验证对象 | 验证方法 | 期待结果 | 实际结果 |
|---|---|---|---|
| 每个分割位置 | 空 body 帧、带正文帧、65536 字节正文帧，前缀长度从 0 到帧长减 1 | 每次都是 `NeedMoreData`，`consumedBytes = 0`；完整帧为 `Success` 且消费整帧 | PASS。三帧的全部前缀与完整帧断言通过 |
| 两帧粘包 | 1001 与 1005 两帧相连；以及第一帧后只带第二帧前 3 字节 | 先解出 1001；再解出 1005。半截第二帧时第二次为 `NeedMoreData`，消费 0 | PASS。先 1001 后 1005；半截第二帧消费 0 |
| 最大 body | `Encode` 后 `TryDecode` 65536 字节正文 | `consumedBytes = 65546`；`Body` 长度 65536，首尾字节一致 | PASS。`consumedBytes = 65546`，首字节 `0xAB`，末字节 `0xCD` |
| 超限 body | `len = 0`、`len = 5`、`len = 65543`，其中 65543 后面再放 8 字节 | 全部 `Invalid`，`consumedBytes = 0`。`len = 65542` 且只有 4 字节时为 `NeedMoreData` | PASS。三组非法长度为 `Invalid`；65542 的 4 字节为 `NeedMoreData` |
| 测试命令 | 在仓库根目录执行 `dotnet test Server/CloudIsle.Tests --filter FrameCodecTests --logger "trx;LogFileName=t08.trx"` | 退出码 0；报告里的通过数大于 0，失败数为 0 | PASS。失败 0，通过 11，总计 11 |

### 待实现步骤

工作目录是 `E:\Workbench\UnityWorkBench\Farming`。只改 `Server/CloudIsle.Tests/FrameCodecTests.cs`。

#### 1. 增加分割与粘包断言

在 `FrameCodecTests` 中增加 `Decode_HandlesSplitAndCoalescedFrames`。里面用循环检查每个前缀：

```csharp
/// <summary>
/// 一帧的每个前缀都视为半包；两帧粘在一起时先只消费第一帧。
/// </summary>
/// <param name="frame">Encode 得到的完整一帧。</param>
static void AssertEverySplitNeedsMoreData(byte[] frame)
{
    for (int length = 0; length < frame.Length; length++)
    {
        DecodeStatus status = FrameCodec.TryDecode(
            frame.AsSpan(0, length),
            out _,
            out int consumedBytes);
        Assert.That(status, Is.EqualTo(DecodeStatus.NeedMoreData), "length=" + length);
        Assert.That(consumedBytes, Is.EqualTo(0), "length=" + length);
    }
}
```

调用三次：`Encode(1001, 0, 空数组)`、`Encode(1005, 9, new byte[] { 1, 2, 3, 4 })`、以及 65536 字节正文的最大帧。每次循环结束后，再对完整 `frame` 断言 `Success`，`consumedBytes` 等于 `frame.Length`。

粘包部分按上一节的 1001 / 1005 两帧编写，包含“第二帧只有前 3 字节”的情况。

- [x] 已写入磁盘

#### 2. 增加最大与超限长度断言

增加 `Decode_RejectsInvalidLength`：

- 空 body 与 65536 字节 body 经过 `Encode` 再 `TryDecode`，`MsgId`、`Seq`、`Body` 与输入一致。最大帧的 `consumedBytes` 为 65546。
- 构造 4 字节小端 `len`：0、5、65543。65543 再追加 8 字节任意数据。三次结果都是 `Invalid`，`consumedBytes` 为 0。
- 单独 4 字节、值为 65542：结果是 `NeedMoreData`，`consumedBytes` 为 0。

- [x] 已写入磁盘

#### 3. 运行测试并保存报告

在仓库根目录执行：

```powershell
$root = 'E:\Workbench\UnityWorkBench\Farming'
Set-Location $root
dotnet test (Join-Path $root 'Server\CloudIsle.Tests\CloudIsle.Tests.csproj') --filter FrameCodecTests --logger "trx;LogFileName=t08.trx"
```

证据写到 `Docs/Acceptance/M1/<run-id>/T08-frames.txt`，记下通过数、失败数、trx 路径，以及最大帧的 `consumedBytes`。

- [x] 已执行并写入证据。证据文件：`Docs/Acceptance/M1/20261018-T08/T08-frames.txt`

本步范围：补全并执行 `FrameCodecTests`。收发循环、账号和数据库测试留在后续任务。

### 遇到的问题和解决方案

本步 11 条断言一次通过，没有需要改 `FrameCodec` 的问题。

