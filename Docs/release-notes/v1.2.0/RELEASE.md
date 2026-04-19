# Netor.Cortana v1.2.0 发布说明

**发布日期：** 2026 年 4 月 20 日
**运行时：** .NET 10 | Avalonia 12 | Native AOT
**平台：** Windows 10/11 x64

---

## 🎯 版本亮点

**这是一个里程碑版本。** Cortana 从「一个智能体对话」进化为「多智能体协作」——在聊天框输入 `@` 即可调用任意子智能体，每个子智能体携带自己独立的插件和 MCP 工具集，由主智能体自主决策何时调用、如何编排。

> 一句话总结：**你的 AI 助手现在有了自己的团队。**

---

## ✨ 新特性：@智能体 多智能体协作

### 核心能力

在聊天输入框中输入 `@`，即弹出智能体选择列表。选中后，该子智能体以工具函数形式注入当前对话，由主智能体根据任务需要自主调度。

**关键设计决策：**

| 设计点 | 实现方式 |
|:-------|:---------|
| 调用方式 | 子智能体通过 `AsAIFunction()` 包装为工具，由主智能体自主决定何时调用 |
| 工具携带 | 每个子智能体**完整携带**自己绑定的 Plugins 和 MCP 工具 |
| 独立配置 | 子智能体可配置独立的 AI 厂商和模型，不同任务用不同模型 |
| 一次性调用 | 子智能体不维护对话历史，每次调用独立执行，避免上下文污染 |
| 无侵入 | 不输入 `@` 时，行为与之前完全一致，零成本升级 |

### 使用场景

```
用户：@服务器管理 检查所有服务器的磁盘使用率，超过80%的列出来
```

主智能体收到后会调用「服务器管理」子智能体（该智能体绑定了 SSH 远程管理插件），子智能体自主连接服务器执行检查，将结果返回给主智能体汇总回复。

```
用户：帮我搜一下 @谷歌搜索 最近的 .NET 10 新特性，然后 @文档助手 整理成一份技术周报
```

主智能体按需先调用谷歌搜索智能体获取资料，再调用文档助手智能体生成周报——全程自动编排。

### 输入体验

- **`@` 触发弹窗** — 输入 `@` 后自动弹出已启用的智能体列表，支持模糊搜索
- **键盘导航** — `↑↓` 选择、`Enter/Tab` 确认、`Esc` 关闭，与 `#` 文件补全交互一致
- **多次提及** — 同一条消息中可提及多个不同的智能体

---

## 🏗️ 架构变更

### AI 层：AIAgentFactory 重构

工厂方法从单一 `Build()` 重构为三层构建模式：

| 方法 | 用途 |
|:-----|:-----|
| `Build()` | 构建主智能体（完整：skills + memory + history + plugins + MCP） |
| `BuildSubAgent()` | 构建子智能体（轻量：plugins + MCP，不带历史/记忆/技能） |
| `BuildWithSubAgents()` | 构建带子智能体工具的主智能体（临时注入 AsAIFunction 工具） |

核心工具组装逻辑抽取为 `AssembleToolProviders()`，主/子智能体共享同一套插件和 MCP 加载机制。

新增 `SubAgentContextProvider`：通过 `AIContextProvider` 标准管道将子智能体的 `AIFunction` 注入主智能体上下文。

### Entity 层：智能体模型扩展

`AgentEntity` 新增三个字段，支持子智能体独立配置：

| 字段 | 类型 | 说明 |
|:-----|:-----|:-----|
| `Avatar` | string | 智能体头像路径 |
| `DefaultProviderId` | string | 默认 AI 厂商 ID，空=跟随会话 |
| `DefaultModelId` | string | 默认 AI 模型 ID，空=跟随会话 |

`AgentService` 新增 `GetByName()` 方法，支持按名称精确查找智能体。

新增 `AgentMention` record，表示用户消息中的 @智能体 提及信息。

`IAiChatEngine.SendMessageAsync` 接口增加 `mentions` 可选参数。

### UI 层

- 新增 `AgentPopup` 弹窗，与现有 `FilePopup` 平级
- 输入框 `@` 检测 + 模糊匹配 + ↑↓ 键盘导航
- `SendMessage()` 流程新增 mentions 收集和传递
- 智能体设置页新增「默认厂商」和「默认模型」下拉框，支持厂商/模型联动

### 数据库迁移

通过 `TryAddColumn` 自动迁移，无需手动干预：

```sql
ALTER TABLE Agents ADD COLUMN Avatar TEXT NOT NULL DEFAULT ''
ALTER TABLE Agents ADD COLUMN DefaultProviderId TEXT NOT NULL DEFAULT ''
ALTER TABLE Agents ADD COLUMN DefaultModelId TEXT NOT NULL DEFAULT ''
```

---

## 📋 变更文件清单

### Entity 层
- `Src/Netor.Cortana.Entitys/Entities/AgentEntity.cs` — +3 字段
- `Src/Netor.Cortana.Entitys/Services/AgentService.cs` — ReadEntity/BindEntity/SQL 更新 + GetByName()
- `Src/Netor.Cortana.Entitys/CortanaDbContext.cs` — +3 行 ALTER TABLE 迁移
- `Src/Netor.Cortana.Entitys/Interfaces/IChatTransport.cs` — 新增 AgentMention record
- `Src/Netor.Cortana.Entitys/Interfaces/IAiChatEngine.cs` — SendMessageAsync +mentions 参数

### AI 层
- `Src/Netor.Cortana.AI/AIAgentFactory.cs` — 重构：AssembleToolProviders + BuildSubAgent + BuildWithSubAgents
- `Src/Netor.Cortana.AI/Providers/SubAgentContextProvider.cs` — **新建**
- `Src/Netor.Cortana.AI/AiChatService.cs` — mentions 调度逻辑

### UI 层
- `Src/Netor.Cortana.AvaloniaUI/Views/MainWindow.axaml` — AgentPopup + 输入提示更新
- `Src/Netor.Cortana.AvaloniaUI/Views/MainWindow.axaml.cs` — @ 检测/键盘导航/mentions 收集
- `Src/Netor.Cortana.AvaloniaUI/Views/Settings/AgentSettingsPage.axaml` — 厂商/模型 ComboBox
- `Src/Netor.Cortana.AvaloniaUI/Views/Settings/AgentSettingsPage.axaml.cs` — 联动逻辑

---

## 升级说明

1. 从 v1.1.x 升级可直接替换程序文件，数据库字段自动迁移。
2. 现有智能体的三个新字段（Avatar、DefaultProviderId、DefaultModelId）默认为空，行为与升级前一致。
3. 要使用 @智能体 功能，需在智能体设置中创建并启用多个智能体，并为它们绑定不同的插件/MCP 工具。

---

## V2 预告

- **多智能体讨论模式** — 多个智能体围绕同一话题自主讨论、协商、得出结论
- **HandoffWorkflow** — 基于 Microsoft.Agents.AI.Workflows 的正式多 Agent 编排
- **智能体头像** — 聊天气泡中显示子智能体专属头像

---

> Netor.Cortana v1.2.0 —— 从独角戏到团队协作，你的 AI 助手进化了。🦞
