using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// MainWindow — 选择器（智能体 / 厂商 / 模型）。
/// </summary>
public partial class MainWindow
{
    // 当前选中的厂商 ID（用于厂商→模型联动）
    private string _currentProviderId = string.Empty;

    // 缓存数据列表（用于选择器项点击回调）
    private List<AgentEntity> _agents = [];
    private List<AiProviderEntity> _providers = [];
    private List<AiModelEntity> _models = [];

    // ──────── 选择器：智能体 ────────

    /// <summary>
    /// 加载智能体列表，填充选择器并设置标题栏和工具栏标签。
    /// </summary>
    private void LoadAgents()
    {
        try
        {
            var agentService = App.Services.GetRequiredService<AgentService>();
            _agents = agentService.GetAll();

            var defaultAgent = _agents.FirstOrDefault(a => a.IsDefault) ?? _agents.FirstOrDefault();
            RefreshAgentDisplay(defaultAgent);
            FillAgentSelector(defaultAgent?.Id);
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "加载智能体列表失败");
        }
    }

    private void RefreshAgentDisplay(AgentEntity? agent = null)
    {
        if (agent is null)
        {
            var agentService = App.Services.GetRequiredService<AgentService>();
            agent = agentService.GetAll().FirstOrDefault(a => a.IsDefault);
        }

        var name = agent?.Name ?? "默认助手";
        //AgentLabel.Text = name;
        ToolbarAgentLabel.Text = name;
    }

    private void RefreshAgentDisplay()
    {
        RefreshAgentDisplay(null);
    }

    private void FillAgentSelector(string? activeId)
    {
        AgentSelectorList.Items.Clear();
        foreach (var agent in _agents)
        {
            var btn = new Button
            {
                Classes = { agent.Id == activeId ? "selector-item-active" : "selector-item" },
                Tag = agent.Id,
                Content = new TextBlock
                {
                    Text = agent.Name,
                    FontSize = 12,
                    Foreground = agent.Id == activeId
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnAgentItemClick;
            AgentSelectorList.Items.Add(btn);
        }
    }

    /// <summary>智能体选择器按钮点击。</summary>
    private void OnAgentSelectorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AgentSelectorPopup.IsOpen = !AgentSelectorPopup.IsOpen;
    }

    /// <summary>智能体列表项点击。</summary>
    private void OnAgentItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string agentId)
        {
            AgentSelectorPopup.IsOpen = false;

            var agent = _agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null) return;

            RefreshAgentDisplay(agent);
            FillAgentSelector(agentId);

            var chatService = App.Services.GetRequiredService<AiChatHostedService>();
            chatService.ChangeAgent(agentId);
        }
    }

    // ──────── 选择器：厂商 ────────

    /// <summary>
    /// 加载厂商列表，填充选择器并触发模型联动加载。
    /// </summary>
    private void LoadProviders()
    {
        try
        {
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            _providers = providerService.GetAll();

            var defaultProvider = _providers.FirstOrDefault(p => p.IsDefault) ?? _providers.FirstOrDefault();
            _currentProviderId = defaultProvider?.Id ?? string.Empty;

            RefreshProviderDisplay(defaultProvider);
            FillProviderSelector(defaultProvider?.Id);

            // 联动加载该厂商的模型
            if (!string.IsNullOrEmpty(_currentProviderId))
            {
                LoadModels(_currentProviderId);
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "加载厂商列表失败");
        }
    }

    private void RefreshProviderDisplay(AiProviderEntity? provider = null)
    {
        if (provider is null)
        {
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            provider = providerService.GetAll().FirstOrDefault(p => p.IsDefault);
        }

        ToolbarProviderLabel.Text = provider?.Name ?? "厂商";
    }

    private void FillProviderSelector(string? activeId)
    {
        ProviderList.Items.Clear();
        foreach (var provider in _providers)
        {
            var btn = new Button
            {
                Classes = { provider.Id == activeId ? "selector-item-active" : "selector-item" },
                Tag = provider.Id,
                Content = new TextBlock
                {
                    Text = provider.Name,
                    FontSize = 12,
                    Foreground = provider.Id == activeId
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnProviderItemClick;
            ProviderList.Items.Add(btn);
        }
    }

    /// <summary>厂商选择器按钮点击。</summary>
    private void OnProviderSelectorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ProviderPopup.IsOpen = !ProviderPopup.IsOpen;
    }

    /// <summary>厂商列表项点击 → 切换厂商并联动加载模型。</summary>
    private void OnProviderItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string providerId)
        {
            ProviderPopup.IsOpen = false;

            var provider = _providers.FirstOrDefault(p => p.Id == providerId);
            if (provider is null) return;

            _currentProviderId = providerId;
            RefreshProviderDisplay(provider);
            FillProviderSelector(providerId);

            // 持久化为默认厂商并通知 AiChatService
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            providerService.SetDefault(providerId);

            var chatService = App.Services.GetRequiredService<AiChatHostedService>();
            chatService.ChangeProvider(providerId);
            LoadModels(providerId);
        }
    }

    // ──────── 选择器：模型 ────────

    /// <summary>
    /// 根据厂商 ID 加载模型列表并填充选择器。
    /// </summary>
    private void LoadModels(string providerId)
    {
        try
        {
            var modelService = App.Services.GetRequiredService<AiModelService>();
            _models = modelService.GetByProviderId(providerId);

            // 若本地无模型，尝试从远程拉取
            if (_models.Count == 0)
            {
                var providerService = App.Services.GetRequiredService<AiProviderService>();
                var provider = providerService.GetById(providerId);
                if (provider is not null)
                {
                    var fetcher = App.Services.GetRequiredService<AiModelFetcherService>();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await fetcher.FetchAndSaveModelsAsync(provider);
                            Dispatcher.UIThread.Post(() => LoadModels(providerId));
                        }
                        catch (Exception ex)
                        {
                            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
                            logger.LogWarning(ex, "远程拉取模型列表失败");
                        }
                    });
                }
                return;
            }

            var defaultModel = _models.FirstOrDefault(m => m.IsDefault) ?? _models.FirstOrDefault();
            RefreshModelDisplay(defaultModel);
            FillModelSelector(defaultModel?.Id);

            // 通知 AiChatService 当前模型
            if (defaultModel is not null)
            {
                var chatService = App.Services.GetRequiredService<AiChatHostedService>();
                chatService.ChangeModel(defaultModel.Id);
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "加载模型列表失败");
        }
    }

    private void RefreshModelDisplay(AiModelEntity? model = null)
    {
        if (model is null)
        {
            var providerService = App.Services.GetRequiredService<AiProviderService>();
            var modelService = App.Services.GetRequiredService<AiModelService>();

            foreach (var provider in providerService.GetAll())
            {
                model = modelService.GetByProviderId(provider.Id)
                    .FirstOrDefault(m => m.IsDefault);
                if (model is not null) break;
            }
        }

        var displayName = model?.DisplayName ?? "未选择模型";
        //ModelLabel.Text = displayName;
        ToolbarModelLabel.Text = displayName;
    }

    private void FillModelSelector(string? activeId, string? filter = null)
    {
        ModelList.Items.Clear();
        foreach (var model in _models)
        {
            // 搜索过滤：匹配 DisplayName 或 Name
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var displayName = model.DisplayName ?? model.Name ?? string.Empty;
                var name = model.Name ?? string.Empty;
                if (!displayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var btn = new Button
            {
                Classes = { model.Id == activeId ? "selector-item-active" : "selector-item" },
                Tag = model.Id,
                Content = new TextBlock
                {
                    Text = model.DisplayName,
                    FontSize = 12,
                    Foreground = model.Id == activeId
                        ? new SolidColorBrush(Color.Parse("#007acc"))
                        : new SolidColorBrush(Color.Parse("#cccccc")),
                },
            };
            btn.Click += OnModelItemClick;
            ModelList.Items.Add(btn);
        }
    }

    /// <summary>模型搜索框文本变更 → 动态过滤模型列表。</summary>
    private void OnModelSearchTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        var keyword = ModelSearchBox.Text?.Trim();
        var activeModel = _models.FirstOrDefault(m =>
        {
            var display = m.DisplayName ?? m.Name ?? string.Empty;
            return display == (ToolbarModelLabel.Text ?? string.Empty);
        });
        FillModelSelector(activeModel?.Id, keyword);
    }

    /// <summary>模型选择器按钮点击。</summary>
    private void OnModelSelectorClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!ModelPopup.IsOpen)
        {
            ModelSearchBox.Text = string.Empty;
        }
        ModelPopup.IsOpen = !ModelPopup.IsOpen;
    }

    /// <summary>模型列表项点击。</summary>
    private void OnModelItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string modelId)
        {
            ModelPopup.IsOpen = false;

            var model = _models.FirstOrDefault(m => m.Id == modelId);
            if (model is null) return;

            RefreshModelDisplay(model);
            FillModelSelector(modelId);

            // 持久化为默认模型并通知 AiChatService
            var modelService = App.Services.GetRequiredService<AiModelService>();
            modelService.SetDefault(modelId);

            var chatService = App.Services.GetRequiredService<AiChatHostedService>();
            chatService.ChangeModel(modelId);
        }
    }
}
