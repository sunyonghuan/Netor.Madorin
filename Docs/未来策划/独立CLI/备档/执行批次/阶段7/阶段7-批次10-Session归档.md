# 阶段 7 批次 10：Session 归档 : 100%

> 状态：已完成

## 目标

- 完成 `session archive <sessionId>` 与 `session unarchive <sessionId>`。
- 归档只修改 Runtime 权威 Session 元数据，不移动、不删除 CanonicalHistory、投影或 Blob。
- 默认 Session 列表隐藏归档项，`--status archived|all` 可查询归档项。
- CLI 只调用 LocalRuntime 服务入口，不直接访问 `state.db` 或内部数据文件。
- 不实现或勾选 delete 和 compact。

## 冻结契约

- archive 将状态设置为 `Archived`，unarchive 将状态设置为 `Active`。
- 对已经处于目标状态的 Session 重复执行时幂等成功，且不伪造额外状态变化。
- 缺失 Session 返回参数错误和稳定错误码 `SessionNotFound`。
- JSON 输出固定包含 `sessionId` 与小写 `status`；人类输出明确显示目标状态。
- 工作区锁、数据库和权限失败返回工作区/数据错误。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/ISessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionCommandsStage7Tests.cs`
- 本批次文档与阶段 7 进度文档

## 实施检查

- [√] 建立归档、恢复、幂等、列表可见性和缺失 Session 红灯测试。
- [√] 增加 Session 状态更新持久化入口并保持事务原子性。
- [√] 增加 LocalRuntime 归档服务入口。
- [√] 实现 archive/unarchive CLI、稳定输出和错误映射。
- [√] 完成定向、Session 回归、EndToEnd 与严格构建验证。
- [√] 刷新 CodeMap 并按证据同步阶段进度。

## 验证记录

- 红灯：`SessionArchive*` 为 0 / 3，全部命中原 `NotImplemented` 成功占位。
- 定向：`SessionArchive*` 为 3 / 3；归档、恢复、幂等、列表/详情状态和缺失 Session 均闭合。
- Session 与命令矩阵回归：4 个相关 EndToEnd 测试类为 29 / 29。
- EndToEnd：157 / 157 通过。
- 解决方案：724 个通过；3 个真实 Provider 用例按外部凭据条件跳过。
- Debug/Release 严格构建：均为 0 警告、0 错误。
- `madorin session archive/unarchive --help`：命令、必填 `sessionId` 和全局参数与冻结规范一致。
- CodeMap：workspace `session` 已刷新到 overlay revision `57`，本批 5 个 C# 文件、135 个符号更新。
- `git diff --check`：通过；仅有工作树既有 LF/CRLF 提示。
- 07 复合项仍包含 delete、compact 和审计，本批不提前勾选；阶段进度保持 31 / 96。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次10-Session归档.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/ISessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionCommandsStage7Tests.cs`
