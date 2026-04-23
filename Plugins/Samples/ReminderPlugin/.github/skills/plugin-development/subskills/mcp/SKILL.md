---
name: mcp
description: 'Cortana MCP 通道子技能。位置：plugin-development/subskills/mcp。用于接入和校验外部 MCP Server 的 stdio、sse、streamable-http 配置。触发关键词：MCP、Model Context Protocol、stdio、sse、streamable-http。'
version: 1
user-invocable: true
---

# Plugin Development MCP

## Flow

1. 先确认需求是否适合 MCP：已有外部服务、跨语言工具、远程工具优先选 MCP。
2. 选择传输方式：本地命令行用 stdio；HTTP 服务按 sse 或 streamable-http。
3. 用 validate-mcp-server-config.ps1 先校验关键字段是否齐全。
4. 再参考 resources/configuration-examples.md 准备 UI 或数据库录入信息。
5. 连接完成后验证远程工具是否能被自动发现。

## Rules

- MCP 通道不编写 Cortana 插件代码，不生成 plugin.json，也不走 zip 安装。
- stdio 模式必须明确 Command、Arguments、EnvironmentVariables 的来源。
- HTTP 模式必须明确 Url 和认证方式；敏感信息不写死在文档示例里。
- 需要宿主内高性能执行或严格 AOT 约束时，不要误选 MCP，应回到 Native。

## Scripts

- scripts/validate-mcp-server-config.ps1

## Resources

- resources/configuration-examples.md