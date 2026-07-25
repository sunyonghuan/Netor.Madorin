# Madorin.AI.Runtime 项目骨架

本目录是独立 AI CLI / Runtime 的可编译项目骨架。它不引用现有
`Netor.Cortana.*` 内部程序集，可在后续迁移为独立仓库。

## 设计依据

实现必须按以下顺序解释文档；存在冲突时，序号靠后的约束优先：

1. [总体方案与总需求](../../Docs/未来策划/独立CLI/README.md)
2. [实施方案 V1](../../Docs/未来策划/独立CLI/方案设计/01-实施方案-V1.md)
3. [架构修订 V1](../../Docs/未来策划/独立CLI/方案设计/02-架构修订-V1.md)
4. [实现框架参考](../../Docs/未来策划/独立CLI/方案设计/03-实现框架参考.md)
5. [CLI 命令规范](../../Docs/未来策划/独立CLI/命令规范/04-CLI命令规范.md)
6. [V1 执行步骤](../../Docs/未来策划/独立CLI/执行步骤/README.md)

特别注意：架构修订中的 `AgentInvocation`、全局单调事件序号、拆分后的
Run 请求、工作区单绑定、恢复握手和工具幂等要求优先于实施方案原始草案。

## 当前范围

当前已建立：

- .NET 10、中央包管理、严格编译和 Native AOT 配置。
- 文档列出的 22 个源码项目、1 个参考宿主和 6 个测试项目。
- Entities、Contracts、Core、Persistence、Tools、Orchestration、Transport、
  Providers、Services、Server、CLI 和 Client 的依赖边界。
- Run 状态机、协议版本、AOT JSON 上下文及首批公共抽象。
- `serve`、`run`、`version`、`doctor`、`providers`、`session`、`db`、
  `storage`、`ctl` 命令树和全局选项。
- OpenAI、Anthropic、OpenAI Compatible、MAF、MEAI、SQLite 的版本基线。

阶段 2 已实现 Runtime 服务、Windows Named Pipe/Unix Socket 传输、双向握手认证、
控制/事件双通道、Blob staging、心跳重连、Host lease 和多实例进程边界。
Provider Adapter、业务模式和后续阶段能力仍按执行步骤文档推进；未进入范围的命令会明确报告
`NotImplemented`，不伪造成功。

## 目录

```text
Madorin.AI.Runtime.slnx
src/
  Madorin.AI.Runtime.Entities
  Madorin.AI.Runtime.Contracts
  Madorin.AI.Runtime.Core
  Madorin.AI.Runtime.Persistence.*
  Madorin.AI.Runtime.Tools.*
  Madorin.AI.Runtime.Orchestration.Abstractions
  Madorin.AI.Runtime.Modes.*
  Madorin.AI.Runtime.Services
  Madorin.AI.Runtime.Transport.*
  Madorin.AI.Runtime.Providers.*
  Madorin.AI.Runtime.Server
  Madorin.AI.Runtime.Cli
  Madorin.AI.Runtime.Client
samples/
  Madorin.AI.Runtime.SampleHost
tests/
  Madorin.AI.Runtime.*.Tests
publish/
  rd.xml
```

## 依赖规则

```text
Entities <- Core <- Services <- Server <- Cli
Contracts <- Transport / Client / Server
Persistence.Abstractions <- Services
Tools.Abstractions <- Services
Orchestration.Abstractions <- Services
Providers.Abstractions <- Provider implementations
```

`Server` 是具体实现的组合根。`Contracts` 和 `Core` 不得引用 MAF、MEAI、
Provider SDK、数据库实现、UI 框架或现有 Madorin 内部程序集。

## 验证

```powershell
dotnet build .\Madorin.AI.Runtime.slnx
dotnet test .\Madorin.AI.Runtime.slnx
dotnet run --project .\src\Madorin.AI.Runtime.Cli -- --help
dotnet publish .\src\Madorin.AI.Runtime.Cli -c Release -r win-x64 --self-contained
```

Native AOT 发布时必须保持零 IL 裁剪/AOT 警告。协议 JSON 必须继续使用
`RuntimeJsonContext` 的源生成元数据，不得回退到反射序列化。
