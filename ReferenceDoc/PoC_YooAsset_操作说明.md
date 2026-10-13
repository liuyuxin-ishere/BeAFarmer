# PoC #3 YooAsset 操作说明

## 已完成

| 项 | 状态 |
|---|---|
| OpenUPM 安装 `com.tuyoogame.yooasset` **3.0.6** | ✔ |
| 收集器：`DefaultPackage` → `Assets/Res/PoC`（可寻址） | ✔ |
| 测试 Prefab：`Assets/Res/PoC/PatchCube.prefab` | ✔ |
| OfflinePlayMode 启动：`YooAssetPoCBoot` | ✔ |
| 场景：`Assets/Scenes/YooAsset_PoC.unity` | ✔ |
| 构建 v1（红）→ Play 显示 `prefab=v1` | ✔ |
| 改 Prefab 为 v2（蓝）→ 仅替换 StreamingAssets → Play 显示 `prefab=v2` | ✔ |

## 验收结论

**通过。** Unity 6000.3.22f1 + YooAsset 3.0.6 本地补丁（OfflinePlayMode / StreamingAssets 替换）可用。

- v1：`[YooAsset PoC] OK package=1 prefab=v1`
- v2：场景出现 `PatchCube_v2`，无 `PatchCube_v1`

## 菜单

`Farming/YooAsset PoC/`

1. Ensure Collector Config  
2. Ensure Prefab v1 (Red)  
3. Switch Prefab to v2 (Blue)  
4. Build Package + Copy StreamingAssets (version=1)  
5. Build Package + Copy StreamingAssets (version=2)  
6. Ensure PoC Scene  
7. Clear Yoo Cache (Library/yoo)  
0. Run Full Setup（一次跑完收集器 + Prefab + 构建 v1 + 场景）

## 复现步骤（补丁闭环）

1. 菜单 **0. Run Full Setup**（或已做过可跳过）。
2. 打开 `Assets/Scenes/YooAsset_PoC.unity`，Enter Play → GUI / Console 应为 **prefab=v1**（红立方体）。
3. Exit Play。
4. 菜单 **5. Build Package + Copy StreamingAssets (version=2)**（会把 Prefab 改成蓝 v2 并覆盖 StreamingAssets）。
5. **不要改代码、不要重开工程**，再次 Enter Play → 应为 **prefab=v2**（蓝立方体）。

> 若仍显示旧内容：先菜单 **7. Clear Yoo Cache**，再 Play。

## 关键路径

| 用途 | 路径 |
|---|---|
| 热更资源 | `Assets/Res/PoC/PatchCube.prefab` |
| 运行时加载 | `Assets/Framework/Resource/YooAssetPoCBoot.cs` |
| Prefab 标记 | `Assets/Framework/Resource/PatchCubeMarker.cs` |
| 编辑器工具 | `Assets/Editor/YooAsset/YooAssetPoCTools.cs` |
| 内置包（运行时读取） | `Assets/StreamingAssets/yoo/DefaultPackage/` |
| 构建输出 | `Bundles/StandaloneWindows64/DefaultPackage/<version>/` |
| 收集器配置 | `Assets/AssetBundleCollectorSetting.asset` |

## 说明

本 PoC 用 **OfflinePlayMode + 清空并拷贝 StreamingAssets** 模拟「本地补丁下发」。  
后续 HostPlayMode（CDN）可在此基础上接远程服务器，不改变收集 / 构建管线。
