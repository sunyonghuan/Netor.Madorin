using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Cli.Config;

public sealed class StandaloneConfig
{
    public string DefaultProvider { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
    public string DefaultAgent { get; set; } = "default";
    public List<ProviderEntry> Providers { get; set; } = [];
}

public sealed class ProviderEntry
{
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string AuthenticationHeader { get; set; } = "Authorization";
    public string? AuthenticationScheme { get; set; } = "Bearer";
    public ProviderCapabilities? Capabilities { get; set; }
    public string ConfigurationVersion { get; set; } = "1";
    public string? ProbePath { get; set; } = "models";
    public List<string> Models { get; set; } = [];
}
