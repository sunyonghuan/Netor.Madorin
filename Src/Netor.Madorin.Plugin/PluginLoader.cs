using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Madorin.Plugin;
using Netor.Madorin.Plugin.Mcp;
using Netor.Madorin.Plugin.Native;
using Netor.Madorin.Plugin.Process;
using Netor.EventHub;
using Netor.EventHub.Interfaces;

using System.Collections.Concurrent;
using System.Text.Json;

namespace Netor.Madorin.Plugin;

/// <summary>
/// 插件安装范围。
/// </summary>
public enum PluginInstallScope
{
    /// <summary>用户级全局插件目录。</summary>
    Global
}

/// <summary>
/// 已加载插件的运行信息。
/// </summary>
public sealed record LoadedPluginInfo(
    IPlugin Plugin,
    string DirectoryPath,
    string DirectoryName,
    PluginInstallScope Scope,
    PluginManifest Manifest);

/// <summary>
/// 插件加载总调度器。
/// 扫描插件目录，根据 plugin.json 中的 runtime 字段委托给对应通道的 Host 加载，
/// 并通过 <see cref="FileSystemWatcher"/> 实现热插拔。
/// </summary>
public sealed class PluginLoader : IDisposable, IAsyncDisposable
{
    private readonly ILogger<PluginLoader> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPublisher _publisher;
    private readonly ISubscriber _subscriber;
    private readonly IAppPaths _appPaths;
    private readonly IPluginRuntimeConfigProvider _runtimeConfigProvider;
    private readonly PluginManifestRegistry _manifestRegistry;

    private readonly ConcurrentDictionary<string, NativePluginHost> _nativeHosts = new();
    private readonly ConcurrentDictionary<string, ProcessPluginHost> _processHosts = new();
    private readonly ConcurrentDictionary<string, McpServerHost> _mcpHosts = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _disposed;

    /// <summary>
    /// 内部插件总线端口，在 DI 注册时由 UI 壳设置。
    /// </summary>
    public int PluginBusPort { get; set; }

    /// <summary>
    /// 默认插件目录根路径。
    /// </summary>
    public string PluginsDirectory => _appPaths.PluginDirectory;

    public PluginLoader(
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory,
        IPublisher publisher,
        ISubscriber subscriber,
        IAppPaths appPaths,
        IPluginRuntimeConfigProvider runtimeConfigProvider,
        PluginManifestRegistry manifestRegistry)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentNullException.ThrowIfNull(runtimeConfigProvider);
        ArgumentNullException.ThrowIfNull(manifestRegistry);

        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
        _publisher = publisher;
        _subscriber = subscriber;
        _appPaths = appPaths;
        _runtimeConfigProvider = runtimeConfigProvider;
        _manifestRegistry = manifestRegistry;
        _logger = loggerFactory.CreateLogger<PluginLoader>();
    }

    /// <summary>
    /// 获取当前所有活跃的插件实例（跨通道聚合）。
    /// </summary>
    public IReadOnlyList<IPlugin> GetActivePlugins()
    {
        return _nativeHosts.Values
            .Where(h => h.IsProcessAlive)
            .SelectMany(h => h.Plugins)
            .Concat(_processHosts.Values
                .Where(h => h.IsProcessAlive)
                .SelectMany(h => h.Plugins))
            .ToList();
    }

    /// <summary>
    /// 获取当前所有已连接的 MCP Server 实例。
    /// </summary>
    public IReadOnlyList<McpServerHost> GetActiveMcpServers()
    {
        return _mcpHosts.Values
            .Where(h => h.IsConnected)
            .ToList();
    }

    /// <summary>
    /// 获取当前已加载的插件目录名列表（不含完整路径，仅目录名）。
    /// </summary>
    public IReadOnlyList<string> GetLoadedPluginDirNames()
    {
        return _nativeHosts.Keys
            .Concat(_processHosts.Keys)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 获取当前已加载插件的目录和来源信息。
    /// </summary>
    public IReadOnlyList<LoadedPluginInfo> GetLoadedPluginInfos()
    {
        return _nativeHosts
            .SelectMany(pair => pair.Value.Plugins.Select(plugin => CreateLoadedPluginInfo(plugin, pair.Key)))
            .Concat(_processHosts.SelectMany(pair => pair.Value.Plugins.Select(plugin => CreateLoadedPluginInfo(plugin, pair.Key))))
            .ToList();
    }

    /// <summary>
    /// 按插件 ID 调用工具。工具名可以是完整工具名，也可以是插件元数据中的 shortName。
    /// </summary>
    public async Task<string?> InvokePluginToolAsync(
        string pluginId,
        string toolName,
        string argsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var plugin = GetActivePlugins()
            .OfType<IInvokablePlugin>()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
        {
            _logger.LogWarning("插件工具调用失败，插件不可用：{PluginId}.{ToolName}", pluginId, toolName);
            return null;
        }

        return await plugin.InvokeToolAsync(toolName, argsJson, cancellationToken);
    }

    /// <summary>
    /// 首次扫描插件目录并加载所有插件。
    /// </summary>
    public async Task ScanAndLoadAsync(CancellationToken cancellationToken = default)
    {
        var pluginDirectories = GetPluginRootDirectories();

        if (pluginDirectories.Count == 0)
        {
            _logger.LogDebug("未配置可用的插件目录，跳过扫描");
            return;
        }

        foreach (var pluginRootDirectory in pluginDirectories)
        {
            if (!Directory.Exists(pluginRootDirectory))
            {
                _logger.LogDebug("插件目录不存在，跳过扫描：{Dir}", pluginRootDirectory);
                continue;
            }

            foreach (var pluginDir in Directory.GetDirectories(pluginRootDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryLoadPluginDirectoryAsync(pluginDir, cancellationToken);
            }
        }

        _logger.LogInformation(
            "插件扫描完成，共扫描 {DirectoryCount} 个插件根目录，加载 {NativeCount} 个原生插件目录，{ProcessCount} 个进程插件目录，{PluginCount} 个插件实例",
            pluginDirectories.Count,
            _nativeHosts.Count,
            _processHosts.Count,
            GetActivePlugins().Count);
    }

    /// <summary>
    /// 启动 <see cref="FileSystemWatcher"/> 监视插件目录变化。
    /// </summary>
    public void StartWatching()
    {
        DisposeWatchers();

        var pluginDirectories = GetPluginRootDirectories(createIfNotExists: true);

        foreach (var pluginRootDirectory in pluginDirectories)
        {
            var watcher = new FileSystemWatcher(pluginRootDirectory)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Created += OnPluginDirectoryCreated;
            watcher.Deleted += OnPluginDirectoryDeleted;
            watcher.Renamed += OnPluginDirectoryRenamed;
            watcher.Error += OnPluginDirectoryWatcherError;

            _watchers.Add(watcher);
        }

        _logger.LogInformation("已启动插件目录监视：{Dirs}", string.Join(", ", pluginDirectories));
    }

    /// <summary>
    /// 卸载所有插件（不含 MCP 服务器）并停止目录监视。
    /// </summary>
    private void UnloadAllPlugins()
    {
        DisposeWatchers();

        foreach (var host in _nativeHosts.Values)
            host.Dispose();
        _nativeHosts.Clear();

        foreach (var host in _processHosts.Values)
            host.Dispose();
        _processHosts.Clear();

        _manifestRegistry.Clear();

        NotifyPluginsChanged();
    }

    /// <summary>
    /// 卸载所有插件后重新扫描加载，并重建目录监视。
    /// 用于工作目录切换时全量刷新。
    /// </summary>
    public async Task ReloadAllPluginsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("工作目录已变更，正在重新加载所有插件…");

        UnloadAllPlugins();
        await ScanAndLoadAsync(cancellationToken);
        StartWatching();
    }

    /// <summary>
    /// 卸载指定插件目录。
    /// </summary>
    public void UnloadPlugin(string pluginDirName)
    {
        foreach (var pluginRootDirectory in GetPluginRootDirectories())
        {
            var pluginPath = Path.Combine(pluginRootDirectory, pluginDirName);
            UnloadPluginByPath(pluginPath);
        }
    }

    /// <summary>
    /// 重新加载指定插件目录（先卸载再加载）。
    /// </summary>
    public async Task<bool> ReloadPluginAsync(string pluginDirName, CancellationToken cancellationToken = default)
    {
        var loaded = false;
        foreach (var pluginRootDirectory in GetPluginRootDirectories())
        {
            var fullPath = Path.Combine(pluginRootDirectory, pluginDirName);

            UnloadPluginByPath(fullPath);

            if (Directory.Exists(fullPath))
            {
                loaded |= await TryLoadPluginDirectoryAsync(fullPath, cancellationToken);
            }
        }

        return loaded;
    }

    /// <summary>
    /// 主动加载指定路径的插件目录。目录必须位于已配置插件根目录的直接子目录下。
    /// </summary>
    public async Task<bool> LoadPluginByPathAsync(string pluginPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);

        var normalizedPluginPath = NormalizeDirectoryPath(pluginPath);
        var allowedRoot = GetPluginRootDirectories()
            .Select(NormalizeDirectoryPath)
            .FirstOrDefault(root => IsDirectChildDirectory(root, normalizedPluginPath));

        if (allowedRoot is null)
        {
            _logger.LogWarning("拒绝加载插件目录，路径不在允许的插件根目录直接子级中：{PluginPath}", pluginPath);
            return false;
        }

        if (!Directory.Exists(normalizedPluginPath))
        {
            _logger.LogWarning("拒绝加载插件目录，目录不存在：{PluginPath}", normalizedPluginPath);
            return false;
        }

        UnloadPluginByPath(normalizedPluginPath);
        return await TryLoadPluginDirectoryAsync(normalizedPluginPath, cancellationToken);
    }

    /// <summary>
    /// 按插件 ID 重新加载已加载插件。
    /// </summary>
    public async Task<bool> ReloadPluginByIdAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var pluginInfo = GetLoadedPluginInfos()
            .FirstOrDefault(info => string.Equals(info.Plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(info.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (pluginInfo is null)
        {
            _logger.LogWarning("按 ID 重载插件失败，插件未加载：{PluginId}", pluginId);
            return false;
        }

        return await ReloadPluginAsync(pluginInfo.DirectoryName, cancellationToken);
    }

    // ──────── 私有方法 ────────

    /// <summary>
    /// 尝试加载单个插件目录。
    /// </summary>
    private async Task<bool> TryLoadPluginDirectoryAsync(string pluginDir, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(pluginDir, "plugin.json");

        if (!File.Exists(manifestPath))
        {
            _logger.LogDebug("跳过无 plugin.json 的目录：{Dir}", pluginDir);
            return false;
        }

        PluginManifest? manifest;

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            manifest = JsonSerializer.Deserialize(json, PluginManifestJsonContext.Default.PluginManifest)?.Normalize();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "plugin.json 格式错误：{Path}", manifestPath);
            return false;
        }

        if (manifest is null)
        {
            _logger.LogWarning("plugin.json 反序列化为 null：{Path}", manifestPath);
            return false;
        }

        if (!manifest.Validate(out var error))
        {
            _logger.LogWarning("plugin.json 校验失败（{Error}）：{Path}", error, manifestPath);
            return false;
        }

        switch (manifest.Runtime)
        {
            case PluginRuntime.Dotnet:
                _logger.LogWarning("旧 .NET 托管插件通道已废弃，跳过：{Id}", manifest.Id);
                return false;

            case PluginRuntime.Native:
                return await LoadNativePluginAsync(pluginDir, manifest, cancellationToken);

            case PluginRuntime.Process:
                return await LoadProcessPluginAsync(pluginDir, manifest, cancellationToken);

            default:
                _logger.LogWarning("未知的 runtime 类型：{Runtime}，跳过：{Id}", manifest.Runtime, manifest.Id);
                return false;
        }
    }

    /// <summary>
    /// 加载 process 通道的插件（直接启动 exe，进程隔离）。
    /// </summary>
    private async Task<bool> LoadProcessPluginAsync(
        string pluginDir,
        PluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var hostLogger = _loggerFactory.CreateLogger<ProcessPluginHost>();
        var host = new ProcessPluginHost(pluginDir, manifest, hostLogger, _runtimeConfigProvider);

        var dataDir = Path.Combine(pluginDir, "data");
        var context = new PluginContext(dataDir, _loggerFactory, _httpClientFactory, _appPaths, PluginBusPort);

        await host.LoadAsync(context, cancellationToken);

        if (host.Plugins.Count > 0)
        {
            _processHosts[pluginDir] = host;
            RegisterManifest(manifest);
            NotifyPluginsChanged();
            return true;
        }
        else
        {
            host.Dispose();
            return false;
        }
    }

    /// <summary>
    /// 加载 native 通道的插件（进程隔离）。
    /// </summary>
    private async Task<bool> LoadNativePluginAsync(
        string pluginDir,
        PluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var hostLogger = _loggerFactory.CreateLogger<NativePluginHost>();
        var host = new NativePluginHost(pluginDir, manifest, hostLogger, _runtimeConfigProvider);

        var dataDir = Path.Combine(pluginDir, "data");
        var context = new PluginContext(dataDir, _loggerFactory, _httpClientFactory, _appPaths, PluginBusPort);

        await host.LoadAsync(context, cancellationToken);

        if (host.Plugins.Count > 0)
        {
            _nativeHosts[pluginDir] = host;
            RegisterManifest(manifest);
            NotifyPluginsChanged();
            return true;
        }
        else
        {
            host.Dispose();
            return false;
        }
    }

    private void RegisterManifest(PluginManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id)) return;

        var pubOps = manifest.PublishedOps.Count > 0
            ? manifest.PublishedOps.Select(p => p.Op).ToArray()
            : Array.Empty<string>();
        var subOps = manifest.SubscribedOps.Count > 0
            ? manifest.SubscribedOps.Select(s => s.Op).ToArray()
            : Array.Empty<string>();
        _manifestRegistry.Register(manifest.Id, pubOps, subOps);
    }

    // ──────── MCP 服务器管理 ────────

    /// <summary>
    /// 从数据库加载所有已启用的 MCP 服务器并建立连接。
    /// </summary>
    public async Task LoadMcpServersAsync(McpServerService mcpService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mcpService);

        var servers = mcpService.GetEnabled();

        foreach (var config in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryConnectMcpServerAsync(config, cancellationToken);
        }

        _logger.LogInformation(
            "MCP 服务器加载完成，共连接 {Count}/{Total} 个",
            _mcpHosts.Values.Count(h => h.IsConnected), servers.Count);
    }

    /// <summary>
    /// 尝试连接单个 MCP 服务器。连接失败时仍加入 _mcpHosts，由 host 内部自动重连。
    /// </summary>
    private async Task TryConnectMcpServerAsync(McpServerEntity config, CancellationToken cancellationToken)
    {
        var host = new McpServerHost(config, _loggerFactory);
        host.ConnectionStateChanged += OnMcpConnectionStateChanged;
        _mcpHosts[config.Id] = host;

        try
        {
            await host.ConnectAsync(cancellationToken);
            NotifyPluginsChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MCP [{Name}] 初始连接失败，将自动重连：{Error}", config.Name, ex.Message);
            // host 保留在 _mcpHosts 中，内部将触发自动重连
            host.StartReconnecting();
        }
    }

    /// <summary>
    /// MCP 服务器连接状态变化回调：发布事件通知 + 刷新工具列表。
    /// </summary>
    private void OnMcpConnectionStateChanged(McpServerHost host)
    {
        _logger.LogInformation(
            "MCP [{Name}] 状态变化：{State}",
            host.Name,
            host.IsConnected ? $"已连接,[{host.Tools.Count}]个工具可用" : host.IsReconnecting ? "重连中" : "已断开");

        _publisher.Publish(Events.OnMcpConnectionStateChanged,
            new McpConnectionStateChangedArgs(host.Name, host.Id, host.IsConnected, host.IsReconnecting));

        NotifyPluginsChanged();
    }

    /// <summary>
    /// 添加并连接一个新的 MCP 服务器。
    /// </summary>
    public async Task AddMcpServerAsync(McpServerEntity config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        // 如果已存在，先移除旧的
        await RemoveMcpServerAsync(config.Id);
        await TryConnectMcpServerAsync(config, cancellationToken);
    }

    /// <summary>
    /// 断开并移除指定的 MCP 服务器。
    /// </summary>
    public async Task RemoveMcpServerAsync(string serverId)
    {
        if (_mcpHosts.TryRemove(serverId, out var host))
        {
            _logger.LogInformation("正在断开 MCP [{Name}]", host.Name);
            host.ConnectionStateChanged -= OnMcpConnectionStateChanged;
            await host.DisposeAsync();
            NotifyPluginsChanged();
        }
    }

    /// <summary>
    /// 重新连接所有已启用的 MCP 服务器。先断开所有，再重新连接。
    /// </summary>
    public async Task ReconnectAllMcpAsync(McpServerService mcpService, CancellationToken cancellationToken = default)
    {
        await DisconnectAllMcpAsync();
        await LoadMcpServersAsync(mcpService, cancellationToken);
    }

    /// <summary>
    /// 断开所有 MCP 服务器连接。
    /// </summary>
    public async Task DisconnectAllMcpAsync()
    {
        var ids = _mcpHosts.Keys.ToList();
        foreach (var id in ids)
            await RemoveMcpServerAsync(id);
    }

    /// <summary>
    /// 重新连接指定 ID 的 MCP 服务器。
    /// </summary>
    public async Task ReconnectMcpAsync(string serverId, McpServerService mcpService, CancellationToken cancellationToken = default)
    {
        await RemoveMcpServerAsync(serverId);
        var config = mcpService.GetById(serverId);
        if (config is not null && config.IsEnabled)
            await TryConnectMcpServerAsync(config, cancellationToken);
    }

    // ──────── FileSystemWatcher 回调 ────────
    private void OnPluginDirectoryCreated(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("检测到新插件目录：{Dir}", e.Name);

        // 延迟一小段时间等待文件写入完成
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await TryLoadPluginDirectoryAsync(e.FullPath, CancellationToken.None);
        });
    }

    private void OnPluginDirectoryDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("检测到插件目录删除：{Dir}", e.Name);

        if (!string.IsNullOrWhiteSpace(e.FullPath))
        {
            UnloadPluginByPath(e.FullPath);
        }
    }

    private void OnPluginDirectoryRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogInformation("检测到插件目录重命名：{OldName} → {NewName}", e.OldName, e.Name);

        if (!string.IsNullOrWhiteSpace(e.OldFullPath))
        {
            UnloadPluginByPath(e.OldFullPath);
        }

        if (e.Name is not null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await TryLoadPluginDirectoryAsync(e.FullPath, CancellationToken.None);
            });
        }
    }

    private void OnPluginDirectoryWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "插件目录监视发生错误");
    }

    /// <summary>
    /// 通过 EventHub 发布插件变更事件。
    /// </summary>
    private void NotifyPluginsChanged()
    {
        try
        {
            _publisher.Publish(Events.OnPluginsChanged, new VoiceSignalArgs());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发布 OnPluginsChanged 事件失败");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeWatchers();

        foreach (var nativeHost in _nativeHosts.Values)
        {
            nativeHost.Dispose();
        }

        _nativeHosts.Clear();

        foreach (var processHost in _processHosts.Values)
        {
            processHost.Dispose();
        }

        _processHosts.Clear();

        // MCP hosts 需要异步释放，在同步 Dispose 中尽力清理
        foreach (var mcpHost in _mcpHosts.Values)
        {
            mcpHost.ConnectionStateChanged -= OnMcpConnectionStateChanged;
            mcpHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _mcpHosts.Clear();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeWatchers();

        foreach (var nativeHost in _nativeHosts.Values)
        {
            nativeHost.Dispose();
        }

        _nativeHosts.Clear();

        foreach (var processHost in _processHosts.Values)
        {
            processHost.Dispose();
        }

        _processHosts.Clear();

        foreach (var mcpHost in _mcpHosts.Values)
        {
            mcpHost.ConnectionStateChanged -= OnMcpConnectionStateChanged;
            await mcpHost.DisposeAsync();
        }

        _mcpHosts.Clear();
    }

    private List<string> GetPluginRootDirectories(bool createIfNotExists = false)
    {
        var directories = new[]
        {
            _appPaths.UserPluginsDirectory
        }
        .Where(static path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        if (createIfNotExists)
        {
            foreach (var directory in directories)
            {
                Directory.CreateDirectory(directory);
            }
        }

        return directories;
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void UnloadPluginByPath(string pluginPath)
    {
        pluginPath = NormalizeDirectoryPath(pluginPath);
        var changed = false;
        string? unregisterId = null;

        if (_nativeHosts.TryRemove(pluginPath, out var nativeHost))
        {
            _logger.LogInformation("正在卸载原生插件目录：{Dir}", pluginPath);
            unregisterId = nativeHost.Manifest.Id;
            nativeHost.Dispose();
            changed = true;
        }

        if (_processHosts.TryRemove(pluginPath, out var processHost))
        {
            _logger.LogInformation("正在卸载进程插件目录：{Dir}", pluginPath);
            unregisterId ??= processHost.Manifest.Id;
            processHost.Dispose();
            changed = true;
        }

        if (changed)
        {
            if (!string.IsNullOrWhiteSpace(unregisterId))
            {
                _manifestRegistry.Unregister(unregisterId);
            }
            NotifyPluginsChanged();
        }
    }

    private LoadedPluginInfo CreateLoadedPluginInfo(IPlugin plugin, string pluginPath)
    {
        var directoryName = Path.GetFileName(pluginPath) ?? string.Empty;
        var manifest = _nativeHosts.TryGetValue(pluginPath, out var nativeHost)
            ? nativeHost.Manifest
            : _processHosts.TryGetValue(pluginPath, out var processHost)
                ? processHost.Manifest
                : throw new InvalidOperationException($"插件宿主不存在：{pluginPath}");

        return new LoadedPluginInfo(plugin, pluginPath, directoryName, PluginInstallScope.Global, manifest);
    }

    private static string NormalizeDirectoryPath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsDirectChildDirectory(string parentDirectory, string candidateDirectory)
    {
        var parent = NormalizeDirectoryPath(parentDirectory);
        var candidate = NormalizeDirectoryPath(candidateDirectory);
        var candidateParent = Path.GetDirectoryName(candidate);
        return !string.IsNullOrWhiteSpace(candidateParent) &&
            string.Equals(NormalizeDirectoryPath(candidateParent), parent, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryNormalizeDirectPluginDirectory(string pluginPath, out string normalizedPluginPath)
    {
        normalizedPluginPath = NormalizeDirectoryPath(pluginPath);
        var candidatePath = normalizedPluginPath;
        return GetPluginRootDirectories(createIfNotExists: true)
            .Any(root => IsDirectChildDirectory(root, candidatePath));
    }
}
