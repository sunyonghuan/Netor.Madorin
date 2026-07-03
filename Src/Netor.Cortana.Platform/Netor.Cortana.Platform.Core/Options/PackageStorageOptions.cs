namespace Netor.Cortana.Platform.Core.Options;

public sealed class PackageStorageOptions
{
    public const string SectionName = "PackageStorage";

    public string Provider { get; set; } = "Local";

    public string RootPath { get; set; } = "Data";

    public string Endpoint { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;

    public string AccessKeyId { get; set; } = string.Empty;

    public string AccessKeySecret { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public bool ForcePathStyle { get; set; } = true;

    public string PublicBaseUrl { get; set; } = string.Empty;
}
