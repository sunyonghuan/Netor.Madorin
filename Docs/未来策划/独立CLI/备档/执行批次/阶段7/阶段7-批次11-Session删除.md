# 阶段 7 批次 11：Session 删除 : 100%

## 目标与冻结边界

- [√] `session delete <sessionId>` 必须显式传入 `--confirm`。
- [√] 默认删除 Runtime 权威数据库记录、`messages/{sessionId}.jsonl` 和可选的 `.compact.json` 投影缓存。
- [√] 默认保留内容寻址 Blob；`--include-blobs` 只移除没有被其他 Session 引用的 Blob。
- [√] CLI 只调用 `LocalRuntime` 高层入口，不直接访问 SQLite、CanonicalHistory 或 Blob 路径。
- [√] 删除前在同一数据目录建立恢复点；文件准备或数据库提交失败时恢复活动文件并保留诊断清单。
- [√] 缺失 Session、拒绝确认、锁冲突和存储故障使用稳定退出码与 JSON 错误码。

## 数据与故障边界

- [√] 已审计 SQLite Schema：只有 Selection、Agent、Meeting 和 Work 主表具有 Session 级联，其他 Session/Run/Step 关联表必须在单个立即事务中显式删除。
- [√] 已确认 CanonicalHistory 为 `messages/{sessionId}.jsonl`，可选投影缓存为 `messages/{sessionId}.compact.json`。
- [√] 已确认 Blob 以 SHA-256 内容寻址并可跨 Session 共享，不能按目标 Session 无条件删除。
- [√] 恢复点清单记录 preparing、prepared、committed 或 restored 状态、移动的活动文件和 Blob，不记录消息正文。
- [√] 数据库提交前失败恢复已移动文件；提交后恢复点作为可审计恢复材料保留。

## 测试与验证

- [√] 未传 `--confirm` 时拒绝删除且 Session 保持可查询。
- [√] 正常删除后 Session、Run、消息索引、Selection、Invocation、Tool/Mode 关联记录均不可见。
- [√] 默认保留 Blob；`--include-blobs` 删除目标独占 Blob，但保留其他 Session 共享 Blob。
- [√] CanonicalHistory 与 `.compact.json` 从活动目录移出，恢复点及清单保留。
- [√] 缺失 Session 返回 `SessionNotFound`，同工作区锁冲突返回稳定工作区错误。
- [√] 定向 Session 命令测试、EndToEnd、全解决方案、Debug/Release 严格构建全部通过。

## 修改文件

- [√] `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次11-Session删除.md`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionDeletionResult.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/ISessionRepository.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/SessionBlobReferences.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSessionRepository.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationStore.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationStoreFailurePoint.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationDeleteRecoveryPoint.cs`
- [√] `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionCommandsStage7Tests.cs`
- [√] `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/ConversationStoreTests.cs`
- [√] `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SqliteSessionRepositoryTests.cs`

## 验证记录

- 红灯：4 个新增删除场景 0 / 4，均命中旧的成功 `NotImplemented` 占位或缺失的 `--include-blobs` 参数。
- 定向回归：`StandaloneSessionCommandsStage7Tests` 14 / 14；删除底层故障与恢复用例 2 / 2。
- 项目回归：Persistence 169 / 169；EndToEnd 161 / 161。
- 解决方案全量：730 个通过；3 个真实 Provider 用例按外部凭据条件跳过。
- 严格构建：Debug/Release `--no-restore -warnaserror -m:1` 均为 0 警告、0 错误。
- CodeMap：baseline `369c49099fd03afd05db23e540096b3bf18f3924`，workspace `session`，overlay revision `63`。
- 差异检查：`git diff --check` 通过；仅有工作树既有的 LF/CRLF 提示。
