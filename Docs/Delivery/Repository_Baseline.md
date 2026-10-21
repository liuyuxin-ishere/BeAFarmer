# 完整工程仓库基线（2026-10-21）

本次按用户确认，将 Git 根目录从 `Assets` 移至 Unity 工程根目录。原提交
`f37bd5083b88ad5087356b95b78db570667cdd25` 保留为直接祖先，未重写历史。
迁移前完整 Git bundle 与索引备份保存在本地忽略目录 `Docs/LocalBackups`。

## 纳入内容

- Assets 及原有 `.meta`；Packages、ProjectSettings。
- Server、Shared 协议/配置源、Tools 生成/构建脚本及必需工具依赖。
- M1 实现复盘、M2 的 80 项开发计划与筛选后的 M1 交付证据。

Library、构建成品、数据库、账号与 Token、原始代理会话、完整本地验收目录被忽略。
可上传的 M1 证据见 [M1 验收报告](M1/M1_Acceptance.md) 和 SHA 清单。

## 上传前验证

从暂存区导出独立源码目录，编译 `Server/CloudIsle.Tests/CloudIsle.Tests.csproj`
（Release，.NET 8）。首次编译失败：`Server/.gitignore` 的 `data/` 在 Windows
不区分大小写，递归忽略了 `CloudIsle.Server/Data` 的四份持久化源码。
修复为仅指向 Server 根运行数据的 `/data/`，重新导出后编译成功，0 错误。
已有 NUnit 断言风格等 57 项警告保留，不影响编译结果。

独立导出源码的服务端回归：225/225 通过，0 失败、0 跳过（17 秒）。
隔离环境首次运行在测试宿主连接阶段中止，增加超时仍未连接；在正常本机权限下
运行同一构建即通过，未修改业务代码。原始中止报告与通过 TRX 均保存在本地
`Docs/LocalBackups/upload-index-tests`；本次使用 `upload-source-r3.trx`，
中止报告不计作通过结果。

历史 `M1_06_Logs.md` 的原始字节前缀 SHA 与迁移前相同。
暂存区没有大于等于 100 MiB 的文件，没有匹配到检查的私钥/访问令牌格式；
95 份导出的 M1 证据对实际使用过的私密值扫描为 0 匹配。

## M2 计划审查

由 Cursor 只读审查后修正以下问题，再进入实施：

| 问题 | 已落入计划的修正 |
|---|---|
| 最终门槛对 Native 闭环依赖不直观 | T070 显式依赖 T065 |
| 初始赠送可能早于提交协调器 | T008 只构造候选；T012 经统一事务一次发放 |
| scope 早于正式协议生成 | T009 增加 T004 依赖 |
| 内存第 65 项淘汰被误当持久结果过期 | L1 miss 回查 DB；仅真实过期才拒绝重做 |
| 经济幂等可能沿用 M1 内存实现 | T010 明确使用持久账本，业务拒绝也记录 |
| 固定新基包门槛可被旧 Token 绕过 | Login 和 Reconnect 都校验版本与桥信息 |

M2 的每项实现另在 `Assets/ReferenceDoc/Logs/M2_*_Logs.md` 留执行记录；
本文件与开发计划不代表 M2 功能已经完成。
