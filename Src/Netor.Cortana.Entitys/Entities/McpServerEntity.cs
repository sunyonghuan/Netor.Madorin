namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// MCP（Model Context Protocol）服务器配置实体。
    /// 每条记录代表一个外部 MCP Server 的连接配置，
    /// 支持 stdio、sse、streamable-http 三种传输模式。
    /// </summary>
    public class McpServerEntity : BaseEntity
    {
        /// <summary>
        /// MCP 服务器的显示名称，用于在 UI 中识别。
        /// 例如："GitHub MCP"、"文件系统 MCP"、"数据库查询"。
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 传输类型：stdio / sse / streamable-http。
        /// </summary>
        public string TransportType { get; set; } = "stdio";

        /// <summary>
        /// stdio 模式下的启动命令。
        /// 例如："npx"、"python"、"node"、"uvx"。
        /// </summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// stdio 模式下的启动参数列表（JSON 数组序列化存储）。
        /// 例如：["-y", "@modelcontextprotocol/server-github"]。
        /// </summary>
        public List<string> Arguments { get; set; } = [];

        /// <summary>
        /// SSE / Streamable HTTP 模式下的服务器地址。
        /// 例如："http://localhost:3000/sse"。
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// 可选的认证密钥（API Key / Bearer Token），用于需要认证的 MCP Server。
        /// </summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>
        /// 环境变量键值对（JSON 对象序列化存储）。
        /// 例如：{"GITHUB_TOKEN": "ghp_xxx", "NODE_ENV": "production"}。
        /// LiteDB 原生支持 Dictionary 存储。
        /// </summary>
        public Dictionary<string, string?> EnvironmentVariables { get; set; } = [];

        /// <summary>
        /// 服务器描述，说明该 MCP Server 提供的能力。
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 是否启用该 MCP 服务器。设为 false 时不会尝试连接。
        /// </summary>
        public bool IsEnabled { get; set; } = true;
    }
}
