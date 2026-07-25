# 阶段 7 批次 12：Session 压缩 : 100%

## 目标与冻结边界

- [√] 审计 `session compact` 命令规范、现有成功占位和 ContextProjection 策略。
- [√] `session compact <sessionId>` 支持 `full`、`sliding-window`、`summary`，默认 `summary`。
- [√] Provider/模型缺省取 Session 当前 Selection，`--provider`、`--model` 只覆盖本次压缩。
- [√] `--dry-run` 只返回计划和 token 估算，不写缓存、不调用 Provider 生成摘要。
- [√] `--force` 绕过有效缓存；否则源历史哈希、策略、Provider、模型和窗口一致时复用缓存。
- [√] CLI 只调用 `LocalRuntime` 高层入口，不直接读写 CanonicalHistory 或 `.compact.json`。

## 数据与策略边界

- [√] `.compact.json` 是非权威缓存，记录 Schema、源历史哈希、策略、Provider/模型、token 估算和投影消息。
- [√] 缓存使用同目录临时文件、flush-to-disk 和原子替换；损坏或过期缓存可安全重建。
- [√] Full/TailWindow 复用现有 ContextProjection 原子单元规则，不拆分工具调用与结果。
- [√] Summary 使用独立无工具 Provider 请求生成一条摘要消息，失败不得覆盖现有有效缓存。
- [√] CanonicalHistory 在 dry-run、缓存命中、重新生成和失败场景下均保持逐字节不变。

## 测试与验证

- [√] 红灯测试命中旧 `NotImplemented` 占位和缺失参数。
- [√] dry-run 返回默认/覆盖选择和估算，且不创建缓存、不调用摘要 Provider。
- [√] summary 首次生成、缓存复用、`--force` 和历史变化失效均可观察。
- [√] sliding-window 保持工具调用/结果原子单元，并按 token 预算裁剪。
- [√] 缺失 Session、非法策略/窗口和工作区锁冲突返回稳定错误。
- [√] 定向回归、全解决方案、Debug/Release 严格构建、CodeMap 和差异检查全部通过。

## 修改文件

- [√] `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次12-Session压缩.md`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Modes.Expert/Madorin.AI.Runtime.Modes.Expert.csproj`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionCompactionOptions.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionCompactionResult.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationCompactCacheV1.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationJsonContext.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationStore.cs`
- [√] `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionCommandsStage7Tests.cs`
- [√] `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/ConversationStoreTests.cs`

## 验证记录

- 红灯：`StandaloneSessionCommandsStage7Tests` 原 14 项通过，新增 6 项失败；失败命中缺少 `--dry-run`、非法参数未校验及旧的成功 `NotImplemented` 占位。
- 定向回归：`StandaloneSessionCommandsStage7Tests` 21 / 21；`ConversationStoreTests` 20 / 20；包含 Provider 失败不覆盖既有有效缓存的故障用例。
- 项目回归：Persistence 171 / 171；EndToEnd 168 / 168。
- 解决方案全量：739 个通过；3 个真实 Provider 用例按外部凭据条件跳过。
- 严格构建：Debug/Release `--no-restore -warnaserror -m:1` 均为 0 警告、0 错误。
- Native AOT：Windows `win-x64` Release 发布成功，0 条 trimming/AOT 警告。
- CodeMap：baseline `369c49099fd03afd05db23e540096b3bf18f3924`，workspace `session`，overlay revision `66`。
- 差异检查：`git diff --check` 通过；仅有工作树既有的 LF/CRLF 提示。
