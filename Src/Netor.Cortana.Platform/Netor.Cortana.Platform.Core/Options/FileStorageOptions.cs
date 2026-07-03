namespace Netor.Cortana.Platform.Core.Options;

public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    public string RootPath { get; set; } = "Data";
}