# 专家自主子智能体编排

> 完成归档稿 v4
> 当前状态：✅ 已完成，已归档到已完成功能规划。代码闭环已接入，自动化回归已通过；真实模型对话、真实宿主环境工具挂载和专家聊天 UI 人工验收作为后续人工验收项保留。
>
> 目标：专家模式主智能体可以把适合后台执行的工作委派给临时子智能体。主智能体继续与用户对话，并通过任务 ID 查询进度、读取结果或取消任务。结果通过普通工具调用返回，由主智能体用普通助手消息反馈给用户。
>
> 执行拆分：[执行步骤](./执行步骤.md)

---

## 一、业务目标

专家模式需要补充一套轻量级后台委派能力：

1. 用户继续和专家主智能体聊天。
2. 主智能体判断某项工作适合后台执行。
3. 主智能体查看系统工具目录，选择子智能体完成任务所需的工具。
4. 主智能体创建临时子智能体后台任务。
5. 子智能体按主智能体指定的提示词、任务说明和工具挂载执行工作。
6. 系统返回稳定的 `jobId` 给主智能体。
7. 主智能体按需查询进度、读取结果或取消任务。
8. 主智能体把子智能体结果整理成普通消息回复用户。

这套能力定位为专家模式的轻量后台任务编排。正式计划、执行、验收、长期追踪由工作模式承载。

---

## 二、核心原则

### 2.1 可见性与执行权分离

主智能体可以看到系统工具目录，用于判断子智能体需要挂载哪些工具。

主智能体自身的工具执行权保持受限，只能执行专家模式允许的编排类工具和基础能力。

子智能体作为后台任务实例运行，按主智能体声明的 `toolMountsJson` 挂载工具并执行任务。

### 2.2 主智能体只负责编排

主智能体不直接执行 PowerShell、插件、MCP、文件写入等高权限工具。

主智能体负责：

- 判断是否需要委派。
- 选择子智能体工具挂载清单。
- 启动后台任务。
- 查询进度。
- 读取结果。
- 取消任务。
- 整合结果并回复用户。

### 2.3 子智能体是临时任务实例

子智能体绑定到一个 `jobId`，生命周期跟随后台任务。

子智能体不进入用户长期智能体列表，不参与专家会话的同步 `@` 提及链路。

### 2.4 生命周期保持简单

第一版只覆盖：

- 启动任务
- 查询状态
- 读取结果
- 取消任务
- 停止运行中任务
- 清理历史任务

不实现任务恢复、复杂审批流、独立任务面板、专用 UI 或长期任务编排。

### 2.5 会话自然持久化

主智能体调用工具、获得 `jobId`、查询结果、向用户反馈，这些内容按普通工具调用流程进入当前专家会话。

子智能体执行记录保存在后台任务记录中，用于状态查询、结果读取和问题排查。

---

## 三、角色职责

### 3.1 主智能体

主智能体负责：

- 理解用户需求。
- 判断是否需要创建后台子智能体任务。
- 查看系统工具目录。
- 选择子智能体工具挂载清单。
- 创建后台子智能体任务。
- 向用户说明任务已启动。
- 查询任务进度。
- 读取任务结果。
- 取消任务。
- 整合子智能体结果并回复用户。

主智能体执行的工具集合：

- `list_system_tool_catalog`
- `start_autonomous_subagent_task`
- `get_subagent_task_status`
- `read_subagent_task_result`
- `cancel_subagent_task`

### 3.2 子智能体

子智能体负责：

- 按主智能体提供的 `childInstructions` 进入角色。
- 按 `task` 完成指定工作。
- 使用 `toolMountsJson` 中挂载的工具。
- 持续更新任务状态和进度摘要。
- 输出可被主智能体读取和转述的结果。

### 3.3 系统工具目录

系统工具目录是只读能力清单。

它提供：

- 工具名称
- 工具来源
- 工具类别
- 工具说明
- 风险等级或能力类型
- 参数摘要

主智能体通过该目录选择子智能体需要的工具。看到目录不等于获得执行权。

---

## 四、工具协议

### 4.1 `list_system_tool_catalog`

用途：列出系统中可供后台子智能体挂载的工具。

返回字段：

```json
{
  "tools": [
    {
      "toolName": "sys_read_file",
      "source": "FileOperationProvider",
      "category": "file",
      "description": "读取文件内容",
      "capability": "read",
      "riskLevel": "low",
      "parameterSummary": "path"
    }
  ]
}
```

约束：

- 返回只读目录。
- 不暴露敏感密钥。
- 不改变主智能体可执行工具集合。
- 工具目录可以包含主智能体不能直接执行的工具。

### 4.2 `start_autonomous_subagent_task`

用途：创建后台子智能体任务。

参数：

```json
{
  "childName": "repo-investigator",
  "childInstructions": "你是代码调查专员，负责读取、搜索和分析代码，输出结论和证据。",
  "task": "调查专家模式中后台任务接入点，输出实现建议。",
  "toolMountsJson": "[\"sys_search_files\",\"sys_read_file\",\"sys_execute_powershell\"]",
  "attachmentPathsJson": "[]",
  "providerId": null,
  "modelId": null,
  "outputContract": "Markdown 调查报告，包含结论、证据、风险和建议。"
}
```

返回：

```json
{
  "jobId": "chatjob_01JZ...",
  "state": "running",
  "childName": "repo-investigator",
  "message": "后台子智能体任务已启动。"
}
```

约束：

- `toolMountsJson` 必须来自系统工具目录，格式为 JSON 数组字符串。
- `providerId`、`modelId`、`attachmentPathsJson` 和 `outputContract` 均为可选参数；不需要指定时可以省略。
- `attachmentPathsJson` 省略时按空附件列表处理。
- `childInstructions` 和 `task` 写入后台任务记录。
- `jobId` 返回后进入主智能体上下文。

### 4.3 `get_subagent_task_status`

用途：查询后台子智能体任务状态。

返回：

```json
{
  "jobId": "chatjob_01JZ...",
  "state": "running",
  "progressDescription": "正在分析 AIAgentFactory.cs",
  "updatedAt": "2026-06-14T10:20:00Z"
}
```

### 4.4 `read_subagent_task_result`

用途：读取任务结果。任务未完成时返回当前进度摘要。

返回：

```json
{
  "jobId": "chatjob_01JZ...",
  "state": "completed",
  "result": "..."
}
```

约束：

- 结果作为普通工具调用结果返回给主智能体。
- 主智能体将结果整理后用普通助手消息回复用户。

### 4.5 `cancel_subagent_task`

用途：取消指定后台子智能体任务。

返回：

```json
{
  "jobId": "chatjob_01JZ...",
  "state": "cancelled",
  "message": "后台子智能体任务已取消。"
}
```

---

## 五、数据模型

### 5.1 `DelegatedAgentJobs`

| 字段 | 说明 |
| --- | --- |
| `Id` | jobId |
| `ScopeKind` | `chat` / `work` / `meeting` |
| `ScopeId` | chat sessionId / work taskId / meetingId |
| `ParentTurnId` | 发起任务的专家 turn |
| `ParentAgentId` | 主智能体 ID |
| `ChildName` | 临时子智能体名称 |
| `ChildInstructions` | 子智能体提示词 |
| `TaskInputJson` | 任务输入 JSON |
| `ToolMountsJson` | 子智能体工具挂载列表 |
| `ProviderId` | 使用的厂商 |
| `ModelId` | 使用的模型 |
| `State` | pending / running / completed / failed / cancelled |
| `ProgressDescription` | 当前进度 |
| `ResultJson` | 最终结果 |
| `Error` | 错误信息 |
| `CreatedAt` | 创建时间 |
| `UpdatedAt` | 更新时间 |
| `CompletedAt` | 完成时间 |

### 5.2 `DelegatedAgentJobLogs`

记录后台任务的轻量日志：

- started
- progress
- completed
- failed
- cancelled

用途：

- 支撑状态查询。
- 支撑结果读取。
- 支撑异常排查。
- 支撑历史任务清理判断。

---

## 六、运行流程

### 6.1 启动任务

1. 用户向专家提出需求。
2. 主智能体调用 `list_system_tool_catalog` 查看工具目录。
3. 主智能体选择子智能体需要的工具。
4. 主智能体调用 `start_autonomous_subagent_task`。
5. 系统创建 `DelegatedAgentJobs` 记录。
6. 系统构建临时子智能体。
7. 系统按 `toolMountsJson` 给子智能体挂载工具。
8. 系统启动后台任务并返回 `jobId`。
9. 主智能体用普通消息告诉用户任务已启动。

### 6.2 查询进度

1. 主智能体按需要调用 `get_subagent_task_status`。
2. 系统读取后台任务状态。
3. 主智能体用普通消息向用户反馈当前进展。

### 6.3 读取结果

1. 主智能体调用 `read_subagent_task_result`。
2. 系统返回最终结果或当前摘要。
3. 主智能体将结果整理后用普通消息回复用户。

### 6.4 取消任务

1. 用户或主智能体决定停止后台任务。
2. 主智能体调用 `cancel_subagent_task`。
3. 系统取消后台任务并更新状态。
4. 主智能体用普通消息告知用户取消结果。

### 6.5 清理任务

系统在应用启动时清理超过保留期的已完成、失败、取消历史任务记录。

清理策略只处理后台任务表和日志表，不影响专家会话消息。

---

## 七、代码接入点

### 7.1 专家模式工具注入

接入位置：

- [ChatTurnPreparationService.CreateChatHandoffTools](../../../Src/Netor.Cortana.AI/ChatTurnPreparationService.cs#L171)

新增：

- `ExpertDelegationTools`

注入工具：

- `list_system_tool_catalog`
- `start_autonomous_subagent_task`
- `get_subagent_task_status`
- `read_subagent_task_result`
- `cancel_subagent_task`

### 7.2 Agent 构建

接入位置：

- [AIAgentFactory.Build](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L145)
- [AIAgentFactory.BuildWithSubAgents](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L233)

需要能力：

- 按 `childInstructions` 构建临时子智能体。
- 按 `ToolMountsJson` 解析并挂载工具。
- 子智能体不挂主会话历史。
- 子智能体执行完成后将结果写入后台任务记录。

### 7.3 后台执行器

新增：

- `DelegatedAgentJobExecutor`
- `DelegatedAgentJobService`
- `DelegatedAgentJobRunner`

职责：

- 创建任务。
- 管理运行中任务。
- 构建并执行子智能体。
- 更新任务状态。
- 保存进度摘要。
- 保存最终结果。
- 处理取消。
- 执行历史清理。

### 7.4 工具目录服务

新增：

- `SystemToolCatalogService`

职责：

- 汇总系统工具、插件工具、MCP 工具和内置工具。
- 输出只读工具目录。
- 标记工具来源、类别、能力和说明。
- 为 `toolMountsJson` 校验提供工具名称集合。

### 7.5 会话回流

后台任务结果通过 `read_subagent_task_result` 返回给主智能体。

主智能体负责将工具结果整理成普通助手消息。

界面复用现有聊天消息和工具调用渲染流程，不新增专用 UI。

---

## 八、实施步骤

### Step 1：数据模型与服务

交付：

- `DelegatedAgentJobEntity`
- `DelegatedAgentJobLogEntity`
- `DelegatedAgentJobService`
- SQLite 表初始化和索引
- JSON 序列化上下文

验收：

- 可以创建任务记录。
- 可以更新任务状态。
- 可以读取任务结果。
- 可以取消任务。
- 可以清理历史任务。

### Step 2：系统工具目录

交付：

- `SystemToolCatalogService`
- `list_system_tool_catalog`
- `toolMountsJson` 名称校验

验收：

- 主智能体能看到系统工具能力清单。
- 工具目录只读。
- 工具目录不改变主智能体执行权。
- 子智能体工具挂载只能引用目录内工具。

### Step 3：专家编排工具

交付：

- `ExpertDelegationTools`
- `start_autonomous_subagent_task`
- `get_subagent_task_status`
- `read_subagent_task_result`
- `cancel_subagent_task`

验收：

- 主智能体能启动后台任务。
- 工具返回稳定 `jobId`。
- 主智能体能查询状态。
- 主智能体能读取结果。
- 主智能体能取消任务。
- 启动、查询和读取失败时返回可读 JSON 错误。

### Step 4：后台执行器

交付：

- `DelegatedAgentJobExecutor`
- `DelegatedAgentJobRunner`
- 运行中任务管理
- 取消和停止处理
- 历史任务清理

验收：

- 子智能体可以在后台执行。
- 子智能体能使用 `toolMountsJson` 中声明的工具。
- 任务完成后状态为 completed。
- 任务失败后状态为 failed。
- 任务取消后状态为 cancelled。
- 应用停止时能停止运行中任务。

### Step 5：普通消息反馈

交付：

- `read_subagent_task_result` 返回普通工具结果。
- 主智能体读取结果后向用户反馈。
- 现有聊天消息流程展示主智能体回复。

验收：

- 用户可以继续与主智能体对话。
- 后台任务结果可被主智能体读取。
- 结果自然出现在当前专家会话里。
- 不出现专用后台任务界面。

### Step 6：测试回归

测试范围：

- 专家普通聊天。
- 系统工具目录可见性。
- 主智能体执行工具限制。
- 后台子智能体工具挂载。
- 后台任务启动、查询、读取、取消。
- 后台任务停止和清理。
- 专家 `@` 子智能体同步调用回归。
- 工作模式回归。
- 会议模式回归。

---

## 九、影响范围

### 9.1 AI 层

涉及：

- `ChatTurnPreparationService`
- `ChatAgentResolver`
- `AIAgentFactory`
- `ExpertDelegationTools`
- `SystemToolCatalogService`
- `DelegatedAgentJobExecutor`

影响：

- 专家模式多一组编排工具。
- 主智能体提示词需要明确工具可见性和执行权边界。
- 临时子智能体构建需要支持动态提示词和工具挂载。
- `Build` / `BuildWithSubAgents` 周边需要保持兼容，避免影响同步 `@` 子智能体调用。

评估：

- 影响等级：中。
- 主要风险：工具目录可见、主智能体不可执行、子智能体可挂载三者的边界必须清楚。
- 建议工作量：1.5-2.5 个工作日。

### 9.2 工具与插件层

涉及：

- 内置工具 Provider
- 插件工具 Provider
- MCP 工具 Provider
- 文件工具
- PowerShell 工具

影响：

- 需要统一汇总工具目录。
- 需要按 `toolMountsJson` 构建子智能体工具集合。
- 需要处理工具不存在、工具加载失败、工具名称冲突等情况。

评估：

- 影响等级：中高。
- 主要风险：系统工具来源分散，目录汇总和实际挂载必须使用同一套工具标识。
- 建议工作量：1-2 个工作日。

### 9.3 数据层

涉及：

- `CortanaDbContext`
- 新实体
- 新服务
- SQLite 表初始化
- JSON 序列化上下文

影响：

- 新增后台委派任务表。
- 新增后台任务日志表。
- 需要任务状态索引和清理策略。

评估：

- 影响等级：中。
- 主要风险：任务状态更新和取消处理需要避免并发写入混乱。
- 建议工作量：1 个工作日。

### 9.4 后台执行层

涉及：

- 后台任务 Runner
- 取消令牌管理
- 运行中任务注册
- 应用停止清理

影响：

- 新增专家模式后台执行链路。
- 需要限制后台任务并发数量，上限为 50 个运行中任务。
- 需要保证取消、失败、完成都能落库。

评估：

- 影响等级：中高。
- 主要风险：后台模型调用、工具调用、取消和异常状态需要完整闭环。
- 建议工作量：1.5-2 个工作日。

### 9.5 UI 层

涉及：

- 现有专家聊天消息渲染
- 现有工具调用渲染

影响：

- 不新增专用 UI。
- 不新增后台任务卡片、弹窗、任务面板或独立入口。
- `jobId`、状态和结果通过普通工具调用结果进入上下文，再由主智能体用普通消息回复。

评估：

- 影响等级：低。
- 建议工作量：0-0.5 个工作日。
- 验证重点：现有聊天和工具调用展示不会渲染空界面。

### 9.6 工作模式与会议模式

涉及：

- 工作模式总经理智能体
- 会议模式主持人智能体
- 通用 Agent 构建链路

影响：

- 第一版只接入专家模式。
- 工作模式和会议模式不新增入口。
- 通用构建能力需要保持兼容，避免破坏已有链路。

评估：

- 影响等级：低到中。
- 主要风险：共享 `AIAgentFactory` 改动波及其他模式。
- 建议工作量：0.5-1 个工作日，主要用于回归测试。

---

## 十、工作量评估

### 10.1 已完成工作量

| 模块 | 工作量 |
| --- | --- |
| 数据模型与服务 | 1 天 |
| 系统工具目录与工具挂载 | 1-2 天 |
| 专家编排工具 | 1 天 |
| 后台执行器与生命周期 | 1.5-2 天 |
| 普通消息回流验证 | 0-0.5 天 |
| 测试与回归 | 1-1.5 天 |
| 合计 | 5.5-8 天 |

### 10.2 剩余验收工作量

| 模块 | 工作量 |
| --- | --- |
| 真实专家模型对话验收 | 0.5-1 天 |
| 真实插件/MCP/PowerShell 宿主环境挂载验收 | 0.5-1 天 |
| 专家聊天 UI 人工验收 | 0.5 天 |
| 工作模式和会议模式真实场景人工回归 | 0.5 天 |
| 合计 | 2-3 天 |

### 10.3 难度判断

整体修改难度：中等。

难点不在 UI，而在工具系统边界：

- 主智能体可见系统工具目录。
- 主智能体不能直接执行目录中的高权限工具。
- 子智能体可以按 `toolMountsJson` 获得工具。
- 后台任务状态、取消、失败和结果需要稳定落库。

### 10.4 验证建议

建议按真实链路验收：

1. 在专家模式真实对话中让主智能体调用 `list_system_tool_catalog`。
2. 创建只挂载文件读取工具的后台任务，查询状态并读取结果。
3. 创建挂载 PowerShell 工具的后台任务，确认工具输出可被主智能体读取。
4. 在真实插件、MCP、PowerShell 宿主环境中创建后台任务，确认目录可见和运行时挂载一致。
5. 创建长任务并取消，确认状态进入 cancelled。
6. 检查专家聊天 UI 没有空消息气泡或空工具卡。
7. 回归工作模式总经理工具集合、工作后台任务、会议主持人/参会者工具集合。

第一版范围保持为轻量级后台委派，交付边界是普通工具调用回流、任务启动、状态查询、结果读取、取消和历史清理。

---

## 十一、验收标准

第一版完成时满足：

1. 专家模式主智能体能看到系统工具目录。
2. 专家模式主智能体自身执行工具仍受限。
3. 主智能体能创建后台子智能体任务。
4. 后台任务返回稳定 `jobId`。
5. 子智能体能使用 `toolMountsJson` 中声明的工具。
6. 用户可以继续与主智能体聊天。
7. 主智能体能查询任务进度。
8. 主智能体能读取任务结果。
9. 主智能体能取消任务。
10. 系统能停止和清理后台任务。
11. 任务结果通过普通工具调用结果返回主智能体。
12. 主智能体用普通助手消息回复用户。
13. 不新增专用后台任务界面。
14. 普通专家聊天不受影响。
15. `@` 子智能体同步调用不受影响。
16. 工作模式不受影响。
17. 会议模式不受影响。

---

## 十二、里程碑

| 阶段 | 目标 | 结果 |
| --- | --- | --- |
| M1 | 数据模型与服务 | 后台任务可记录、查询、取消和清理 |
| M2 | 工具目录 | 主智能体可见系统工具清单 |
| M3 | 专家编排工具 | 主智能体可启动和管理后台任务 |
| M4 | 后台执行器 | 子智能体可使用挂载工具后台执行任务 |
| M5 | 普通消息回流 | 任务结果通过普通工具调用和助手消息回到会话 |
| M6 | 测试回归 | 专家、工作、会议模式稳定 |

---

## 十三、当前自动化验证

已通过：

- `dotnet build Netor.Cortana.slnx`
- `dotnet build Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj -c Release`
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --no-restore`（124 passed）
- `dotnet test Tests\Netor.Cortana.Entitys.Tests\Netor.Cortana.Entitys.Tests.csproj --no-restore`
- `dotnet test Tests\Netor.Cortana.MeetingMode.Tests\Netor.Cortana.MeetingMode.Tests.csproj --no-build --no-restore`
- `dotnet test Tests\Netor.Cortana.UI.Tests\Netor.Cortana.UI.Tests.csproj --no-restore --filter "FullyQualifiedName~UiChatOutputChannelTests"`
- `dotnet test Tests\Netor.Cortana.UI.Tests\Netor.Cortana.UI.Tests.csproj --no-restore`
- `dotnet test Tests\Netor.Cortana.Plugin.Tests\Netor.Cortana.Plugin.Tests.csproj --no-restore`
- `dotnet test Tests\Netor.Cortana.Plugin.Process.Tests\Netor.Cortana.Plugin.Process.Tests.csproj --no-restore`
- `dotnet test Tests\Netor.Cortana.Networks.Tests\Netor.Cortana.Networks.Tests.csproj --no-restore`
- `dotnet test Tests\Netor.Cortana.Store\Netor.Cortana.Store.Tests.csproj --no-restore`
- `dotnet test Tests\Netor.Cortana.Platform\Netor.Cortana.Platform.Tests\Netor.Cortana.Platform.Tests.csproj --no-restore`

剩余人工验收：

- 真实专家模型对话端到端。
- 真实插件、MCP、PowerShell 宿主环境工具挂载。
- 专家聊天 UI 空界面人工确认。
- 工作模式和会议模式真实场景回归。
