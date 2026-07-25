# 阶段 7 批次 9：Session 导出 : 100%

> 状态：已完成

## 目标

- 完成 `session export <sessionId>` 的 `markdown`、`txt`、`jsonl` 输出。
- 默认排除 reasoning 与工具调用/结果详情，仅在显式参数下包含。
- 输出缺省写 stdout；指定 `--output` 时使用原子替换，失败不覆盖旧文件。
- 只通过 LocalRuntime/ConversationStore 服务读取 CanonicalHistory，不由 CLI 拼接内部消息文件路径。
- 不实现或勾选 delete、archive/unarchive 和 compact。

## 冻结契约

- `--format` 接受 `markdown`、`txt`、`jsonl`，默认 `markdown`。
- `--include-reasoning` 和 `--include-tool-calls` 默认关闭。
- 缺失 Session、非法格式和空输出路径返回参数错误；数据或输出文件失败返回工作区/数据错误。
- JSONL 每行是一个经过筛选的完整消息对象；Markdown/TXT 使用稳定角色标签。
- Blob 文本只在既有限额内展开，超限或缺失时保留明确省略标记。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionExportWriter.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionExportStage7Tests.cs`
- 本批次文档与阶段 7 进度文档

## 实施检查

- [√] 建立格式、过滤、stdout、原子文件和失败路径红灯测试。
- [√] 增加 LocalRuntime CanonicalHistory 读取入口。
- [√] 实现三种导出格式与默认脱敏过滤。
- [√] 实现原子输出文件和稳定错误映射。
- [√] 完成定向、EndToEnd、全量与严格构建回归。
- [√] 刷新 CodeMap 并按证据同步阶段进度。

## 验证记录

- 红灯：`StandaloneSessionExportStage7Tests` 为 0 / 5，均命中原 `NotImplemented` 成功占位。
- 定向：`StandaloneSessionExportStage7Tests` 为 5 / 5；默认脱敏、显式包含、TXT、逐行可解析 JSONL、原子替换和错误映射均通过。
- Session 与命令矩阵回归：4 个相关测试类合跑 18 / 18 通过。
- JSONL 保留过滤后空 `content` 的完整消息信封，不删除 GSN/消息序号对应的记录。
- EndToEnd：最终 154 / 154 通过；全量暴露的事件通道半就绪竞态已在独立返工文档闭合。
- 解决方案：721 个通过；3 个真实 Provider 用例按外部凭据条件跳过。
- Debug/Release 严格构建：均为 0 警告、0 错误。
- CodeMap：workspace `session` 已刷新到 overlay revision `56`，123 个文件重建索引、1580 个符号更新。
- `git diff --check`：通过；仅有工作树既有 LF/CRLF 提示。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次9-Session导出.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionExportWriter.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionExportStage7Tests.cs`
