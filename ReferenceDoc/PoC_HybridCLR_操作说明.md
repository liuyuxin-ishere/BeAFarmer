# PoC #2 HybridCLR 操作说明

## 已完成

| 项 | 状态 |
|---|---|
| 嵌入 HybridCLR 9.0.2 | ✔ |
| 本地 Installer（绕过失效 git 代理） | ✔ |
| `Game.Framework` / `Game.Hotfix` 程序集 | ✔ |
| 热更程序集配置为 `Game.Hotfix` | ✔ |
| 场景 `Assets/Scenes/HybridCLR_PoC.unity` | ✔ |
| 编辑器 Play：Entry 输出 `patch v1` | ✔ |
| Windows IL2CPP 出包 + 只换补丁 | ✖ 缺 IL2CPP 模块 |

## 菜单

`Farming/HybridCLR PoC/`

1. Configure Player + HotUpdate Assemblies  
2. Install From Local（已装过可跳过）  
3. Generate All（需 IL2CPP 模块）  
4. Copy Hotfix DLL to StreamingAssets  
5. CompileDll + Copy（改热更代码后的补丁流程）

## 你需要做的（闭环验收）

1. **Unity Hub** → 安装 `Windows Build Support (IL2CPP)`（对应 6000.3.22f1）。
2. 回到工程，执行菜单 **3. Generate All**。
3. **File → Build And Run**（Standalone Windows 64，IL2CPP），确认界面显示 `patch v1`。
4. 将 `Assets/Hotfix/Entry.cs` 中 `HotfixVersion` 改为 `v2`。
5. 执行菜单 **5. CompileDll + Copy**。
6. **不要重打安装包**，只把  
   `Assets/StreamingAssets/Game.Hotfix.dll.bytes`  
   覆盖到已构建客户端同路径，再启动 → 应显示 `patch v2`。

## 关键路径

- 热更入口：`Assets/Hotfix/Entry.cs`
- AOT 加载：`Assets/Framework/Boot/HotfixLoader.cs`
- 编译输出：`Library/HybridCLR/HotUpdateDlls/<Target>/Game.Hotfix.dll`
- 运行时补丁：`Assets/StreamingAssets/Game.Hotfix.dll.bytes`
