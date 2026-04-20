# Netor.Cortana v1.2.1 发布说明

**发布日期：** 2026 年 4 月 20 日
**运行时：** .NET 10 | Avalonia 12 | Native AOT
**平台：** Windows 10/11 x64

---

## 🎯 版本亮点

本版本聚焦于**压缩系统可靠性**和**设置界面体验优化**，修复了老版对话压缩摘要失败导致历史消息丢失的严重问题，同时改进了压缩提示词以提升摘要质量。

---

## 🐛 Bug 修复

### 老版压缩摘要失败导致数据丢失（严重）

**问题：** 老版 `CompactedContext` 系统在 LLM 生成摘要失败时（返回 `[Summary unavailable]`），仍然将 `CompactedAtCount` 标记为已压缩。后续加载历史时，被标记的消息既没有有效摘要覆盖，也不再被加载为原始消息，导致大量对话历史实质丢失。

**修复：** 在 `ProvideChatHistoryAsync` 中加载老版缓存时，检测摘要内容是否有效。若摘要为空或全部包含 `[Summary unavailable]`，则跳过老版缓存，回退到加载全部消息，并输出 Warning 日志。

### 压缩客户端每次创建新实例未释放

**问题：** `ResolveCompactionClient` 每次压缩调用都通过 `driver.CreateChatClient()` 创建新的 `IChatClient` 实例，未缓存也未 Dispose，可能导致连接泄漏。

**修复：** 新增 `_cachedCompactionClient` 字段缓存实例，相同 `Compaction.ModelId` 时复用；配置变更时自动释放旧实例并重建；`ChatHistoryDataProvider` 实现 `IDisposable` 确保退出时释放。

---

## ✨ 改进

### 压缩摘要质量提升

重写压缩提示词，从模糊的"保留技术细节"改为结构化的保留规则：

- **必须完整保留：** 代码块（不截断）、Shell 命令、SQL、文件路径、URL、所有数值型数据、错误信息、用户关键决策
- **可以精简：** 寒暄、重复问答、已被后续修正覆盖的中间尝试
- **输出格式：** Markdown 结构化（标题分隔话题、代码围栏标注语言、关键决策粗体标注）
- **目标比例：** 从"≤30%"调整为"20%~40%，宁多勿少"

### 系统设置界面输入框右对齐

设置项的输入控件（文本框、下拉框、开关等）统一靠右对齐，标签在左、控件在右，布局更加规整。

### 工具管理列表简化

插件和 MCP 服务器条目不再展开显示每个工具的名称列表，仅保留名称和工具数量统计（如 `Google 搜索 (3 个工具)`），避免工具过多时列表冗长。

### TotalTokenCount 注释修正

明确标注 `ChatSessionEntity.TotalTokenCount` 为历史累加值（所有 API 调用之和），不代表当前上下文窗口大小。

---

## 📋 变更文件清单

### AI 层
- `Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs` — 压缩客户端缓存 + IDisposable + 老版摘要失败回退 + 提示词重写

### Entity 层
- `Src/Netor.Cortana.Entitys/Entities/ChatSessionEntity.cs` — TotalTokenCount 注释修正

### UI 层
- `Src/Netor.Cortana.AvaloniaUI/Views/Settings/SystemSettingsPage.axaml.cs` — 输入控件右对齐
- `Src/Netor.Cortana.AvaloniaUI/Views/Settings/ToolManagementPage.axaml.cs` — 移除工具名称列表显示

---

## ⬆️ 升级说明

直接替换文件即可，无需数据库迁移。老版压缩摘要失败的会话将在下次加载时自动回退到完整消息模式。
