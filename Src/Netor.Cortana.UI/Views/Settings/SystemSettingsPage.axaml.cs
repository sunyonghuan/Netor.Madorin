using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Plugin.Voice;
using Netor.Cortana.Voice;

using System.Net;
using System.Net.Sockets;

namespace Netor.Cortana.UI.Views.Settings;

public partial class SystemSettingsPage : UserControl
{
    private SystemSettingsService SettingsService => App.Services.GetRequiredService<SystemSettingsService>();

    // 保存每个设置项的输入控件，key = entity.Id
    private readonly Dictionary<string, Control> _editors = [];
    private DispatcherTimer? _toastTimer;

    public SystemSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        SettingsService.DeleteSetting("Voice.WakeWordEnabled");
        SettingsService.EnsurePlatformSettings();

        SettingsService.EnsureSetting("AI.Trace.Enabled",
            group: "调试", displayName: "AI 全量调试日志",
            description: "记录 AI 请求、流式更新、响应和异常的完整调试日志。发布版默认关闭，开启后可用于排查工具调用与上下文问题。",
            defaultValue: "false", valueType: "bool", sortOrder: 0);
        SettingsService.EnsureSetting("AI.Provider.Kimi.MaxTools",
            group: "AI", displayName: "Kimi 最大工具数量",
            description: "Kimi 当前工具数量上限。默认 128；设置为 0 表示不限制，用于兼容 Kimi 后续放开限制的情况。环境变量 CORTANA_KIMI_MAX_TOOLS 优先于此设置。",
            defaultValue: "128", valueType: "int", sortOrder: 20);

        SettingsService.EnsureSetting("Logging.File.MinimumLevel",
            group: "日志", displayName: "文件日志最小级别",
            description: "写入 app 日志文件的最小日志级别。选择 Warning 时会记录 Warning、Error、Critical；选择 Information 时会额外记录普通运行信息。修改后重启应用生效。",
            defaultValue: "Warning", valueType: "logLevel", sortOrder: 0);

        SettingsService.EnsureSetting("Agent.DefaultName",
            group: "Agent", displayName: "默认智能体",
            description: "启动新会话或未显式选择智能体时使用的 Agent。",
            defaultValue: "default", valueType: "agent", sortOrder: 0);

        SettingsContainer.Children.Clear();
        _editors.Clear();

        var allSettings = SettingsService.GetAll()
            .Where(s => !s.Id.StartsWith("Plugin:", StringComparison.Ordinal))
            .Where(s => !string.Equals(s.Group, "插件配置", StringComparison.Ordinal));
        var groups = allSettings.GroupBy(s => s.Group).OrderBy(g => g.Min(s => s.SortOrder));

        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Key))
                continue;

            // 分组标题
            var header = new TextBlock
            {
                Text = group.Key,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = (IBrush)this.FindResource("TextBrush")!,
                Margin = new Thickness(0, 8, 0, 4),
            };
            SettingsContainer.Children.Add(header);

            // 分组内的设置项
            foreach (var entity in group.OrderBy(s => s.SortOrder))
            {
                var row = BuildSettingRow(entity);
                SettingsContainer.Children.Add(row);
            }

            // 分隔线
            SettingsContainer.Children.Add(new Border
            {
                Height = 1,
                Background = (IBrush)this.FindResource("Surface1Brush")!,
                Margin = new Thickness(0, 8),
            });
        }
    }

    private Border BuildSettingRow(SystemSettingsEntity entity)
    {
        if (entity.Id == "Platform.BaseUrl")
        {
            return BuildPlatformBaseUrlRow(entity);
        }

        var label = new TextBlock
        {
            Text = entity.DisplayName,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var hint = new TextBlock
        {
            Text = entity.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        Control editor = entity.ValueType switch
        {
            "bool" => new ToggleSwitch
            {
                IsChecked = bool.TryParse(entity.Value, out var b) && b,
                OnContent = "开",
                OffContent = "关",
            },
            "int" => new NumericUpDown
            {
                Value = int.TryParse(entity.Value, out var i) ? i : 0,
                Minimum = 0,
                FormatString = "0",
                Width = 150,
                Background = (IBrush)this.FindResource("Surface0Brush")!,
                Foreground = (IBrush)this.FindResource("TextBrush")!,
            },
            "float" => new NumericUpDown
            {
                Value = double.TryParse(entity.Value, out var f) ? (decimal)f : 0,
                Increment = 0.1m,
                FormatString = "0.##",
                Width = 150,
                Background = (IBrush)this.FindResource("Surface0Brush")!,
                Foreground = (IBrush)this.FindResource("TextBrush")!,
            },
            "model" => BuildModelSelector(entity.Value),
            var _ when entity.Id == "Agent.DefaultName" => BuildAgentSelector(entity.Value),
            "logLevel" => BuildLogLevelSelector(entity.Value),
            var valueType when valueType.StartsWith("voicePlugin:", StringComparison.OrdinalIgnoreCase) =>
                BuildVoicePluginSelector(valueType, entity.Value),
            _ => new TextBox
            {
                Text = entity.Value,
                Width = 250,
                Classes = { "form-input" },
            },
        };

        _editors[entity.Id] = editor;

        var editorRow = new DockPanel { Margin = new Thickness(0, 4) };
        DockPanel.SetDock(editor, Dock.Right);
        editorRow.Children.Add(editor);
        editorRow.Children.Add(label);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(editorRow);
        stack.Children.Add(hint);

        return new Border
        {
            Background = (IBrush)this.FindResource("Surface0Brush")!,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            Child = stack,
        };
    }

    private Border BuildPlatformBaseUrlRow(SystemSettingsEntity entity)
    {
        var label = new TextBlock
        {
            Text = entity.DisplayName,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var hint = new TextBlock
        {
            Text = entity.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var input = new TextBox
        {
            Text = entity.Value,
            MinWidth = 300,
            Width = 360,
            Classes = { "form-input" },
        };
        _editors[entity.Id] = input;

        var result = new TextBlock
        {
            Text = "可先测试连接，确认 API 服务与地址可用。",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var localButton = new Button { Content = "本地开发", Classes = { "btn-secondary" } };
        localButton.Click += (_, _) =>
        {
            input.Text = AppBranding.LocalPlatformApiBaseUrl;
            result.Text = "已填入本地开发地址，保存后生效。";
        };

        var productionButton = new Button { Content = "线上平台", Classes = { "btn-secondary" } };
        productionButton.Click += (_, _) =>
        {
            input.Text = AppBranding.ProductionPlatformApiBaseUrl;
            result.Text = "已填入线上平台地址，保存后生效。";
        };

        var customButton = new Button { Content = "自定义", Classes = { "btn-secondary" } };
        customButton.Click += (_, _) =>
        {
            input.Focus();
            result.Text = "请输入自定义平台 API 地址后测试连接。";
        };

        var testButton = new Button { Content = "测试连接", Classes = { "btn-primary" } };
        testButton.Click += async (_, _) => await TestPlatformConnectionAsync(input, result);

        var editorRow = new DockPanel { Margin = new Thickness(0, 4) };
        DockPanel.SetDock(input, Dock.Right);
        editorRow.Children.Add(input);
        editorRow.Children.Add(label);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
            Children = { localButton, productionButton, customButton, testButton },
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(editorRow);
        stack.Children.Add(hint);
        stack.Children.Add(buttonRow);
        stack.Children.Add(result);

        return new Border
        {
            Background = (IBrush)this.FindResource("Surface0Brush")!,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            Child = stack,
        };
    }

    private async Task TestPlatformConnectionAsync(TextBox input, TextBlock result)
    {
        result.Text = "正在测试平台连接...";
        var tester = App.Services.GetRequiredService<IPlatformConnectionTester>();
        var testResult = await tester.TestAsync(input.Text ?? string.Empty);
        result.Text = testResult.Message;
    }

    private Control BuildModelSelector(string currentValue)
    {
        var modelService = App.Services.GetRequiredService<AiModelService>();
        var providerService = App.Services.GetRequiredService<AiProviderService>();
        var providers = providerService.GetAll();

        var cboProvider = new ComboBox { MinWidth = 120, MaxWidth = 180 };
        cboProvider.Classes.Add("form-combo");
        var cboModel = new ComboBox { MinWidth = 140, MaxWidth = 220 };
        cboModel.Classes.Add("form-combo");

        // 填充厂商列表
        cboProvider.Items.Add(new ComboBoxItem { Content = "（跟随当前模型）", Tag = "" });
        var preselectedProviderIndex = 0;
        for (var i = 0; i < providers.Count; i++)
        {
            cboProvider.Items.Add(new ComboBoxItem { Content = providers[i].Name, Tag = providers[i].Id });
            // 根据当前值反推所属厂商
            if (!string.IsNullOrEmpty(currentValue))
            {
                var models = modelService.GetByProviderId(providers[i].Id);
                if (models.Any(m => m.Id == currentValue))
                    preselectedProviderIndex = i + 1;
            }
        }

        void FillModels(string providerId, string selectedModelId)
        {
            cboModel.Items.Clear();
            cboModel.Items.Add(new ComboBoxItem { Content = "（跟随当前模型）", Tag = "" });
            if (string.IsNullOrEmpty(providerId))
            {
                cboModel.SelectedIndex = 0;
                return;
            }

            var models = modelService.GetByProviderId(providerId);
            var selectedIndex = 0;
            for (var i = 0; i < models.Count; i++)
            {
                var displayName = string.IsNullOrWhiteSpace(models[i].DisplayName) ? models[i].Name : models[i].DisplayName;
                cboModel.Items.Add(new ComboBoxItem { Content = displayName, Tag = models[i].Id });
                if (models[i].Id == selectedModelId) selectedIndex = i + 1;
            }
            cboModel.SelectedIndex = selectedIndex;
        }

        cboProvider.SelectionChanged += (_, _) =>
        {
            var pid = cboProvider.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty;
            FillModels(pid, string.Empty);
        };

        cboProvider.SelectedIndex = preselectedProviderIndex;
        var selectedProviderId = cboProvider.SelectedItem is ComboBoxItem { Tag: string t } ? t : string.Empty;
        FillModels(selectedProviderId, currentValue);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { cboProvider, cboModel },
        };

        // 用 Tag 存放模型 ComboBox 的引用，保存时从这里取值
        panel.Tag = cboModel;
        return panel;
    }

    private ComboBox BuildAgentSelector(string currentValue)
    {
        var combo = new ComboBox
        {
            MinWidth = 220,
            MaxWidth = 320,
        };
        combo.Classes.Add("form-combo");

        var agents = App.Services.GetRequiredService<AgentService>().GetSelectable();
        var selectedIndex = -1;
        for (var i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            combo.Items.Add(new ComboBoxItem { Content = $"{agent.Name} ({agent.Id})", Tag = agent.Id });
            if (string.Equals(agent.Id, currentValue, StringComparison.Ordinal))
            {
                selectedIndex = i;
            }
        }

        if (selectedIndex < 0 && !string.IsNullOrWhiteSpace(currentValue))
        {
            combo.Items.Add(new ComboBoxItem { Content = $"{currentValue}（未找到）", Tag = currentValue });
            selectedIndex = combo.Items.Count - 1;
        }

        combo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : agents.Count > 0 ? 0 : -1;
        combo.IsEnabled = combo.Items.Count > 0;
        return combo;
    }

    private ComboBox BuildVoicePluginSelector(string valueType, string currentValue)
    {
        var combo = new ComboBox
        {
            MinWidth = 220,
            MaxWidth = 320,
        };
        combo.Classes.Add("form-combo");

        if (!TryGetVoiceCapability(valueType, out var capability))
        {
            combo.Items.Add(new ComboBoxItem { Content = "能力类型无效", Tag = currentValue });
            combo.SelectedIndex = 0;
            combo.IsEnabled = false;
            return combo;
        }

        combo.Items.Add(new ComboBoxItem { Content = "自动选择", Tag = string.Empty });

        var registry = App.Services.GetRequiredService<VoiceCapabilityRegistry>();
        var plugins = registry.GetAvailablePlugins(capability);

        if (plugins.Count == 0)
        {
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = "未安装可用插件", Tag = string.Empty });
            combo.SelectedIndex = 0;
            combo.IsEnabled = false;
            return combo;
        }

        var selectedIndex = 0;
        for (var i = 0; i < plugins.Count; i++)
        {
            var plugin = plugins[i];
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{plugin.Name} ({plugin.PluginId})",
                Tag = plugin.PluginId
            });

            if (string.Equals(plugin.PluginId, currentValue, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i + 1;
            }
        }

        combo.SelectedIndex = selectedIndex;
        return combo;
    }

    private ComboBox BuildLogLevelSelector(string currentValue)
    {
        var combo = new ComboBox
        {
            MinWidth = 160,
            MaxWidth = 220,
        };
        combo.Classes.Add("form-combo");

        var levels = new[]
        {
            (Value: "Verbose", Label: "Verbose - 全部日志"),
            (Value: "Debug", Label: "Debug - 调试及以上"),
            (Value: "Information", Label: "Information - 信息及以上"),
            (Value: "Warning", Label: "Warning - 警告及以上"),
            (Value: "Error", Label: "Error - 错误及以上"),
            (Value: "Fatal", Label: "Fatal - 致命错误")
        };

        var selectedIndex = 3;
        for (var i = 0; i < levels.Length; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = levels[i].Label, Tag = levels[i].Value });
            if (string.Equals(levels[i].Value, currentValue, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
        }

        combo.SelectedIndex = selectedIndex;
        return combo;
    }

    private static bool TryGetVoiceCapability(string valueType, out VoicePluginCapability capability)
    {
        capability = default;
        const string prefix = "voicePlugin:";

        return valueType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && VoicePluginCapabilityIds.TryParse(valueType[prefix.Length..], out capability);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var logger = App.Services.GetRequiredService<ILogger<SystemSettingsPage>>();

        try
        {
            var oldTtsSpeed = SettingsService.GetValue("Tts.Speed", 1.0f);
            var oldWelcomeGreeting = SettingsService.GetValue("Tts.WelcomeGreeting", "主人，我在!");
            var updates = new List<(string Key, string Value)>();
            foreach (var (key, control) in _editors)
            {
                var value = control switch
                {
                    ToggleSwitch ts => (ts.IsChecked ?? false).ToString().ToLowerInvariant(),
                    NumericUpDown nud => nud.Value?.ToString() ?? "0",
                    ComboBox cbo => cbo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty,
                    StackPanel sp when sp.Tag is ComboBox modelCbo =>
                        modelCbo.SelectedItem is ComboBoxItem { Tag: string modelId } ? modelId : string.Empty,
                    TextBox tb => tb.Text ?? string.Empty,
                    _ => string.Empty,
                };
                updates.Add((key, value));
            }

            // 端口冲突检测：保存前验证新的统一服务端口是否可用。
            // 注意：新网络协议只有一个 WebSocket 服务端口，不再单独配置或热重启 PluginBus。
            var portEntry = updates.FirstOrDefault(u => u.Key == "WebSocket.Port");
            var oldConfiguredWebSocketPort = SettingsService.GetValue<int>("WebSocket.Port", 0);
            var webSocketPortChanged = false;
            if (portEntry != default)
            {
                if (int.TryParse(portEntry.Value, out var newPort) && newPort != oldConfiguredWebSocketPort)
                {
                    webSocketPortChanged = true;

                    if (newPort is < 1 or > 65535)
                    {
                        await ShowDialogAsync("端口无效", "端口号必须在 1 ~ 65535 范围内。");
                        return;
                    }

                    if (!IsPortAvailable(newPort))
                    {
                        await ShowDialogAsync("端口占用", $"端口 {newPort} 已被占用，请更换其他端口。");
                        return;
                    }
                }
            }

            SettingsService.SaveBatch(updates);

            var voicePluginMessage = await ApplyVoicePluginSettingsAsync(updates, CancellationToken.None);
            var ttsPluginMessage = await ApplyTtsPluginSettingsAsync(
                updates,
                oldTtsSpeed,
                oldWelcomeGreeting,
                CancellationToken.None);

            var message = webSocketPortChanged
                ? "设置已保存。服务端口修改后需要重启软件才能生效。"
                : "设置已保存。";
            if (!string.IsNullOrWhiteSpace(ttsPluginMessage))
            {
                message += Environment.NewLine + ttsPluginMessage;
            }
            if (!string.IsNullOrWhiteSpace(voicePluginMessage))
            {
                message += Environment.NewLine + voicePluginMessage;
            }

            await ShowDialogAsync("保存成功", message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存系统设置失败");
            await ShowDialogAsync("保存失败", $"保存设置时发生错误：{ex.Message}");
        }
    }

    private async Task<string?> ApplyTtsPluginSettingsAsync(
        IReadOnlyList<(string Key, string Value)> updates,
        float oldTtsSpeed,
        string oldWelcomeGreeting,
        CancellationToken cancellationToken)
    {
        var ttsSpeedEntry = updates.FirstOrDefault(u => u.Key == "Tts.Speed");
        var greetingEntry = updates.FirstOrDefault(u => u.Key == "Tts.WelcomeGreeting");
        var voiceTtsEntry = updates.FirstOrDefault(u => u.Key is "Voice.Tts.Enabled" or "Voice.Tts.PluginId");
        var ttsPluginChanged = voiceTtsEntry != default;
        var speedChanged = ttsSpeedEntry != default
            && float.TryParse(ttsSpeedEntry.Value, out var newSpeed)
            && Math.Abs(newSpeed - oldTtsSpeed) > 0.0001f;
        var greetingChanged = greetingEntry != default
            && !string.Equals(greetingEntry.Value, oldWelcomeGreeting, StringComparison.Ordinal);

        if (!ttsPluginChanged && !speedChanged && !greetingChanged)
        {
            return null;
        }

        if (!SettingsService.GetValue("Voice.Tts.Enabled", false))
        {
            return null;
        }

        var adapter = App.Services.GetRequiredService<TtsPluginAdapter>();
        var configure = await adapter.ConfigureAsync(cancellationToken);
        if (!configure.Ok && configure.Code != "skipped")
        {
            return $"TTS 插件配置下发失败：{configure.Message}";
        }

        if (!greetingChanged && !ttsPluginChanged)
        {
            return configure.Code == "skipped" ? "当前没有可用 TTS 插件，语音合成配置将在插件可用后生效。" : null;
        }

        var greeting = await adapter.RegenerateGreetingAsync(cancellationToken);
        if (!greeting.Ok && greeting.Code != "skipped")
        {
            return $"TTS 欢迎语缓存更新失败：{greeting.Message}";
        }

        return greeting.Code == "skipped" ? "当前没有可用 TTS 插件，欢迎语将在插件可用后重新生成。" : null;
    }

    private static async Task<string?> ApplyVoicePluginSettingsAsync(
        IReadOnlyList<(string Key, string Value)> updates,
        CancellationToken cancellationToken)
    {
        var voicePluginChanged = updates.Any(static update => update.Key is
            "Voice.Kws.Enabled" or "Voice.Kws.PluginId" or
            "Voice.Stt.Enabled" or "Voice.Stt.PluginId" or
            "Voice.Tts.Enabled" or "Voice.Tts.PluginId");

        if (!voicePluginChanged)
        {
            return null;
        }

        var coordinator = App.Services.GetService<VoicePipelineCoordinator>();
        if (coordinator is null)
        {
            return "语音设置已保存；语音流水线尚未启动，将在下次启动时生效。";
        }

        await coordinator.ApplySettingsAsync(cancellationToken);
        return "语音插件设置已实时应用。";
    }

    /// <summary>
    /// 检测指定端口是否可用。
    /// </summary>
    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// 显示轻量提示。
    /// </summary>
    private async Task ShowDialogAsync(string title, string message)
    {
        ShowToast(title, message);
        await Task.CompletedTask;
    }

    private void ShowToast(string title, string message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? title : $"{title}：{message}";
        Netor.Cortana.UI.UiPromptService.ShowInlineToast(ToastBorder, ToastText, text, ref _toastTimer);
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        SettingsService.ResetAllToDefault();
        LoadSettings();
    }
}
