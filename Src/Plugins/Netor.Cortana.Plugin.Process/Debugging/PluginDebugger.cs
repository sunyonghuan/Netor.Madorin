using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

using Netor.Cortana.Plugin.Process.Protocol;
using Netor.Cortana.Plugin.Process.Settings;

namespace Netor.Cortana.Plugin.Process.Debugging;

/// <summary>
/// Process 插件的强类型调试器基类。
/// <para>
/// <b>测试/调试时使用</b>：Generator 会为每个 <c>[Plugin]</c> 类生成一个继承自此类的
/// <c>{PluginName}Debugger</c>，暴露所有 <c>[Tool]</c> 方法的强类型调用。
/// 用户无需构造 JSON，直接调方法即可。
/// </para>
/// <para>
/// 内部通过内存管道（<see cref="Pipe"/>）与 Generator 生成的 <c>Program.RunPluginAsync</c>
/// 通信，完全不启动真实进程。协议与生产环境一致，因此通过 Debugger 的测试
/// 覆盖了完整的消息循环路径。
/// </para>
/// </summary>
public abstract class PluginDebugger : IAsyncDisposable
{
    private readonly Pipe _hostToPlugin = new();
    private readonly Pipe _pluginToHost = new();
    private readonly TextReader _pluginInput;
    private readonly TextWriter _pluginOutput;
    private readonly StreamWriter _hostWriter;
    private readonly StreamReader _hostReader;
    private readonly Task _pluginTask;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// 启动消息循环。子类构造时传入 Generator 生成的入口方法
    /// （签名 <c>Task RunPluginAsync(TextReader input, TextWriter output)</c>）。
    /// </summary>
    protected PluginDebugger(Func<TextReader, TextWriter, Task> pluginRunner)
    {
        ArgumentNullException.ThrowIfNull(pluginRunner);

        // 插件侧：从 hostToPlugin 读，写入 pluginToHost
        _pluginInput = new StreamReader(_hostToPlugin.Reader.AsStream(), Encoding.UTF8);
        _pluginOutput = new StreamWriter(_pluginToHost.Writer.AsStream(), Encoding.UTF8) { AutoFlush = true };

        // 宿主侧（测试代码）：写入 hostToPlugin，从 pluginToHost 读
        _hostWriter = new StreamWriter(_hostToPlugin.Writer.AsStream(), Encoding.UTF8) { AutoFlush = true };
        _hostReader = new StreamReader(_pluginToHost.Reader.AsStream(), Encoding.UTF8);

        // 启动插件循环（后台运行，直到收到 destroy 或 stdin 关闭）
        _pluginTask = Task.Run(async () =>
        {
            try
            {
                await pluginRunner(_pluginInput, _pluginOutput).ConfigureAwait(false);
            }
            finally
            {
                // 插件退出后关闭输出管道，让宿主侧 ReadLine 返回 null
                await _pluginToHost.Writer.CompleteAsync().ConfigureAwait(false);
            }
        });
    }

    /// <summary>发送 <c>get_info</c>，返回插件元数据。</summary>
    public async Task<PluginInfoData> GetInfoAsync()
    {
        var resp = await SendRequestAsync(new HostRequest { Method = "get_info" }).ConfigureAwait(false);
        if (!resp.Success)
            throw new InvalidOperationException($"get_info 失败: {resp.Error}");

        if (string.IsNullOrEmpty(resp.Data))
            throw new InvalidOperationException("get_info 返回数据为空");

        return JsonSerializer.Deserialize(resp.Data, ProcessProtocolJsonContext.Default.PluginInfoData)
            ?? throw new InvalidOperationException("get_info 响应反序列化失败");
    }

    /// <summary>
    /// 发送 <c>init</c>，注入宿主配置。
    /// 不调用则插件中任何依赖 <c>PluginSettings</c> 的工具都会抛异常。
    /// </summary>
    public Task InitAsync(
        string? dataDirectory = null,
        string? workspaceDirectory = null,
        string? pluginDirectory = null,
        int wsPort = 0)
    {
        var config = new InitConfig
        {
            DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "cortana-debugger"),
            WorkspaceDirectory = workspaceDirectory ?? Directory.GetCurrentDirectory(),
            PluginDirectory = pluginDirectory ?? AppContext.BaseDirectory,
            WsPort = wsPort,
            Extensions = new InitExtensions
            {
                ChatWsEndpoint = $"ws://localhost:{wsPort}/ws/",
                ConversationFeedEndpoint = $"ws://localhost:{wsPort}/internal/conversation-feed/",
                ConversationFeedProtocol = "conversation-feed",
                ConversationFeedVersion = "1.0.0"
            }
        };
        return InitAsync(config);
    }

    /// <summary>发送 <c>init</c>，使用完整配置对象。</summary>
    public async Task InitAsync(InitConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var argsJson = JsonSerializer.Serialize(config, ProcessProtocolJsonContext.Default.InitConfig);
        var resp = await SendRequestAsync(new HostRequest { Method = "init", Args = argsJson })
            .ConfigureAwait(false);

        if (!resp.Success)
            throw new InvalidOperationException($"init 失败: {resp.Error}");
    }

    /// <summary>
    /// 由 Generator 生成的强类型方法内部调用。
    /// 用户不直接调用此方法。
    /// </summary>
    protected async Task<string> InvokeAsync(string toolName, DebuggerArgs args)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);
        ArgumentNullException.ThrowIfNull(args);

        var resp = await SendRequestAsync(new HostRequest
        {
            Method = "invoke",
            ToolName = toolName,
            Args = args.ToJson()
        }).ConfigureAwait(false);

        if (!resp.Success)
            throw new InvalidOperationException($"工具 {toolName} 执行失败: {resp.Error}");

        return resp.Data ?? string.Empty;
    }

    /// <summary>串行发送一条请求，等待并解析对应响应。</summary>
    private async Task<HostResponse> SendRequestAsync(HostRequest request)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PluginDebugger));

        await _ioLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(request, ProcessProtocolJsonContext.Default.HostRequest);
            await _hostWriter.WriteLineAsync(json).ConfigureAwait(false);
            await _hostWriter.FlushAsync().ConfigureAwait(false);

            var line = await _hostReader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
                throw new InvalidOperationException("插件已关闭输出流，无法读取响应");

            return JsonSerializer.Deserialize(line, ProcessProtocolJsonContext.Default.HostResponse)
                ?? throw new InvalidOperationException("响应反序列化失败");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// 发送 <c>destroy</c>，等待插件循环结束并释放所有资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            // 尽力发送 destroy，插件可能已经退出
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var json = JsonSerializer.Serialize(
                    new HostRequest { Method = "destroy" },
                    ProcessProtocolJsonContext.Default.HostRequest);
                await _hostWriter.WriteLineAsync(json).ConfigureAwait(false);
                await _hostWriter.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _ioLock.Release();
            }
        }
        catch
        {
            // 已关闭或写入失败，忽略
        }

        // 关闭宿主侧写入管道，让插件的 ReadLineAsync 在处理完 destroy 后返回 null
        try
        {
            await _hostToPlugin.Writer.CompleteAsync().ConfigureAwait(false);
        }
        catch { /* 忽略 */ }

        // 等待插件循环退出（最多 5 秒）
        try
        {
            await _pluginTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch { /* 超时或失败，继续清理 */ }

        _hostWriter.Dispose();
        _hostReader.Dispose();
        _pluginInput.Dispose();
        _pluginOutput.Dispose();
        _ioLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
