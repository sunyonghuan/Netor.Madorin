---
title: MCP 服务接入
version: 1
---

# MCP 服务接入

Cortana 作为 MCP **客户端**连接外部 MCP Server，不写 plugin.json，通过 UI 配置持久化到数据库。

## 1. 配置字段（`McpServerEntity`）

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `Name` | string(64) | ✅ | 显示名 |
| `TransportType` | string(32) | ✅ | `stdio` / `sse` / `streamable-http` |
| `Command` | string(512) | stdio ✅ | 启动命令：`npx` / `uvx` / `python` / `node` 等 |
| `Arguments` | List\<string\> | stdio | 命令参数 |
| `Url` | string(512) | http ✅ | HTTP 服务地址 |
| `ApiKey` | string(256) | — | Bearer Token（可选） |
| `EnvironmentVariables` | Dict | — | 环境变量（stdio 模式） |
| `Description` | string(1024) | — | 描述 |
| `IsEnabled` | bool | — | 默认 true |

## 2. 配置示例

**GitHub MCP（stdio）**

```yaml
Command: npx
Arguments: ["-y", "@modelcontextprotocol/server-github"]
EnvironmentVariables:
  GITHUB_TOKEN: ghp_xxx
```

**文件系统 MCP（stdio）**

```yaml
Command: npx
Arguments: ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\me\\Documents"]
```

**远程 HTTP MCP**

```yaml
TransportType: streamable-http
Url: http://localhost:3000
ApiKey: <可选>
```

## 3. 什么时候选 MCP

- 已有现成的 MCP Server（社区/自建）直接接入
- 不需要 Cortana 专属的工具契约（`[Tool]` 属性、AOT 限制等）
- 团队内共享一个 Server 比分发插件更合适

其它场景选 Native 或 Process。
