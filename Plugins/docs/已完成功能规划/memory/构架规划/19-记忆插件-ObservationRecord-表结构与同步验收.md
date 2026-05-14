# 记忆插件 ObservationRecord 表结构与同步验收

## 范围
- 仓库：Plugins/Src/Cortana.Plugins.Memory
- 表：observation_records（memory.db）
- 目标：按照《05-记忆体系设计》补齐 ObservationRecord 所需字段，并验证批量导入/实时入库路径同步新列。

## 设计对齐
- 文档：Docs/记忆体系的搭建/05-记忆体系设计.md
- ObservationRecord 关键字段：id、agentId、sessionId、turnId、messageId、eventType、role、content、attachments、timestamp、traceId、sourceFacts。
- 本次实现映射：timestamp → createdTimestamp（保留现有命名，语义对齐）。

## 实施项
- 表新增列：agentId、turnId、messageId、eventType、attachments（JSON，默认[]）、traceId、sourceFacts（JSON，默认{}）。
- 幂等迁移：启动时 TryAddColumn 补列，兼容旧库。
- 索引：
  - IX_obs_session_time(sessionId, createdTimestamp)
  - IX_obs_messageId(messageId)
  - IX_obs_eventType(eventType)
- 写入路径：
  - 批量导入 IngestExportBatch：填充上述列；attachments 支持 attachments/Attachments/assets/Assets；sourceFacts 写入整条 item 原始 JSON。
  - 实时事件 TryIngestLiveEvent：根据 eventType 派生 messageId；content 写文本；attachments 合并；sourceFacts 写入 payload 原始 JSON；尽力填充 AgentId/TurnId/TraceId。

## 验证步骤
1) 构建插件与控制台
```powershell
dotnet build e:\Netor.me\Cortana\Plugins\Src\Cortana.Plugins.Memory\Cortana.Plugins.Memory.csproj -c Debug
dotnet build e:\Netor.me\Cortana\Tests\MemoryIngestConsole\MemoryIngestConsole.csproj -c Debug
```
2) 运行控制台（启动即建库 + 打印表结构/索引）
```powershell
dotnet run --project e:\Netor.me\Cortana\Tests\MemoryIngestConsole\MemoryIngestConsole.csproj -c Debug
```

## 运行摘录
- 连接 feed 失败（本地未启服务）属预期，不影响建库：
  - 连接 conversation-feed 失败：ws://localhost:65321/internal/conversation-feed/
- 表结构（节选）：
  - id TEXT PK
  - sessionId TEXT NOT NULL
  - role TEXT NOT NULL
  - content TEXT NULL
  - createdTimestamp INTEGER NOT NULL
  - modelName TEXT NULL
  - agentId TEXT
  - turnId TEXT
  - messageId TEXT
  - eventType TEXT
  - attachments TEXT NOT NULL DEFAULT '[]'
  - traceId TEXT
  - sourceFacts TEXT NOT NULL DEFAULT '{}'
- 索引：
  - IX_obs_eventType
  - IX_obs_messageId
  - IX_obs_session_time

## 结果
- 表结构与索引按设计补齐，控制台成功输出 schema 与索引名称，验收通过。

## 后续建议
- 若需要对 delta 片段做合并，可在召回侧基于 `messageId` 聚合 `assistant.delta`；当前保留片段利于重放与审计。
- 预留下阶段表骨架：memory_fragments / memory_links / memory_abstractions（仅建表与索引，不改召回），便于后续迭代。
