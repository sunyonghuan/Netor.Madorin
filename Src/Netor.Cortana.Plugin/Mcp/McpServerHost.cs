using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Plugin.Mcp;

/// <summary>
/// 单个 MCP Server 的生命周期管理器。
/// 根据 <see cref="McpServerEntity"/> 的配置创建对应的传输层并建立连接，
/// 获取工具列表后供 <see cref="McpContextProvider"/> 使用。
/// </summary>
public sealed class McpServerHost : IAsyncDisposable
{
    private readonly McpServerEntity _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpServerHost> _logger;

    private McpClient? _client;
    private IList<McpClientTool>? _tools;
    private CancellationTokenSource? _reconnectCts;
    private bool _disposed;

    /// <summary>
    /// 重连退避间隔（秒）。依次为 10s、20s、30s、60s，之后固定 60s 循环。
    /// </summary>
    private static readonly int[] ReconnectDelaysSec = [10, 20, 30, 60];

    /// <summary>
    /// MCP 服务器的数据库 ID。
    /// </summary>
    public string Id => _config.Id;

    /// <summary>
    /// MCP 服务器的显示名称。
    /// </summary>
    public string Name => _config.Name;

    /// <summary>
    /// MCP 服务器的描述信息。
    /// </summary>
    public string Description => _config.Description;

    /// <summary>
    /// 当前连接获取到的工具列表。
    /// </summary>
    public IReadOnlyList<AITool> Tools => (IReadOnlyList<AITool>?)_tools ?? [];

    /// <summary>
    /// 连接是否已建立且存活。检查底层 McpClient.Completion 确保连接真正可用。
    /// </summary>
    public bool IsConnected => _client is not null && !_client.Completion.IsCompleted;

    /// <summary>
    /// 是否正在进行自动重连。
    /// </summary>
    public bool IsReconnecting => _reconnectCts is not null && !_reconnectCts.IsCancellationRequested;

    /// <summary>
    /// 连接状态发生变化时触发（连接成功、断线、重连放弃）。
    /// 外部（如 PluginLoader）可订阅此事件来刷新 UI。
    /// </summary>
    public event Action<McpServerHost>? ConnectionStateChanged;

    public McpServerHost(McpServerEntity config, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpServerHost>();
    }

    private const int ConnectTimeoutMs = 15_000;

    /// <summary>
    /// 外部调用：连接（或重连）MCP Server。
    /// 会先停止正在进行的自动重连，然后执行一次连接。
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        StopReconnecting();
        await ConnectCoreAsync(cancellationToken);
    }

    /// <summary>
    /// 核心连接逻辑：创建传输层、建立连接、获取工具列表、启动断线监听。
    /// 不操作 _reconnectCts，供外部 ConnectAsync 和内部重连循环共用。
    /// </summary>
    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        IClientTransport? transport = null;
        try
        {
            transport = _config.TransportType.ToLowerInvariant() switch
            {
                "stdio" => CreateStdioTransport(),
                "sse" or "streamable-http" => CreateHttpTransport(),
                _ => throw new InvalidOperationException($"不支持的传输类型：{_config.TransportType}")
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConnectTimeoutMs);

            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new() { Name = "Netor.Cortana", Version = "1.0.0", Title = "Netor.Cortana" } },
                _loggerFactory,
                timeoutCts.Token);

            transport = null; // McpClient 已接管 transport 生命周期

            _tools = await _client.ListToolsAsync(cancellationToken: timeoutCts.Token);

            _logger.LogInformation(
                "MCP Server [{Name}] 连接成功，获取到 {Count} 个工具",
                _config.Name, _tools.Count);

            _ = MonitorConnectionAsync(_client);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("MCP Server [{Name}] 连接超时（{TimeoutMs}ms）", _config.Name, ConnectTimeoutMs);
            await CleanupConnectionAsync(transport);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP Server [{Name}] 连接失败", _config.Name);
            await CleanupConnectionAsync(transport);
            throw;
        }
    }

    /// <summary>
    /// 清理连接资源：先释放 McpClient（它会释放已接管的 transport），再释放未被接管的 transport。
    /// </summary>
    private async Task CleanupConnectionAsync(IClientTransport? orphanedTransport)
    {
        if (_client is not null)
        {
            try { await _client.DisposeAsync(); } catch { }
            _client = null;
            _tools = null;
        }

        // 释放未被 McpClient 接管的 transport（CreateAsync 失败时）
        switch (orphanedTransport)
        {
            case IAsyncDisposable ad:
                try { await ad.DisposeAsync(); } catch { }
                break;
            case IDisposable d:
                try { d.Dispose(); } catch { }
                break;
        }
    }

    // ──────── 断线监听与自动重连 ────────

    /// <summary>
    /// 监听 McpClient.Completion，当连接断开时自动触发重连。
    /// </summary>
    private async Task MonitorConnectionAsync(McpClient client)
    {
        try
        {
            var completion = await client.Completion;

            // 已被主动 Dispose 或已启动新连接，无需重连
            if (_disposed || _client != client) return;

            if (completion.Exception is not null)
                _logger.LogWarning(completion.Exception,
                    "MCP Server [{Name}] 连接异常断开", _config.Name);
            else
                _logger.LogInformation("MCP Server [{Name}] 连接已关闭", _config.Name);

            // 清理旧连接
            await CleanupConnectionAsync(null);
            ConnectionStateChanged?.Invoke(this);

            // 启动自动重连
            await ReconnectWithBackoffAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP Server [{Name}] 连接监听异常", _config.Name);
        }
    }

    /// <summary>
    /// 无限重连，递增退避（10s → 20s → 30s → 60s 循环）。
    /// 仅通过 <see cref="StopReconnecting"/> 或 Dispose 终止。
    /// </summary>
    private async Task ReconnectWithBackoffAsync()
    {
        if (_disposed) return;

        _reconnectCts = new CancellationTokenSource();
        var ct = _reconnectCts.Token;

        for (int attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            var delaySec = ReconnectDelaysSec[Math.Min(attempt - 1, ReconnectDelaysSec.Length - 1)];

            _logger.LogInformation(
                "MCP [{Name}] 第 {Attempt} 次重连，{Delay}s 后开始",
                _config.Name, attempt, delaySec);

            try
            {
                await Task.Delay(delaySec * 1000, ct);
                await ConnectCoreAsync(ct);

                _logger.LogInformation("MCP [{Name}] 重连成功", _config.Name);
                ConnectionStateChanged?.Invoke(this);
                return; // MonitorConnectionAsync 已在 ConnectCoreAsync 内启动
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("MCP [{Name}] 重连已取消", _config.Name);
                return;
            }
            catch (Exception ex)
            {
                // 仅记录简要信息，不记录完整堆栈，避免日志膨胀
                _logger.LogWarning(
                    "MCP [{Name}] 第 {Attempt} 次重连失败：{Error}",
                    _config.Name, attempt, ex.Message);
            }
        }
    }

    /// <summary>
    /// 停止正在进行的自动重连。
    /// </summary>
    private void StopReconnecting()
    {
        if (_reconnectCts is not null)
        {
            try { _reconnectCts.Cancel(); } catch { }
            _reconnectCts.Dispose();
            _reconnectCts = null;
        }
    }

    /// <summary>
    /// 从外部启动自动重连循环（用于初始连接失败后触发重连）。
    /// </summary>
    public void StartReconnecting()
    {
        if (_disposed) return;
        _ = ReconnectWithBackoffAsync();
    }

    /// <summary>
    /// 重新获取工具列表（用于工具变更通知后刷新）。
    /// </summary>
    public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null) return;

        _tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);

        _logger.LogInformation(
            "MCP Server [{Name}] 工具列表已刷新，共 {Count} 个工具",
            _config.Name, _tools.Count);
    }

    private StdioClientTransport CreateStdioTransport()
    {
        if (string.IsNullOrWhiteSpace(_config.Command))
            throw new InvalidOperationException($"MCP Server [{_config.Name}] 的启动命令未配置");

        var options = new StdioClientTransportOptions
        {
            Command = _config.Command,
            Arguments = _config.Arguments,
            Name = _config.Name
        };

        // 合并环境变量
        if (_config.EnvironmentVariables.Count > 0)
        {
            options.EnvironmentVariables = new Dictionary<string, string?>(_config.EnvironmentVariables);
        }

        return new StdioClientTransport(options, _loggerFactory);
    }

    private HttpClientTransport CreateHttpTransport()
    {
        if (string.IsNullOrWhiteSpace(_config.Url))
            throw new InvalidOperationException($"MCP Server [{_config.Name}] 的 HTTP 地址未配置");

        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(_config.Url),
            Name = _config.Name,
            TransportMode = string.Equals(_config.TransportType, "sse", StringComparison.OrdinalIgnoreCase)
                ? HttpTransportMode.Sse
                : HttpTransportMode.StreamableHttp
        };

        // 添加认证头
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            options.AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {_config.ApiKey}"
            };
        }

        return new HttpClientTransport(options, _loggerFactory);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        StopReconnecting();

        if (_client is not null)
        {
            _logger.LogInformation("正在断开 MCP Server [{Name}]", _config.Name);

            try { await _client.DisposeAsync(); } catch { }
            _client = null;
            _tools = null;
        }
    }
}
