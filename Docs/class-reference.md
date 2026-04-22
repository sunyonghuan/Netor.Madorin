# 核心类文件说明

> 状态：按当前仓库实现整理。默认主线以 Netor.Cortana.AvaloniaUI、Native 插件通道和 MCP 通道为准。

## 项目一览

| 项目 | 目标框架 | 当前定位 |
|------|---------|----------|
| Netor.Cortana.AvaloniaUI | net10.0 | 当前主项目 UI，默认开发、调试和发布入口 |
| Netor.Cortana | net10.0-windows | 遗留 WinForms UI，保留兼容和历史参考 |
| Netor.Cortana.AI | net10.0 | AI 编排、模型接入、Agent 组装 |
| Netor.Cortana.Voice | net10.0 | KWS、STT、TTS 等语音能力 |
| Netor.Cortana.Networks | net10.0 | WebSocket 与网络侧能力 |
| Netor.Cortana.Plugin | net10.0 | 插件运行时、通道路由和热插拔 |
| Netor.Cortana.Entitys | net10.0 | SQLite 持久化、实体与配置存储 |
| Netor.Cortana.NativeHost | net10.0 | Native 插件宿主子进程 |
| Netor.Cortana.Plugin.Native | net10.0 | Native 插件开发包 |
| Netor.Cortana.Plugin.Native.Generator | netstandard2.0 | Native 插件源码生成器 |

## 当前主入口

### Netor.Cortana.AvaloniaUI

| 文件 | 类型 | 说明 |
|------|------|------|
| Program.cs | 入口 | Avalonia 应用启动入口 |
| App.axaml / App.axaml.cs | 应用 | Avalonia 应用定义与全局启动逻辑 |
| AppPaths.cs | 路径 | 用户数据、工作区、模型与插件路径定位 |
| WindowController.cs | 控制器 | 主窗口与子窗口的展示控制 |
| Views/ | 视图 | 当前主界面视图和页面组织 |
| Controls/ | 控件 | Avalonia 自定义控件 |
| Providers/ | 提供者 | 面向 UI 的输出通道和桥接能力 |

### Netor.Cortana.AI

| 文件/目录 | 说明 |
|-----------|------|
| AIAgentFactory 相关实现 | 组装聊天客户端、工具和上下文提供者 |
| Provider 相关实现 | 模型提供商与能力接入 |

### Netor.Cortana.Voice

| 能力 | 说明 |
|------|------|
| Wake word | 唤醒词监听 |
| STT | 实时语音识别 |
| TTS | 文本转语音与播放链路 |

### Netor.Cortana.Networks

| 能力 | 说明 |
|------|------|
| WebSocketServer | 对外暴露 WebSocket 接口，推送 AI 和语音事件 |

### Netor.Cortana.Plugin

| 组件 | 说明 |
|------|------|
| PluginLoader | 插件扫描、校验、加载和热插拔总入口 |
| PluginManifest | plugin.json 模型；当前运行时以 native / process / mcp 为主，旧 dotnet 仅识别后跳过 |
| Native/ | 当前推荐的本地插件主路线 |
| Mcp/ | 当前推荐的远程/跨语言工具接入路线 |

## 插件体系说明

### 推荐路线

- Native：当前推荐的本地插件模式，适合 AOT、高隔离和高性能场景。
- MCP：当前推荐的远程工具接入模式，适合跨语言和外部服务能力复用。

### 兼容路线

- Process：当前推荐的可执行文件插件路线，适合需要完整 JIT 生态或跨语言实现的场景。
- 旧 Dotnet 通道已经退场，不再提供独立契约层或推荐开发路径。

## 数据与配置

### Netor.Cortana.Entitys

| 组件 | 说明 |
|------|------|
| CortanaDbContext | 基于 SQLite 的轻量持久化上下文 |
| AgentEntity / AiProviderEntity / AiModelEntity | AI 配置与模型实体 |
| McpServerEntity | MCP Server 连接配置实体 |
| ChatSessionEntity / ChatMessageEntity | 对话会话和消息存储 |
| Services/ | 基于 Microsoft.Data.Sqlite 的 CRUD 服务 |

## Native 基础设施

### Netor.Cortana.NativeHost

| 文件 | 说明 |
|------|------|
| Program.cs | Native 插件宿主子进程入口，负责加载原生 DLL、处理 stdin/stdout 协议和生命周期 |

### Native 开发包

| 项目 | 说明 |
|------|------|
| Netor.Cortana.Plugin.Native | Native 插件开发时引用的主包 |
| Netor.Cortana.Plugin.Native.Generator | 自动生成工具注册、JsonContext、plugin.json 等构建产物 |

## 历史说明

- 本文档不再把 Netor.Cortana 旧 WinForms UI 视为主项目。
- 对于历史设计细节，请结合对应文档顶部的状态说明判断是否仍然适用。