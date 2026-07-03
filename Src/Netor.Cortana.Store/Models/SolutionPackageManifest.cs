namespace Netor.Cortana.Store.Models;

public sealed record SolutionPackageManifest(
    string? Id,
    string Slug,
    string Name,
    string Version,
    string? Description,
    SolutionPackageAssets? Assets);

public sealed record SolutionPackageAssets(
    IReadOnlyList<SolutionPackageAsset>? Plugins,
    IReadOnlyList<SolutionPackageAsset>? Skills,
    IReadOnlyList<SolutionPackageAsset>? Agents);

public sealed record SolutionPackageAsset(
    string Slug,
    string? Version,
    string? Path);

public sealed record SolutionInstalledAssetsFile(
    string SolutionSlug,
    string SolutionVersion,
    DateTimeOffset InstalledAtUtc,
    IReadOnlyList<SolutionInstalledAssetRecord> Assets,
    IReadOnlyList<SolutionAssetOperationRecord>? History = null);

public sealed record SolutionInstalledAssetRecord(
    AssetType Type,
    string Slug,
    string? Version,
    string Path,
    string InstalledPath,
    string Source);

public sealed record SolutionAssetOperationRecord(
    AssetType Type,
    string Slug,
    string Operation,
    string? PreviousVersion,
    string? NewVersion,
    string Result,
    string? Message,
    DateTimeOffset RecordedAtUtc);
