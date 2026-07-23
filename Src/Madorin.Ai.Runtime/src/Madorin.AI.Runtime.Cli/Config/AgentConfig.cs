namespace Madorin.AI.Runtime.Cli.Config;

public sealed class AgentConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public float Temperature { get; set; } = 0.7f;
}
