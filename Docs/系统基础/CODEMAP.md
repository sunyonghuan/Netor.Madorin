# Netor.Cortana — CODEMAP 基准

> **建立时间：** 2026-07-19  **分支：** master  **代码规模：** 758 个 .cs 文件 / ~140 k LOC / 19 个源码项目

---

## 一、项目清单

| 项目 | 目标框架 | 文件数 | LOC | 简述 |
|---|---|---|---|---|
| **Netor.Cortana.Entitys** | net10.0 | 85 | 11 255 | 领域实体、SQLite 数据层、跨域接口 |
| **Netor.Cortana.Plugin** | net10.0 | 44 | 7 620 | 插件加载、生命周期、多宿主调度 |
| **Netor.Cortana.Store** | net10.0-windows | 33 | 3 877 | 插件市场客户端、安装/卸载、账号 |
| **Netor.Cortana.Networks** | net10.0 | 48 | 6 585 | WebSocket PluginBus 中继服务 |
| **Netor.Cortana.AI** | net10.0 | 172 | 24 482 | 多 Agent 编排、LLM 驱动层、工作流 |
| **Netor.Cortana.UI** | net10.0-windows | 75 | 25 828 | Avalonia 桌面主壳、所有交互视图 |
| **Netor.Cortana.Voice** | net10.0 | 24 | 3 125 | 语音管线（KWS/STT/TTS）、麦克风仲裁 |
| **Netor.Cortana.NativeHost** | net10.0 | 1 | 331 | 原生插件宿主进程（P/Invoke 桥） |
| **Netor.Cortana.Plugin.Native** | net10.0 | 8 | 382 | 原生插件属性与运行时类型 |
| **Netor.Cortana.Plugin.Native.Generator** | netstandard2.0 | 12 | 2 810 | Roslyn 源生成器 → Startup.g.cs |
| **Netor.Cortana.Plugin.Native.Debugger** | net10.0 | 16 | 1 255 | 原生插件调试 REPL |
| **Netor.Cortana.Plugin.Process** | net10.0 | 26 | 2 569 | 进程插件运行时类型与消息循环 |
| **Netor.Cortana.Plugin.Process.Generator** | netstandard2.0 | 11 | 2 417 | Roslyn 源生成器 → Program.g.cs |
| **Platform.Core** | net10.0 | 7 | 75 | 平台共享配置模型 |
| **Platform.Entitys** | net10.0 | 111 | 27 848 | EF Core DbContext、平台所有数据表 |
| **Platform.Services** | net10.0 | 20 | 2 711 | 平台业务逻辑（认证/订阅/订单/创作者） |
| **Platform.Api** | net10.0 | 2 | 1 508 | ASP.NET Core 最小化 API（公开 REST） |
| **Platform.Admin** | net10.0 | 47 | 13 160 | MVC 管理后台 + MCP Server |
| **Platform.Web** | net10.0 | 16 | 1 599 | MVC 用户前端（资产浏览/账号/订阅） |

---

## 二、依赖关系图

```
Entitys  (无外部项目依赖)
   ↑
   ├─ Plugin           ──→ Entitys
   │    ↑
   │    ├─ Store       ──→ Entitys, Plugin
   │    ├─ Voice       ──→ Entitys, Plugin
   │    └─ AI         ──→ Entitys, Plugin
   │         ↑
   │         └─ Networks ──→ Entitys, AI
   │               ↑
   │               └─ UI  ──→ Entitys, AI, Voice, Plugin, Networks, Store
   │
   └─ Plugins/
        ├─ NativeHost          (独立进程，无项目引用)
        ├─ Plugin.Native       ──→ Plugin.Native.Generator
        ├─ Plugin.Native.Debugger ──→ Plugin.Native
        └─ Plugin.Process      ──→ Plugin.Process.Generator

Platform 子图（彼此隔离于桌面端）:
   Platform.Core
      ↑
      ├─ Platform.Entitys ──→ Core
      │      ↑
      │      └─ Platform.Services ──→ Entitys, Core
      │              ↑
      │              ├─ Platform.Api   ──→ Services, Entitys, Core
      │              ├─ Platform.Admin ──→ Services, Entitys, Core
      │              └─ Platform.Web   ──→ Services, Entitys, Core
```

---

## 三、核心库层

### 3.1 Netor.Cortana.Entitys — 领域基础

**职责：** 全局领域模型、SQLite 数据库访问、跨层接口契约。

**关键类型：**

| 类型 | 说明 |
|---|---|
| `CortanaDbContext` | SQLite WAL 模式；主数据访问入口 |
| `ChatSessionEntity` | 对话会话（含存档/置顶） |
| `ChatMessageEntity` | 单条消息（含资产附件） |
| `AiModelEntity` / `AiProviderEntity` | 模型与服务商配置 |
| `AgentEntity` | Agent 配置（模型绑定、系统提示词） |
| `McpServerEntity` | MCP 服务器配置 |
| `SystemSettingsEntity` | 全局应用设置 |
| `IAiChatEngine` | **核心接口**：发送消息、创建/恢复会话、取消 |
| `IAiProxySessionManager` | 外部代理会话管理 |
| `IChatTransport` | 消息传输抽象（解耦输入/输出） |
| `IAppPaths` | 目录解析（插件、工作区、配置） |
| `IRealtimeProcessOutput` | 命令输出实时流 |
| `Events`（静态） | 全局 EventHub 事件注册表 |

**外部包：** `Netor.EventHub 1.2.8` · `Microsoft.Data.Sqlite 10.0.8` · `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`

---

### 3.2 Netor.Cortana.Plugin — 插件编排核心

**职责：** 统一插件加载、生命周期管理、多宿主协调（原生/进程/MCP）。

**关键类型：**

| 类型 | 说明 |
|---|---|
| `PluginLoader` | 主调度器；扫描目录、路由宿主、热重载（FileSystemWatcher） |
| `IPlugin` | 通用插件接口（Id/Name/Version/Tools/InitializeAsync） |
| `IInvokablePlugin` | 继承 IPlugin；定义 InvokeToolAsync |
| `PluginManifest` | plugin.json 反序列化；包含 PluginRuntime 枚举 |
| `IPluginContext` | 注入到插件 Init：DataDirectory/Logger/HttpClient/BusPort |
| `LoadedPluginInfo` | record：插件实例 + 元数据 + 路径 + 作用域 |
| `NativePluginHost` | 管理 NativeHost 子进程（stdio JSON-RPC） |
| `ProcessPluginHost` | 管理进程插件子进程（stdio JSON-RPC） |
| `McpServerHost` | MCP 服务器连接管理 |
| `PowerShellExecutor` | 内置 PowerShell 执行插件 |
| `PluginManifestRegistry` | 发布/订阅操作中央注册表 |

**外部包：** `Microsoft.Agents.AI 1.7.0` · `Microsoft.Extensions.AI 10.6.0` · `ModelContextProtocol 1.3.0` · `Netor.EventHub 1.2.8`

---

### 3.3 Netor.Cortana.Store — 包管理与市场

**职责：** 资产安装/卸载、市场 HTTP 客户端、账号/连接/激活管理。

**关键接口：**
- `IPackageInstallService` — 下载→暂存→验证→注册全流程
- `IInstalledAssetStore` — SQLite 持久化已安装资产列表
- `IPlatformMarketClient` — REST 市场 API 客户端
- `IPlatformConnectionTester` — 连通性预检
- `IPluginActivationService` — 安装后触发插件重载回调

**外部包：** `Avalonia 12.0.3` · `System.Security.Cryptography.ProtectedData 10.0.0`

---

### 3.4 Netor.Cortana.Networks — 实时消息总线

**职责：** WebSocket PluginBus 服务端；广播域事件到外部插件/客户端。

**关键类型：**

| 类型 | 说明 |
|---|---|
| `WebSocketPluginBusServerService` | 主宿主；监听端口、接受 WebSocket 升级 |
| `IPluginBusBroadcaster` | 广播核心接口 |
| `PluginBusChatDispatcher` | 聊天消息路由与序列化 |
| `PluginBusMemorySupplyDispatcher` | 上下文记忆注入分发 |
| `PluginBusModelCapabilityDispatcher` | 模型能力广播 |
| `PluginBusSubscriptionRegistry` | 客户端订阅映射 (clientId, topic) |

**外部包：** `Microsoft.AspNetCore.App`（框架引用）· `Netor.EventHub 1.2.8`

---

## 四、AI 层

### 4.1 Netor.Cortana.AI — 多 Agent 编排核心

**职责：** 多提供商 LLM 驱动、会话管理、工作流执行、会议模式、HITL、长期记忆。

**子系统目录：**

| 目录 | 职责 |
|---|---|
| `Agents/` | Agent 工厂与解析器 |
| `Chat/` | 会话生命周期、消息持久化、流式输出 |
| `Drivers/` | LLM 提供商驱动注册表与各厂商实现 |
| `Orchestration/` | 多 Agent 编排路由（工具委派/HandoffChat/工作流） |
| `Delegation/` | 委派 Agent 作业执行、系统工具目录 |
| `WorkMode/` | 长任务工作流、检查点、死锁检测、HITL |
| `MeetingMode/` | 群组/会议模式编排 |
| `Memory/` | 长期记忆上下文注入 |
| `Providers/` | 历史数据、模型用途解析、能力代理 |
| `Hitl/` | 人机介入（暂停/恢复）抽象层 |
| `Proxys/` | Ollama 代理后端 |

**关键类型（Top 20）：**

| 类型 | 子系统 | 说明 |
|---|---|---|
| `AIAgentFactory` | Agents | 创建/配置 AIAgent，含提供商与工具绑定 |
| `ChatAgentResolver` | Agents | 轮次级 Agent 解析（模型/提供商选择） |
| `IAgentOrchestrator` | Orchestration | 编排模式契约（None/委派/Handoff/工作流） |
| `AgentOrchestrator` | Orchestration | 默认编排器，模式回退与子 Agent 路由 |
| `ChatSessionService` | Chat/Runtime | 会话生命周期、历史恢复、提供商绑定 |
| `ChatTurnExecutor` | Chat/Turns | 单轮 Agent.RunAsync + 流式 + 诊断 |
| `ChatMessageWriter` | Chat/Messages | 用户/助手消息写入、内容规范化 |
| `ChatHistoryDataProvider` | Providers | SQLite 历史持久化；历史分页 |
| `IAiProviderDriver` | Drivers/Core | **驱动接口**：CreateChatClient/图像/视频/模型发现 |
| `AiProviderDriverRegistry` | Drivers/Core | 所有驱动实例注册表（按提供商解析） |
| `OpenAiProviderDriver` | Drivers/Native | OpenAI 原生协议 |
| `AnthropicProviderDriver` | Drivers/Native | Anthropic 协议（内部 Netor.Anthropic 包） |
| `KimiProviderDriver` | Drivers/Compatible | Kimi 专用协议（含工具数限制补丁） |
| `OllamaProviderDriver` | Drivers/Compatible | 本地 Ollama 兼容协议 |
| `OpenAiCompatibleProviderDriverBase` | Drivers/Compatible | 通用 OpenAI 兼容基类（阿里云/Gemini/GLM 等） |
| `WorkflowExecutor` | WorkMode | 多步长任务执行、子 Agent、检查点 |
| `ProjectLeadService` | WorkMode | 工作流步骤管理与调度 |
| `MeetingExecutor` | MeetingMode | 群组会议编排（GroupChatManager） |
| `IHitlNotifier` / `IHitlContext` | Hitl | HITL 暂停/恢复抽象 |
| `LongMemorySupplyClient` | Memory | 长期记忆上下文注入 |

**LLM 提供商支持：**

| 类别 | 提供商 |
|---|---|
| 原生协议 | OpenAI · Anthropic · Azure OpenAI |
| OpenAI 兼容 | Kimi · Deepseek · Ollama · 阿里云 DashScope · Gemini · GLM · 自定义端点 |

**工具/能力标记：** `FunctionCall` · `Reasoning`（扩展思考）· 图像生成 · 视频生成 · 上下文长度

**外部包：** `Microsoft.Agents.AI 1.7.0` · `Microsoft.Extensions.AI 10.6.0` · `Netor.Anthropic 12.21.11` · `Netor.EventHub 1.2.8` · `OpenTelemetry.Api 1.15.3` · `Microsoft.Data.Sqlite`

---

## 五、插件体系

### 5.1 两种插件运行时对比

| 维度 | Native 插件 | Process 插件 |
|---|---|---|
| 执行方式 | 进程内 DLL（P/Invoke） | 独立子进程（stdio JSON-RPC） |
| 宿主 | `NativeHost.exe` 通过 NativeLibrary.Load | 父进程 spawn 子进程，管道 stdio |
| IPC | 函数指针（Marshal UTF-8 字符串） | 行分隔 JSON（stdin/stdout） |
| DI 生命周期 | 单例（static ServiceProvider） | 每请求独立 DI 作用域 |
| 异步 | 同步桥接（.GetAwaiter().GetResult()） | 全程 async/await |
| 崩溃影响 | NativeHost 子进程崩溃，父进程不受影响 | 插件子进程崩溃，管道关闭通知父进程 |
| 代码生成 | NativePluginGenerator → `Startup.g.cs`（5 个 UnmanagedCallersOnly 导出） | ProcessPluginGenerator → `Program.g.cs`（RunPluginAsync 入口） |
| 调试支持 | `Plugin.Native.Debugger`（反射 REPL） | 生成器生成 `{Class}Debugger.g.cs`（强类型单元测试辅助） |

### 5.2 插件协议（5 方法）

```
get_info   → 返回 plugin.json（工具元数据、设置 Schema、能力声明）
init       → 传入配置 JSON → 初始化 DI / 读取设置
invoke     → 传入 toolName + args JSON → 执行工具 → 返回结果 JSON
free       → 释放 Native IntPtr（仅 Native）
destroy    → 停止 HostedService，释放 ServiceProvider
```

### 5.3 源生成器生成内容

**Native.Generator：**
- `Startup.g.cs` — 5 个 `[UnmanagedCallersOnly]` 导出函数 + 工具路由字典 + 桥接方法（参数解析 + 服务解析 + 调用 + 序列化）
- `plugin.json` — 嵌入到 `get_info` 的元数据
- 诊断警告（参数类型不支持、工具名冲突等）

**Process.Generator：**
- `Program.g.cs` — `RunPluginAsync()` 两个重载（Console / TextReader+TextWriter）+ 工具路由字典（`ToolInvoker` async 委派）+ `RegisterTools()` 方法
- `{Class}Debugger.g.cs` — 强类型单元测试辅助类，支持内存管道
- `plugin.json` — 包含工具和设置 Schema

### 5.4 关键属性（开发时）

```csharp
[Plugin(Id="...", Name="...", Version="1.0.0", Tags=[], Capabilities=[], Instructions=null)]
[Tool(Name="...", Description="...")]
[Parameter(Description="...", Required=true)]
[PluginSetting(Key="...", Type=PluginSettingType.String, Required=true)]
[RequiredHostCapability("...")]
```

---

## 六、UI 与语音

### 6.1 Netor.Cortana.UI — 主桌面壳

**框架：** Avalonia 12.0.3（跨平台，Release 启用 Native AOT win-x64 自包含）

**启动链：**
```
Program.cs → BuildAvaloniaApp() → StartWithClassicDesktopLifetime()
  → App.axaml.cs OnFrameworkInitializationCompleted()
      → ServiceCollection（100+ 服务注册）
      → MainWindow / FloatWindow / BubbleWindow 创建
      → EventHub 初始化
      → PluginLoader 启动
      → 后台服务调度
      → 托盘图标
```

**关键类型：**

| 类型 | 说明 |
|---|---|
| `MainWindow` | 主聊天窗口；选项卡切换、会话加载、EventHub 订阅 |
| `SettingsWindow` | Agent/插件/模型/MCP 配置 UI |
| `FloatWindow` / `BubbleWindow` | 浮动唤醒提示、系统通知 |
| `MainWindowVm` | 主窗口状态与命令 ViewModel |
| `LeftPanelVm` | 左侧面板工作区/会话树 ViewModel |
| `ChatInputVm` / `WorkModeInputVm` / `MeetingInputVm` | 各模式输入区 ViewModel |
| `UiChatOutputChannel` | 实时流式 AI 响应渲染到 UI |
| `WindowToolProvider` / `AiConfigToolProvider` | AI 工具上下文（窗口信息、配置） |
| `PluginManagementProvider` | 插件操作 AI 工具提供者 |

**外部包：** `Avalonia 12.0.3` · `Markdig 1.1.2`（Markdown 渲染）· `Serilog`（结构化日志）· `Netor.EventHub 1.2.8`

---

### 6.2 Netor.Cortana.Voice — 语音管线

**职责：** KWS（关键词唤醒）→ STT（语音识别）→ TTS（文字转语音）状态机；独占麦克风仲裁。

**引擎：**
- **ASR：** sherpa-onnx Paraformer Streaming（流式非自回归，16 kHz，中英双语），通过插件（voice.stt）提供模型/二进制
- **TTS：** 纯插件化（voice.tts），异步流式接口
- **KWS：** 纯插件化（voice.kws），独占麦克风

**状态机（VoicePipelineCoordinator）：**
```
Idle → Listening(KWS) → Greeting → Recognizing(STT) → AwaitingTts → Speaking(KWS barge-in)
```
*barge-in：AI 响应过程中再次唤醒 → 取消当前任务 → 重新开始识别*

**关键类型：**

| 类型 | 说明 |
|---|---|
| `VoicePipelineCoordinator` | 状态机主体；事件订阅与麦克风切换 |
| `IVoiceCoordinator` | UI 侧取消接口 |
| `SttPluginAdapter` / `TtsPluginAdapter` / `KwsPluginAdapter` | 三类插件桥接适配器 |
| `VoiceInputChannel` | IHostedService；接收唤醒事件并路由到 STT/AI |
| `VoiceChatOutputChannel` | AI Token 流 → TTS 实时播放 |

**DI 入口：** `VoiceServiceExtensions.AddCortanaVoice()`

**外部包：** `SherpaOnnx`（ONNX Runtime ASR/KWS 推理绑定）· `Netor.EventHub 1.2.8`

---

## 七、运营平台（Platform）

### 7.1 Platform.Core
纯配置模型（`DatabaseOptions` · `FileStorageOptions` · `PackageStorageOptions` · `DocsMediaOptions`），无外部依赖，无 DI 注册。

### 7.2 Platform.Entitys — 数据层

**ORM：** EF Core 10.0.8（SQL Server 主 / SQLite 备，由 `DatabaseOptions.Provider` 配置）；AOT 编译模型。

**主表分组：**

| 分组 | 表 |
|---|---|
| 账号 | Account · AccountRole · AccountRolePair · AccountWallet |
| 资产 | Asset · AssetVersion · Category · PricingPlan |
| 创作者 | CreatorProfile · CreatorSettlement |
| 下载 | DownloadRecord |
| 订阅/订单 | Subscription · Order · Transaction |
| 评论 | AssetReview |
| 文档 | DocCategory · DocArticle |
| 管理员 | Manager · ManagerRole · ManagerMcpToken · ManagerAuditLog · IdempotencyRecord |

**DI 入口：** `DependencyInjection.AddPlatformDbContext()`（支持提供商切换）

### 7.3 Platform.Services — 业务层

约 20 个 Scoped 服务：`AuthService` · `MarketService` · `SubscriptionService` · `OrderService` · `DownloadService` · `CreatorService` · `PackageStorageService`（Local/S3）· `AssetReviewService` · `CreatorSettlementService` · `PackageRiskService`

**外部包：** `AWSSDK.S3 4.0.24`（S3 包存储）

**DI 入口：** `DependencyInjection.AddPlatformServices()`

### 7.4 Platform.Api — 公开 REST API

**框架：** ASP.NET Core 最小化 API（`Program.cs` 916 行，全部端点内联）

**认证：** Bearer Token（`ApiTokenService`，30 天有效期）

**主要端点组：**
```
/api/v1/auth             登录/令牌
/api/v1/assets           资产浏览/详情/下载
/api/v1/entitlements     订阅权益
/api/v1/client/*         客户端安装清单/同步/更新检查
/api/v1/creator/*        创作者申请/资产提交/结算
/api/v1/me/*             个人订阅/下载历史
/api/v1/orders           订单创建/支付
/api/v1/skills/*         技能 Manifest 分发
```

### 7.5 Platform.Admin — 管理后台

**框架：** ASP.NET Core MVC + 嵌入 MCP Server（`/mcp` 端点）

**认证：** Cookie（8 小时）+ 自定义 MCP Auth Scheme

**MCP 工具：** `AccountsTool` · `AssetsTool` · `DashboardTool` · `DocsTool`（供 AI Agent 直接操作后台）

**外部包：** `ModelContextProtocol.AspNetCore 1.4.0`

### 7.6 Platform.Web — 用户前端

**框架：** ASP.NET Core MVC，Cookie 认证（7 天）

**覆盖页面：** 资产浏览 · 账号登录/注销 · 订阅管理 · 创作者门户

**外部包：** `Markdig 0.37.0`（文档 Markdown 渲染）

---

## 八、测试项目

| 项目 | 覆盖目标 |
|---|---|
| `Netor.Cortana.AI.Tests` | AI 层单元测试 |
| `Netor.Cortana.Entitys.Tests` | 实体/数据层 |
| `Netor.Cortana.Plugin.Tests` | 插件核心 |
| `Netor.Cortana.Plugin.Process.Tests` | 进程插件 |
| `Netor.Cortana.Store.Tests` | 包管理 |
| `Netor.Cortana.UI.Tests` | UI 层 |
| `Netor.Cortana.Voice.Tests` | 语音管线 |
| `Netor.Cortana.Networks.Tests` | 网络层 |
| `Netor.Cortana.MeetingMode.Tests` | 会议模式 |
| `Netor.Cortana.Platform.Tests` | 平台层 |
| `PluginBusPublisher` / `PluginBusSubscriber` | PluginBus 手工集成探针 |
| `MemoryIngestConsole` | 记忆摄取控制台工具 |
| `FeedHostConsole` / `FeedProbe` / `FeedSettingsCli` | Feed 系统调试工具 |

---

## 九、关键横切关注点

| 关注点 | 实现方式 |
|---|---|
| **事件总线** | `Netor.EventHub`（内部包），全局静态事件注册表（`Events` 类） |
| **AI 工具** | `Microsoft.Agents.AI` + `Microsoft.Extensions.AI`；统一 `IChatClient` 抽象 |
| **AOT / Trim** | UI Release 启用 Native AOT；所有 JSON 通过 `JsonSerializerContext` 源生成 |
| **可观测性** | `OpenTelemetry.Api 1.15.3`（AI 层）；`Serilog`（UI 层滚动日志） |
| **数据库** | 桌面端：SQLite WAL；平台端：SQL Server / SQLite 可切换（EF Core） |
| **HITL** | `IHitlNotifier` / `IHitlContext` 标准化模式（WorkMode/MeetingMode 各自实现） |
| **提示词** | 可插拔链：嵌入资源 → 数据库 → 本地文件（`CompositePromptProvider`） |
| **插件热重载** | `PluginLoader` 通过 `FileSystemWatcher` 监听插件目录变更 |
| **令牌追踪** | `TokenTrackingChatClient` 包装器；子 Agent 令牌不覆盖主进度条 |

---

*此文件由 Kiro 自动生成，作为 master 分支 2026-07-19 的代码地图基准。后续可手动或脚本方式维护。*
