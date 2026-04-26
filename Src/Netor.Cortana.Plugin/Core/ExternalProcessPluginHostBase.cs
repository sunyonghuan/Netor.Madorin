using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Plugin;
using Netor.Cortana.Plugin.Native;
using ProcessDiag = System.Diagnostics.Process;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 进程隔离型插件宿主基类。
/// 封装子进程启动、stdin/stdout JSON 协议通信、生命周期管理。
/// 子类只需实现 <see cref="CreateProcessStartInfo"/> 提供启动参数。
/// </summary>
public abstract class ExternalProcessPluginHostBase : IDisposable
{
    private readonly ILogger _logger;
    private readonly List<IPlugin> _plugins = [];
    private ProcessDiag? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _disposed;

    /// <summary>已加载的插件实例列表。</summary>
    public IReadOnlyList<IPlugin> Plugins => _plugins;

    /// <summary>插件目录路径。</summary>
    public string PluginDirectory { get; }

    /// <summary>插件清单。</summary>
    public PluginManifest Manifest { get; }

    /// <summary>子进程是否正在运行。</summary>
    public bool IsProcessAlive => _process is { HasExited: false };

    protected ExternalProcessPluginHostBase(string pluginDirectory, PluginManifest manifest, ILogger logger)
    {
        PluginDirectory = pluginDirectory;
        Manifest = manifest;
        _logger = logger;
    }

    /// <summary>
    /// 构建子进程的启动信息。找不到可执行文件时应抛出 <see cref="FileNotFoundException"/>。
    /// </summary>
    protected abstract ProcessStartInfo CreateProcessStartInfo();

    /// <summary>
    /// 启动子进程并通过协议初始化插件。
    /// </summary>
    public async Task LoadAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var startInfo = CreateProcessStartInfo();
            StartProcess(startInfo);
            await InitializePluginAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "加载插件失败：{Dir}", PluginDirectory);
            KillProcess();
        }
    }

    /// <summary>
    /// 向子进程发送请求并等待响应。子进程已退出时返回错误响应。
    /// </summary>
    public async Task<NativeHostResponse> SendRequestAsync(NativeHostRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsProcessAlive)
            return new NativeHostResponse { Success = false, Error = "插件子进程已退出" };

        await _sendLock.WaitAsync(cancellationToken);

        try
        {
            var json = JsonSerializer.Serialize(request, NativePluginJsonContext.Default.NativeHostRequest);
            await _writer!.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _writer.FlushAsync(cancellationToken);

            var responseLine = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(30), cancellationToken);

            if (responseLine is null)
                return new NativeHostResponse { Success = false, Error = "插件子进程无响应或已退出" };

            var response = JsonSerializer.Deserialize(responseLine, NativePluginJsonContext.Default.NativeHostResponse);
            return response ?? new NativeHostResponse { Success = false, Error = "响应反序列化失败" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "与插件子进程通信失败");
            return new NativeHostResponse { Success = false, Error = ex.Message };
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsProcessAlive)
        {
            try
            {
                var request = new NativeHostRequest { Method = NativeHostMethods.Destroy };
                var json = JsonSerializer.Serialize(request, NativePluginJsonContext.Default.NativeHostRequest);
                _writer?.WriteLine(json);
                _writer?.Flush();
                _process?.WaitForExit(3000);
            }
            catch { }
        }

        KillProcess();
        _sendLock.Dispose();
        _plugins.Clear();
    }

    // ──────── 私有方法 ────────

    private void StartProcess(ProcessStartInfo startInfo)
    {
        _process = ProcessDiag.Start(startInfo)
            ?? throw new InvalidOperationException($"启动插件子进程失败：{startInfo.FileName}");

        _writer = _process.StandardInput;
        _writer.AutoFlush = false;
        _reader = _process.StandardOutput;

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _logger.LogWarning("[Plugin:{Id}] {Stderr}", Manifest.Id, e.Data);
        };
        _process.BeginErrorReadLine();

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
            _logger.LogWarning(
                "插件子进程已退出（插件 {Id}，退出码 {Code}）",
                Manifest.Id, _process?.ExitCode);

        _logger.LogDebug("插件子进程已启动（PID={Pid}，文件={File}）", _process.Id, startInfo.FileName);
    }

    private async Task InitializePluginAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var infoResponse = await SendRequestAsync(
            new NativeHostRequest { Method = NativeHostMethods.GetInfo }, cancellationToken);

        if (!infoResponse.Success || string.IsNullOrWhiteSpace(infoResponse.Data))
        {
            _logger.LogWarning("获取插件信息失败：{Error}", infoResponse.Error ?? "空数据");
            KillProcess();
            return;
        }

        NativePluginInfo? info;

        try
        {
            info = JsonSerializer.Deserialize(infoResponse.Data, NativePluginJsonContext.Default.NativePluginInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "插件信息 JSON 解析失败");
            KillProcess();
            return;
        }

        if (info is null || string.IsNullOrWhiteSpace(info.Id))
        {
            _logger.LogWarning("插件信息无效（缺少 id）");
            KillProcess();
            return;
        }

        var configJson = JsonSerializer.Serialize(new NativePluginInitConfig
        {
            DataDirectory = Path.Combine(PluginDirectory, "data"),
            WorkspaceDirectory = context.WorkspaceDirectory,
            WsPort = context.WsPort,
            PluginDirectory = PluginDirectory,
            Extensions = new NativePluginInitExtensions
            {
                ["chatWsEndpoint"] = CortanaWsEndpoints.BuildChatEndpoint(context.WsPort),
                ["conversationFeedEndpoint"] = CortanaWsEndpoints.BuildConversationFeedEndpoint(context.FeedPort > 0 ? context.FeedPort : context.WsPort),
                ["conversationFeedPort"] = (context.FeedPort > 0 ? context.FeedPort : context.WsPort).ToString(),
                ["conversationFeedProtocol"] = CortanaWsEndpoints.ConversationFeedProtocol,
                ["conversationFeedVersion"] = CortanaWsEndpoints.ConversationFeedVersion
            }
        }, NativePluginJsonContext.Default.NativePluginInitConfig);

        var initResponse = await SendRequestAsync(
            new NativeHostRequest { Method = NativeHostMethods.Init, Args = configJson }, cancellationToken);

        if (!initResponse.Success)
        {
            _logger.LogWarning("插件 [{Id}] 初始化失败：{Error}", info.Id, initResponse.Error);
            KillProcess();
            return;
        }

        var wrapper = new NativePluginWrapper(this, info);
        _plugins.Add(wrapper);

        _logger.LogInformation(
            "已加载插件：{Id} ({Name} v{Version})，工具数：{ToolCount}",
            info.Id, info.Name, info.Version, wrapper.Tools.Count);
    }

    private async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_reader is null) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await _reader.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("插件子进程响应超时（{Timeout}s）", timeout.TotalSeconds);
            return null;
        }
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _process?.Dispose();
            _process = null;
            _writer = null;
            _reader = null;
        }
    }
}
