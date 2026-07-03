# 长期记忆事件链路修复

> 修复目标：让工作模式与会议模式的实时事件能正确进入长期记忆系统，并修复 `AllowWorkflowMemory` 开关失效以及 `OrchestrationTask` 表与运行时数据不一致两个连带问题。

## 文件索引

- [01-问题诊断与影响分析.md](01-问题诊断与影响分析.md) — 检测到的 4 个缺陷及其连锁影响
- [02-修改方案.md](02-修改方案.md) — 事件参数扩展、发布点改造、协议字段补齐、Memory 插件适配
- [03-执行计划.md](03-执行计划.md) — 分阶段落地步骤、验证方式、回滚策略

## 背景

记忆系统的主链路（订阅、握手、批次摄取、协议路由）已接通，但实时事件载荷瘦身过度，导致：

1. **工作模式实时事件进库即丢弃**：`WorkTaskCompletedArgs` 只有 `TaskId / FinalReport`，`AgentId / SourceSessionId / TraceId` 全部为空，被 `MemoryProcessingService` 第 112 行 guard 直接挡掉。
2. **会议模式实时事件元数据残缺**：`SessionId / Topic / WorkspaceId / HostAgentId / Model` 缺失，召回时无法定位源会议、跨工作区可能泄漏。
3. **`AllowWorkflowMemory` 开关永远不生效**：UI 设置项与数据库字段都已就位，但事件 payload 不带，Memory 插件读不到永远走默认 `true`。
4. **`OrchestrationTask` 表与运行时数据脱节**：schema 在 `CortanaDbContext` 创建，但代码只对 `WorkTasks` 表读写；`PluginBusWorkflowHistoryDispatcher` 查 `OrchestrationTask` 等同于查空表，工作模式历史回放永远 0 行。

历史回放在会议模式下可遮蔽问题 2（重启后能补齐），但工作模式因为问题 4 连历史回放也是空的，整体表现为"工作模式跑了一阵子之后什么记忆都没攒住"。

## 落地策略摘要

- 工作模式实时事件补齐 `ManagerAgentId / ManagerAgentName / SourceSessionId / WorkspaceId / CompletedAt / AllowMemoryIngest`，让 Memory 插件能通过 `AgentId + Content` guard，并让 agent 级记忆开关生效。
- 会议模式实时事件补齐 `SessionId / WorkspaceId / Topic / HostAgentId / Model / CreatedAt / EndedAt`，让实时 summary / final 与历史回放字段语义一致。
- 工作模式历史回放从空的 `OrchestrationTask` 改查 `WorkTasks`，但只导出 Memory 插件真正会摄入的成功任务：`CompletedAt IS NOT NULL AND ErrorMessage IS NULL AND IFNULL(FinalReport, '') <> ''`。
- Workflow 与 Meeting 历史回放都使用复合游标分页：`(LastActiveAt, Id)` / `(UpdatedAt, CursorId)`，避免同一毫秒多条记录跨 batch 时漏回放。
- `MeetingExportRecord.CursorId` 仅作为服务端分页辅助字段，使用 `[JsonIgnore]`，不改变 PluginBus payload 协议。

## 修复优先级

P0（阻塞）：问题 1、问题 4
P1（紧跟）：问题 2、问题 3

## 不修改的范围

- 不动记忆插件的存储 schema、抽取算法、衰减策略
- 不动 `LongMemoryContextProvider` 在 Workflow / Meeting 模式下的早退行为（这属于"记忆供应注入"另外一个独立议题，不在本次范围）
- 不引入"记忆 → host UI 反向推送"链路（按用户确认这条不需要）
