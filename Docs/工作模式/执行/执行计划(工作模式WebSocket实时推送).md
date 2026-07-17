# 执行计划：工作模式 WebSocket 实时推送

> **状态**：✅ 编码完成，自动化验证通过，待真实联调
> **优先级**：P1
> **预估工作量**：2-3 小时
> **目标版本**：v1.4.0
> **创建日期**：2026-06-02

---

## 一、目标

补全工作模式 WebSocket 实时推送功能，使记忆插件能够实时接收工作任务完成事件。

**当前状态**：
- ✅ 工作模式历史回放已完整实现
- ✅ 记忆插件代码已准备好接收事件
- ❌ 缺少事件转发服务，导致实时事件无法推送

**期望结果**：
- ✅ 工作任务完成时，记忆插件 1 秒内收到 `workflow.task.completed` 事件
- ✅ 事件自动入库到 `ObservationRecord` 表
- ✅ 与历史回放机制配合，实现完整的工作模式记忆支持

**最新进度（2026-06-02）**：
- ✅ 任务 1-4 已完成：转发服务、JsonContext、DI 注册、编译验证
- ✅ Networks 测试已覆盖真实 PluginBus WebSocket 订阅和 `work.task.completed` → `workflow.task.completed` 广播
- ✅ Memory 插件测试已覆盖宿主风格 PascalCase payload 写入 `ObservationRecord`
- ⏳ 主程序 + 真实 Memory 插件联调与性能稳定性测试待执行

---

## 二、任务分解

### 任务 1：创建工作模式事件转发服务

**文件**：`Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketWorkflowFeedRelayService.cs`

**实现步骤**：

#### 1.1 复制对话模式转发服务作为模板

```bash
# 参考文件
Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketConversationFeedRelayService.cs
```

#### 1.2 修改类定义和构造函数

```csharp
namespace Netor.Cortana.Networks;

/// <summary>
/// 订阅宿主内部 Workflow 事件，并通过内部 PluginBus 转发给插件侧订阅者。
/// </summary>
public sealed class WebSocketWorkflowFeedRelayService(
    IPluginBusBroadcaster server,
    ISubscriber subscriber) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeEvents();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
    
    // ... 实现方法
}
```

#### 1.3 实现事件订阅方法

订阅以下关键事件：

```csharp
private void SubscribeEvents()
{
    // 1. 任务完成（记忆插件必需）
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

    // 2. 任务失败
    subscriber.Subscribe<WorkTaskFailedArgs>(
        Events.OnWorkTaskFailed, 
        async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkTaskFailed.Eventid,
                args,
                WebSocketJsonContext.Default.WorkTaskFailedArgs);
            return false;
        });

    // 3. 任务取消
    subscriber.Subscribe<WorkTaskCancelledArgs>(
        Events.OnWorkTaskCancelled, 
        async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkTaskCancelled.Eventid,
                args,
                WebSocketJsonContext.Default.WorkTaskCancelledArgs);
            return false;
        });

    // 4. 任务标题更新
    subscriber.Subscribe<WorkTaskTitleUpdatedArgs>(
        Events.OnWorkTaskTitleUpdated, 
        async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkTaskTitleUpdated.Eventid,
                args,
                WebSocketJsonContext.Default.WorkTaskTitleUpdatedArgs);
            return false;
        });
}
```

#### 1.4 实现事件广播方法

```csharp
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
```

#### 1.5 添加必要的 using 声明

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Hosting;
using Netor.Cortana.Entitys;
using Netor.EventHub;
```

**检查点**：
- [x] 文件创建完成
- [x] 代码编译通过
- [x] 订阅了 4 个关键事件
- [x] 广播方法使用正确的 topic 和 op

---

### 任务 2：扩展 WebSocketJsonContext

**文件**：`Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs`

**实现步骤**：

#### 2.1 添加工作模式事件类型序列化支持

在 `WebSocketJsonContext` 类定义上方添加以下特性：

```csharp
// 工作模式事件参数（阶段 1 新增）
[JsonSerializable(typeof(WorkTaskCompletedArgs))]
[JsonSerializable(typeof(WorkTaskFailedArgs))]
[JsonSerializable(typeof(WorkTaskCancelledArgs))]
[JsonSerializable(typeof(WorkTaskTitleUpdatedArgs))]
```

**完整上下文示例**：

```csharp
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
// ... 现有类型 ...
// 工作模式事件参数
[JsonSerializable(typeof(WorkTaskCompletedArgs))]
[JsonSerializable(typeof(WorkTaskFailedArgs))]
[JsonSerializable(typeof(WorkTaskCancelledArgs))]
[JsonSerializable(typeof(WorkTaskTitleUpdatedArgs))]
internal partial class WebSocketJsonContext : JsonSerializerContext
{
}
```

**检查点**：
- [x] 添加了 4 个事件类型的序列化支持
- [x] 代码编译通过（AOT 源生成器运行）
- [x] 无编译警告

---

### 任务 3：注册服务到 DI 容器

**文件**：`Src/Netor.Cortana.Networks/Extensions/NetworkServiceExtensions.cs`

**实现步骤**：

#### 3.1 在 AddCortanaNetworks 方法中添加注册代码

找到 `WebSocketConversationFeedRelayService` 的注册位置（约第 29-30 行），在其后添加：

```csharp
// 对话模式事件转发（现有代码）
services.AddSingleton<WebSocketConversationFeedRelayService>();
services.AddSingleton<IHostedService>(sp => 
    sp.GetRequiredService<WebSocketConversationFeedRelayService>());

// 工作模式事件转发（新增代码）
services.AddSingleton<WebSocketWorkflowFeedRelayService>();
services.AddSingleton<IHostedService>(sp => 
    sp.GetRequiredService<WebSocketWorkflowFeedRelayService>());
```

**检查点**：
- [x] 服务注册代码添加完成
- [x] 位置正确（在 ConversationFeedRelayService 之后）
- [x] 代码编译通过

---

### 任务 4：编译和初步验证

**实施步骤**：

#### 4.1 清理和重新编译

```bash
cd Src/Netor.Cortana.Networks
dotnet clean
dotnet build
```

#### 4.2 检查编译输出

预期结果：
- [x] 无编译错误
- [x] 无编译警告
- [x] AOT 源生成器成功运行

#### 4.3 检查服务注册

启动主程序，检查日志：

预期日志：
```
[Info] PluginBus 服务器已启动，端口：52841
[Info] WebSocketConversationFeedRelayService 已启动
[Info] WebSocketWorkflowFeedRelayService 已启动  // 新增
```

**检查点**：
- [x] 编译成功
- [ ] 主程序正常启动
- [ ] WorkflowFeedRelayService 已注册并启动

---

### 任务 5：端到端功能测试

**自动化覆盖（已完成）**：
- ✅ 真实 PluginBus WebSocket 订阅 `workflow` topic 后可收到 `workflow.task.completed`
- ✅ Memory 插件 `MemoryWorkflowEventHandler` 可处理宿主风格 `workflow.task.completed` payload 并写入 `ObservationRecord`

**手工测试环境准备**：
1. 启动主程序（Cortana.exe）
2. 启动记忆插件（Cortana.Plugins.Memory.exe）

**测试用例 1：插件订阅验证**

步骤：
1. 查看记忆插件日志
2. 确认订阅消息包含 `workflow` topic

预期日志：
```
[Info] Memory 插件订阅 workflow topic 启用
[Info] PluginBus 订阅成功: ["conversation", "memory", "model", "workflow"]
```

**测试用例 2：实时事件推送**

步骤：
1. 在工作台创建一个简单任务：
   - 任务内容："帮我写一个 Hello World 程序"
   - 等待任务完成
2. 观察记忆插件日志

预期日志：
```
[Info] 收到 workflow.task.completed 事件: taskId={任务ID}
[Info] Workflow task 已入记忆：{任务ID}
```

**测试用例 3：数据库验证**

步骤：
1. 使用 SQLite 工具打开记忆插件数据库
2. 查询 `ObservationRecord` 表

预期查询：
```sql
SELECT Id, EventType, Content, CreatedAt 
FROM ObservationRecord 
WHERE EventType = 'workflow.task.completed'
ORDER BY CreatedAt DESC
LIMIT 5;
```

预期结果：
- [ ] 找到刚才完成的任务记录
- [ ] `EventType = 'workflow.task.completed'`
- [ ] `Content` 包含任务的 FinalReport
- [ ] 时间戳正确

**测试用例 4：历史回放配合测试**

步骤：
1. 完成 2-3 个工作任务（实时推送入库）
2. 停止记忆插件
3. 再完成 1 个工作任务
4. 重新启动记忆插件（触发历史回放）
5. 查看数据库

预期结果：
- [ ] 前 2-3 个任务已入库（实时推送）
- [ ] 第 4 个任务通过历史回放补齐
- [ ] 无重复记录
- [ ] 所有任务都已入库

**测试用例 5：兼容性测试**

步骤：
1. 测试对话模式（专家模式）功能
2. 测试会议模式功能

预期结果：
- [ ] 对话模式正常工作
- [ ] 会议模式正常工作
- [ ] 记忆插件对对话模式的处理不受影响

---

### 任务 6：性能和稳定性测试

**测试场景 1：高频任务测试**

步骤：
1. 连续创建 10 个工作任务
2. 观察事件推送延迟

预期结果：
- [ ] 每个任务完成后 1 秒内插件收到事件
- [ ] 无事件丢失
- [ ] 无异常断连

**测试场景 2：长时间运行测试**

步骤：
1. 启动主程序和记忆插件
2. 运行 1 小时，期间完成多个工作任务
3. 检查内存和 CPU 使用

预期结果：
- [ ] 内存无明显增长
- [ ] CPU 使用正常
- [ ] WebSocket 连接稳定

---

## 三、验收标准

### 功能验收
- [x] 工作任务完成后 PluginBus `workflow` 订阅者收到 `workflow.task.completed` 事件（自动化测试覆盖）
- [x] 事件载荷包含完整的 `FinalReport` 内容（自动化测试覆盖）
- [x] 记忆插件成功将任务写入 `ObservationRecord` 表（插件侧自动化测试覆盖）
- [ ] 插件重启后历史回放能获取所有历史任务
- [ ] 对话模式和会议模式功能不受影响

### 技术验收
- [x] 代码编译无错误
- [x] AOT 源生成器正常工作
- [x] 服务正确注册到 DI 容器
- [x] PluginBus WebSocket 自动化连接稳定
- [ ] 真实主程序 + 插件长连接稳定，无异常断连

### 性能验收
- [ ] 事件转发延迟 < 100ms
- [ ] 内存无异常增长
- [ ] CPU 使用正常

---

## 四、回滚方案

如果实施过程中遇到严重问题，执行以下回滚步骤：

### 回滚步骤

1. **删除新增文件**
```bash
rm Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketWorkflowFeedRelayService.cs
```

2. **撤销 DI 注册**

在 `NetworkServiceExtensions.cs` 中删除：
```csharp
services.AddSingleton<WebSocketWorkflowFeedRelayService>();
services.AddSingleton<IHostedService>(sp => 
    sp.GetRequiredService<WebSocketWorkflowFeedRelayService>());
```

3. **撤销 JsonContext 修改**

在 `WebSocketJsonContext.cs` 中删除工作模式事件类型的序列化特性。

4. **重新编译**
```bash
dotnet clean
dotnet build
```

5. **验证回滚**
- [ ] 主程序正常启动
- [ ] 对话模式功能正常
- [ ] 工作模式功能正常（只是插件收不到实时事件）

---

## 五、风险与应对

### 风险 1：编译错误

**现象**：添加代码后编译失败

**排查**：
1. 检查命名空间是否正确
2. 检查事件 Args 类型是否存在于 `Netor.Cortana.Entitys`
3. 检查 `WebSocketJsonContext` 是否正确生成

**应对**：
- 参考 `WebSocketConversationFeedRelayService.cs` 的完整实现
- 确保所有 using 声明完整

### 风险 2：事件收不到

**现象**：插件订阅成功但收不到 `workflow.task.completed` 事件

**排查**：
1. 检查 `WorkflowExecutor.cs` 是否正常发布事件
2. 检查 EventHub 订阅是否成功（添加日志）
3. 检查 PluginBus 广播是否执行（添加日志）
4. 检查插件是否正确订阅 workflow topic

**应对**：
- 在 `SubscribeEvents` 方法中添加日志确认订阅
- 在 `BroadcastWorkflowEventAsync` 中添加日志确认广播

### 风险 3：插件重复入库

**现象**：同一个任务被插件重复写入数据库

**排查**：
1. 检查 `MemoryWorkflowEventHandler.BuildRecordFromTaskCompleted` 方法
2. 确认使用 `taskId` 作为 `ObservationRecord.Id`

**应对**：
- 记忆插件已有基于 ID 的去重机制
- 如仍重复，检查 `IMemoryStore.InsertObservation` 实现

---

## 六、时间规划

### 预估时间分配

| 任务 | 预估时间 | 备注 |
|------|---------|------|
| 任务 1：创建转发服务 | 1 小时 | 复制修改，代码量约 120 行 |
| 任务 2：扩展 JsonContext | 15 分钟 | 添加 4 个类型序列化 |
| 任务 3：注册服务 | 5 分钟 | 2 行代码 |
| 任务 4：编译验证 | 15 分钟 | 编译和初步检查 |
| 任务 5：功能测试 | 45 分钟 | 5 个测试用例 |
| 任务 6：性能测试 | 30 分钟 | 2 个测试场景 |
| **总计** | **2.5-3 小时** | |

### 建议时间安排

**方案 A：一次性完成**
- 连续工作 3 小时
- 适合：紧急需求

**方案 B：分阶段完成**
- 第一天：任务 1-4（编码和编译）- 1.5 小时
- 第二天：任务 5-6（测试验证）- 1.5 小时
- 适合：常规开发

---

## 七、完成检查清单

### 代码实施
- [x] `WebSocketWorkflowFeedRelayService.cs` 创建完成
- [x] `WebSocketJsonContext.cs` 扩展完成
- [x] `NetworkServiceExtensions.cs` 注册完成
- [x] 代码编译通过
- [x] 无编译警告

### 功能测试
- [x] 插件成功订阅 workflow topic（自动化 WebSocket 测试覆盖）
- [x] 实时事件推送正常（自动化 WebSocket 测试覆盖）
- [x] 数据库入库正常（Memory 插件测试覆盖）
- [ ] 历史回放配合正常
- [ ] 兼容性测试通过

### 性能测试
- [ ] 高频任务测试通过
- [ ] 长时间运行稳定
- [ ] 无内存泄漏

### 文档更新
- [x] 更新主方案文档状态
- [x] 记录测试结果
- [ ] 更新版本号规划

---

## 八、后续工作

完成本执行计划后，可以考虑：

1. **会议模式 WebSocket 分发**（参考主方案文档阶段 2）
2. **事件订阅优化**（按条件过滤）
3. **性能监控**（添加事件转发延迟监控）

---

**创建时间**：2026-06-02  
**文档版本**：v1.0  
**关联文档**：[工作模式和会议模式WebSocket分发补全方案.md](./工作模式和会议模式WebSocket分发补全方案.md)
