# UI 线程卡死风险审计：PowerShell 长输出（主因）与其它相关路径

审计日期：2026-07-15（首版）  
2026-07-15 修订（第二版：剔除退出路径推测项，只保留经代码确认的对话时卡死路径）  
2026-07-15 再修订（第三版：按用户实测反馈重排优先级——PowerShell 长输出确认为真实卡死主因；插件管理页同步卸载因单次点击场景降级为次要）

审计范围：`Src\Netor.Cortana.Plugin\`、`Src\Netor.Cortana.UI\`、`Src\Netor.Cortana.AI\` 中在**正常对话运行期间**可能冻结 UI 线程的调用链  
用户明确排除：应用退出路径（`App.Exit` → `ShutdownApplicationAsync().GetAwaiter().GetResult()` → 各 `Dispose*` 里的 `WaitForExit(3000)` / `WaitAsync()` 无超时）不在本次审计范围内。

---

## 摘要（第三版）

用户实测反馈：**长任务下频繁调用 PowerShell 时会出现界面卡顿甚至假死**。经逐行代码核对，主因锁定为 PS 逐行输出经无节流 `Dispatcher.UIThread.Post` 打爆 UI 队列。

- **P0 主因（用户实测已确认卡死）**：PS 长输出下 Dispatcher 队列被逐行 Post 洪水（第 1 节）
- **P1 支撑因素（放大 P0 主因）**：`RealtimeProcessCard.FlushContent` 长输出下 TextBlock 全量重排累积成本（第 2 节）
- **P2 次要（代码上是同步阻塞，但单次点击场景，用户已确认不是实际痛点）**：插件管理页 Unload/Delete 同步阻塞 UI 线程（第 3 节）
- **RULED OUT（用户特别怀疑但经代码确认非卡死原因）**：AI 对话中 UI 侧调用插件工具（第 4 节）
- **SSH**：仓库中不存在独立 SSH 通道，无卡死风险（第 5 节）

---

## 1. PowerShell 长输出下 Dispatcher 队列被逐行 Post 淹没 **【P0 主因】**

- **风险等级**：HIGH（用户实测已确认）
- **用户反馈原话**：「在长任务里面需要调用 PS 完成很多任务或者是一个长任务的时候，我发现界面有卡死的现象」
- **触发场景**：AI 触发的 PS 命令产出大量 stdout / stderr，或长任务中连续多次调用 PS
  - 典型场景：`Get-ChildItem -Recurse`、`git log --stat`、`npm install`、构建脚本、批量文件操作、`Get-EventLog`、日志抓取等
- **经确认的证据链（producer → consumer 逐行核对）**：

### 1.1 Producer：PS 读线程逐行触发事件

`Src\Netor.Cortana.Plugin\BuiltIn\PowerShell\PowerShellExecutor.cs:182-211`
```csharp
private async Task ReadStreamAsync(StreamReader reader, StringBuilder builder, bool isError)
{
    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        builder.AppendLine(line);
        if (isError)
        {
            OnErrorReceived?.Invoke(line);
            _logger.LogError("PS错误: {Line}", line);        // per-line 日志
        }
        else
        {
            OnOutputLineReceived?.Invoke(line);
            _logger.LogInformation("PS输出: {Line}", line);  // per-line 日志
        }
    }
}
```
每读到一行就触发一次事件 + 一次日志。**无节流、无合并、无背压**。

### 1.2 Publisher：每行 fire-and-forget 一次事件

`Src\Netor.Cortana.Plugin\BuiltIn\PowerShell\PowerShellProvider.cs:240-241`
```csharp
void OnOutput(string line) => _ = PublishProcessEventAsync(processId, "running", line, null, stopwatch.ElapsedMilliseconds, ct);
void OnError(string line)  => _ = PublishProcessEventAsync(processId, "running", $"[stderr] {line}", null, stopwatch.ElapsedMilliseconds, ct);
```
每行 → 一次 `PublishProcessEventAsync` → `_realtimeOutput.OnProcessEventAsync(evt)`。

### 1.3 UI 派发入口：每行一次 Dispatcher.Post

`Src\Netor.Cortana.UI\Channels\UiChatOutputChannel.cs:31-45`
```csharp
public Task OnProcessEventAsync(RealtimeProcessEvent evt, CancellationToken ct = default)
{
    if (ct.IsCancellationRequested) return Task.CompletedTask;
    if (string.IsNullOrWhiteSpace(evt.ProcessId)) return Task.CompletedTask;

    Dispatcher.UIThread.Post(() => HandleProcessEvent(evt));   // ← 每行一次 Post，默认优先级
    return Task.CompletedTask;
}
```
**每一行 = 一次 `Dispatcher.UIThread.Post`**。`Post` 未指定 `DispatcherPriority`，走默认（`Send/Normal` 级），**与用户点击、键盘输入同优先级**。

### 1.4 UI 端处理：每行三次 UI 调用

`Src\Netor.Cortana.UI\Channels\UiChatOutputChannel.cs:243-249`
```csharp
handle.UpdateStatus(status, normalizedEvent.ExitCode, normalizedEvent.DurationMs);
handle.AppendContent(normalizedEvent.Content);
ScrollToBottom();
```
每行 =
- 一次 `UpdateStatus`（内部又 `Post` 一次 `UpdateHeader` 到 UI 线程）
- 一次 `AppendContent`
- 一次 `ScrollToBottom`（触发 `ScrollViewer` 布局失效）

### 1.5 卡死机制解释

- PS 高吞吐命令可秒级产生数千行 → Dispatcher 队列被数千个 Normal 优先级委托积压。
- Avalonia 的用户输入事件（点击、键盘、拖动、动画帧）不高于 `Normal`，被排到队尾。
- 视觉表现：**点按钮无响应、滚动卡顿、输入延迟、动画掉帧**——用户描述的「界面卡死」的典型症状。
- 长任务中连续多次 PS 调用会**累积**——每次 PS 命令产生的 Post 堆叠到同一个 UI 队列，且每个 `RealtimeProcessCard` 都持有自己的 150ms `DispatcherTimer`（`_flushTimer`），Card 数量增多时定时器本身也在 UI 队列上竞争。

### 1.6 缓解因素（经确认，但只减轻渲染层，未解决 Dispatcher 洪水本身）

`Src\Netor.Cortana.UI\Controls\Common\RealtimeProcessCard.axaml.cs:54`
```csharp
_flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => FlushContent());
_flushTimer.Start();
```
- `AppendContent`（`:80-106`）只做 `StringBuilder.Append` + `_isDirty=true`，很轻；
- `TextBlock.Text` 赋值放到 150ms `Background` 优先级定时器（见第 2 节）。
- **这意味着渲染层已节流**，不会因每行文字导致每帧重排。
- 但 **`Dispatcher.UIThread.Post(HandleProcessEvent)` 本身没节流**——委托依然按行排队执行 `UpdateStatus` / `AppendContent` / `ScrollToBottom`，把 Dispatcher 主队列的 Normal-slot 全部塞满。这是用户实测卡顿的直接原因。

### 1.7 修复方向（推荐从上到下依次落地）

**修复 A（P0，最直接见效）**：`UiChatOutputChannel.OnProcessEventAsync:43`  
把 `Dispatcher.UIThread.Post(() => HandleProcessEvent(evt))` 改为
```csharp
Dispatcher.UIThread.Post(() => HandleProcessEvent(evt), DispatcherPriority.Background);
```
让 PS 输出处理让位于用户输入。**改动最小、风险最低**。

**修复 B（P0，根治）**：在 `PowerShellProvider.OnOutput` / `OnError` 侧引入逐会话合并缓冲区
- 每 100ms 或每 200 行合并一次 `PublishProcessEventAsync`。
- 参考已有 debounce 实现：`Src\Netor.Cortana.AI\Providers\SkillDirectoryWatcherService.cs`（200ms `Timer`）、`Src\Netor.Cortana.Entitys\Services\AgentFileWatcher.cs`（同样 200ms）。

**修复 C（P0）**：`HandleProcessEvent` 中 running 分支的 `ScrollToBottom()`  
连续 running 事件时不每次滚动，改为节流（100ms coalesce）或只在 flush 时滚动。

**修复 D（P1）**：`PowerShellExecutor.ReadStreamAsync:198,203` 的 per-line 日志  
`LogInformation("PS输出: {Line}", line)` 在长命令下会向日志 sink 写数千次。若 sink 走同步 File 或走 UI，是次生放大器——降级为 `LogTrace` 或按批次记录。

### 1.8 推荐修复实现：`System.Threading.Channels` 解耦生产/消费

用户提出的思路：**用 Channel 把 PS 输出压入队列，独立 reader 顺序读取、批量合并后再更新界面**。此方案能同时承载「修复 B（合并事件）」+ 天然背压 + 未来扩展点，是修复 A 的加强版根治方案。

**关键 insight**：Channel 本身**不是**节流器，是「让批量合并逻辑更好写的容器」。如果 reader 依然「读一行 → `Dispatcher.UIThread.Post` 一行」，Dispatcher 队列一样会被塞爆。**真正解决卡死的动作是 reader 侧做批量合并**，channel 只是承载这个逻辑的载体。

#### 架构

```
[PS stdout/stderr]  ──►  [ReadStreamAsync]  ──►  Channel<PsOutputChunk>  ──►  [Consumer Task]  ──►  批量合并  ──►  单次 PublishProcessEventAsync  ──►  UI
   (pwsh 进程)          (PS 读线程，Producer)   (Bounded, per-processId)      (后台线程，Reader)      (100ms/200 行阈值)      (Dispatcher.Post, Background 优先级)
```

- **Producer**：`ReadStreamAsync` 的每一行 `TryWrite` 入 channel（不再 `Invoke` 事件、不再 fire-and-forget publish）
- **Channel**：`Channel.CreateBounded<PsOutputChunk>`，`SingleReader = true`
- **Consumer**：独立后台 Task，攒够 100ms 或 200 行做一次合并 publish
- **UI**：只收到已合并的粗粒度事件，`Dispatcher.Post` 频率下降 1~2 个数量级

#### 参考实现（PowerShellProvider 侧）

```csharp
// 记录：区分 stdout / stderr，保留原始行以便按需带 [stderr] 前缀
internal readonly record struct PsOutputChunk(string Line, bool IsError);

private async Task PumpOutputAsync(
    string processId,
    Channel<PsOutputChunk> channel,
    Stopwatch stopwatch,
    CancellationToken ct)
{
    var batch = new StringBuilder();
    var reader = channel.Reader;

    while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
    {
        batch.Clear();
        var lineCount = 0;
        var deadline = Environment.TickCount + 100;   // 100ms 阈值

        while (lineCount < 200
               && Environment.TickCount < deadline
               && reader.TryRead(out var chunk))
        {
            batch.AppendLine(chunk.IsError ? $"[stderr] {chunk.Line}" : chunk.Line);
            lineCount++;
        }

        if (batch.Length > 0)
        {
            await PublishProcessEventAsync(
                processId, "running", batch.ToString(),
                null, stopwatch.ElapsedMilliseconds, ct).ConfigureAwait(false);
        }
    }
}

// 启动侧（原 OnOutput/OnError 处替换）
var channel = Channel.CreateBounded<PsOutputChunk>(new BoundedChannelOptions(capacity: 10_000)
{
    FullMode      = BoundedChannelFullMode.Wait,   // 关键：天然背压
    SingleReader  = true,
    SingleWriter  = false,                          // stdout/stderr 两个 writer
    AllowSynchronousContinuations = false,
});

void OnOutput(string line) => channel.Writer.TryWrite(new PsOutputChunk(line, IsError: false));
void OnError(string line)  => channel.Writer.TryWrite(new PsOutputChunk(line, IsError: true));

var pumpTask = Task.Run(() => PumpOutputAsync(processId, channel, stopwatch, ct), ct);

try
{
    // ... 现有 ExecuteAsync 逻辑 ...
}
finally
{
    channel.Writer.TryComplete();
    try { await pumpTask.ConfigureAwait(false); } catch { /* 忽略取消 */ }
}
```

#### 为什么 `FullMode = Wait` 是安全的（甚至是好的）

Writer 是 PS 的 stdout/stderr 读线程（`ReadStreamAsync`）。当 channel 满时：
- 若用 `TryWrite`：返回 false，直接丢一行（不推荐，用户看不到完整输出）
- 若用 `WriteAsync`：writer 被 await 阻塞 → `ReadStreamAsync` 停顿 → pwsh 的 stdout pipe 缓冲区填满 → **pwsh 自己被 OS 阻塞在 `WriteFile`**

第二种是**天然背压**：让高吞吐命令自己慢下来，而不是把 UI 拖死。丢日志（`DropOldest`/`DropWrite`）对 PS 场景反直觉——用户希望看到完整输出。

> 推荐用 `WriteAsync`（背压）而非 `TryWrite`（可能丢行）。容量 10000 行足够吸收正常抖动，只有恶性洪水才会触发背压。

#### 与现有 `_flushTimer` 的关系（两级缓冲各司其职）

```
[Producer] ──► Channel（层 1：事件密度合并，100ms/200行 → 合并 publish）
             ↓
        [UI Dispatcher]（Background 优先级，让位于用户输入）
             ↓
        [RealtimeProcessCard._flushTimer]（层 2：渲染节流，150ms → TextBlock 一次赋值）
             ↓
        [Avalonia 渲染]
```

**层 1（新增，Channel + 后台合并）**：解决**Dispatcher 队列被 Post 洪水打爆**（第 1 节 P0 主因）  
**层 2（已存在，`RealtimeProcessCard._flushTimer:54`）**：解决**TextBlock 全量重排累积成本**（第 2 节 P1 支撑因素）

两层职责正交，都要保留。层 2 无需改动。

#### 生命周期与清理

- **Channel 归属**：随本次 PS 执行会话生命周期，`ExecuteAsync` 的 `finally` 里 `Writer.TryComplete()` 优雅关闭
- **Pump Task**：随 `ct` 取消或 channel 完成后自然退出
- **多会话**：每个 `processId` 独立一个 Channel + Pump Task，互不干扰；`SessionRegistry` 中的长期会话按会话粒度维护 channel
- **取消传播**：外部 `ct` 取消 → pump Task 抛 `OperationCanceledException` → 剩余未 drain 的 chunk 丢弃（可选择在 catch 里 flush 一次残余 batch，让用户看到「取消瞬间」的最后几行）

#### 相较「修复 A + 修复 B」的取舍

| 维度 | 单做 A（Post 加 Background 优先级） | A + B（Channel 合并 + Background 优先级） |
|---|---|---|
| 改动面 | XS（1 行） | M（新增 pump + channel 生命周期） |
| 见效程度 | 中（UI 输入不被排后，但队列仍在堆积） | 高（队列压力从根源下降 1~2 数量级） |
| 背压 | 无 | 天然（Channel 满 → pwsh 被阻塞在 WriteFile） |
| 复用性 | 单点 | 高（未来 SSH、进程插件流式输出可复用同一 pipeline） |
| 风险 | 极低 | 低（新增异步组件需覆盖测试） |

**推荐落地节奏**：
1. **先做修复 A**（`DispatcherPriority.Background`，1 行改动），实测能否缓解卡顿——若够，Channel 方案作为 P1 计划；
2. **若 A 不够**（说明 UI 线程被 `UpdateStatus` / `AppendContent` CPU 打满，不只是排队问题），立即上 Channel 方案（本节实现）；
3. Channel 方案上线后，**层 2（`_flushTimer` 150ms）保持不变**，两级缓冲互补。

#### 测试要点

- **单元测试**：Pump 逻辑独立可测——喂 1000 行、断言 publish 次数 ≤ 10（100ms/200 行阈值）
- **压力测试**：`while ($true) { Get-Date; Start-Sleep -Milliseconds 1 }` 类高频命令，观察 UI 输入延迟应保持 < 100ms
- **背压验证**：故意让 consumer 慢（sleep 500ms/batch），观察 pwsh 是否被阻塞在 WriteFile（Process Explorer 看 stdout pipe 状态）
- **取消验证**：命令执行中途取消，pump task 应在 1 秒内退出，无泄漏

---

## 2. `RealtimeProcessCard.FlushContent` 长输出下 TextBlock 全量重排 **【P1 支撑因素】**

- **风险等级**：MEDIUM（放大第 1 节主因）
- **触发场景**：单次 PS 命令持续产出，`_contentBuffer` 增长到几十 KB 到几 MB；或多个 PS Card 同时存活
- **经确认的证据链**：

`Src\Netor.Cortana.UI\Controls\Common\RealtimeProcessCard.axaml.cs:206-216`
```csharp
private void FlushContent()
{
    if (!_isDirty) return;
    ContentBlock.Text = _contentBuffer.ToString();   // ① 全量拷贝
    DetailScroller.ScrollToEnd();                    // ② 触发 layout invalidate
    _isDirty = false;
}
```
每 150ms 一次：
- `_contentBuffer.ToString()`：分配一个和当前累积内容等长的 `string`（UTF-16，长度 × 2 字节）。5MB 输出 → 10MB 临时字符串 → GC 压力线性增长。
- `ContentBlock.Text = ...`：Avalonia `TextBlock` 全量 `TextLayout.Rebuild`（O(N)）。5M 字符即使 monospace 也要几百 ms。
- `DetailScroller.ScrollToEnd()`：触发 `ScrollViewer` 的 `Measure/Arrange`。

单次 flush 就足以超过 16.6ms 帧预算 → UI 掉帧、动画卡顿。且该 flush 定时器**每个 Card 都独立运行**，Card 越多累积成本越高。

### 修复方向

- **单个 Card 输出长度上限**：超过阈值（如 256KB）后停止追加，UI 显示「输出已截断，完整日志见 xxx」。
- **改用 `TextBox` + `AppendText` 增量追加**替代 `TextBlock.Text` 全量赋值（若 Avalonia 版本支持）。
- Card 完成后（`Complete` / `CompleteCollapsed`）`_flushTimer.Stop()` 已经在做，无需额外处理（`RealtimeProcessCard.axaml.cs:124`）。

---

## 3. 插件管理页 Unload/Delete 同步阻塞 UI 线程 **【P2 次要】**

- **风险等级**：LOW-MEDIUM（代码上是同步阻塞，但用户已确认非实际痛点）
- **用户反馈**：「本身这个过程不可能多个同时出现，用户操作它也是一个一个的去点击，所以说这个地方的卡死基本上不可能」
- **保留原因**：代码事实成立，属于潜在改进项，但不作为本轮修复重点

### 3.1 证据链

- `PluginManagementPage.axaml.cs:1345` `OnUnloadClick` 同步 `void` handler → `PluginLoader.UnloadPlugin(...)`
- `PluginManagementPage.axaml.cs:1377` `OnDeleteClick` 同步 → `UnloadPlugin` + `Directory.Delete(recursive)`（`:1402-1407`）
- `PluginLoader.cs:751` `UnloadPluginByPath` → `nativeHost.Dispose()` / `processHost.Dispose()`
- `ExternalProcessPluginHostBase.cs:130` `_process?.WaitForExit(3000);`（Destroy 路径）
- `ExternalProcessPluginHostBase.cs:266` `_process.WaitForExit(3000);`（Kill 后强制等待）

最坏时长：3s + 3s = 6s + 目录递归删除 I/O。

### 3.2 是否修复

用户可自行决定：
- 若认为「点一次卸载卡 6 秒」可接受 → 保持现状；
- 若认为需要视觉反馈 → `OnUnloadClick` / `OnDeleteClick` 改 `async void`，`ExternalProcessPluginHostBase` 提供 `DisposeAsync()`（内部 `Process.WaitForExitAsync(cts)`），`Directory.Delete` 走 `Task.Run`。

不列入 P0/P1，与主因（PS 长输出）无耦合。

---

## 4. RULED OUT：AI 对话中 UI 侧调用插件工具**不会**卡死 UI

用户特别怀疑「AI 调插件时可能卡 UI」，经代码核对**这条路径不成立**：

- `Src\Netor.Cortana.UI\ViewModels\ExpertMode\ChatInputVm.cs:462`
  ```csharp
  await _chatService.SendMessageAsync(text ?? string.Empty, cancellationToken, attachments);
  ```
  UI ViewModel 全程 `await`，不阻塞 UI 线程。

- `Src\Netor.Cortana.AI\AiChatHostedService.cs:184-249`
  `SendMessageAsync` 全程 `.ConfigureAwait(false)`，async 状态机不回到 UI SynchronizationContext。

- `Src\Netor.Cortana.AI\ChatTurnExecutor.cs:139`
  ```csharp
  await foreach (var chunk in turnAgent.RunStreamingAsync(msg, turnSession, cancellationToken: streamCts.Token))
  ```
  AI 流式循环 + 内部工具调用（含 `InvokePluginToolAsync`）全部运行在线程池，UI 线程始终空闲。

- `Src\Netor.Cortana.Plugin\Core\ExternalProcessPluginHostBase.cs:84-114` `SendRequestAsync` 是纯 async + 30 秒读超时 + `SemaphoreSlim`。即使插件宿主完全静默，也只是 async caller 卡在超时，UI 不阻塞。

- UI 项目内全仓 grep `.Result` / `GetAwaiter().GetResult()` / `Dispatcher.UIThread.Invoke`（同步版）→ 未见命中插件调用链的代码。

**结论**：长对话下的 UI 卡死不是来自「AI 调插件」本身，而是来自**PS 工具产出的高吞吐输出经逐行 Dispatcher.Post 打爆 UI 队列**（第 1 节）。这也解释了为什么用户观察到「PS 对话特别是长对话」会卡——只有 PS 会持续吐大量 stdout，其它工具（文件读写、Web 请求、MCP 调用）都是一次性 return。

---

## 5. SSH

仓库中不存在独立 SSH 通道：

- `Src\Netor.Cortana.Plugin\BuiltIn\PowerShell\PowerShellProvider.cs:91`：文案提示 AI「SSH 走专用 SSH 插件」，但仓库中并无该插件实现。
- `Src\Netor.Cortana.Plugin\BuiltIn\PowerShell\PowerShellProvider.cs:407-453`：`LooksLikeInteractiveSshCommand` 只做字符串拒绝，不启动 `ssh.exe`。

结论：SSH 子系统**没有 UI 卡死风险**（0 处发现）。

---

## 6. 修复优先级与预期收益

| 优先级 | 项 | 位置 | 预计工作量 | 用户可感收益 |
|---|---|---|---|---|
| P0-A | `UiChatOutputChannel.OnProcessEventAsync` 改 `DispatcherPriority.Background` | `UiChatOutputChannel.cs:43` | XS（1 处改动） | PS 长输出期间用户输入立即响应 |
| P0-B | `PowerShellProvider` 侧按 100ms 或 200 行合并 PS 输出事件（推荐用 Channel 解耦，见 §1.8） | `PowerShellProvider.cs:240-241` | M（新增合并器 / Channel + pump） | Dispatcher 队列 Post 次数减少 1~2 个数量级，天然背压 |
| P0-C | `HandleProcessEvent` running 分支的 `ScrollToBottom()` 节流 | `UiChatOutputChannel.cs:249` | S | 长输出中滚动不再卡顿 |
| P1-A | 单 Card 输出长度上限（256KB） + 截断提示 | `RealtimeProcessCard.axaml.cs:80-96` | S | 极端命令不再拖累消息列表 |
| P1-B | `PowerShellExecutor` per-line 日志降级为 `LogTrace` 或批量 | `PowerShellExecutor.cs:198,203` | XS | 减小日志 sink 压力 |
| P2 | 插件管理页 Unload/Delete 异步化 | `PluginManagementPage.axaml.cs:1345,1377` | M | 卸载/删除按钮不再有 3~6s 静默（次要，视用户是否需要） |

**建议先做 P0-A（改动最小、风险最低、立即验证是否解决用户实测卡顿）**，再评估 P0-B/C 是否必要。

---

## 审计方法与保证

1. 每一条 HIGH/MEDIUM 发现均**经代码文件+行号核对**，未使用推测。
2. 排除了用户明确指定不在范围内的退出路径类发现（首版第 1.4、3.2、3.3、3.4、3.5、3.6、3.7 节）。
3. 用户特别怀疑的「插件调用卡 UI」结论为**否定**，理由是 UI → AI → 插件全链 `await`+`ConfigureAwait(false)`，UI 项目内无同步阻塞点。
4. 用户实测反馈作为定性依据：将 PS 长输出确定为**实际卡死主因**，其它同步阻塞项按「实际触发频率×单次影响」重排优先级。
5. 核对文件列表：
   - `PluginManagementPage.axaml.cs`
   - `PluginLoader.cs`
   - `ExternalProcessPluginHostBase.cs`
   - `PowerShellExecutor.cs`
   - `PowerShellProvider.cs`
   - `UiChatOutputChannel.cs`
   - `RealtimeProcessCard.axaml.cs`
   - `ChatInputVm.cs`
   - `AiChatHostedService.cs`
   - `ChatTurnExecutor.cs`
