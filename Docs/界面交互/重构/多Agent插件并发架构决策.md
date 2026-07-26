# 多 Agent 插件并发架构决策

> 日期：2026-07-21（2026-07-23 修订：补充插件作用域、发现机制与更新流程）
> 状态：已决策，待实施

---

## 决策摘要

1. **并发隔离**：每个 Agent 拥有独立的插件进程隔离，首次工具调用时按需懒加载（方案 C + 懒加载）。
2. **插件作用域**：引入 `PluginScope`，区分 `WorkspaceShared`（工作区级共享，如 StreamX Hub）与 `AgentIsolated`（Agent 级隔离，如 FileBrowser）。授权型主插件在工作区初始化时立即启动，子插件仍懒加载。
3. **发现与可见性**：`PluginLoader` 增加纯元数据发现缓存 `_discoveredManifests`，设置中心读发现缓存而非运行实例，未启动插件也能显示与配置。
4. **更新/卸载**：设置中心支持卸载→替换文件→重载流程；插件被 Agent 使用中（有活跃进程）时禁止更新。
5. **配置存储**：运行时配置值继续存数据库，Agent 绑定关系继续存 `manifest.yaml`，维持现状。

---

## 原问题

### 问题 1：并发阻塞
现有架构是工作区级插件单例 + `SemaphoreSlim(1,1)` 串行化。多个 Agent 并行时调用同一插件会互相阻塞，丧失并行优势。

### 问题 2：主/子插件授权链断裂
StreamX 这类体系是「主插件 + 子插件」结构：子插件启动时按 `subscribe → subscribe → publish query → 等待 snapshot`（5 秒超时）向主插件申请授权。若按纯懒加载各自独立启动，会出现两种失败：

- 只实例化了子插件，主插件（Hub）未运行，子插件 `query.v1` 无人应答，一律以 `ENTITLEMENT_UNKNOWN` 拒绝所有调用。
- 主插件与子插件实例化之间存在通讯时间差，授权尚未到达，Agent 调用即失败。

### 问题 3：未运行插件在设置中心不可见
设置中心从运行实例读插件列表。懒加载下未被调用过的插件没有进程，导致「已安装但看不到、无法配置、无法授权绑定」。

---

## 方案一：并发隔离（方案 C + 懒加载）

### 核心思路

```
Agent 实例化
  └─ 接收工具快照（tool snapshot）
       ├─ 包含：插件ID、工具名称、参数定义、Scope
       └─ 不启动任何插件进程

首次调用某工具
  ├─ 通知 Agent 状态："插件启动中..."（透传到 UI 显示）
  └─ 查询：此 Agent 的 PluginId 对应进程是否已启动？
       ├─ 否 → 按需启动该插件进程 → 执行调用
       └─ 是 → 直接执行调用

Agent 销毁
  └─ 停止并释放该 Agent 名下所有已启动的 AgentIsolated 插件进程
```

### `AgentPluginManager`

LLM 工具调用本质是串行的，同一 Agent 内不会对同一插件并发调用，使用 `Dictionary` 即可：

```csharp
class AgentPluginManager : IAsyncDisposable
{
    private readonly Dictionary<string, IPluginHost> _running = new();
    private readonly IReadOnlyList<ToolSnapshot> _snapshots;
    private readonly WorkspacePluginManager _workspacePluginManager;

    public event Action<string>? OnPluginStarting;
    public event Action<string>? OnPluginReady;

    public async Task<string> InvokeToolAsync(
        string pluginId, string toolName, string argsJson,
        CancellationToken ct)
    {
        var snapshot = _snapshots.First(s => s.PluginId == pluginId);

        // WorkspaceShared 插件委托给工作区管理器
        if (snapshot.Scope == PluginScope.WorkspaceShared)
            return await _workspacePluginManager.InvokeAsync(pluginId, toolName, argsJson, ct);

        // AgentIsolated 懒加载
        if (!_running.TryGetValue(pluginId, out var host))
        {
            OnPluginStarting?.Invoke(pluginId);
            host = CreateHost(snapshot);
            await host.StartAsync(ct);
            _running[pluginId] = host;
            OnPluginReady?.Invoke(pluginId);
        }
        return await host.InvokeAsync(toolName, argsJson, ct);
    }

    public async ValueTask DisposeAsync()
    {
        // 只清理本 Agent 名下的 AgentIsolated 进程
        foreach (var host in _running.Values)
        {
            try { await host.StopAsync(); } catch { }
            await host.DisposeAsync();
        }
        _running.Clear();
    }
}
```

---

## 方案二：插件作用域（PluginScope）

### 根因

StreamX Hub 等主插件本质上是**工作区级服务**，子插件是 Agent 的工具，但子插件需要主插件提供授权。纯懒加载把所有插件等同对待，导致子插件启动时主插件未就绪。

### 设计

插件分两类：

| Scope | 代表 | 生命周期 | 启动方式 |
|---|---|---|---|
| `WorkspaceShared` | StreamX Hub、事件总线节点 | 工作区级，所有 Agent 共享一个进程 | 工作区初始化时**立即启动** |
| `AgentIsolated` | FileBrowser、PowerShell 等工具插件 | Agent 级，每个 Agent 独立进程 | 首次调用时懒加载 |

`plugin.json` 新增可选字段（默认 `agent-isolated`）：

```json
{
  "id": "streamx_hub",
  "scope": "workspace-shared"
}
```

### `ToolSnapshot` 和 `WorkspacePluginManager`

```csharp
enum PluginScope { AgentIsolated, WorkspaceShared }

record ToolSnapshot(
    string PluginId,
    string PluginCommand,
    IReadOnlyList<AITool> Tools,
    PluginScope Scope = PluginScope.AgentIsolated
);

class WorkspacePluginManager : IAsyncDisposable
{
    private readonly Dictionary<string, IPluginHost> _running = new();

    // 工作区初始化时调用，立即启动所有 WorkspaceShared 插件
    public async Task StartAllAsync(
        IEnumerable<ToolSnapshot> snapshots, CancellationToken ct)
    {
        foreach (var s in snapshots.Where(s => s.Scope == PluginScope.WorkspaceShared))
        {
            var host = CreateHost(s);
            await host.StartAsync(ct);
            _running[s.PluginId] = host;
        }
    }

    public Task<string> InvokeAsync(
        string pluginId, string toolName, string argsJson, CancellationToken ct)
        => _running[pluginId].InvokeAsync(toolName, argsJson, ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _running.Values)
            await host.DisposeAsync();
    }
}
```

### 生命周期图

```
工作区初始化
  └─ WorkspacePluginManager.StartAllAsync()
       └─ StreamXHub#Workspace [已就绪，等待子插件连接]

Agent A 实例化    → [快照: StreamXHub(Shared), FileBrowser(Isolated)]
Agent B 实例化    → [快照: StreamXHub(Shared), CodeExec(Isolated)]

Agent A 调 FileBrowser  → 启动 FileBrowser#A → 向 StreamXHub 请求授权 → Hub 已就绪，立即应答
Agent B 调 CodeExec     → 启动 CodeExec#B   → 无需主插件，直接执行

Agent A 销毁  → 停止 FileBrowser#A（WorkspaceShared 不停）
工作区关闭    → WorkspacePluginManager.DisposeAsync() → 停止 StreamXHub#Workspace
```

### 解决的问题

- **子插件找不到主插件**：Hub 在工作区初始化时已就绪，任何子插件启动时都能正常完成授权握手。
- **授权延迟**：时间差从「Hub 启动时间 + 通信时间」缩短为纯通信时间（毫秒级），在 `WaitForFirstSnapshotAsync(5s)` 窗口内轻松完成。

---

## 方案三：插件发现缓存（_discoveredManifests）

### 根因

`PluginManagementPage.RefreshPlugins()` 当前调用 `PluginLoader.GetLoadedPluginInfos()`，该方法只返回 `_nativeHosts`/`_processHosts` 中有**活跃进程**的插件。懒加载下，从未被调用的 `AgentIsolated` 插件没有进程，设置中心看不到，无法绑定、无法配置。

### 设计原则

**将「插件可见」与「插件运行」分离**：设置中心需要的是 `plugin.json` 里的元数据，不需要进程。

### `PluginLoader` 改动

在 `PluginLoader` 里增加一级纯元数据缓存，在 `TryLoadPluginDirectoryAsync` 读完 `plugin.json` 校验通过后立即写入，不依赖进程是否启动：

```csharp
// 新增字段
private readonly ConcurrentDictionary<string, (PluginManifest Manifest, string DirectoryPath)>
    _discoveredManifests = new(StringComparer.OrdinalIgnoreCase);

// TryLoadPluginDirectoryAsync 里，manifest 校验通过后立即缓存：
_discoveredManifests[pluginDir] = (manifest, pluginDir);
// 后续 LoadNativePluginAsync / LoadProcessPluginAsync 不变

// UnloadPluginByPath 里同步清理：
_discoveredManifests.TryRemove(pluginPath, out _);

// 对外暴露：
public IReadOnlyList<DiscoveredPluginInfo> GetDiscoveredPlugins()
    => _discoveredManifests.Values
        .Select(entry => new DiscoveredPluginInfo(
            entry.Manifest,
            entry.DirectoryPath,
            IsPluginRunning(entry.DirectoryPath)))
        .ToList();

// 供设置中心判断是否允许更新：
public bool IsDiscoveredPluginRunning(string pluginId)
    => _discoveredManifests.Values
        .Where(e => string.Equals(e.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase))
        .Select(e => e.DirectoryPath)
        .Any(IsPluginRunning);

private bool IsPluginRunning(string pluginPath)
    => _nativeHosts.ContainsKey(pluginPath) || _processHosts.ContainsKey(pluginPath);
```

已有的 `FileSystemWatcher`（`StartWatching()`）监控目录创建事件，会调用 `TryLoadPluginDirectoryAsync`，写入 `_discoveredManifests` 后设置中心刷新即可看到新插件，**无需额外改动目录监控逻辑**。

### 设置中心改动

```csharp
// PluginManagementPage.RefreshPlugins() 里改一行：
// 原来：_plugins.AddRange(PluginLoader.GetLoadedPluginInfos()...);
// 改为：
_plugins.AddRange(PluginLoader.GetDiscoveredPlugins()
    .OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase));
```

`DiscoveredPluginInfo.IsRunning` 可在列表项上显示「未运行」标签，提示用户插件已安装但尚未被调用。所有配置项（`SettingsSchema`、`RequiredHostCapabilities`、绑定关系）均从 `Manifest` 读取，不依赖进程。

---

## 方案四：插件更新与卸载流程

### 更新流程

插件以目录形式存放在 `AppPaths.UserPluginsDirectory`，更新需要替换文件，必须先释放文件锁：

```
① 设置中心点击「更新」
② 检查 IsDiscoveredPluginRunning(pluginId)
   ├─ true  → 提示「插件正在被智能体使用，请等待对话结束后再更新」，终止
   └─ false → 继续
③ 调用 PluginLoader.UnloadPlugin(dirName)（停止进程，释放文件锁）
④ 替换插件目录下的文件（Store 下载覆盖 / 手动复制）
⑤ 调用 PluginLoader.ReloadPluginAsync(dirName)
⑥ 设置中心 RefreshPlugins()，显示新版本
```

`PluginManagementProvider` 已向 AI 暴露 `sys_unload_plugin` / `sys_reload_plugin` 工具，AI 可按相同流程驱动更新，无需重复实现。

### 使用中阻断

在 `OnUnloadClick`、`OnDeleteClick`（设置中心）和 Store 更新入口统一检查：

```csharp
if (PluginLoader.IsDiscoveredPluginRunning(_selectedPlugin.Manifest.Id))
{
    ShowPageToast(
        "插件正在被智能体使用，请等待对话结束后再操作",
        TimeSpan.FromSeconds(3));
    return;
}
```

`WorkspaceShared` 插件（如 StreamX Hub）同样适用此检查。Hub 运行期间停止它会导致所有子插件授权失效，需要在 Toast 中额外提示：「该插件为工作区共享插件，停止后所有依赖子插件将失去授权」。

### 删除后清理

现有 `RemovePluginBindingFromAllAgents(pluginId)` 已完整实现：遍历所有 Agent，从 `BoundPlugins` 移除该 `pluginId` 并调 `AgentService.Update`。保留此逻辑不变。

---

## 方案五：配置存储决策

### 结论：运行时配置值留数据库，绑定关系留文件

| 数据 | 存储位置 | 原因 |
|---|---|---|
| 插件运行时配置（API Key、端口等） | 数据库（`SystemSettingsService`） | 敏感值有 `PluginConfigValueProtector` 加密保护；插件卸载重装后 `pluginId` 不变，配置自动保留 |
| Agent 与插件的绑定关系（`BoundPlugins`） | Agent `manifest.yaml` | 已实现，随 Agent 文件一起版本管理 |
| Agent 与 MCP 服务绑定（`BoundMcp`） | Agent `manifest.yaml` | 同上 |

Key 格式不变：`Plugin:{pluginId}:Config:{key}`，设置中心通过 `SettingsService.EnsureSetting` + `SettingsService.SaveBatch` 读写，逻辑不变。

`PluginSettingDescriptor.SettingsSchema` 来源从运行实例改为 `_discoveredManifests` 里的 `Manifest.SettingsSchema`，对 UI 层无感知，仅改数据来源一行。

---

## 改造范围汇总

| 文件 | 改动 | 说明 |
|---|---|---|
| `PluginManifest.cs` | 新增 `scope` 字段解析 | `plugin.json` 可选 `"scope": "workspace-shared"`，默认 `agent-isolated` |
| 新增 `PluginScope.cs` | 新建 | `enum PluginScope { AgentIsolated, WorkspaceShared }` |
| 新增 `ToolSnapshot.cs` | 新建 | 纯元数据，含 `Scope` 字段 |
| `PluginLoader.cs` | 新增 `_discoveredManifests` | manifest 校验通过后立即缓存；新增 `GetDiscoveredPlugins()`、`IsDiscoveredPluginRunning()` |
| 新增 `DiscoveredPluginInfo.cs` | 新建 | `(Manifest, DirectoryPath, IsRunning)` |
| 新增 `WorkspacePluginManager.cs` | 新建 | 工作区级插件启动/调用/释放 |
| 新增 `AgentPluginManager.cs` | 新建 | 懒加载 + `Scope` 分流 + 状态通知 + `DisposeAsync` 全清理 |
| `ExternalProcessPluginHostBase.cs` | 移除锁 | `SemaphoreSlim(1,1)` 不再需要（单 Agent 串行保证） |
| `AIAgentFactory.cs` | 修改 | 注入 `AgentPluginManager`（含 `WorkspacePluginManager` 引用）而非共享插件列表 |
| 工作区初始化入口 | 修改 | 在 Agent 创建前先 `WorkspacePluginManager.StartAllAsync()` |
| `PluginManagementPage.axaml.cs` | 修改 | `RefreshPlugins` 改用 `GetDiscoveredPlugins()`；卸载/删除/更新前加 `IsDiscoveredPluginRunning` 检查 |

---

## 可复用的现有代码

| 逻辑 | 现有实现 | 复用方式 |
|---|---|---|
| 目录监控热插拔 | `PluginLoader.StartWatching()` + `FileSystemWatcher` | 不变，`OnPluginDirectoryCreated` 内部调 `TryLoadPluginDirectoryAsync`，写入 `_discoveredManifests` 即自动完成 |
| Agent 绑定配置读写 | `AgentEntity.BoundPlugins` / `AgentService.Update` | 不变，`pluginId` 作 key |
| 插件配置值读写 | `SystemSettingsService.EnsureSetting` / `SaveBatch` | 不变 |
| 插件配置项 UI | `BuildPluginSettings` / `PluginSettingDescriptor` | 数据来源从运行实例改为 `_discoveredManifests[manifest]`，UI 层无需改动 |
| 删除后清理绑定 | `RemovePluginBindingFromAllAgents` | 不变 |
| AI 驱动更新 | `PluginManagementProvider`（`sys_unload_plugin` / `sys_reload_plugin`） | 不变，AI 工具调用流程与设置中心按钮流程等价 |

---

## 关联文档

- [插件跨平台兼容性评估.md](插件跨平台兼容性评估.md)
- [UI重构技术选型决策.md](UI重构技术选型决策.md)
- [插件事件总线开发指南.md](../../插件体系/策划/三方对接文档/插件开发指南/插件事件总线开发指南.md)
- [StreamX 三方插件接入指南](../../../../Workspace/StreamX/docs/对外合作/三方插件接入指南.md)
