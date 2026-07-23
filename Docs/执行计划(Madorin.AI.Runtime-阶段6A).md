# 阶段 6A 专家模式与 JSONL 推进计划 : 100%

## Step 1 复核约束与当前基线 : 100%
- [√] 读取阶段 6A、执行步骤总览及规定的上位设计文档。
- [√] 建立 CodeMap baseline 和隔离 workspace，纳入当前未提交代码。
- [√] 核对 .NET SDK、目标框架、现有进度和工作区并行改动。

## Step 2 审计未完成项与依赖门禁 : 100%
- [√] 逐项定位 Invocation 生命周期、事件归一化和唯一终态实现证据。
- [√] 审计 JSONL 大内容 Blob、索引重建和受限 Blob 读取缺口。
- [√] 审计 TurnOverride、session.resume/rehydrate 和恢复首轮校验缺口。
- [√] 区分本轮可独立闭合项与阶段 4B/5、真实 Provider、宿主反向 RPC 门禁项。

审计结论（2026-07-23）：

- `ExpertModeOrchestrator` 会吞掉由 Run timeout 触发的取消并持久化为 `Cancelled`，导致外层 `RuntimeServer` 无法发出 `run.failed/RunTimedOut`；该问题已有失败的端到端用例，可在本轮闭合。
- 大文本 Blob 外置、Blob 元数据/流/省略读取模式和索引重建入口已经存在，但缺少针对性测试；索引重建当前只恢复 `message_index`，尚未从 JSONL header 恢复 `sessions`。
- `TurnOverride` 当前仅用于本轮 `effectiveSelection`，未发现写回 `_sessionSelections` 的路径，但仍需端到端测试固定该契约。
- `session.resume/rehydrate` 已具备历史摘要、Selection、AgentSnapshot、Blob ID 和版本校验基础；`LastCheckpoint`、完整附件描述和完全可恢复诊断仍未闭合。
- 每 Invocation 新建 MAF `IAgent`、真实 Provider、生产 Tool Gateway 与宿主/MCP 反向 RPC 联合验收不在本轮最小修补范围，继续受阶段 4B/5 与外部环境门禁约束。

## Step 3 补齐阶段 6A 可独立实现 : 100%
- [√] 先补失败用例、协议快照或测试替身，固定剩余行为。
- [√] 实现审计确认的最小缺口，不改动无关阶段代码。
- [√] 对并行阶段已有改动进行兼容性审查，避免覆盖用户工作。

## Step 4 验证质量与恢复链路 : 100%
- [√] 运行阶段 6A 定向测试并处理新增失败。
- [√] 运行涉及项目 Debug/Release 严格构建。
- [√] 刷新 CodeMap overlay 并复核变更后的调用关系。
- [√] 记录仍受外部凭据、跨平台或前置阶段约束的未验收项。

## Step 5 更新执行文档与总览 : 100%
- [√] 仅将有代码、测试和验证证据的检查项标记为完成。
- [√] 重算阶段 6A 各章节、总进度及执行步骤总览。
- [√] 在本文末尾登记全部修改文件和验证结果。

## 修改文件

- `Docs/执行计划(Madorin.AI.Runtime-阶段6A).md`
- `Docs/未来策划/独立CLI/执行步骤/06A-专家模式与JSONL.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeExecutionCore.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationStore.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/ConversationStoreTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeControlTests.cs`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-timeout-terminal.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-timeout-terminal-rework-1.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-timeout-terminal-rework-2.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-timeout-terminal-rework-3.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-timeout-terminal-rework-4.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-jsonl-rebuild-blob.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-jsonl-rebuild-rework-1.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-jsonl-rebuild-tests.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-jsonl-rebuild-tests-rework-1.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-jsonl-rebuild-tests-rework-2.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-turn-override-test.md`
- `Src/Madorin.Ai.Runtime/qwen-stage6a-turn-override-test-rework-1.md`

## 验证结果（2026-07-23）

- `ConversationStoreTests`：14/14 通过，覆盖 JSONL header 重建 Session/消息索引、备份、不可恢复诊断及大正文 Blob 的 Metadata/Stream/Omit/超限读取。
- `RuntimeControlTests`：10/10 通过，覆盖 timeout 的 `Failed/RunTimedOut` 唯一终态及 `TurnOverride` 不写回 Session Selection。
- 6A 模式定向测试：11/11 通过；协议与版本化快照定向测试：23/23 通过。
- 全解决方案 Debug/Release `dotnet build Madorin.AI.Runtime.slnx --no-restore -warnaserror` 均为 0 警告、0 错误。
- CodeMap baseline 为 `f7792ae2d12cf563e3a0c4e34115a424ffe5ea5d`，workspace 为 `session-06a`，最终 overlay revision 为 10。

## 保留门禁

- 每 Invocation 新建 MAF `IAgent`、完整恢复元数据、真实 Provider 调用、生产 Tool Gateway、宿主/MCP 反向 RPC、跨平台路径矩阵和发布制品验收仍受阶段 4B/5 或外部环境约束。
- 阶段 6A 执行文档保持“执行中”，本计划的 100% 仅表示本轮确认可独立闭合的范围已经完成。
