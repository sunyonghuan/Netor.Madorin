---
name: architecture
description: 'Cortana 插件工程规范子技能。位置：plugin-development/subskills/architecture。用于插件架构设计、职责分离、依赖注入、AOT 安全编码、配置与日志、质量门禁。触发关键词：插件架构、职责分离、依赖注入、工程规范、AOT 安全、插件设计。'
version: 1
user-invocable: true
---

# Plugin Development Architecture

## Workflow

1. 定义插件边界：入口只做组合。
2. 划分层：Tools / Application / Domain or Contracts / Infrastructure / Composition。
3. 明确状态与生命周期。
4. 确定配置、序列化、日志方案。
5. 通过 AOT 约束检查后再实现。

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

## AOT Rules

- 禁止动态代理、Emit、动态反射扫描。
- 自定义返回类型必须注册到 JsonSerializerContext。
- 外部包必须先经过包版本查询和 AOT 探测。

## Resources

- resources/checklist.md

## Scripts

- scripts/validate-plugin-architecture.ps1