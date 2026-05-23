# MCP 通用独立运行模式方案

## 背景

当前 Memory 插件已经具备宿主插件模式和 MCP 控制台模式两个入口。原先的双入口方案有一个隐含前提：MCP 模式仍然复用插件模式的初始化参数、数据目录、工作区信息以及宿主 conversation-feed 摄取链路。

这个前提不适合通用 MCP 场景。

宿主插件模式只服务 Madorin 当前宿主环境，宿主可以注入 `PluginSettings`，也可以提供内部 WebSocket conversation-feed。因此插件模式继续使用宿主协议是合理的。

MCP 模式的定位不同：它应该作为可移植的独立记忆服务，被 Claude Desktop、Cline、Continue、Cursor 或其他支持 MCP 的软件直接启动。通用 MCP 客户端通常只提供 `command`、`args`、`env`，不会传 Madorin 插件初始化参数，也不会提供 Madorin 的 conversation-feed。因此 MCP 模式必须单独实例化，不能以宿主插件初始化作为前置条件。

## 核心结论

MCP 模式应从“插件模式的另一个入口”调整为“独立运行时模式”。

两种模式共享记忆核心能力，但实例化边界、配置来源和上下文摄取方式必须分离。

| 模式 | 定位 | 初始化来源 | 上下文来源 | 是否依赖 Madorin 宿主 |
| --- | --- | --- | --- | --- |
| 宿主插件模式 | Madorin 内部插件 | 宿主传入 `PluginSettings` | conversation-feed WebSocket | 是 |
| MCP 通用模式 | 可移植独立记忆服务 | 自己构造运行时配置 | MCP 工具显式写入 / 调用方传入 | 否 |

## 设计目标

1. MCP 模式无初始化参数也能启动并正常暴露工具。
2. MCP 模式不注册、不连接、不等待 Madorin conversation-feed。
3. MCP 模式不依赖宿主传入 `PluginSettings`。
4. MCP 模式使用程序启动目录下的默认数据目录，便于将发布目录整体压缩、复制和迁移。
5. MCP 模式通过 MCP 工具显式接收上下文、写入记忆、召回记忆。
6. 插件模式保持现有行为不变，继续使用宿主配置和 WebSocket 自动摄取。
7. 业务核心继续复用 `Services`、`Processing`、`Storage`，避免复制记忆规则。

## 模式边界

### 宿主插件模式

宿主插件模式保留以下能力：

- 由 Madorin 宿主加载 DLL。
- 由宿主传入 `PluginSettings`。
- 使用 `MemoryIngestService` 连接 conversation-feed。
- 从宿主事件中解析 `agentId`、`workspaceId`、`sessionId`、`turnId`、`messageId` 等上下文。
- 适合当前 Madorin 数组环境，不作为通用移植入口。

### MCP 通用模式

MCP 通用模式只承担独立服务职责：

- 由 MCP 客户端启动 `memory.exe`。
- 不要求 `--config`。
- 不要求 `MADORIN_PLUGIN_CONFIG`。
- 不注册 `MemoryIngestService`。
- 不读取 conversation-feed 端口。
- 不把缺失宿主初始化视为错误。
- 默认创建并使用程序启动目录下的数据目录。
- 通过 MCP 工具显式接收对话上下文和记忆写入请求。

## 配置模型

MCP 模式需要自己的运行时配置类型，建议新增 `MemoryMcpRuntimeOptions`，不要继续把 `PluginSettings` 作为 MCP 入口的核心配置对象。

建议字段：

| 字段 | 说明 | 默认值 |
| --- | --- | --- |
| `DataDirectory` | 数据目录 | `{AppContext.BaseDirectory}/data` |
| `DatabaseFileName` | 数据库文件名 | `memory.db` |
| `DefaultAgentId` | 默认调用方标识 | `mcp-default` |
| `DefaultWorkspaceId` | 默认工作区标识 | `default` |
| `DefaultSource` | 默认来源 | `mcp` |
| `EnableAutoProcessing` | 是否启用后台处理 | `true` |

配置优先级：

1. 命令行参数，例如 `--data-dir <path>`、`--agent-id <id>`、`--workspace-id <id>`。
2. 环境变量，例如 `MADORIN_MEMORY_DATA_DIR`、`MADORIN_MEMORY_AGENT_ID`、`MADORIN_MEMORY_WORKSPACE_ID`。
3. 程序目录配置文件，例如 `{AppContext.BaseDirectory}/config.json`。
4. 内置默认值。

重要约束：所有配置项都必须有默认值。任何 MCP 客户端只配置下面内容也应可运行：

```json
{
  "mcpServers": {
    "memory": {
      "command": "C:\\path\\to\\memory.exe"
    }
  }
}
```

## 数据目录策略

MCP 通用模式优先按便携包设计。默认数据应跟随程序目录，而不是写入用户级目录。这样发布产物可以作为一个完整目录被压缩、复制、备份或迁移到其他机器。

MCP 模式应默认写入程序启动目录下的 `data` 子目录：

| 平台 | 默认目录 |
| --- | --- |
| Windows | `{memory.exe 所在目录}\\data` |
| Linux | `{memory.dll 所在目录}/data` |
| macOS | `{memory.dll 所在目录}/data` |

数据库默认路径：

```plaintext
{AppContext.BaseDirectory}/data/memory.db
```

配置文件默认路径：

```plaintext
{AppContext.BaseDirectory}/config.json
```

如果运行目录不可写，允许用户通过 `--data-dir` 或 `MADORIN_MEMORY_DATA_DIR` 改到其他位置，但这属于显式部署选择，不作为默认行为。

插件模式仍沿用宿主传入的 `PluginSettings.DataDirectory`。

## 跨平台打包策略

MCP 模式需要区分 Windows 便携 exe 包和 Linux/macOS 便携 DLL 包。

Windows 推荐发布自包含 exe：

```powershell
dotnet publish Src/Madorin.Plugins.Memory/Madorin.Plugins.Memory.csproj -c Release -p:OutputType=Exe -p:PublishAot=true -r win-x64 --self-contained true -o Releases/memory-mcp-win-x64
```

Linux 推荐发布 framework-dependent DLL，减少包体积并避免不同发行版 native/AOT 兼容差异：

```powershell
dotnet publish Src/Madorin.Plugins.Memory/Madorin.Plugins.Memory.csproj -c Release -p:OutputType=Exe -r linux-x64 --self-contained false -o Releases/memory-mcp-linux-x64
```

Linux MCP 客户端配置示例：

```json
{
  "mcpServers": {
    "memory": {
      "command": "dotnet",
      "args": ["/opt/memory-mcp/memory.dll"]
    }
  }
}
```

发布包建议结构：

```plaintext
memory-mcp-<rid>/
├─ memory.exe 或 memory.dll
├─ config.json              # 可选；不存在时使用默认值
├─ data/                    # 默认数据库目录；可随包迁移
│  └─ memory.db
└─ README 或客户端配置示例
```

压缩包迁移时，整个目录一起移动即可保留配置和记忆数据。

## 上下文摄取策略

MCP 通用模式没有宿主 WebSocket，因此不能等待外部自动推送对话。上下文摄取必须通过 MCP 工具显式完成。

建议新增工具：

| 工具 | 作用 |
| --- | --- |
| `memory_record_turn` | 记录单轮用户/助手消息，进入观察记录和后续处理链路 |
| `memory_record_conversation` | 批量记录一段对话，适合客户端一次性同步最近上下文 |
| `memory_set_scope` | 设置当前 MCP 进程默认 `agentId` / `workspaceId` / `source` |
| `memory_get_scope` | 查看当前默认作用域 |

现有工具继续保留：

| 工具 | MCP 通用模式行为 |
| --- | --- |
| `memory_recall` | 根据查询召回长期记忆 |
| `memory_supply_context` | 生成可注入 prompt 的结构化记忆包 |
| `memory_add_note` | 用户明确授权时写入人工记忆 |
| `memory_list_recent` | 查看最近记忆 |
| `memory_get_status` | 查看状态 |

`memory_record_turn` 建议参数：

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `role` | 是 | `user`、`assistant`、`system`、`tool` |
| `content` | 是 | 消息内容 |
| `agentId` | 否 | 不传则使用默认 agent |
| `workspaceId` | 否 | 不传则使用默认 workspace |
| `sessionId` | 否 | 不传则自动生成或使用默认会话 |
| `turnId` | 否 | 不传则自动生成 |
| `messageId` | 否 | 不传则自动生成 |
| `source` | 否 | 默认 `mcp` |
| `createdTimestamp` | 否 | 不传则使用当前时间 |

这样 MCP 客户端无需理解 Madorin 内部事件格式，只需要把自己能拿到的对话文本交给记忆服务。

## 身份与工作区策略

通用 MCP 客户端不一定会传工作区，也不一定知道 agentId。MCP 模式必须具备默认作用域。

作用域解析优先级：

1. 工具参数显式传入的 `agentId` / `workspaceId`。
2. `memory_set_scope` 设置的进程级默认值。
3. 环境变量或配置文件中的默认值。
4. 内置默认值：`mcp-default` / `default`。

注意：不要把“缺失 workspaceId”视为错误。通用模式下默认 workspace 是正常状态。

## 服务注册策略

建议把当前 `Program.cs` 的 MCP DI 组装改为独立扩展方法，例如：

```plaintext
AddMemoryMcpRuntime(options)
```

MCP 注册内容：

- `MemoryMcpRuntimeOptions`
- `IMemoryRuntimeContext`
- `IMemoryDatabase` 的 MCP 版本或数据目录适配器
- `IMemoryStore`
- 召回、供应、状态、人工写入、最近列表服务
- 处理服务与抽象生成服务
- `MemoryProcessingHostedService`
- MCP 工具适配器

MCP 不注册：

- `MemoryIngestService`
- conversation-feed WebSocket 客户端
- 任何要求宿主初始化参数才能工作的服务

插件注册内容保持现状，由 `Startup.cs` 和宿主生成入口负责。

## Storage 适配策略

当前 `SqliteMemoryDatabase` 直接依赖 `PluginSettings`。为了让 MCP 模式不再依赖插件初始化参数，建议引入更小的数据库路径配置抽象：

```plaintext
IMemoryDatabaseOptions
    DataDirectory
    DatabaseFileName
```

插件模式：

```plaintext
PluginSettings -> PluginMemoryDatabaseOptions -> SqliteMemoryDatabase
```

MCP 模式：

```plaintext
MemoryMcpRuntimeOptions -> McpMemoryDatabaseOptions -> SqliteMemoryDatabase
```

这样 `SqliteMemoryDatabase` 不再知道 `PluginSettings`，也不会迫使 MCP 模式构造假的插件配置。

## Processing 适配策略

现有处理链路可以继续复用，但需要给 MCP 工具提供一个“从 MCP 消息创建 observation”的轻量服务。

建议新增：

```plaintext
IMemoryObservationWriter
```

职责：

- 接收标准化后的 message / conversation 输入。
- 补齐 agentId、workspaceId、sessionId、turnId、messageId、timestamp。
- 写入 `MemoryObservation`。
- 交给现有后台处理服务继续抽取 fragment 和 abstraction。

插件模式的 `MemoryIngestService` 也可以后续改为调用同一个 writer，从而减少 WebSocket 解析逻辑和 MCP 工具写入逻辑的重复。

## 向后兼容策略

1. 插件模式不变：仍然由宿主注入配置，仍然自动订阅 conversation-feed。
2. MCP 模式不再承诺读取 `MADORIN_PLUGIN_CONFIG`；如需兼容旧调试方式，可临时保留但不作为主路径。
3. MCP 工具名称保持稳定，新增工具不破坏现有工具。
4. 数据库表结构不因本方案变化而立即迁移。
5. AOT 约束继续保留：显式 DI、显式工具注册、JSON source generation。

## 风险与应对

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| MCP 客户端不主动调用摄取工具 | 记忆只能召回旧内容，不能学习新对话 | 提供客户端配置提示词模板；支持 `memory_record_conversation` 批量补录 |
| 不同 MCP 客户端共用默认 workspace | 记忆混杂 | 支持 `memory_set_scope`；文档建议每个客户端设置独立 workspace |
| 程序目录不可写 | MCP 无法创建 `data/memory.db` | 允许显式设置 `--data-dir` 或 `MADORIN_MEMORY_DATA_DIR` |
| 便携包迁移导致多个客户端共用同一数据库 | 记忆可能被不同客户端共同读写 | 通过 `memory_set_scope` 或配置文件区分 workspace |
| 直接移除 `PluginSettings` 依赖范围较大 | 实施风险升高 | 先加数据库 options 抽象，再逐步迁移 |
| MCP 工具过多导致模型选择不稳定 | 调用质量下降 | 工具描述明确区分“召回”“记录”“人工记忆” |

## 建议实施阶段

### 阶段 1：配置与实例化解耦

目标：MCP 无参数启动稳定。

任务：

1. 新增 `MemoryMcpRuntimeOptions`。
2. 新增 MCP 配置解析器，支持 CLI、ENV、程序目录配置文件、默认值。
3. 新增数据库路径 options 抽象，移除 `SqliteMemoryDatabase` 对 `PluginSettings` 的直接依赖。
4. 调整 `Program.cs`，使用 MCP 专用注册路径。
5. 验证 `memory.exe` 无参数启动、Windows AOT 发布、Linux DLL 发布、状态工具可调用。

### 阶段 2：MCP 显式上下文摄取

目标：通用客户端可以把对话写入记忆系统。

任务：

1. 新增 `IMemoryObservationWriter`。
2. 新增 `memory_record_turn`。
3. 新增 `memory_record_conversation`。
4. 补充 MCP 工具测试，验证参数默认值和 handler 转发。
5. 验证写入 observation 后后台处理可以生成 fragment。

### 阶段 3：作用域管理

目标：在没有宿主 workspace 的情况下仍可隔离记忆。

任务：

1. 新增 `IMemoryRuntimeContext`。
2. 新增 `memory_set_scope`、`memory_get_scope`。
3. 让 recall、supply、add_note、list_recent、record_turn 都使用统一作用域解析。
4. 补充多 workspace 测试。

### 阶段 4：文档与分发

目标：让第三方 MCP 客户端能直接接入。

任务：

1. 编写 Windows exe 与 Linux DLL 两套 MCP 客户端配置示例。
2. 编写推荐系统提示词片段，指导模型何时调用 `memory_record_turn` 和 `memory_recall`。
3. 编写发布脚本，输出 Windows exe 便携包和 Linux DLL 便携包。
4. 补充 AOT 验证脚本。

## 验收标准

1. `memory.exe` 无任何参数启动成功。
2. 不设置 `MADORIN_PLUGIN_CONFIG` 也能在程序目录 `data/memory.db` 创建数据库并调用 `memory_get_status`。
3. MCP 模式不会尝试连接 conversation-feed。
4. `memory_record_turn` 能写入 observation。
5. 后台处理能从 MCP 写入的 observation 生成 fragment。
6. `memory_recall` 能召回 MCP 写入产生的记忆。
7. 插件模式原有测试继续通过。
8. AOT 发布没有 IL2026 / IL3050 警告。

## 当前决策

从本方案开始，MCP 模式不再被视为“插件模式带一个控制台入口”，而是 Memory 插件的通用独立运行形态。

后续代码修改应围绕这个边界展开：插件模式保留宿主集成优势，MCP 模式追求零初始化、可移植、显式上下文摄取和跨软件接入。
