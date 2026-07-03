---
name: architecture
description: 'Madorin 插件工程规范子技能。位置：subskills/architecture。用于插件架构设计、职责分离、依赖注入、AOT 安全编码、插件元数据协议、宿主能力申请、设置 schema、配置与日志、质量门禁。触发关键词：插件架构、职责分离、依赖注入、工程规范、AOT 安全、插件设计。'
version: 2
user-invocable: true
---

# Plugin Development Architecture

## Workflow

1. 定义插件边界：入口只做组合。
2. 划分层：Tools / Application / Domain or Contracts / Infrastructure / Composition。
3. 明确状态与生命周期。
4. 确定插件元数据协议：提供能力、申请宿主能力、设置 schema、运行时注入参数。
5. 确定配置、序列化、日志方案。
6. 通过 AOT 约束检查后再实现。

## Layer Rules

| 层 | 责任 | 禁止 |
|---|---|---|
| Entry / Composition | 注册依赖，连接宿主上下文 | 业务逻辑、I/O |
| Tools | 参数映射、结果映射 | 直接访问文件、HTTP、数据库 |
| Application | 用例编排、错误翻译 | 宿主细节、UI |
| Domain / Contracts | 纯规则、纯模型 | I/O、日志、容器访问 |
| Infrastructure | 文件、HTTP、WebSocket、系统调用 | 暴露工具接口 |

## DI Rules

- 所有依赖构造函数注入。
- 不使用 ServiceLocator。
- 只在组合根 Build 容器。
- 工具类只依赖 Application 服务和上下文适配器。
- 插件目录、数据目录、工作区目录由宿主注入。

## Metadata Protocol Rules

- Native / Process 共享元数据协议见 `resources/plugin-metadata-protocol.md`。
- `[Plugin]` 只描述插件基础元数据和插件“提供给宿主”的 `Capabilities`。
- `[RequiredHostCapability]` 只描述插件“希望宿主提供”的能力申请；声明本身不代表已经授权。
- `[PluginSetting]` 只描述设置界面 schema 和宿主存储规则；实际运行时读取统一走 `PluginSettings`。
- 这些 Attribute 都应挂在入口类上，不要分散到工具类。
- 需要用户授权理解的文本写在 `Reason` / `Description` / `Label`，不要暴露内部实现细节。
- 敏感配置必须标记 `Sensitive = true`，并在工具实现、日志和异常信息中避免泄露。
- 可选宿主能力必须允许降级运行；必需宿主能力必须在调用路径上显式校验并给出清晰错误。

## AOT Rules

- 禁止动态代理、Emit、动态反射扫描。
- 自定义返回类型必须注册到 JsonSerializerContext。
- 外部包必须先经过包版本查询和 AOT 探测。

## Resources

- resources/checklist.md
- resources/plugin-metadata-protocol.md

## Scripts

- scripts/validate-plugin-architecture.ps1
