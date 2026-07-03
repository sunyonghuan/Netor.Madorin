using Netor.Cortana.Platform.Admin.Mcp;

namespace Netor.Cortana.Platform.Admin.Models.Profile;

public sealed class McpTokenViewModel
{
    public IReadOnlyList<AdminMcpTokenSummary> Tokens { get; init; } = [];

    public string? PlainToken { get; init; }

    public string? PlainTokenNote { get; init; }

    public string EndpointUrl { get; init; } = string.Empty;

    public string HealthUrl { get; init; } = string.Empty;

    public string ProtocolType { get; init; } = "MCP Streamable HTTP";

    public string AuthHeaderExample { get; init; } = "Authorization: Bearer mcp_xxx";

    public bool Enabled { get; init; }
}
