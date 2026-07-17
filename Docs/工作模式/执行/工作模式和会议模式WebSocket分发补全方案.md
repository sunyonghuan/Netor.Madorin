# 工作模式和会议模式 WebSocket 事件分发补全方案

> **状态**：✅ 阶段 1-2 编码完成，自动化端到端式验证通过
> **优先级**：P1（工作模式）/ P2（会议模式）
> **创建日期**：2026-06-02
> **目标版本**：v1.4.x

> **最新进度（2026-06-02）**：工作模式实时推送、会议模式历史回放与实时推送均已完成代码实现；Networks 测试已覆盖真实 PluginBus WebSocket 订阅、实时广播与 meeting history replay；Memory 插件测试已覆盖 `workflow.task.completed`、会议阶段性 summary、会议最终 summary 入库。会议长期记忆采用 summary-only 策略，普通会议发言不入库。

---

## 一、背景与问题

### 1.1 当前状态

根据 2026-06-02 代码复查，WebSocket 事件分发机制在三种模式下的完成度如下：

| 模式 | 历史回放 | 实时推送 | 记忆插件集成 | 完成度 |
|------|---------|---------|------------|--------|
| **对话模式** | ✅ 完整 | ✅ 完整 | ✅ 完整 | 100% |
| **工作模式** | ✅ 完整 | ✅ 自动化端到端式验证通过 | ✅ 插件侧自动化入库验证通过，待真实联调 | 98% |
| **会议模式** | ✅ 阶段性/最终 summary 回放验证通过 | ✅ summary-only 实时推送验证通过 | ✅ 阶段性/最终 summary 入库验证通过 | 98% |

### 1.2 核心问题

**问题 1：工作模式实时推送缺失（已完成代码实现）**
- 事件已定义：`Events.cs:132-196`（15+ 个事件）
- 事件已发布：`WorkflowExecutor.cs` 和 `WorkModeStreamProcessor.cs` 正常发布
- 历史回放已实现：`PluginBusWorkflowHistoryDispatcher.cs` 完整
- 记忆插件已准备：`MemoryWorkflowEventHandler.cs` 可以处理但收不到
- **已补齐环节**：新增 `WebSocketWorkflowFeedRelayService` 转发服务

**问题 2：会议模式长期记忆未覆盖未结束会议（已完成 summary-only 接入）**
- 数据层完整：`MeetingSessions` 等表已存在
- 事件已定义：`Events.cs:197-246`（13 个事件）
- 事件已发布：`MeetingStreamProcessor.cs` 正常发布
- **已补齐环节**：
  - 协议常量已定义（`CortanaWsEndpoints.MeetingTopic` 与 meeting history/event operations）
  - 已新增 `PluginBusMeetingHistoryDispatcher`
  - 已新增 `WebSocketMeetingFeedRelayService`
- **已补齐记忆接入**：新增 `MemoryMeetingEventHandler`，仅写入阶段性 summary 与最终 summary，避免会议全文污染长期记忆

### 1.3 业务影响

**工作模式：**
- 记忆插件只能在冷启动时通过历史回放获取工作任务
- 无法实时接收工作任务完成事件并入库
- 影响长期记忆的实时性和完整性

**会议模式：**
- 未结束会议如果只等待最终总结，会导致长期记忆无法召回阶段性成果
- 已通过 `meeting.message.completed` 中的 `MessageRole = "summary"` 接入阶段性总结
- 已通过 `meeting.completed` / `meeting.history.batch` 接入最终总结

---

## 二、技术方案

### 2.1 架构设计原则

**复用现有模式（Proven Pattern）**
- 对话模式的 WebSocket 分发架构已验证：
  - `WebSocketConversationFeedRelayService` → 实时推送
  - `PluginBusConversationHistoryDispatcher` → 历史回放
  - `MemoryConversationEventHandler` → 插件处理
- 工作模式已部分实现（历史回放完整）
- **策略**：直接复制对话模式的成功模式，修改 topic 和事件类型

**兼容现有协议**
- PluginBus 协议已支持多 topic：`conversation`, `workflow`, `memory`, `model`
- 协议版本 `1.2.0` 已预留扩展空间
- 不需要修改协议结构，只需添加新 topic

**最小代码量**
- 工作模式：~130 行代码（1 个新文件 + 2 行注册）
- 会议模式：~600 行代码（4 个新文件 + 集成代码）

### 2.2 技术架构图

```
┌─────────────────────────────────────────────────────────────────┐
│                          EventHub（进程内事件总线）                │
└────────┬───────────────────────────────────────────────────────┘
         │
         ├─► WebSocketConversationFeedRelayService (✅ 已实现)
         │   └─► 订阅 conversation.* 事件 → PluginBus conversation topic
         │
         ├─► WebSocketWorkflowFeedRelayService (✅ 已实现)
         │   └─► 订阅 work.* 事件 → PluginBus workflow topic
         │
         └─► WebSocketMeetingFeedRelayService (✅ 已实现)
             └─► 订阅 meeting.* 事件 → PluginBus meeting topic

┌─────────────────────────────────────────────────────────────────┐
│              WebSocketPluginBusServerService                     │
│  ├─ PluginBusConversationHistoryDispatcher (✅ 已实现)           │
│  ├─ PluginBusWorkflowHistoryDispatcher (✅ 已实现)               │
│  └─ PluginBusMeetingHistoryDispatcher (✅ 已实现)                │
└────────┬───────────────────────────────────────────────────────┘
         │ PluginBus 协议
         ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Memory 插件（独立进程）                         │
│  ├─ MemoryConversationEventHandler (✅ 已实现)                   │
│  ├─ MemoryWorkflowEventHandler (✅ 已实现但收不到事件)            │
│  └─ MemoryMeetingEventHandler (✅ 已实现，summary-only)          │
└─────────────────────────────────────────────────────────────────┘
```

---

## 三、实施步骤

### 阶段 1：工作模式实时推送补全（优先级 P1）

#### 目标
- 补全工作模式 WebSocket 实时推送
- 实现记忆插件实时接收工作任务完成事件
- 不破坏现有功能和协议

#### 实施清单

**步骤 1.1：创建工作模式事件转发服务**

文件：`Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketWorkflowFeedRelayService.cs`

实现要点：
- 复制 `WebSocketConversationFeedRelayService.cs` 为模板
- 修改类名为 `WebSocketWorkflowFeedRelayService`
- 订阅关键工作模式事件（至少包含以下事件）：
  - `Events.OnWorkTaskCompleted` - 任务完成（记忆插件必需）
  - `Events.OnWorkTaskFailed` - 任务失败
  - `Events.OnWorkTaskCancelled` - 任务取消
  - `Events.OnWorkTaskTitleUpdated` - 标题更新
- 修改广播参数：
  - `Topic = CortanaWsEndpoints.WorkflowTopic`
  - `Op = CortanaWsEndpoints.WorkflowEventPublishOperation`
- 使用 `WebSocketJsonContext` 进行 AOT 安全序列化

关键代码结构：
```csharp
public sealed class WebSocketWorkflowFeedRelayService(
    IPluginBusBroadcaster server,
    ISubscriber subscriber) : IHostedService
{
    private void SubscribeEvents()
    {
        subscriber.Subscribe<WorkTaskCompletedArgs>(
            Events.OnWorkTaskCompleted, 
            async (_, args) =>
            {
                await BroadcastWorkflowEventAsync(
                    Events.OnWorkTaskCompleted.Eventid,
                    args,
                    WebSocketJsonContext.Default.WorkTaskCompletedArgs);
                return false;
            });
        
        // ... 订阅其他事件
    }

    private Task BroadcastWorkflowEventAsync<TArgs>(
        string eventType,
        TArgs args,
        JsonTypeInfo<TArgs> jsonTypeInfo)
    {
        var payload = JsonSerializer.SerializeToElement(args, jsonTypeInfo);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "event",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.WorkflowTopic,
            Op = CortanaWsEndpoints.WorkflowEventPublishOperation,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = eventType,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return server.BroadcastPluginBusAsync(
            CortanaWsEndpoints.WorkflowTopic, 
            message);
    }
}
```

**步骤 1.2：注册服务到 DI 容器**

文件：`Src/Netor.Cortana.Networks/Extensions/NetworkServiceExtensions.cs`

在 `AddCortanaNetworks` 方法中添加：
```csharp
// 在 WebSocketConversationFeedRelayService 注册后添加
services.AddSingleton<WebSocketWorkflowFeedRelayService>();
services.AddSingleton<IHostedService>(sp => 
    sp.GetRequiredService<WebSocketWorkflowFeedRelayService>());
```

**步骤 1.3：扩展 WebSocketJsonContext**

文件：`Src/Netor.Cortana.Networks/WebSockets/WebSocketJsonContext.cs`

添加工作模式事件 Args 的源生成序列化支持：
```csharp
[JsonSerializable(typeof(WorkTaskCompletedArgs))]
[JsonSerializable(typeof(WorkTaskFailedArgs))]
[JsonSerializable(typeof(WorkTaskCancelledArgs))]
[JsonSerializable(typeof(WorkTaskTitleUpdatedArgs))]
// ... 其他需要的事件类型
internal partial class WebSocketJsonContext : JsonSerializerContext
{
}
```

**步骤 1.4：验收测试**

测试用例：
1. 启动主程序和记忆插件
2. 查看记忆插件日志，确认订阅了 `workflow` topic
3. 在工作台创建一个简单任务并等待完成
4. 查看记忆插件日志，确认收到 `workflow.task.completed` 事件
5. 查询记忆插件数据库 `ObservationRecord` 表，确认任务已入库

验收标准：
- [ ] 工作任务完成后 1 秒内记忆插件收到事件
- [ ] `ObservationRecord` 表新增一条记录，`EventType = 'workflow.task.completed'`
- [ ] `Content` 字段包含 `FinalReport` 内容
- [ ] 对话模式和会议模式功能不受影响

**预估工作量：**
- 代码编写：1-2 小时
- 测试验证：1 小时
- 总计：2-3 小时

---

### 阶段 2：会议模式 WebSocket 分发支持（优先级 P2）

#### 目标
- 完整实现会议模式 WebSocket 分发（历史回放 + 实时推送）
- 支持记忆插件接收会议阶段性 summary 与最终 summary
- 支持未结束会议通过阶段性 summary 被长期记忆召回

#### 前置决策

**决策点 1：会议内容以什么粒度进入长期记忆？**

选项 A：会议全文 / 逐条发言进入长期记忆
- 优势：细节最完整
- 劣势：会议记录体量大，容易污染长期记忆，召回噪音高
- 结论：不采用

选项 B：会议内容完全不进入长期记忆
- 优势：保持记忆库聚焦，会议有独立的归档机制
- 劣势：永续会议未结束前，长期记忆无法召回阶段性成果
- 结论：不采用

选项 C：仅会议 summary 进入长期记忆（已采用）
- 优势：保留阶段性成果和正式结论，同时避免会议全文污染长期记忆
- 实施：
  - 阶段性总结：`MeetingMessages.MessageRole = 'summary'` → `meeting.message.completed`
  - 最终总结：`MeetingSessions.FinalSummaryMd` / `meeting.completed`
  - 普通发言、thinking、tool 事件不入库

**当前决策**：选项 C（summary-only）
- 理由：
  1. 永续会议可能长期未结束，必须让阶段性 summary 进入长期记忆
  2. 最终总结代表会议正式结论，应作为最终版记忆记录
  3. 普通会议发言量大且噪音高，不适合直接写入长期记忆
  4. 阶段 summary 用 `meeting_summary_{MessageId}` 去重，最终 summary 用 `meeting_final_{MeetingId}` 去重

#### 实施清单

**步骤 2.1：扩展协议常量定义**

状态：✅ 已完成（`PluginBusVersion = 1.3.0`，新增 `MeetingTopic` 与 meeting operations）

文件：`Src/Netor.Cortana.Entitys/CortanaWsEndpoints.cs`

添加会议模式常量：
```csharp
/// <summary>会议模式 topic。</summary>
public const string MeetingTopic = "meeting";

// 会议模式 operations
public const string MeetingEventPublishOperation = "meeting.event.publish";
public const string MeetingHistoryReplayOperation = "meeting.history.replay";
public const string MeetingHistoryBatchOperation = "meeting.history.batch";
public const string MeetingHistoryCompletedOperation = "meeting.history.completed";

// 更新协议版本注释
public const string PluginBusVersion = "1.3.0"; // 新增 meeting topic
```

**步骤 2.2：创建会议历史回放 Dispatcher**

状态：✅ 已完成（回放阶段性 summary 与已完成会议最终 summary）

文件：`Src/Netor.Cortana.Networks/WebSockets/PluginBusMeetingHistoryDispatcher.cs`

实现要点：
- 复制 `PluginBusWorkflowHistoryDispatcher.cs` 为模板
- 修改查询表为 `MeetingSessions` 和 `MeetingMessages`
- 回放粒度：summary-only
  - `MeetingMessages.MessageRole = 'summary'` → 阶段性总结，包含未结束会议
  - `MeetingSessions.Status = 2 AND FinalSummaryMd <> ''` → 最终总结
- 不回放普通会议发言、thinking、tool 事件（避免数据量过大）

查询示例：
```csharp
var rows = db.Query(
    """
    SELECT
        ms.Id AS MeetingId,
        ms.SessionId,
        ms.Topic,
        ms.Status,
        ms.HostAgentId,
        ms.ParticipantsJson,
        ms.FinalSummaryMd,
        ms.CreatedAt,
        ms.UpdatedAt,
        s.Categorize AS WorkspaceId
    FROM MeetingSessions ms
    LEFT JOIN ChatSessions s ON s.Id = ms.SessionId
    WHERE ms.UpdatedAt >= @Since
      AND ms.Status = 2  -- 只回放已完成的会议
    ORDER BY ms.UpdatedAt
    LIMIT @Limit
    """,
    // ... mapper
);
```

**步骤 2.3：创建会议实时推送 Relay**

状态：✅ 已完成（转发 `meeting.created` / `meeting.completed` / `meeting.cancelled`，并 summary-only 转发 `meeting.message.completed`）

文件：`Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketMeetingFeedRelayService.cs`

实现要点：
- 复制 `WebSocketWorkflowFeedRelayService.cs` 为模板
- 订阅关键会议事件：
  - `Events.OnMeetingCreated`
  - `Events.OnMeetingCompleted`
  - `Events.OnMeetingCancelled`
  - `Events.OnMeetingMessageCompleted`（仅 `MessageRole == "summary"` 时转发）
- Topic 改为 `CortanaWsEndpoints.MeetingTopic`

**步骤 2.4：集成到 WebSocketPluginBusServerService**

状态：✅ 已完成

文件：`Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs`

修改点：
1. 构造函数添加 dispatcher：
```csharp
private readonly PluginBusMeetingHistoryDispatcher _meetingHistoryDispatcher;

public WebSocketPluginBusServerService(...)
{
    // ...
    _meetingHistoryDispatcher = new PluginBusMeetingHistoryDispatcher(
        db, logger, SendAsync);
}
```

2. `HandleMessageAsync` 添加路由：
```csharp
// 在 workflow.history.replay 处理后添加
if (string.Equals(type, "request", StringComparison.Ordinal)
    && string.Equals(op, CortanaWsEndpoints.MeetingHistoryReplayOperation, 
                     StringComparison.Ordinal))
{
    var payload = root.TryGetProperty("payload", out var payloadElement) 
        && payloadElement.ValueKind == JsonValueKind.Object
        ? payloadElement
        : root;
    var since = payload.TryGetProperty("sinceTimestamp", out var s) 
        ? s.GetInt64() : 0L;
    var batch = payload.TryGetProperty("batchSize", out var b) 
        ? Math.Clamp(b.GetInt32(), 100, 2000) : 500;
    var requestId = root.TryGetProperty("requestId", out var meetingRequestId) 
        ? meetingRequestId.GetString() : null;
    await _meetingHistoryDispatcher.ReplayAsync(id, requestId, since, batch, ct);
    return;
}
```

**步骤 2.5：注册服务**

状态：✅ 已完成

文件：`Src/Netor.Cortana.Networks/Extensions/NetworkServiceExtensions.cs`

```csharp
services.AddSingleton<WebSocketMeetingFeedRelayService>();
services.AddSingleton<IHostedService>(sp => 
    sp.GetRequiredService<WebSocketMeetingFeedRelayService>());
```

**步骤 2.6：扩展 WebSocketJsonContext**

状态：✅ 已完成（会议事件参数、历史批次与完成载荷均已加入 AOT 源生成上下文）

添加会议事件序列化支持：
```csharp
[JsonSerializable(typeof(MeetingCreatedArgs))]
[JsonSerializable(typeof(MeetingCompletedArgs))]
[JsonSerializable(typeof(MeetingCancelledArgs))]
// ...
```

**步骤 2.7：记忆插件 summary-only 入库**

状态：✅ 已完成

文件：`Plugins/Src/Cortana.Plugins.Memory/Services/MemoryMeetingEventHandler.cs`

实现要点：
- 处理 `meeting.message.completed` 且 `MessageRole == "summary"`：写入阶段性 summary
- 处理 `meeting.completed`：写入最终 summary
- 处理 `meeting.history.batch`：写入历史阶段性/最终 summary
- 忽略普通会议消息，避免长期记忆膨胀
- 阶段性 summary 记录 ID：`meeting_summary_{MessageId}`
- 最终 summary 记录 ID：`meeting_final_{MeetingId}`

**步骤 2.8：验收测试**

测试用例：
1. 创建并完成一个会议
2. 插件发起 `meeting.history.replay` 请求
3. 验证返回的会议列表包含刚才的会议
4. 再创建一个会议，验证实时推送
5. （如果实现了记忆插件）查询 `ObservationRecord` 确认入库

验收标准：
- [x] 历史回放能返回已完成的会议列表（真实 PluginBus WebSocket 测试覆盖）
- [x] 会议完成时实时推送 `meeting.completed` 事件（真实 PluginBus WebSocket 测试覆盖）
- [x] 未结束会议的阶段性 summary 能通过历史回放返回（真实 PluginBus WebSocket 测试覆盖）
- [x] 阶段性 summary 能通过 `meeting.message.completed` 实时推送（真实 PluginBus WebSocket 测试覆盖）
- [x] 不影响对话模式和工作模式（Networks 测试项目通过，含 workflow 真实 WebSocket 广播验证）
- [x] 会议阶段性/最终 summary 进入长期记忆（Memory 插件测试覆盖）

**预估工作量：**
- 协议扩展：1 小时
- 历史回放：2-3 小时
- 实时推送：2 小时
- 集成测试：2 小时
- 记忆插件（可选）：2 小时
- 总计：7-10 小时

---

## 四、风险与应对

### 4.1 兼容性风险

**风险**：修改 `PluginBusVersion` 可能导致旧插件断连

**应对**：
- 采用向下兼容策略（已有机制）
- 旧插件不订阅新 topic 不会受影响
- 握手消息中 `topics` 字段按白名单过滤

### 4.2 性能风险

**风险**：增加事件订阅和广播可能影响性能

**应对**：
- 事件转发是异步的（`PublishAsync`）
- PluginBus 广播已优化（按 topic 订阅分发）
- 只订阅关键事件（不是所有事件都转发）

### 4.3 数据一致性风险

**风险**：历史回放和实时推送的数据可能重复

**应对**：
- 记忆插件已有去重机制（通过 ID 判断）
- 历史回放基于时间戳增量查询
- 插件冷启动时先回放历史，再接收实时事件

---

## 五、优先级与时间规划

### 5.1 优先级排序

1. **P1 - 工作模式实时推送**（阶段 1）
   - 理由：工作模式已上线，记忆功能不完整影响用户体验
   - 工作量：2-3 小时
   - 收益：记忆插件能实时接收工作任务

2. **P2 - 会议模式历史回放**（阶段 2.1-2.4）
   - 理由：会议模式已上线，但无法通过插件获取历史
   - 工作量：5-6 小时
   - 收益：为未来扩展预留能力

3. **P3 - 会议模式实时推送**（阶段 2.5-2.6）
   - 理由：补全会议模式的实时能力
   - 工作量：2 小时
   - 收益：会议事件实时分发

4. **P4 - 会议 summary-only 记忆**（阶段 2.7）
   - 理由：永续会议未结束前也需要能从长期记忆召回阶段性成果
   - 工作量：2 小时
   - 收益：阶段性 summary 与最终 summary 可检索，普通会议发言不污染长期记忆

### 5.2 里程碑

**里程碑 1：工作模式完整支持**
- 时间：1 个工作日
- 交付物：`WebSocketWorkflowFeedRelayService.cs` + 测试通过
- 验收：记忆插件能实时接收并入库工作任务

**里程碑 2：会议模式基础支持**
- 时间：2 个工作日
- 交付物：历史回放 + 实时推送 + 协议扩展
- 验收：插件能获取会议历史和实时事件

**里程碑 3：会议 summary-only 记忆**
- 时间：1 个工作日
- 交付物：`MemoryMeetingEventHandler.cs`
- 验收：会议阶段性 summary 与最终 summary 进入长期记忆

---

## 六、验收标准

### 6.1 功能验收

**工作模式：**
- [x] 创建工作任务 → 完成 → PluginBus `workflow` 订阅者收到事件（自动化端到端式测试覆盖）
- [x] 记忆插件数据库新增记录，内容正确（插件侧自动化测试覆盖）
- [ ] 插件重启后历史回放能获取所有历史任务
- [x] 对话模式功能不受影响（Networks 测试项目通过）

**会议模式：**
- [x] 插件能通过历史回放获取已完成会议最终 summary（真实 PluginBus WebSocket 测试覆盖）
- [x] 插件能通过历史回放获取未结束会议阶段性 summary（真实 PluginBus WebSocket 测试覆盖）
- [x] 阶段性 summary 可通过 `meeting.message.completed` 实时推送（真实 PluginBus WebSocket 测试覆盖）
- [x] 会议完成时插件实时收到 `meeting.completed` 事件（真实 PluginBus WebSocket 测试覆盖）
- [x] 会议阶段性/最终 summary 进入长期记忆（Memory 插件测试覆盖）
- [x] 对话模式和工作模式功能不受影响（Networks 测试项目通过）

### 6.2 性能验收

- [ ] 事件转发延迟 < 100ms
- [ ] 历史回放 1000 条记录 < 5 秒
- [ ] WebSocket 连接稳定，无异常断连
- [ ] 内存无明显增长

### 6.3 兼容性验收

- [ ] 旧版本记忆插件仍能正常工作（不订阅新 topic）
- [ ] 新版本插件兼容旧版本宿主（降级运行）
- [ ] 协议版本号正确更新

---

## 七、后续优化方向

1. **事件过滤机制**：允许插件按条件订阅事件（如只订阅特定工作区的任务）
2. **批量推送优化**：高频事件批量打包发送，减少网络开销
3. **压缩支持**：大型事件载荷启用压缩（如会议详细记录）
4. **断线重连恢复**：插件断线重连后自动补发丢失的事件

---

## 八、参考文档

- [websocket-api.md](../系统流程与规划/websocket-api.md) - 当前 PluginBus 协议说明
- [07-事件分流与插件兼容设计.md](../已完成功能规划/多智能体编排模式策划/07-事件分流与插件兼容设计.md) - 工作模式事件设计
- [会议模式方案策划/README.md](会议模式方案策划/README.md) - 会议模式整体设计

---

**最后更新**：2026-06-02  
**负责人**：待分配  
**审核人**：待审核
