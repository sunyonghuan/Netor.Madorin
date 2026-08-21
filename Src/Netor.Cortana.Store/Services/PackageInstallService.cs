using System.Buffers;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Netor.Cortana.Entitys;
using Netor.Madorin.Plugin;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;
using Netor.EventHub;

namespace Netor.Cortana.Store.Services;

public sealed class PackageInstallService(
    IAppPaths appPaths,
    IHttpClientFactory httpClientFactory,
    IAssetInstallTargetResolver targetResolver,
    IInstalledAssetStore installedAssetStore,
    IPluginActivationService pluginActivationService,
    IPlatformAccountStore accountStore,
    IPlatformBaseUrlProvider baseUrlProvider,
    IPublisher publisher) : IPackageInstallService
{
    private const int MaxZipEntries = 4096;
    private const long MaxSingleFileBytes = 256L * 1024 * 1024;

    private string StoreRoot => Path.Combine(appPaths.UserDataDirectory, "store");

    private string StagingRoot => Path.Combine(StoreRoot, "staging");

    public Task CleanupStaleStagingAsync(CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(StagingRoot))
        {
            Directory.Delete(StagingRoot, recursive: true);
        }

        Directory.CreateDirectory(StagingRoot);
        return Task.CompletedTask;
    }

    public async Task<PackageInstallResult> InstallAsync(StoreInstallManifest manifest, CancellationToken cancellationToken = default)
    {
        var stagingDirectory = Path.Combine(StagingRoot, $"{SanitizeDirectoryName(manifest.AssetSlug)}-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(stagingDirectory, "package.zip");
        var extractDirectory = Path.Combine(stagingDirectory, "extract");

        try
        {
            Directory.CreateDirectory(stagingDirectory);

            PublishProgress(manifest, "下载资源包", 0.1);
            await DownloadAndValidateAsync(manifest, archivePath, cancellationToken);

            PublishProgress(manifest, "解压资源包", 0.35);
            Directory.CreateDirectory(extractDirectory);
            await ExtractArchiveSafelyAsync(archivePath, extractDirectory, manifest.AssetType, cancellationToken);

            var packageRoot = ResolvePackageRoot(extractDirectory, manifest.AssetType);
            PublishProgress(manifest, "校验清单", 0.6);
            await ValidatePackageManifestAsync(packageRoot, manifest.AssetType, cancellationToken);

            PublishProgress(manifest, "写入本地目录", 0.75);
            if (manifest.AssetType == AssetType.Solution)
            {
                var solutionAsset = await InstallSolutionPackageAsync(manifest, packageRoot, cancellationToken);
                await installedAssetStore.UpsertAsync(solutionAsset, cancellationToken);
                PublishProgress(manifest, "安装完成", 1);
                publisher.Publish(
                    StoreEvents.OnAssetInstallCompleted,
                    new AssetInstallCompletedArgs(manifest.AssetId, manifest.AssetSlug, solutionAsset.InstalledDirectory));

                return PackageInstallResult.Success(solutionAsset);
            }

            var installedDirectory = await ReplaceInstalledDirectoryAsync(
                manifest.AssetType,
                manifest.AssetSlug,
                packageRoot,
                cancellationToken);

            var installedAsset = new InstalledAssetRecord(
                manifest.AssetId,
                manifest.AssetSlug,
                manifest.AssetType,
                manifest.AssetName,
                manifest.Version.VersionId,
                manifest.Version.VersionName,
                manifest.Version.PackageHash,
                manifest.Version.PackageSize,
                installedDirectory,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

            await installedAssetStore.UpsertAsync(installedAsset, cancellationToken);
            PublishProgress(manifest, "安装完成", 1);
            publisher.Publish(
                StoreEvents.OnAssetInstallCompleted,
                new AssetInstallCompletedArgs(manifest.AssetId, manifest.AssetSlug, installedDirectory));

            return PackageInstallResult.Success(installedAsset);
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or InvalidDataException
                                    or HttpRequestException
                                    or CryptographicException
                                    or JsonException
                                    or OperationCanceledException)
        {
            var message = ex is OperationCanceledException ? "安装已取消。" : ex.Message;
            publisher.Publish(StoreEvents.OnAssetInstallFailed, new AssetInstallFailedArgs(manifest.AssetId, manifest.AssetSlug, message));
            return PackageInstallResult.Failed(message);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    public async Task<PackageInstallResult> UninstallAsync(InstalledAssetRecord asset, CancellationToken cancellationToken = default)
    {
        try
        {
            if (asset.AssetType == AssetType.Solution)
            {
                await UninstallSolutionPackageAsync(asset, cancellationToken);
            }
            else
            {
                await DeleteInstalledDirectoryAsync(asset.AssetType, asset.AssetSlug, asset.InstalledDirectory, cancellationToken);
            }

            await installedAssetStore.RemoveAsync(asset.AssetSlug, cancellationToken);
            return PackageInstallResult.Uninstalled(asset);
        }
        catch (Exception ex) when (ex is IOException
                                    or UnauthorizedAccessException
                                    or InvalidDataException
                                    or OperationCanceledException)
        {
            var message = ex is OperationCanceledException ? "卸载已取消。" : ex.Message;
            return PackageInstallResult.Failed(message);
        }
    }

    private async Task DownloadAndValidateAsync(StoreInstallManifest manifest, string archivePath, CancellationToken cancellationToken)
    {
        if (!string.Equals(manifest.Version.HashAlgorithm, "SHA256", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"不支持的哈希算法：{manifest.Version.HashAlgorithm}");
        }

        using var httpClient = httpClientFactory.CreateClient("CortanaStore");
        var session = await accountStore.LoadAsync(cancellationToken);
        var baseUrl = session?.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = baseUrlProvider.GetBaseUrl();
        }
        else
        {
            baseUrl = StorePlatformUrlHelper.NormalizeApiBaseUrl(baseUrl);
        }

        httpClient.BaseAddress = new Uri(StorePlatformUrlHelper.NormalizeApiBaseUrl(baseUrl));
        if (!string.IsNullOrWhiteSpace(session?.AccessToken))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }

        using var response = await httpClient.GetAsync(
            manifest.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await StoreHttpResponseHelper.EnsureSuccessAsync(response, "下载资源包", cancellationToken);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(archivePath);
        using var sha256 = SHA256.Create();

        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long totalBytes = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > manifest.Version.PackageSize)
                {
                    throw new InvalidDataException("下载包大小超过安装清单声明。");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha256.TransformBlock(buffer, 0, read, null, 0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        sha256.TransformFinalBlock([], 0, 0);
        if (totalBytes != manifest.Version.PackageSize)
        {
            throw new InvalidDataException("下载包大小与安装清单不一致。");
        }

        var actualHash = Convert.ToHexString(sha256.Hash ?? []).ToLowerInvariant();
        if (!string.Equals(actualHash, manifest.Version.PackageHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("下载包 SHA256 与安装清单不一致。");
        }
    }

    private static async Task ExtractArchiveSafelyAsync(
        string archivePath,
        string extractDirectory,
        AssetType assetType,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var limits = GetAssetLimits(assetType);
        var extractRoot = Path.GetFullPath(extractDirectory);
        var entryCount = 0;
        long totalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            if (entryCount > MaxZipEntries)
            {
                throw new InvalidDataException("Zip 文件数量超过限制。");
            }

            if (entry.FullName.Contains('\\'))
            {
                throw new InvalidDataException("Zip 路径必须使用正斜杠。");
            }

            if (Path.IsPathFullyQualified(entry.FullName))
            {
                throw new InvalidDataException("Zip 内不允许绝对路径。");
            }

            var isDirectoryEntry = entry.FullName.EndsWith('/');
            var normalizedEntryName = entry.FullName.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(normalizedEntryName)
                || normalizedEntryName.Split('/').Any(part => part is "" or "." or ".."))
            {
                throw new InvalidDataException("Zip 内包含非法路径片段。");
            }

            if (IsUnixSymbolicLink(entry))
            {
                throw new InvalidDataException("Zip 内不允许符号链接。");
            }

            if (entry.Length > MaxSingleFileBytes || entry.Length > limits.MaxSingleFileBytes)
            {
                throw new InvalidDataException("Zip 内单文件超过限制。");
            }

            totalBytes += entry.Length;
            if (totalBytes > limits.MaxTotalBytes)
            {
                throw new InvalidDataException("Zip 解压后总大小超过限制。");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
            if (!destinationPath.StartsWith(extractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destinationPath, extractRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Zip 包含路径穿越内容。");
            }

            if (isDirectoryEntry)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = entry.Open();
            await using var target = File.Create(destinationPath);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private static bool IsUnixSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var mode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        return mode == unixSymbolicLink;
    }

    private static string ResolvePackageRoot(string extractDirectory, AssetType assetType)
    {
        var manifestFileName = GetManifestFileName(assetType);
        if (File.Exists(Path.Combine(extractDirectory, manifestFileName)))
        {
            return extractDirectory;
        }

        var childDirectories = Directory.GetDirectories(extractDirectory);
        if (childDirectories.Length == 1 && File.Exists(Path.Combine(childDirectories[0], manifestFileName)))
        {
            return childDirectories[0];
        }

        throw new InvalidDataException($"资源包缺少 {manifestFileName} 清单文件。");
    }

    private static async Task ValidatePackageManifestAsync(
        string packageRoot,
        AssetType assetType,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(packageRoot, GetManifestFileName(assetType));
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"资源包缺少 {Path.GetFileName(manifestPath)} 清单文件。");
        }

        if (assetType != AssetType.Plugin)
        {
            return;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var pluginManifest = JsonSerializer.Deserialize(json, PluginManifestJsonContext.Default.PluginManifest)?.Normalize();
        if (pluginManifest is null)
        {
            throw new InvalidDataException("plugin.json 反序列化为空。");
        }

        if (!pluginManifest.Validate(out var error))
        {
            throw new InvalidDataException($"plugin.json 校验失败：{error}");
        }
    }

    private async Task<string> ReplaceInstalledDirectoryAsync(
        AssetType assetType,
        string assetSlug,
        string packageRoot,
        CancellationToken cancellationToken)
    {
        var targetRoot = targetResolver.ResolveRootDirectory(assetType);
        Directory.CreateDirectory(targetRoot);

        var directoryName = SanitizeDirectoryName(assetSlug);
        var targetDirectory = Path.Combine(targetRoot, directoryName);
        var backupDirectory = $"{targetDirectory}.bak-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        var incomingDirectory = $"{targetDirectory}.incoming-{Guid.NewGuid():N}";

        CopyDirectory(packageRoot, incomingDirectory);

        try
        {
            if (assetType == AssetType.Plugin && Directory.Exists(targetDirectory))
            {
                pluginActivationService.Unload(directoryName);
                await Task.Delay(300, cancellationToken);
            }

            if (Directory.Exists(targetDirectory))
            {
                Directory.Move(targetDirectory, backupDirectory);
            }

            Directory.Move(incomingDirectory, targetDirectory);

            if (assetType == AssetType.Plugin)
            {
                var loaded = await pluginActivationService.LoadByPathAsync(targetDirectory, cancellationToken);
                if (!loaded)
                {
                    throw new InvalidDataException("插件安装后加载失败。");
                }
            }

            TryDeleteDirectory(backupDirectory);
            return targetDirectory;
        }
        catch
        {
            if (Directory.Exists(targetDirectory))
            {
                TryDeleteDirectory(targetDirectory);
            }

            if (Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, targetDirectory);
                if (assetType == AssetType.Plugin)
                {
                    _ = await pluginActivationService.LoadByPathAsync(targetDirectory, cancellationToken);
                }
            }

            TryDeleteDirectory(incomingDirectory);
            throw;
        }
    }

    private async Task<InstalledAssetRecord> InstallSolutionPackageAsync(
        StoreInstallManifest manifest,
        string packageRoot,
        CancellationToken cancellationToken)
    {
        var solutionManifest = await ReadSolutionManifestAsync(packageRoot, cancellationToken);
        var solutionSlug = string.IsNullOrWhiteSpace(solutionManifest.Slug) ? manifest.AssetSlug : solutionManifest.Slug;
        var solutionVersion = string.IsNullOrWhiteSpace(solutionManifest.Version) ? manifest.Version.VersionName : solutionManifest.Version;
        var solutionDirectory = Path.Combine(
            targetResolver.ResolveRootDirectory(AssetType.Solution),
            SanitizeDirectoryName(solutionSlug));

        var previousAssets = await ReadSolutionInstalledAssetsAsync(solutionDirectory, cancellationToken);
        var installedAssets = new List<SolutionInstalledAssetRecord>();
        var operationHistory = new List<SolutionAssetOperationRecord>();

        foreach (var child in EnumerateSolutionAssets(solutionManifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousAsset = previousAssets.Assets.FirstOrDefault(x => x.Type == child.AssetType &&
                                                                           string.Equals(x.Slug, child.Asset.Slug, StringComparison.OrdinalIgnoreCase));

            try
            {
                var childDirectory = ResolveSolutionAssetDirectory(packageRoot, child.Asset, child.FolderName);
                await ValidatePackageManifestAsync(childDirectory, child.AssetType, cancellationToken);

                var installedPath = await ReplaceInstalledDirectoryAsync(
                    child.AssetType,
                    child.Asset.Slug,
                    childDirectory,
                    cancellationToken);

                if (child.AssetType == AssetType.Agent)
                {
                    publisher.Publish(Events.OnAgentChange, new DataChangeArgs(child.Asset.Slug, ChangeType.Update));
                }

                installedAssets.Add(new SolutionInstalledAssetRecord(
                    child.AssetType,
                    child.Asset.Slug,
                    child.Asset.Version,
                    GetSolutionAssetRelativePath(child.Asset, child.FolderName),
                    installedPath,
                    "solution"));

                operationHistory.Add(CreateOperationRecord(
                    child.AssetType,
                    child.Asset.Slug,
                    previousAsset is null ? "install" : "update",
                    previousAsset?.Version,
                    child.Asset.Version,
                    "success",
                    null));
            }
            catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or InvalidDataException
                                        or OperationCanceledException)
            {
                operationHistory.Add(CreateOperationRecord(
                    child.AssetType,
                    child.Asset.Slug,
                    previousAsset is null ? "install" : "update",
                    previousAsset?.Version,
                    child.Asset.Version,
                    "failed",
                    ex is OperationCanceledException ? "安装已取消。" : ex.Message));
                await WriteSolutionInstallRecordAsync(
                    packageRoot,
                    solutionDirectory,
                    solutionSlug,
                    solutionVersion,
                    previousAssets,
                    installedAssets,
                    operationHistory,
                    cancellationToken);
                throw;
            }
        }

        foreach (var oldAsset in previousAssets.Assets)
        {
            if (installedAssets.Any(x => x.Type == oldAsset.Type &&
                                         string.Equals(x.Slug, oldAsset.Slug, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            await DeleteSolutionChildIfUnreferencedAsync(oldAsset, solutionDirectory, cancellationToken);
            operationHistory.Add(CreateOperationRecord(
                oldAsset.Type,
                oldAsset.Slug,
                "remove",
                oldAsset.Version,
                null,
                "success",
                null));
        }

        await WriteSolutionInstallRecordAsync(
            packageRoot,
            solutionDirectory,
            solutionSlug,
            solutionVersion,
            previousAssets,
            installedAssets,
            operationHistory,
            cancellationToken);

        return new InstalledAssetRecord(
            manifest.AssetId,
            manifest.AssetSlug,
            manifest.AssetType,
            manifest.AssetName,
            manifest.Version.VersionId,
            manifest.Version.VersionName,
            manifest.Version.PackageHash,
            manifest.Version.PackageSize,
            solutionDirectory,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private async Task UninstallSolutionPackageAsync(InstalledAssetRecord solution, CancellationToken cancellationToken)
    {
        var installedAssets = await ReadSolutionInstalledAssetsAsync(solution.InstalledDirectory, cancellationToken);
        foreach (var asset in installedAssets.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteSolutionChildIfUnreferencedAsync(asset, solution.InstalledDirectory, cancellationToken);
        }

        await DeleteInstalledDirectoryAsync(AssetType.Solution, solution.AssetSlug, solution.InstalledDirectory, cancellationToken);
    }

    private async Task DeleteSolutionChildIfUnreferencedAsync(
        SolutionInstalledAssetRecord asset,
        string currentSolutionDirectory,
        CancellationToken cancellationToken)
    {
        if (await IsInstalledAsStandaloneAssetAsync(asset, cancellationToken) ||
            await IsReferencedByAnotherSolutionAsync(asset, currentSolutionDirectory, cancellationToken))
        {
            return;
        }

        await DeleteInstalledDirectoryAsync(asset.Type, asset.Slug, asset.InstalledPath, cancellationToken);
    }

    private static SolutionAssetOperationRecord CreateOperationRecord(
        AssetType assetType,
        string slug,
        string operation,
        string? previousVersion,
        string? newVersion,
        string result,
        string? message)
        => new(
            assetType,
            slug,
            operation,
            previousVersion,
            newVersion,
            result,
            message,
            DateTimeOffset.UtcNow);

    private static Task WriteSolutionInstallRecordAsync(
        string packageRoot,
        string solutionDirectory,
        string solutionSlug,
        string solutionVersion,
        SolutionInstalledAssetsFile previousAssets,
        IReadOnlyList<SolutionInstalledAssetRecord> installedAssets,
        IReadOnlyList<SolutionAssetOperationRecord> operationHistory,
        CancellationToken cancellationToken)
        => ReplaceSolutionRecordDirectoryAsync(
            packageRoot,
            solutionDirectory,
            new SolutionInstalledAssetsFile(
                solutionSlug,
                solutionVersion,
                DateTimeOffset.UtcNow,
                installedAssets,
                [.. (previousAssets.History ?? []), .. operationHistory]),
            cancellationToken);

    private async Task<bool> IsInstalledAsStandaloneAssetAsync(
        SolutionInstalledAssetRecord asset,
        CancellationToken cancellationToken)
    {
        var installedAssets = await installedAssetStore.LoadAsync(cancellationToken);
        return installedAssets.Any(x => x.AssetType == asset.Type &&
                                        string.Equals(x.AssetSlug, asset.Slug, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> IsReferencedByAnotherSolutionAsync(
        SolutionInstalledAssetRecord asset,
        string currentSolutionDirectory,
        CancellationToken cancellationToken)
    {
        var solutionRoot = targetResolver.ResolveRootDirectory(AssetType.Solution);
        if (!Directory.Exists(solutionRoot))
        {
            return false;
        }

        var current = Path.GetFullPath(currentSolutionDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var solutionDirectory in Directory.EnumerateDirectories(solutionRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Path.GetFullPath(solutionDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var installedAssets = await ReadSolutionInstalledAssetsAsync(solutionDirectory, cancellationToken);
            if (installedAssets.Assets.Any(x => x.Type == asset.Type &&
                                                string.Equals(x.Slug, asset.Slug, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task DeleteInstalledDirectoryAsync(
        AssetType assetType,
        string assetSlug,
        string installedDirectory,
        CancellationToken cancellationToken)
    {
        var targetRoot = Path.GetFullPath(targetResolver.ResolveRootDirectory(assetType));
        var directoryName = SanitizeDirectoryName(assetSlug);
        var targetDirectory = string.IsNullOrWhiteSpace(installedDirectory)
            ? Path.Combine(targetRoot, directoryName)
            : installedDirectory;
        targetDirectory = Path.GetFullPath(targetDirectory);

        if (!IsDirectChildDirectory(targetRoot, targetDirectory))
        {
            throw new InvalidDataException($"资源安装目录不在允许范围内：{targetDirectory}");
        }

        if (assetType == AssetType.Plugin)
        {
            pluginActivationService.Unload(directoryName);
            await Task.Delay(300, cancellationToken);
        }

        DeleteDirectoryOrThrow(targetDirectory);

        if (assetType == AssetType.Agent)
        {
            publisher.Publish(Events.OnAgentChange, new DataChangeArgs(assetSlug, ChangeType.Delete));
        }
    }

    private static async Task<SolutionPackageManifest> ReadSolutionManifestAsync(
        string packageRoot,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(packageRoot, "solution.json");
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync(
            stream,
            StoreJsonContext.Default.SolutionPackageManifest,
            cancellationToken);

        if (manifest is null)
        {
            throw new InvalidDataException("solution.json 反序列化为空。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Slug))
        {
            throw new InvalidDataException("solution.json 缺少 slug。");
        }

        if (!EnumerateSolutionAssets(manifest).Any())
        {
            throw new InvalidDataException("解决方案至少需要包含一种插件、技能或智能体资源。");
        }

        return manifest;
    }

    private static IEnumerable<SolutionAssetInstallItem> EnumerateSolutionAssets(SolutionPackageManifest manifest)
    {
        foreach (var asset in manifest.Assets?.Plugins ?? [])
        {
            yield return new SolutionAssetInstallItem(AssetType.Plugin, "plugins", asset);
        }

        foreach (var asset in manifest.Assets?.Skills ?? [])
        {
            yield return new SolutionAssetInstallItem(AssetType.Skill, "skills", asset);
        }

        foreach (var asset in manifest.Assets?.Agents ?? [])
        {
            yield return new SolutionAssetInstallItem(AssetType.Agent, "agents", asset);
        }
    }

    private static string ResolveSolutionAssetDirectory(
        string packageRoot,
        SolutionPackageAsset asset,
        string folderName)
    {
        if (string.IsNullOrWhiteSpace(asset.Slug))
        {
            throw new InvalidDataException("解决方案子资源缺少 slug。");
        }

        var relativePath = GetSolutionAssetRelativePath(asset, folderName);
        var root = Path.GetFullPath(packageRoot);
        var directory = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(directory))
        {
            throw new InvalidDataException($"解决方案子资源目录不存在或越界：{relativePath}");
        }

        return directory;
    }

    private static string GetSolutionAssetRelativePath(SolutionPackageAsset asset, string folderName)
    {
        var relativePath = string.IsNullOrWhiteSpace(asset.Path)
            ? $"{folderName}/{asset.Slug}"
            : asset.Path;

        relativePath = relativePath.Replace('\\', '/').Trim('/');
        if (relativePath.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..") ||
            Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException($"解决方案子资源路径非法：{relativePath}");
        }

        return relativePath;
    }

    private static async Task<SolutionInstalledAssetsFile> ReadSolutionInstalledAssetsAsync(
        string solutionDirectory,
        CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(solutionDirectory, "installed-assets.json");
        if (!File.Exists(filePath))
        {
            return new SolutionInstalledAssetsFile(string.Empty, string.Empty, DateTimeOffset.MinValue, []);
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync(
                stream,
                StoreJsonContext.Default.SolutionInstalledAssetsFile,
                cancellationToken) ?? new SolutionInstalledAssetsFile(string.Empty, string.Empty, DateTimeOffset.MinValue, []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SolutionInstalledAssetsFile(string.Empty, string.Empty, DateTimeOffset.MinValue, []);
        }
    }

    private static async Task ReplaceSolutionRecordDirectoryAsync(
        string packageRoot,
        string solutionDirectory,
        SolutionInstalledAssetsFile installedAssets,
        CancellationToken cancellationToken)
    {
        var incomingDirectory = $"{solutionDirectory}.incoming-{Guid.NewGuid():N}";
        var backupDirectory = $"{solutionDirectory}.bak-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";

        Directory.CreateDirectory(incomingDirectory);
        foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(incomingDirectory, Path.GetFileName(file)), overwrite: false);
        }

        await using (var stream = File.Create(Path.Combine(incomingDirectory, "installed-assets.json")))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                installedAssets,
                StoreJsonContext.Default.SolutionInstalledAssetsFile,
                cancellationToken);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(solutionDirectory)!);
        try
        {
            if (Directory.Exists(solutionDirectory))
            {
                Directory.Move(solutionDirectory, backupDirectory);
            }

            Directory.Move(incomingDirectory, solutionDirectory);
            TryDeleteDirectory(backupDirectory);
        }
        catch
        {
            if (Directory.Exists(solutionDirectory))
            {
                TryDeleteDirectory(solutionDirectory);
            }

            if (Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, solutionDirectory);
            }

            TryDeleteDirectory(incomingDirectory);
            throw;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            File.Copy(file, Path.Combine(destinationDirectory, relative), overwrite: false);
        }
    }

    private static string GetManifestFileName(AssetType assetType)
        => assetType switch
        {
            AssetType.Plugin => "plugin.json",
            AssetType.Skill => "skill.md",
            AssetType.Agent => "agent.json",
            AssetType.Solution => "solution.json",
            _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, "未知资源类型。")
        };

    private static AssetPackageLimits GetAssetLimits(AssetType assetType)
        => assetType switch
        {
            AssetType.Plugin => new AssetPackageLimits(512L * 1024 * 1024, 128L * 1024 * 1024),
            AssetType.Skill => new AssetPackageLimits(32L * 1024 * 1024, 8L * 1024 * 1024),
            AssetType.Agent => new AssetPackageLimits(64L * 1024 * 1024, 16L * 1024 * 1024),
            AssetType.Solution => new AssetPackageLimits(256L * 1024 * 1024, 64L * 1024 * 1024),
            _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, "未知资源类型。")
        };

    private static string SanitizeDirectoryName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '-' : ch)
            .ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryOrThrow(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        Directory.Delete(directory, recursive: true);
    }

    private static bool IsDirectChildDirectory(string parentDirectory, string candidateDirectory)
    {
        var parent = Path.GetFullPath(parentDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidateDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateParent = Path.GetDirectoryName(candidate);

        return !string.IsNullOrWhiteSpace(candidateParent) &&
            string.Equals(
                candidateParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                parent,
                StringComparison.OrdinalIgnoreCase);
    }

    private void PublishProgress(StoreInstallManifest manifest, string stage, double progress)
        => publisher.Publish(
            StoreEvents.OnAssetInstallProgress,
            new AssetInstallProgressArgs(manifest.AssetId, manifest.AssetSlug, stage, progress));

    private readonly record struct AssetPackageLimits(long MaxTotalBytes, long MaxSingleFileBytes);

    private readonly record struct SolutionAssetInstallItem(
        AssetType AssetType,
        string FolderName,
        SolutionPackageAsset Asset);
}
