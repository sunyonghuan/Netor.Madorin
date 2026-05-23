# Netor.Madorin

<div align="center">

<img src="Res/images/044417fdc1cb787fed6996ea10ab0082.png" width="380" alt="Netor.Madorin" />

**不是又一个聊天框，而是一只真正能接管工具链的本地 AI 大龙虾。**

本地优先 · 模型自由 · 插件扩展 · 多智能体协作 · Native AOT 极速启动

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia 12](https://img.shields.io/badge/Avalonia-12-8B44AC)](https://avaloniaui.net/)
[![Native AOT](https://img.shields.io/badge/Native-AOT-00C853)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![License](https://img.shields.io/badge/License-MIT-blue)](#许可证)

</div>

---

## 这东西到底狠在哪？

现在的 AI 工具太多了：网页套壳、聊天侧边栏、固定模型入口、收费套餐、插件半残废。看起来热闹，真干活的时候还是要你自己复制、粘贴、切窗口、查日志、改文件、接接口。

**Madorin 的目标不是陪你聊天，而是替你把活干完。**

它是一个本地 AI 宿主，也是一套可扩展的工具调度系统。它能把模型、插件、MCP、WebSocket、文件系统、语音、编辑器工作流和多智能体编排揉到一起，让 AI 不只是“回答问题”，而是能真正调工具、读上下文、改项目、接外部系统、沉淀工作流。

一句话：

> **别人做 AI 聊天窗口，Madorin 做 AI 工作底座。**

---

## 一眼看懂：Madorin 能给你什么？

| 场景 | Madorin 的玩法 |
| --- | --- |
| 想换模型 | 不被平台绑架，OpenAI-compatible、国产模型、企业网关、私有 API 都能接。 |
| 想接 Copilot / 编辑器 | 内置 Ollama 本地协议代理，把远程模型伪装成本地模型送进现有工具链。 |
| 想让 AI 真干活 | 插件、MCP、文件工具、脚本工具、窗口工具、Office 工具都可以成为 AI 的手脚。 |
| 想多角色协作 | 输入 `@` 调用子智能体，每个智能体有独立模型、提示词和工具集。 |
| 想保留本地数据 | 会话、配置、工作区、插件、资源都在本机和工作区目录内。 |
| 想排查 AI 工具链 | AI trace 可记录请求、响应、流式片段和工具协议诊断。 |
| 想扩展能力 | Native / Process / MCP 三条扩展路线，能本地跑，也能接远程服务。 |

这不是“多一个按钮”的小工具，而是把 AI 从聊天框里放出来，让它真正摸到你的工程、文件、插件、模型和外部系统。

---

## 核心卖点

### 1. 模型入口自由：把你自己的模型送进工具链

很多 AI 软件最烦的一点就是：**模型入口被平台锁死。**

你想用国产模型？等适配。
你想用企业内部模型？自己写插件。
你想用私有部署？体验砍半。
你想把模型接进编辑器？又是一轮配置地狱。

Madorin 直接走另一条路：

```text
http://localhost:11434
```

它内置 **Ollama 本地协议代理**，可以把 Madorin 里配置好的远程模型、企业模型、国产模型、私有 API 模型，对外伪装成本机 Ollama 模型。

外部工具以为自己在调用本地 Ollama，实际上请求由 Madorin 转发到你真正想用的模型。

支持端点包括：

- `GET /api/version`
- `GET /api/tags`
- `POST /api/chat`
- `POST /api/generate`
- `POST /api/show`
- `GET /v1/models`
- `POST /v1/chat/completions`

这意味着什么？

> **不重写 VS Code，不魔改 Visual Studio，不再造半成品 IDE。Madorin 只做最关键的一件事：把模型选择权还给你。**

编辑器继续做编辑器擅长的事：代码补全、上下文理解、文件编辑、任务执行。Madorin 在背后负责把你想用的模型送进去。

---

### 2. 多智能体协作：不是一个 AI 在硬撑，而是一队 AI 在干活

Madorin 支持 `@智能体` 调用。

每个智能体都可以有自己的：

- 模型
- 提示词
- 插件工具
- MCP 工具
- 工作职责

你可以让一个智能体负责搜索，一个负责运维，一个负责写文档，一个负责代码分析。主对话负责调度，它们各自带工具干自己的活。

比如：

- `@服务器管理`：连接服务器、检查负载、查日志、生成巡检报告。
- `@谷歌搜索`：联网搜索、筛资料、整理引用。
- `@文档助手`：把零散结果整理成规范文档。
- `@脚本执行`：运行 C# 脚本或本地自动化任务。

这就不是“问一个模型一句话”了，而是把 AI 拆成岗位：有人查资料，有人动手，有人总结，有人复盘。

> **一个聊天机器人很普通，一群带工具的智能体才像真正的数字员工。**

---

### 3. 插件系统：让 AI 长出手脚

只会聊天的 AI，最多是顾问。
能调工具的 AI，才像员工。

Madorin 当前推荐三条扩展路线：

| 通道 | 适合什么 | 亮点 |
| --- | --- | --- |
| Native | C/C++/Rust/C# AOT、高性能本地能力 | NativeHost 子进程承载，崩溃隔离。 |
| Process | 跨语言可执行程序、需要完整运行时生态的工具 | stdio NDJSON 协议，适合把外部程序变成 AI 工具。 |
| MCP | 外部工具服务、远程系统、标准化工具生态 | 支持 stdio / SSE / streamable-http。 |

本地工作区插件默认部署到：

```text
.madorin/plugins/
```

仓库里已经维护了一批真实插件工程：

- `Madorin.Plugins.Bt`：宝塔面板运维工具。
- `Madorin.Plugins.GoogleSearch`：谷歌搜索。
- `Madorin.Plugins.Memory`：记忆引擎。
- `Madorin.Plugins.Office`：Word / Excel / PowerPoint 文档工具。
- `Madorin.Plugins.Reminder`：定时提醒。
- `Madorin.Plugins.ScriptRunner`：C# 脚本运行器。
- `Madorin.Plugins.WsBridge`：WebSocket 中转。
- `Madorin.Plugins.ApplicationLauncher` / `WindowManagement`：本地应用与窗口管理。

插件不是摆设。它们就是 AI 的手、脚、眼睛和工具箱。

---

### 4. 聊天历史不是文本坟场，而是可恢复的工具链上下文

很多 AI 产品的历史记录只是“看起来有上下文”。一旦涉及工具调用、函数结果、多轮 tool_calls，就容易乱、丢、断、报 400。

Madorin 在 v1.2.6 之后把聊天消息内容结构化持久化：

- 普通文本
- function call
- function result
- 工具结果
- 生成资源

都可以写入 SQLite，并通过结构化 `ContentsJson` 恢复。

v1.3.3 又进一步修复了 OpenAI-compatible / DeepSeek 多工具结果场景下的工具历史重排问题，避免同一条 tool message 被重复追加导致协议错误。

同时增加 AI trace：

```text
.madorin/logs/ai-traces
```

可诊断：

- orphan tool message
- missing tool response
- tool message 不相邻
- assistant tool_calls 与 tool result 的 callId 映射
- 请求、响应、流式片段、异常堆栈

这意味着 Madorin 不是“跑通 Demo 就算完”，而是在认真处理真实 AI 工具链里最麻烦的协议细节。

---

### 5. WebSocket 接入：外部程序也能接进来

Madorin 内置轻量 WebSocket 服务，方便外部应用连接主对话或订阅内部对话事件。

端点常量定义在 `Src/Netor.Madorin.Entitys/MadorinWsEndpoints.cs`。

| 端点 | 作用 |
| --- | --- |
| `/ws/` | 对话 WebSocket，支持发送用户消息、停止生成、接收 token/done/error。 |
| `/internal/conversation-feed/` | 内部对话事件 feed，供插件或外部进程订阅对话事件。 |

默认端口：

```text
52841
```

这让 Madorin 不只是一个 GUI 应用，也可以成为其他程序背后的 AI 对话服务。

---

### 6. 语音能力：让本地助手真的开口和听话

语音模块位于 `Src/Netor.Madorin.Voice`，基于 Sherpa-ONNX：

- KWS：关键词唤醒
- STT：语音识别
- TTS：语音合成

当前模型目录：

```text
Src/sherpa_models/
├── KWS/
├── STT/
└── TTS/
```

模型文件不提交到 Git，发布前需要确保本地目录完整。

语音能力后续也在向插件化方向演进：KWS / STT / TTS 都可以独立为 Process 插件，模型跟随插件包发布，主程序只保留语音编排。

这条路一旦打通，Madorin 的主程序会更轻，语音能力也能像普通插件一样安装、替换、升级。

---

## 最近版本到底进化了什么？

| 版本 | 进化点 |
| --- | --- |
| v1.3.3 | 工具历史重排修复、AI 全量调试日志、工具协议诊断。 |
| v1.3.1 | Ollama 本地协议代理，把远程模型伪装成本地模型。 |
| v1.2.9 | reasoning / function call 能力位，修复 OpenAI 兼容工具链断裂。 |
| v1.2.7 | 文件工具返回结果统一结构化，AI 更容易稳定解析。 |
| v1.2.6 | 用途级模型路由、结构化聊天历史、工具上下文恢复。 |
| v1.2.0 | `@智能体` 多智能体协作。 |

发布说明位于 `Docs/release-notes/`。

---

## 技术架构

| 模块 | 说明 |
| --- | --- |
| `Src/Netor.Madorin.UI` | 当前主程序，Avalonia UI、系统设置、托盘、工作区、历史面板、代理窗口。 |
| `Src/Netor.Madorin.AI` | AI 编排、模型接入、Agent、聊天历史组装、工具链协议处理、AI trace。 |
| `Src/Netor.Madorin.Entitys` | 实体、SQLite 数据库、系统设置、事件参数、共享常量。 |
| `Src/Netor.Madorin.Plugin` | 插件加载、本地插件通道、MCP 接入、内置文件工具等。 |
| `Src/Netor.Madorin.Networks` | WebSocket 服务、Ollama/OpenAI 兼容代理网络层。 |
| `Src/Netor.Madorin.Voice` | KWS/STT/TTS 语音能力。 |
| `Src/Plugins` | Native/Process 插件 SDK、NativeHost、源码生成器、调试器。 |
| `Plugins/Src` | 随仓库维护的实际插件工程。 |

| 类型 | 技术 |
| --- | --- |
| 运行时 | .NET 10 |
| UI | Avalonia 12 |
| 发布 | Native AOT、Self-contained、win-x64 |
| AI | Microsoft.Extensions.AI、Microsoft.Agents.AI、OllamaSharp |
| MCP | ModelContextProtocol 1.2.0 |
| 数据 | SQLite（纯 ADO.NET，AOT 友好） |
| 事件 | Netor.EventHub |
| 日志 | Microsoft.Extensions.Logging + Serilog File |
| 语音 | Sherpa-ONNX |

---

## 快速开始

### 环境要求

- Windows 10/11 x64
- .NET 10 SDK
- PowerShell 7
- 如需创建 GitHub Release：GitHub CLI (`gh`) 并完成登录

### 构建

```powershell
dotnet build .\Netor.Madorin.slnx
```

### 运行主程序

```powershell
dotnet run --project .\Src\Netor.Madorin.UI\Netor.Madorin.UI.csproj
```

### 发布 UI + NativeHost

```powershell
.\Build\ui.publish.ps1
```

输出目录：

```text
Realases/Madorin
```

该脚本会发布：

- `Src/Netor.Madorin.UI/Netor.Madorin.UI.csproj`
- `Src/Plugins/Netor.Madorin.NativeHost/Netor.Madorin.NativeHost.csproj`

### 打包 zip 和 SHA256

```powershell
.\Build\ui.package.ps1
```

默认生成：

```text
Realases/Netor.Madorin-v{Version}-win-x64.zip
Realases/Netor.Madorin-v{Version}-win-x64.sha256
```

### 创建 GitHub Release

```powershell
.\Build\github.release.ps1 -Tag v1.3.3
```

该脚本只处理 tag、release notes 和资产上传，不执行 publish，也不自动修改版本号。

### 插件包发布

```powershell
.\Build\plugin.publish.ps1
```

---

## 当前目录结构

```text
Netor.Madorin/
├── Netor.Madorin.slnx
├── README.md
├── Build/
│   ├── ui.publish.ps1
│   ├── ui.package.ps1
│   ├── github.release.ps1
│   └── plugin.publish.ps1
├── Docs/
│   ├── release-notes/
│   └── 系统流程与规划/
├── Plugins/
│   ├── Madorin.Plugins.slnx
│   ├── docs/
│   └── Src/
│       ├── Madorin.Plugins.Bt/
│       ├── Madorin.Plugins.GoogleSearch/
│       ├── Madorin.Plugins.Memory/
│       ├── Madorin.Plugins.Office/
│       ├── Madorin.Plugins.Reminder/
│       ├── Madorin.Plugins.ScriptRunner/
│       └── Madorin.Plugins.WsBridge/
├── Realases/
├── Res/
└── Src/
    ├── Netor.Madorin.AI/
    ├── Netor.Madorin.Entitys/
    ├── Netor.Madorin.Networks/
    ├── Netor.Madorin.Plugin/
    ├── Netor.Madorin.UI/
    ├── Netor.Madorin.Voice/
    ├── Plugins/
    └── sherpa_models/
```

---

## 常用文档

| 文档 | 说明 |
| --- | --- |
| `Docs/release-notes/v1.3.6/RELEASE.md` | 插件授权、宿主模型能力、长期记忆默认注入与 system.notice。 |
| `Docs/release-notes/v1.3.3/RELEASE.md` | AI 工具链协议修复与 trace。 |
| `Docs/release-notes/v1.3.1/RELEASE.md` | Ollama 本地协议代理。 |
| `Docs/release-notes/v1.2.9/RELEASE.md` | reasoning / function call 能力位与工具链修复。 |
| `Docs/系统流程与规划/UI-编译打包发布流程.md` | UI 编译、打包、发布流程。 |
| `Docs/系统流程与规划/websocket-api.md` | WebSocket 接入协议。 |
| `Plugins/docs/plugin-native.md` | Native 插件开发。 |
| `Plugins/docs/plugin-mcp.md` | MCP 插件/服务接入。 |
| `Plugins/docs/native-plugin-dev-guide.md` | Native 插件开发细节。 |

---

## 注意事项

- 当前主线项目是 `Src/Netor.Madorin.UI`。
- 当前代码版本以 `Src/Netor.Madorin.UI/Netor.Madorin.UI.csproj` 中的 `<Version>` 为准；当前为 **1.3.8**。
- 发布输出目录拼写沿用历史路径 `Realases/`，不要误改为 `Releases/`。
- `Src/sherpa_models/` 不提交到 Git，但当前语音发布仍需要 `KWS`、`STT`、`TTS` 子目录完整。
- 旧发布脚本仍保留在 `Build/` 中用于历史链路，不推荐新流程使用。

---

## 许可证

本项目采用 MIT 许可证开源，可自由使用、修改和分发。

---

<div align="center">

**Netor.Madorin** — 别再只让 AI 说话了，让它接管工具，开始干活。

</div>
