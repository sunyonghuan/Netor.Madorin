# MCP 双入口与工具抽离方案

## 目标

当前 Memory 插件优先保持插件模式可用，同时为 MCP 模式预留独立宿主入口。整体采用单项目、双入口、双适配、单核心实现、分发布命令的结构。

## 架构原则

1. 单项目承载插件模式和 MCP 模式。
2. 插件模式继续由宿主加载 DLL，不改变现有插件入口语义。
3. MCP 模式后续通过 `Program.cs` 作为控制台入口启动。
4. 插件工具和 MCP 工具只做协议适配，不承载真实业务规则。
5. 工具真实实现抽离到 `ToolHandlers` 层。
6. `ToolHandlers` 只调用服务层，不直接访问 SQLite、数据库路径或内部表结构。
7. P1 写入工具继续强制用户确认、写入原因、候选状态和 mutation 审计。
8. 首版 MCP 只开放 P0/P1 工具，管理类工具延后。

## 推荐层次

```plaintext
插件 Tool Attribute 适配器
		│
		├── ToolHandlers
		│       │
MCP Tool 适配器 ┘
				│
			Services
				│
			IMemoryStore
				│
			SQLite 内部实现
```

## 目录规划

```plaintext
Src/Cortana.Plugins.Memory/
├─ Startup.cs                              # 插件入口
├─ Program.cs                              # 后续 MCP 入口
├─ ToolHandlers/                           # 工具核心实现层
├─ Tools/                                  # 插件适配层
├─ Mcp/                                    # 后续 MCP 适配层
├─ Services/
├─ Storage/
├─ Models/
└─ Docs/
```

## 工具抽离策略

`MemoryReadTools` 和 `MemoryWriteTools` 后续只保留：

- 插件特性声明。
- 插件参数描述。
- 调用对应 handler。
- 返回统一工具 envelope 字符串。

`ToolHandlers` 负责：

- 工具级参数校验。
- 默认 agentId、workspaceId 归一化。
- count/limit 上限控制。
- traceId、triggerSource 生成。
- 服务调用。
- 结果序列化为当前工具 envelope。

后续 MCP 适配器复用同一批 handler，避免复制业务规则。

## 发布策略

项目文件默认保持插件友好，不在日常开发中直接改为控制台项目。发布时通过 MSBuild 属性覆盖输出类型。

插件发布：

```powershell
dotnet publish Src/Cortana.Plugins.Memory/Cortana.Plugins.Memory.csproj -c Release -p:OutputType=Library
```

MCP 发布：

```powershell
dotnet publish Src/Cortana.Plugins.Memory/Cortana.Plugins.Memory.csproj -c Release -p:OutputType=Exe -r win-x64 --self-contained true
```

Linux MCP 发布：

```powershell
dotnet publish Src/Cortana.Plugins.Memory/Cortana.Plugins.Memory.csproj -c Release -p:OutputType=Exe -r linux-x64 --self-contained true
```

AOT 发布后续作为可选目标：

```powershell
dotnet publish Src/Cortana.Plugins.Memory/Cortana.Plugins.Memory.csproj -c Release -p:OutputType=Exe -p:PublishAot=true -r linux-x64 --self-contained true
```

## AOT 预留要求

1. 尽量使用显式 DI 注册。
2. 避免运行时程序集扫描注册工具。
3. JSON 序列化继续优先使用 source generation。
4. MCP 工具适配器后续显式声明和注册。
5. 如果 MCP SDK 或插件依赖出现裁剪/AOT 冲突，再考虑条件编译或拆分项目。

## 阶段计划

1. 先落盘本方案。
2. 抽离现有插件工具真实实现到 `ToolHandlers`。
3. 保持插件工具行为和测试结果不变。
4. 后续再加入 MCP `Program.cs` 和 MCP 适配器。
5. 最后补充分发布脚本和 AOT 验证。
