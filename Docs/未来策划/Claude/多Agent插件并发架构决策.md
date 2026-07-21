# 多Agent插件并发架构决策

> 日期：2026-07-21
> 状态：已决策，待实施

---

## 决策

**方案C + 按需懒加载**：每个 Agent 拥有独立的插件进程隔离，但不在实例化时全量启动，仅在首次工具调用时按需启动对应插件。

---

## 原问题

现有架构是工作区级插件单例 + `SemaphoreSlim(1,1)` 串行化。多个 Agent 并行时调用同一插件会互相阻塞，丧失并行优势。

---

## 设计方案

### 核心思路

```
Agent 实例化
  └─ 接收工具快照（tool snapshot）
       ├─ 包含：插件ID、工具名称、参数定义
       └─ 不启动任何插件进程

首次调用某工具
  ├─ 通知 Agent 状态："插件启动中..."（透传到 UI 显示）
  └─ 查询：此 Agent 的 PluginId 对应进程是否已启动？
       ├─ 否 → 按需启动该插件进程 → 执行调用
       └─ 是 → 直接执行调用

Agent 销毁
  └─ 停止并释放该 Agent 名下所有已启动的插件进程（必须全部清理）
```

### 生命周期图

```
Agent A 实例化    → [快照: FileBrowser, PowerShell, Voice]  无进程
Agent B 实例化    → [快照: FileBrowser, CodeExec]           无进程

Agent A 调 FileBrowser  → 启动 FileBrowser#A → 执行  ← UI显示"插件启动中"
Agent B 调 FileBrowser  → 启动 FileBrowser#B → 执行  ← 与A完全独立，无阻塞
Agent A 调 FileBrowser  → FileBrowser#A 已在运行 → 直接执行
Agent A 调 PowerShell   → 启动 PowerShell#A   → 执行

Agent A 销毁     → 停止 FileBrowser#A, PowerShell#A（全部清理）
Agent B 销毁     → 停止 FileBrowser#B
```

---

## 关键设计点

### 1. 工具快照（Tool Snapshot）

Agent 实例化时注入的是**元数据**，不是运行实例：

```csharp
record ToolSnapshot(
    string PluginId,
    string PluginCommand,          // 启动命令/DLL路径
    IReadOnlyList<AITool> Tools    // 工具定义（供LLM使用）
);
```

工具定义已注册到 LLM 上下文，可以被 Agent 调用，但进程未启动。

### 2. 按需启动逻辑

LLM 工具调用本质是串行的（等结果再发下一个），因此同一 Agent 内不会出现对同一插件的并发调用，`Dictionary` 取代 `ConcurrentDictionary` 即可：

```csharp
class AgentPluginManager : IAsyncDisposable
{
    private readonly Dictionary<string, IPluginHost> _running = new();
    private readonly IReadOnlyList<ToolSnapshot> _snapshots;

    public async Task<string> InvokeToolAsync(
        string pluginId, string toolName, string argsJson,
        CancellationToken ct)
    {
        if (!_running.TryGetValue(pluginId, out var host))
        {
            // 首次调用：通知状态，按需启动
            var snapshot = _snapshots.First(s => s.PluginId == pluginId);
            OnPluginStarting?.Invoke(pluginId);   // → 传递状态给 Agent → UI
            host = CreateHost(snapshot);
            await host.StartAsync(ct);
            _running[pluginId] = host;
            OnPluginReady?.Invoke(pluginId);
        }

        return await host.InvokeAsync(toolName, argsJson, ct);
    }

    // Agent 销毁时必须调用，停止所有已启动的插件进程
    public async ValueTask DisposeAsync()
    {
        foreach (var host in _running.Values)
        {
            try { await host.StopAsync(); }
            catch { /* 进程已退出则忽略 */ }
            await host.DisposeAsync();
        }
        _running.Clear();
    }
}
```

### 3. 首次延迟的状态通知

启动延迟通过状态事件透传到 UI，用户可见进度，不是黑盒等待：

```csharp
// AgentPluginManager 暴露事件
public event Action<string>? OnPluginStarting;  // pluginId
public event Action<string>? OnPluginReady;

// Agent 层订阅并转发到 UI
_pluginManager.OnPluginStarting += id =>
    ReportStatus($"正在启动插件 {id}...");
```

### 4. 进程崩溃恢复

子进程意外退出后自动重启：

```csharp
if (host.IsAlive == false)
{
    _running.Remove(pluginId);
    // 下次调用重新走懒启动流程
}
```

### 5. 进程归属

```
工作区
├── Agent A
│   └── AgentPluginManager
│       ├── FileBrowser#A  [PID:1234]
│       └── PowerShell#A   [PID:1235]
└── Agent B
    └── AgentPluginManager
        └── FileBrowser#B  [PID:1236]
```

进程数 = 各 Agent 实际调用过的插件数之和，远小于全量启动的 N×M。

---

## 对现有代码的改造范围

| 文件 | 改动 | 说明 |
|------|------|------|
| `PluginLoader.cs` | 重构 | 从全局单例改为快照工厂 |
| `ExternalProcessPluginHostBase.cs` | 移除锁 | `SemaphoreSlim(1,1)` 不再需要 |
| `AIAgentFactory.cs` | 修改 | 注入 `AgentPluginManager` 而非共享插件列表 |
| 新增 `AgentPluginManager.cs` | 新建 | 懒启动 + 状态通知 + DisposeAsync 全清理 |
| 新增 `ToolSnapshot.cs` | 新建 | 纯元数据 |

---

## 关联文档

- [插件跨平台兼容性评估.md](插件跨平台兼容性评估.md)
- [UI重构技术选型决策.md](UI重构技术选型决策.md)

> 日期：2026-07-21
> 状态：已决策，待实施

---

## 决策

**方案C + 按需懒加载**：每个 Agent 拥有独立的插件进程隔离，但不在实例化时全量启动，仅在首次工具调用时按需启动对应插件。

---

## 原问题

现有架构是工作区级插件单例 + `SemaphoreSlim(1,1)` 串行化。多个 Agent 并行时调用同一插件会互相阻塞，丧失并行优势。

---

## 设计方案

### 核心思路

```
Agent 实例化
  └─ 接收工具快照（tool snapshot）
       ├─ 包含：插件ID、工具名称、参数定义
       └─ 不启动任何插件进程

首次调用某工具
  └─ 查询：此 Agent 的 PluginId 对应进程是否已启动？
       ├─ 否 → 按需启动该插件进程 → 执行调用
       └─ 是 → 直接执行调用

Agent 销毁
  └─ 清理该 Agent 名下所有已启动的插件进程
```

### 生命周期图

```
Agent A 实例化    → [快照: FileBrowser, PowerShell, Voice]  无进程
Agent B 实例化    → [快照: FileBrowser, CodeExec]           无进程

Agent A 调 FileBrowser  → 启动 FileBrowser#A → 执行  ← 首次有启动延迟
Agent B 调 FileBrowser  → 启动 FileBrowser#B → 执行  ← 与A完全独立，无阻塞
Agent A 调 FileBrowser  → FileBrowser#A 已在运行 → 直接执行
Agent A 调 PowerShell   → 启动 PowerShell#A   → 执行

Agent A 销毁     → 关闭 FileBrowser#A, PowerShell#A
Agent B 销毁     → 关闭 FileBrowser#B
```

---

## 关键设计点

### 1. 工具快照（Tool Snapshot）

Agent 实例化时注入的是**元数据**，不是运行实例：

```csharp
// Agent 构造时接收
record ToolSnapshot(
    string PluginId,
    string PluginCommand,   // 启动命令/DLL路径
    IReadOnlyList<AITool> Tools  // 工具定义（供LLM使用）
);
```

工具定义已注册到 LLM 上下文，可以被 Agent 调用，但进程未启动。

### 2. 按需启动逻辑

```csharp
class AgentPluginManager : IAsyncDisposable
{
    // Key: pluginId, Value: 已启动的 Host
    private readonly ConcurrentDictionary<string, IPluginHost> _running = new();
    private readonly IReadOnlyList<ToolSnapshot> _snapshots;

    public async Task<string> InvokeToolAsync(
        string pluginId, string toolName, string argsJson,
        CancellationToken ct)
    {
        // 懒启动：首次调用时按需启动
        var host = await _running.GetOrAddAsync(pluginId, async id =>
        {
            var snapshot = _snapshots.First(s => s.PluginId == id);
            var host = CreateHost(snapshot);
            await host.StartAsync(ct);
            return host;
        });

        return await host.InvokeAsync(toolName, argsJson, ct);
    }

    public async ValueTask DisposeAsync()
    {
        // Agent 销毁时清理所有属于它的插件进程
        foreach (var host in _running.Values)
            await host.DisposeAsync();
    }
}
```

### 3. 进程归属

```
工作区
├── Agent A
│   └── AgentPluginManager
│       ├── FileBrowser#A  [进程 PID:1234]
│       └── PowerShell#A   [进程 PID:1235]
└── Agent B
    └── AgentPluginManager
        └── FileBrowser#B  [进程 PID:1236]
```

每个 Agent 的 `AgentPluginManager` 独立管理进程，互不干扰。

---

## 性能权衡

| 项目 | 影响 | 说明 |
|------|------|------|
| 首次调用延迟 | +50–200ms | 进程启动开销（一次性，后续无延迟） |
| 内存占用 | 按需增长 | 未被调用的插件不占内存，优于方案C全量启动 |
| 并发能力 | 完全并行 | 不同 Agent 的同名插件互不阻塞 |
| 进程数上限 | N × 实际使用插件数 | 远小于 N × M（M为全部插件数） |

---

## 对现有代码的改造范围

| 文件 | 改动 | 说明 |
|------|------|------|
| `PluginLoader.cs` | 重构 | 从全局单例改为提供快照工厂 |
| `ExternalProcessPluginHostBase.cs` | 移除 | `SemaphoreSlim(1,1)` 不再需要（单Agent串行保证） |
| `AIAgentFactory.cs` | 修改 | 注入 `AgentPluginManager` 而非共享插件列表 |
| 新增 `AgentPluginManager.cs` | 新建 | 按需启动 + 生命周期绑定到 Agent |
| 新增 `ToolSnapshot.cs` | 新建 | 纯元数据，可序列化 |

---

## 关联评估文档

- [插件跨平台兼容性评估.md](插件跨平台兼容性评估.md)
- [UI重构技术选型决策.md](UI重构技术选型决策.md)
