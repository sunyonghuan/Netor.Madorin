using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Madorin.AI.Runtime.Cli.Config;

public sealed class StandaloneConfigLoader
{
    private const string ConfigFileName = "config.json";
    private const string ConfigLockFileName = "config.lock";
    private const string AgentsDirectoryName = "agents";
    private static readonly TimeSpan ConfigLockTimeout = TimeSpan.FromSeconds(5);

    public StandaloneConfigLoader()
        : this(GetDefaultConfigDirectory())
    {
    }

    public StandaloneConfigLoader(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ConfigDirectory = Path.GetFullPath(configDirectory);
    }

    public string ConfigDirectory { get; }

    public string ConfigPath => Path.Combine(ConfigDirectory, ConfigFileName);

    public string ConfigLockPath => Path.Combine(ConfigDirectory, ConfigLockFileName);

    public string AgentsDirectory => Path.Combine(ConfigDirectory, AgentsDirectoryName);

    public static string GetDefaultConfigDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            throw new InvalidOperationException("The current user profile directory is unavailable.");
        }

        return Path.Combine(userProfile, ".madorin");
    }

    public async Task<StandaloneConfig?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        try
        {
            await using var stream = OpenRead(ConfigPath);
            return await JsonSerializer.DeserializeAsync(
                    stream,
                    ConfigJsonContext.Default.StandaloneConfig,
                    ct)
                ?? throw new JsonException($"Configuration file '{ConfigPath}' is empty.");
        }
        catch (JsonException ex) when (!ex.Message.Contains(ConfigPath, StringComparison.Ordinal))
        {
            throw new JsonException($"Configuration file '{ConfigPath}' contains invalid JSON.", ex);
        }
    }

    public async Task<IReadOnlyList<AgentConfig>> LoadAgentsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(AgentsDirectory))
        {
            return [];
        }

        var documents = await LoadAgentDocumentsAsync(ct).ConfigureAwait(false);
        return [.. documents.Select(static document => document.Agent)];
    }

    public async Task<IReadOnlyList<AgentConfigDocument>> LoadAgentDocumentsAsync(
        CancellationToken ct = default)
    {
        if (!Directory.Exists(AgentsDirectory))
        {
            return [];
        }

        var documents = new List<AgentConfigDocument>();
        foreach (var path in Directory.EnumerateFiles(AgentsDirectory, "*.json")
                     .Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = OpenRead(path);
                var agent = await JsonSerializer.DeserializeAsync(
                        stream,
                        ConfigJsonContext.Default.AgentConfig,
                        ct)
                    ?? throw new JsonException($"Agent file '{path}' is empty.");
                documents.Add(new AgentConfigDocument(path, agent));
            }
            catch (JsonException ex) when (!ex.Message.Contains(path, StringComparison.Ordinal))
            {
                throw new JsonException($"Agent file '{path}' contains invalid JSON.", ex);
            }
        }

        return documents;
    }

    public async Task SaveAsync(StandaloneConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await using var configLock = await AcquireConfigLockAsync(ct).ConfigureAwait(false);
        await WriteAtomicallyAsync(
            ConfigPath,
            config,
            ConfigJsonContext.Default.StandaloneConfig,
            ct).ConfigureAwait(false);
    }

    public async Task SaveAgentAsync(AgentConfig agent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (!StandaloneConfigValidator.IsSafeAgentId(agent.Id))
        {
            throw new ArgumentException("Agent id contains unsafe file name characters.", nameof(agent));
        }

        await using var configLock = await AcquireConfigLockAsync(ct).ConfigureAwait(false);
        var path = Path.Combine(AgentsDirectory, $"{agent.Id}.json");
        await WriteAtomicallyAsync(
            path,
            agent,
            ConfigJsonContext.Default.AgentConfig,
            ct).ConfigureAwait(false);
    }

    public async Task<StandaloneConfig> UpdateConfigAsync(
        Func<StandaloneConfig?, StandaloneConfig> update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await using var configLock = await AcquireConfigLockAsync(ct).ConfigureAwait(false);
        var latest = await LoadAsync(ct).ConfigureAwait(false);
        var updated = update(latest)
            ?? throw new InvalidOperationException("The configuration update returned null.");
        await WriteAtomicallyAsync(
            ConfigPath,
            updated,
            ConfigJsonContext.Default.StandaloneConfig,
            ct).ConfigureAwait(false);
        return updated;
    }

    public async Task<AgentConfig> UpdateAgentAsync(
        string agentId,
        Func<AgentConfig?, AgentConfig> update,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(update);
        if (!StandaloneConfigValidator.IsSafeAgentId(agentId))
        {
            throw new ArgumentException("Agent id contains unsafe file name characters.", nameof(agentId));
        }

        await using var configLock = await AcquireConfigLockAsync(ct).ConfigureAwait(false);
        var path = Path.Combine(AgentsDirectory, $"{agentId}.json");
        AgentConfig? latest = null;
        if (File.Exists(path))
        {
            await using var stream = OpenRead(path);
            latest = await JsonSerializer.DeserializeAsync(
                    stream,
                    ConfigJsonContext.Default.AgentConfig,
                    ct)
                .ConfigureAwait(false);
        }

        var updated = update(latest)
            ?? throw new InvalidOperationException("The Agent update returned null.");
        if (!string.Equals(agentId, updated.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An Agent update cannot change its id.");
        }

        await WriteAtomicallyAsync(
            path,
            updated,
            ConfigJsonContext.Default.AgentConfig,
            ct).ConfigureAwait(false);
        return updated;
    }

    public async Task DeleteAgentAsync(
        string agentId,
        string? replacementDefaultAgent = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (!StandaloneConfigValidator.IsSafeAgentId(agentId))
        {
            throw new ArgumentException("Agent id contains unsafe file name characters.", nameof(agentId));
        }

        await using var configLock = await AcquireConfigLockAsync(ct).ConfigureAwait(false);
        var path = Path.Combine(AgentsDirectory, $"{agentId}.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Agent '{agentId}' was not found.", path);
        }

        var config = await LoadAsync(ct).ConfigureAwait(false);
        if (config is not null
            && config.DefaultAgent.Equals(agentId, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(replacementDefaultAgent))
            {
                throw new InvalidOperationException(
                    "A replacement default Agent must be selected before deleting the current default Agent.");
            }

            if (!StandaloneConfigValidator.IsSafeAgentId(replacementDefaultAgent))
            {
                throw new ArgumentException(
                    "Replacement Agent id contains unsafe file name characters.",
                    nameof(replacementDefaultAgent));
            }

            var replacementPath = Path.Combine(
                AgentsDirectory,
                $"{replacementDefaultAgent}.json");
            if (!File.Exists(replacementPath)
                || replacementDefaultAgent.Equals(agentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replacement Agent '{replacementDefaultAgent}' does not exist.");
            }

            config.DefaultAgent = replacementDefaultAgent;
            await WriteAtomicallyAsync(
                    ConfigPath,
                    config,
                    ConfigJsonContext.Default.StandaloneConfig,
                    ct)
                .ConfigureAwait(false);
        }

        File.Delete(path);
    }

    public async Task<string> BackupConfigAsync(CancellationToken ct = default)
    {
        await using var configLock = await AcquireConfigLockAsync(ct).ConfigureAwait(false);
        return BackupConfigUnderLock(ct);
    }

    public async Task<string> ReplaceConfigWithBackupAsync(
        StandaloneConfig config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await using var configLock = await AcquireConfigLockAsync(ct).ConfigureAwait(false);
        var backupPath = BackupConfigUnderLock(ct);
        await WriteAtomicallyAsync(
            ConfigPath,
            config,
            ConfigJsonContext.Default.StandaloneConfig,
            ct).ConfigureAwait(false);
        return backupPath;
    }

    private string BackupConfigUnderLock(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException("The standalone configuration does not exist.", ConfigPath);
        }

        var timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMddTHHmmssfffZ",
            System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(ConfigDirectory, $"config.{timestamp}.bak.json");
        File.Copy(ConfigPath, backupPath, overwrite: false);
        SetFilePermissions(backupPath);
        return backupPath;
    }

    public static string MaskApiKey(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return apiKey.Length <= 4 ? "***" : $"{apiKey[..4]}***";
    }

    private static FileStream OpenRead(string path)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
    }

    private async Task<FileStream> AcquireConfigLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ConfigDirectory);
        SetDirectoryPermissions(ConfigDirectory);

        var stopwatch = Stopwatch.StartNew();
        IOException? lastException = null;
        while (stopwatch.Elapsed < ConfigLockTimeout)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(ConfigLockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                });
                SetFilePermissions(ConfigLockPath);
                return stream;
            }
            catch (IOException ex)
            {
                lastException = ex;
            }

            var remaining = ConfigLockTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(50)
                        ? remaining
                        : TimeSpan.FromMilliseconds(50),
                    ct)
                .ConfigureAwait(false);
        }

        throw new ConfigBusyException(ConfigLockPath, ConfigLockTimeout, lastException);
    }

    private static async Task WriteAtomicallyAsync<T>(
        string destinationPath,
        T value,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The destination directory is unavailable.");
        Directory.CreateDirectory(destinationDirectory);
        SetDirectoryPermissions(destinationDirectory);

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Path.GetRandomFileName()}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            {
                await JsonSerializer.SerializeAsync(stream, value, jsonTypeInfo, ct)
                    .ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();
            SetFilePermissions(temporaryPath);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void SetDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var currentUser = GetCurrentUserSid();
            var security = new DirectorySecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var currentUser = GetCurrentUserSid();
            var security = new FileSecurity();
            security.SetOwner(currentUser);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
    }
}
