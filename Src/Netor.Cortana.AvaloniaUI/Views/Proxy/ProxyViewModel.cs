using System.ComponentModel;
using System.Runtime.CompilerServices;

using Avalonia.Threading;

using Netor.Cortana.Entitys.Proxy;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Networks.Proxy;

namespace Netor.Cortana.AvaloniaUI.Views.Proxy;

/// <summary>
/// Ollama Proxy 小窗口 ViewModel。
/// 只负责协议桥接配置和监控：本地选择厂商，外部请求传入模型。
/// 状态文字完全由 StateChanged 事件回调驱动，开关只管启停。
/// </summary>
public sealed class ProxyViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SystemSettingsService _settings;
    private readonly AiProviderService _providerService;
    private readonly OllamaProxyServerService _proxyServer;
    private readonly ProxyUsageTracker _usageTracker;

    private bool _enabled;
    private string _host = "localhost";
    private int _port = 11434;
    private string _providerId = string.Empty;
    private string _version = "0.21.2";
    private bool _allowLan;
    private int _maxConcurrentRequests = 2;
    private string _statusText = "未启动";
    private string _url = "http://localhost:11434";
    private string _lastError = string.Empty;
    private AiProxyUsageSnapshot _usage = new(0, 0, 128000, 0, 0, 0, 0, 0, string.Empty, string.Empty);

    public ProxyViewModel(
        SystemSettingsService settings,
        AiProviderService providerService,
        OllamaProxyServerService proxyServer,
        ProxyUsageTracker usageTracker)
    {
        _settings = settings;
        _providerService = providerService;
        _proxyServer = proxyServer;
        _usageTracker = usageTracker;

        _proxyServer.StateChanged += OnProxyStateChanged;
        _usageTracker.UsageChanged += OnUsageChanged;

        Load();
        RefreshRuntimeState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public List<OptionItem> Providers { get; private set; } = [];

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string Host
    {
        get => _host;
        set
        {
            if (SetField(ref _host, string.IsNullOrWhiteSpace(value) ? "localhost" : value.Trim()))
            {
                RefreshUrlPreview();
            }
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            var normalized = value <= 0 || value > 65535 ? 11434 : value;
            if (SetField(ref _port, normalized)) RefreshUrlPreview();
        }
    }

    public string ProviderId
    {
        get => _providerId;
        set => SetField(ref _providerId, value ?? string.Empty);
    }

    public string Version
    {
        get => _version;
        set => SetField(ref _version, string.IsNullOrWhiteSpace(value) ? "0.21.2" : value.Trim());
    }

    public bool AllowLan
    {
        get => _allowLan;
        set => SetField(ref _allowLan, value);
    }

    public int MaxConcurrentRequests
    {
        get => _maxConcurrentRequests;
        set => SetField(ref _maxConcurrentRequests, value <= 0 ? 2 : value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string Url
    {
        get => _url;
        private set => SetField(ref _url, value);
    }

    public string LastError
    {
        get => _lastError;
        private set => SetField(ref _lastError, value);
    }

    public long LastInputTokens => _usage.LastInputTokens;
    public long MaxContextTokens => _usage.MaxContextTokens;
    public double ContextUsagePercent => Math.Round(_usage.ContextUsageRatio * 100, 1);
    public long TotalRequests => _usage.TotalRequests;
    public long ActiveRequests => _usage.ActiveRequests;
    public long SucceededRequests => _usage.SucceededRequests;
    public long FailedRequests => _usage.FailedRequests;
    public string LastModelName => string.IsNullOrWhiteSpace(_usage.LastModelName) ? "等待请求" : _usage.LastModelName;

    public void Load()
    {
        _settings.EnsureOllamaProxySettings();

        Providers = [new OptionItem("", "系统默认厂商")];
        Providers.AddRange(_providerService.GetAll().Select(p => new OptionItem(p.Id, p.Name)));

        _enabled = _settings.GetValue("Proxy.Ollama.Enabled", false);
        _host = _settings.GetValue("Proxy.Ollama.Host", "localhost");
        _port = _settings.GetValue<int>("Proxy.Ollama.Port", 11434);
        _providerId = _settings.GetValue("Proxy.Ollama.ProviderId", string.Empty);
        _version = _settings.GetValue("Proxy.Ollama.Version", "0.21.2");
        _allowLan = _settings.GetValue("Proxy.Ollama.AllowLan", false);
        _maxConcurrentRequests = _settings.GetValue<int>("Proxy.Ollama.MaxConcurrentRequests", 2);

        RefreshUrlPreview();
        RaiseAll();
    }

    /// <summary>
    /// 保存配置并启停服务。开关只管启停，状态文字由 StateChanged 事件回调驱动。
    /// </summary>
    public async Task SaveAndApplyAsync(CancellationToken cancellationToken = default)
    {
        _settings.SaveBatch([
            ("Proxy.Ollama.Enabled", Enabled.ToString().ToLowerInvariant()),
            ("Proxy.Ollama.Host", Host),
            ("Proxy.Ollama.Port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Proxy.Ollama.Mode", "ModelOnly"),
            ("Proxy.Ollama.ProviderId", ProviderId),
            ("Proxy.Ollama.ModelId", string.Empty),
            ("Proxy.Ollama.AgentId", string.Empty),
            ("Proxy.Ollama.Version", Version),
            ("Proxy.Ollama.AllowLan", AllowLan.ToString().ToLowerInvariant()),
            ("Proxy.Ollama.MaxConcurrentRequests", MaxConcurrentRequests.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ]);

        // 先停再按需启动，StateChanged 事件会自动回调 RefreshRuntimeState 更新状态文字
        await _proxyServer.StopProxyAsync(cancellationToken);

        if (Enabled)
        {
            await _proxyServer.StartProxyAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 从 server 快照刷新运行时状态到 UI。由 StateChanged 事件回调触发。
    /// </summary>
    public void RefreshRuntimeState()
    {
        var state = _proxyServer.GetStateSnapshot();
        StatusText = ToStatusText(state.Status);
        Url = state.Url;
        LastError = string.IsNullOrWhiteSpace(state.LastError) ? _usageTracker.LastError : state.LastError;
        RefreshUsage();
    }

    private void RefreshUrlPreview()
    {
        Url = $"http://{(string.IsNullOrWhiteSpace(Host) ? "localhost" : Host)}:{Port}";
    }

    private void OnProxyStateChanged()
    {
        Dispatcher.UIThread.Post(RefreshRuntimeState);
    }

    private void OnUsageChanged()
    {
        Dispatcher.UIThread.Post(RefreshUsage);
    }

    private void RefreshUsage()
    {
        _usage = _usageTracker.GetSnapshot();
        OnPropertyChanged(nameof(LastInputTokens));
        OnPropertyChanged(nameof(MaxContextTokens));
        OnPropertyChanged(nameof(ContextUsagePercent));
        OnPropertyChanged(nameof(TotalRequests));
        OnPropertyChanged(nameof(ActiveRequests));
        OnPropertyChanged(nameof(SucceededRequests));
        OnPropertyChanged(nameof(FailedRequests));
        OnPropertyChanged(nameof(LastModelName));
        if (!string.IsNullOrWhiteSpace(_usage.LastError)) LastError = _usage.LastError;
    }

    private static string ToStatusText(OllamaProxyStatus status) => status switch
    {
        OllamaProxyStatus.Disabled => "未启用",
        OllamaProxyStatus.Starting => "启动中",
        OllamaProxyStatus.Running => "运行中",
        OllamaProxyStatus.Stopping => "停止中",
        OllamaProxyStatus.PortInUse => "端口占用",
        OllamaProxyStatus.AccessDenied => "权限不足",
        OllamaProxyStatus.BackendUnavailable => "后端不可用",
        OllamaProxyStatus.Failed => "启动失败",
        _ => "已停止"
    };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Providers));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(ProviderId));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(AllowLan));
        OnPropertyChanged(nameof(MaxConcurrentRequests));
    }

    public void Dispose()
    {
        _proxyServer.StateChanged -= OnProxyStateChanged;
        _usageTracker.UsageChanged -= OnUsageChanged;
    }
}

/// <summary>
/// UI 下拉选项。
/// </summary>
public sealed record OptionItem(string Id, string Name)
{
    public override string ToString() => Name;
}
