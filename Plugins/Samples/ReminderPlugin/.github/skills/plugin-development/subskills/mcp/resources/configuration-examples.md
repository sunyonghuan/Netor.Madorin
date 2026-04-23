# MCP Configuration Examples

## 选型原则

- 本地命令行工具优先 stdio。
- 远程服务优先 streamable-http；只有服务端仅支持 SSE 时才选 sse。

## stdio 示例

适合 npx、uvx、python 等本地命令启动的 MCP Server。

```text
Name: GitHub MCP
TransportType: stdio
Command: npx
Arguments: -y @modelcontextprotocol/server-github
EnvironmentVariables:
	GITHUB_TOKEN=从安全存储注入
```

## streamable-http 示例

适合远程部署、双向流能力完整的 MCP Server。

```text
Name: Remote Analytics
TransportType: streamable-http
Url: https://mcp.example.com/api
ApiKey: 从安全存储注入
```

## sse 示例

```text
Name: Legacy SSE Server
TransportType: sse
Url: http://localhost:3000/sse
```

## 验证命令

```powershell
.\skills\plugin-development\subskills\mcp\scripts\validate-mcp-server-config.ps1 -TransportType stdio -Name github -Command npx -Arguments '-y','@modelcontextprotocol/server-github' -EnvironmentVariables @{ GITHUB_TOKEN = '***' }
```