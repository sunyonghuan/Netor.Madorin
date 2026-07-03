namespace Netor.Cortana.Platform.Core.Options;

public sealed class DocsMediaOptions
{
    public const string SectionName = "DocsMedia";

    public string RootPath { get; set; } = string.Empty;

    public string RequestPath { get; set; } = "/docs-media";

    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;
}
