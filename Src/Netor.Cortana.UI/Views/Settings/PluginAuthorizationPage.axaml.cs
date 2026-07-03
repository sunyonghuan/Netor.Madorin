using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Microsoft.Data.Sqlite;

using Netor.Cortana.Plugin;
using Netor.Cortana.Plugin.Mcp;

namespace Netor.Cortana.UI.Views.Settings;

public partial class PluginAuthorizationPage : UserControl
{
    private const string LlmCapability = "llm";
    private const string HostLlmCapability = "host.llm.invoke.v1";
    private const string LocalPluginKind = "本地插件";
    private const string McpServerKind = "MCP 服务";

    private SystemSettingsService SettingsService => App.Services.GetRequiredService<SystemSettingsService>();
    private PluginLoader PluginLoader => App.Services.GetRequiredService<PluginLoader>();
    private McpServerService McpServerService => App.Services.GetRequiredService<McpServerService>();

    private readonly List<AuthorizablePlugin> _plugins = [];
    private readonly Dictionary<string, LlmAuthorizationEditors> _authorizationEditors = new(StringComparer.OrdinalIgnoreCase);
    private AuthorizablePlugin? _selectedPlugin;

    public PluginAuthorizationPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshPlugins();
    }

    private void RefreshPlugins()
    {
        var selectedId = _selectedPlugin?.Id;
        _plugins.Clear();

        _plugins.AddRange(PluginLoader.GetLoadedPluginInfos().Select(AuthorizablePlugin.FromLoadedPlugin));
        _plugins.AddRange(McpServerService.GetAll().Select(AuthorizablePlugin.FromMcpServer));
        _plugins.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        _selectedPlugin = !string.IsNullOrWhiteSpace(selectedId)
            ? _plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            : _plugins.FirstOrDefault();

        BuildPluginList();
        BuildAuthorizationPanel();
    }

    private void BuildPluginList()
    {
        PluginListPanel.Children.Clear();

        var search = TxtSearch.Text?.Trim() ?? string.Empty;
        var filtered = _plugins.Where(plugin => MatchesSearch(plugin, search)).ToList();
        if (filtered.Count == 0)
        {
            PluginListPanel.Children.Add(new TextBlock
            {
                Text = "暂无匹配插件或 MCP 服务",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20)
            });
            return;
        }

        foreach (var plugin in filtered)
        {
            PluginListPanel.Children.Add(BuildPluginListItem(plugin));
        }
    }

    private Border BuildPluginListItem(AuthorizablePlugin plugin)
    {
        var isSelected = string.Equals(_selectedPlugin?.Id, plugin.Id, StringComparison.OrdinalIgnoreCase);
        var llmEnabled = SettingsService.GetValue(GetCapabilityKey(plugin.Id), false);
        var mode = GetLlmAuthorizationMode(plugin);
        var isRequiredUnauthorized = IsRequiredHostLlmUnauthorized(plugin, mode, llmEnabled);

        var title = new TextBlock
        {
            Text = plugin.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var meta = new TextBlock
        {
            Text = $"{plugin.Kind} · {GetLlmAuthorizationSummary(plugin, mode, llmEnabled)}",
            Foreground = isRequiredUnauthorized
                ? (IBrush)this.FindResource("DangerBrush")!
                : (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(plugin.Description) ? plugin.Id : plugin.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(title);
        stack.Children.Add(meta);
        stack.Children.Add(desc);

        var border = new Border
        {
            Background = isSelected
                ? (IBrush)this.FindResource("Surface1Brush")!
                : (IBrush)this.FindResource("Surface0Brush")!,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = stack,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };

        border.PointerPressed += (_, _) =>
        {
            _selectedPlugin = plugin;
            BuildPluginList();
            BuildAuthorizationPanel();
        };

        return border;
    }

    private void BuildAuthorizationPanel()
    {
        AuthorizationPanel.Children.Clear();
        _authorizationEditors.Clear();

        if (_selectedPlugin is null)
        {
            AuthorizationPanel.Children.Add(new TextBlock
            {
                Text = "请选择一个插件",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40)
            });
            return;
        }

        var mode = GetLlmAuthorizationMode(_selectedPlugin);
        if (mode == LlmAuthorizationMode.None)
        {
            AuthorizationPanel.Children.Add(BuildNoAuthorizationCard(_selectedPlugin));
            return;
        }

        EnsureAuthorizationSettings(_selectedPlugin.Id);
        AuthorizationPanel.Children.Add(BuildLlmAuthorizationCard(_selectedPlugin, mode));
    }

    private Border BuildLlmAuthorizationCard(AuthorizablePlugin plugin, LlmAuthorizationMode mode)
    {
        var keyPrefix = GetCapabilityPrefix(plugin.Id);
        var isEnabled = SettingsService.GetValue($"{keyPrefix}:Enabled", false);
        var isRequiredUnauthorized = IsRequiredHostLlmUnauthorized(plugin, mode, isEnabled);
        var enabled = new ToggleSwitch
        {
            IsChecked = isEnabled,
            OnContent = "允许该插件使用大模型",
            OffContent = "禁止该插件使用大模型",
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var providerModelSelector = BuildProviderModelSelector(
            SettingsService.GetValue($"{keyPrefix}:ProviderId", string.Empty),
            SettingsService.GetValue($"{keyPrefix}:ModelId", string.Empty));

        var maxInput = BuildNumberEditor(SettingsService.GetValue($"{keyPrefix}:MaxInputTokens", 128000), 0, 200000, 1000);
        var maxOutput = BuildNumberEditor(SettingsService.GetValue($"{keyPrefix}:MaxOutputTokens", 128000), 0, 200000, 1000);
        var timeout = BuildNumberEditor(SettingsService.GetValue($"{keyPrefix}:TimeoutMs", 30000), 1000, 600000, 1000);
        var concurrency = BuildNumberEditor(SettingsService.GetValue($"{keyPrefix}:MaxConcurrency", 3), 1, 32, 1);
        var allowBackground = new ToggleSwitch
        {
            IsChecked = SettingsService.GetValue($"{keyPrefix}:AllowBackground", true),
            OnContent = "允许后台调用",
            OffContent = "仅允许前台调用",
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _authorizationEditors[keyPrefix] = new LlmAuthorizationEditors(
            enabled,
            providerModelSelector.ProviderCombo,
            providerModelSelector.ModelCombo,
            maxInput,
            maxOutput,
            timeout,
            concurrency,
            allowBackground);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("108,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8,
            Margin = new Thickness(0, 10, 0, 0)
        };

        AddFormRow(grid, 0, "总开关", enabled);
        AddFormRow(grid, 1, "厂商", providerModelSelector.ProviderCombo);
        AddFormRow(grid, 2, "模型", providerModelSelector.ModelCombo);
        AddFormRow(grid, 3, "输入 Token", maxInput);
        AddFormRow(grid, 4, "输出 Token", maxOutput);
        AddFormRow(grid, 5, "超时时间", timeout);
        AddFormRow(grid, 6, "并发数", concurrency);
        AddFormRow(grid, 7, "后台调用", allowBackground);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var saveButton = new Button { Content = "保存大模型授权" };
        saveButton.Classes.Add("btn-primary");
        saveButton.Click += OnSaveClick;
        buttons.Children.Add(saveButton);

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = GetLlmAuthorizationTitle(mode),
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(BuildInfoText(GetLlmAuthorizationDescription(mode)));
        var requestDetails = BuildHostLlmRequestDetails(plugin, mode);
        if (requestDetails is not null)
        {
            stack.Children.Add(requestDetails);
        }

        if (isRequiredUnauthorized)
        {
            stack.Children.Add(BuildWarningText("该插件将大模型能力标记为必需。未授权时仍允许加载插件，但相关核心功能可能降级或不可用。"));
        }
        stack.Children.Add(grid);
        stack.Children.Add(buttons);

        return new Border
        {
            Background = (IBrush)this.FindResource("Surface0Brush")!,
            BorderBrush = isRequiredUnauthorized
                ? (IBrush)this.FindResource("DangerBrush")!
                : (IBrush)this.FindResource("Surface1Brush")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            Child = stack
        };
    }

    private Border BuildNoAuthorizationCard(AuthorizablePlugin plugin)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = "宿主能力申请",
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(BuildInfoText($"{plugin.Name} 未声明大模型宿主能力申请，也没有可继续展示的历史授权入口。"));

        return new Border
        {
            Background = (IBrush)this.FindResource("Surface0Brush")!,
            BorderBrush = (IBrush)this.FindResource("Surface1Brush")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            Child = stack
        };
    }

    private void EnsureAuthorizationSettings(string pluginId)
    {
        var keyPrefix = GetCapabilityPrefix(pluginId);
        SettingsService.EnsureSetting($"{keyPrefix}:Enabled", "", "", "", "false", "hidden", 0);
        SettingsService.EnsureSetting($"{keyPrefix}:ProviderId", "", "", "", "", "hidden", 0);
        SettingsService.EnsureSetting($"{keyPrefix}:ModelId", "", "", "", "", "hidden", 0);
        SettingsService.EnsureSetting($"{keyPrefix}:MaxInputTokens", "", "", "", "128000", "hidden", 0);
        SettingsService.EnsureSetting($"{keyPrefix}:MaxOutputTokens", "", "", "", "128000", "hidden", 0);
        SettingsService.EnsureSetting($"{keyPrefix}:TimeoutMs", "", "", "", "30000", "hidden", 0);
        SettingsService.EnsureSetting($"{keyPrefix}:MaxConcurrency", "", "", "", "3", "hidden", 0);
        SettingsService.EnsureSetting($"{keyPrefix}:AllowBackground", "", "", "", "true", "hidden", 0);
    }

    private ProviderModelSelector BuildProviderModelSelector(string selectedProviderId, string selectedModelId)
    {
        var providerService = App.Services.GetRequiredService<AiProviderService>();
        var modelService = App.Services.GetRequiredService<AiModelService>();
        var providers = providerService.GetAll();

        var providerCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        providerCombo.Classes.Add("form-combo");
        var modelCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        modelCombo.Classes.Add("form-combo");

        providerCombo.Items.Add(new ComboBoxItem { Content = "（未绑定厂商）", Tag = string.Empty });
        var providerIndex = 0;
        for (var i = 0; i < providers.Count; i++)
        {
            providerCombo.Items.Add(new ComboBoxItem { Content = providers[i].Name, Tag = providers[i].Id });
            if (string.Equals(providers[i].Id, selectedProviderId, StringComparison.OrdinalIgnoreCase)) providerIndex = i + 1;
        }

        void FillModels(string providerId, string modelId)
        {
            modelCombo.Items.Clear();
            modelCombo.Items.Add(new ComboBoxItem { Content = "（未绑定模型）", Tag = string.Empty });
            if (string.IsNullOrWhiteSpace(providerId))
            {
                modelCombo.SelectedIndex = 0;
                return;
            }

            var models = modelService.GetByProviderId(providerId);
            var modelIndex = 0;
            for (var i = 0; i < models.Count; i++)
            {
                var displayName = string.IsNullOrWhiteSpace(models[i].DisplayName) ? models[i].Name : models[i].DisplayName;
                modelCombo.Items.Add(new ComboBoxItem { Content = displayName, Tag = models[i].Id });
                if (string.Equals(models[i].Id, modelId, StringComparison.OrdinalIgnoreCase)) modelIndex = i + 1;
            }

            modelCombo.SelectedIndex = modelIndex;
        }

        providerCombo.SelectionChanged += (_, _) =>
        {
            var providerId = providerCombo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty;
            FillModels(providerId, string.Empty);
        };

        providerCombo.SelectedIndex = providerIndex;
        FillModels(selectedProviderId, selectedModelId);

        return new ProviderModelSelector(providerCombo, modelCombo);
    }

    private NumericUpDown BuildNumberEditor(int value, int minimum, int maximum, int increment) => new()
    {
        Value = value,
        Minimum = minimum,
        Maximum = maximum,
        Increment = increment,
        FormatString = "0",
        Width = 170,
        HorizontalAlignment = HorizontalAlignment.Right,
        Background = (IBrush)this.FindResource("Surface0Brush")!,
        Foreground = (IBrush)this.FindResource("TextBrush")!
    };

    private void AddFormRow(Grid grid, int row, string labelText, Control editor)
    {
        var label = new TextBlock
        {
            Text = labelText,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            FontSize = 12
        };

        Grid.SetRow(label, row);
        Grid.SetColumn(label, 0);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(label);
        grid.Children.Add(editor);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedPlugin is null)
        {
            return;
        }

        var keyPrefix = GetCapabilityPrefix(_selectedPlugin.Id);
        if (!_authorizationEditors.TryGetValue(keyPrefix, out var editors))
        {
            return;
        }

        try
        {
            EnsureAuthorizationSettings(_selectedPlugin.Id);
            SettingsService.SaveBatch([
                ($"{keyPrefix}:Enabled", GetToggleValue(editors.Enabled)),
                ($"{keyPrefix}:ProviderId", GetComboTag(editors.ProviderCombo)),
                ($"{keyPrefix}:ModelId", GetComboTag(editors.ModelCombo)),
                ($"{keyPrefix}:MaxInputTokens", GetNumberValue(editors.MaxInputTokens)),
                ($"{keyPrefix}:MaxOutputTokens", GetNumberValue(editors.MaxOutputTokens)),
                ($"{keyPrefix}:TimeoutMs", GetNumberValue(editors.TimeoutMs)),
                ($"{keyPrefix}:MaxConcurrency", GetNumberValue(editors.MaxConcurrency)),
                ($"{keyPrefix}:AllowBackground", GetToggleValue(editors.AllowBackground))
            ]);

            BuildPluginList();
            BuildAuthorizationPanel();
            ShowToast("保存成功");
        }
        catch (SqliteException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            await ShowDialogAsync("授权保存失败", $"数据库写入失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            await ShowDialogAsync("授权保存失败", ex.Message);
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => RefreshPlugins();

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => BuildPluginList();

    private void ShowToast(string message)
    {
        Netor.Cortana.UI.UiPromptService.ShowToast(AuthorizationPanel, message, TimeSpan.FromMilliseconds(1800));
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        await Netor.Cortana.UI.UiPromptService.ShowDialogAsync(this, title, message);
    }

    private TextBlock BuildInfoText(string text) => new()
    {
        Text = text,
        Foreground = (IBrush)this.FindResource("SubtextBrush")!,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12
    };

    private TextBlock BuildWarningText(string text) => new()
    {
        Text = text,
        Foreground = (IBrush)this.FindResource("DangerBrush")!,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12
    };

    private StackPanel? BuildHostLlmRequestDetails(AuthorizablePlugin plugin, LlmAuthorizationMode mode)
    {
        if (mode != LlmAuthorizationMode.Requested)
        {
            return null;
        }

        var capabilities = plugin.RequiredHostCapabilities
            .Where(static capability => string.Equals(capability.Id, HostLlmCapability, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (capabilities.Count == 0)
        {
            return null;
        }

        var stack = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 4, 0, 2)
        };

        stack.Children.Add(new TextBlock
        {
            Text = "申请明细",
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold
        });

        foreach (var capability in capabilities)
        {
            stack.Children.Add(BuildHostLlmRequestDetailText(capability));
        }

        return stack;
    }

    private TextBlock BuildHostLlmRequestDetailText(AuthorizableRequiredHostCapability capability)
    {
        var purpose = string.IsNullOrWhiteSpace(capability.Purpose)
            ? "未声明用途"
            : capability.Purpose!;
        var required = capability.Required ? "必需" : "可选";
        var reason = string.IsNullOrWhiteSpace(capability.Reason)
            ? "未提供申请原因"
            : capability.Reason!;

        return new TextBlock
        {
            Text = $"{purpose} · {required}：{reason}",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
    }

    private static bool MatchesSearch(AuthorizablePlugin plugin, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        return plugin.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Kind.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private LlmAuthorizationMode GetLlmAuthorizationMode(AuthorizablePlugin plugin)
    {
        if (string.Equals(plugin.Kind, McpServerKind, StringComparison.OrdinalIgnoreCase))
        {
            return LlmAuthorizationMode.McpCompatibility;
        }

        if (plugin.RequiredHostCapabilities.Any(static capability =>
            string.Equals(capability.Id, HostLlmCapability, StringComparison.OrdinalIgnoreCase)))
        {
            return LlmAuthorizationMode.Requested;
        }

        var raw = SettingsService.GetValue(GetCapabilityKey(plugin.Id));
        return bool.TryParse(raw, out var enabled) && enabled
            ? LlmAuthorizationMode.HistoryCompatibility
            : LlmAuthorizationMode.None;
    }

    private static string GetLlmAuthorizationSummary(AuthorizablePlugin plugin, LlmAuthorizationMode mode, bool enabled)
    {
        if (IsRequiredHostLlmUnauthorized(plugin, mode, enabled))
        {
            return "必需能力未授权";
        }

        return mode switch
        {
            LlmAuthorizationMode.Requested => enabled ? "大模型已授权" : "申请大模型授权",
            LlmAuthorizationMode.HistoryCompatibility => "历史大模型授权",
            LlmAuthorizationMode.McpCompatibility => enabled ? "大模型已授权" : "MCP 兼容授权",
            _ => "未申请大模型"
        };
    }

    private static bool IsRequiredHostLlmUnauthorized(
        AuthorizablePlugin plugin,
        LlmAuthorizationMode mode,
        bool enabled)
    {
        return mode == LlmAuthorizationMode.Requested
            && !enabled
            && plugin.RequiredHostCapabilities.Any(static capability =>
                capability.Required
                && string.Equals(capability.Id, HostLlmCapability, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetLlmAuthorizationTitle(LlmAuthorizationMode mode)
    {
        return mode switch
        {
            LlmAuthorizationMode.Requested => "大模型授权（按申请展示）",
            LlmAuthorizationMode.HistoryCompatibility => "大模型授权（历史兼容）",
            LlmAuthorizationMode.McpCompatibility => "大模型授权（MCP 兼容）",
            _ => "大模型授权"
        };
    }

    private static string GetLlmAuthorizationDescription(LlmAuthorizationMode mode)
    {
        return mode switch
        {
            LlmAuthorizationMode.Requested => "该插件声明需要调用宿主大模型。授权后，插件可在宿主强校验下使用大模型能力。",
            LlmAuthorizationMode.HistoryCompatibility => "该插件未声明新版宿主能力申请，但存在已启用的历史授权，因此继续保留入口。",
            LlmAuthorizationMode.McpCompatibility => "MCP 服务当前没有插件清单声明协议，因此大模型授权继续按兼容模式展示。",
            _ => "授权插件拥有使用大模型的权利，可调用大模型完成相应的工作。"
        };
    }

    private static string GetCapabilityPrefix(string pluginId) => $"Plugin:{pluginId}:Capability:{LlmCapability}";

    private static string GetCapabilityKey(string pluginId) => $"{GetCapabilityPrefix(pluginId)}:Enabled";

    private static string GetToggleValue(ToggleSwitch toggle) =>
        (toggle.IsChecked ?? false).ToString().ToLowerInvariant();

    private static string GetComboTag(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty;

    private static string GetNumberValue(NumericUpDown number) =>
        Convert.ToInt32(number.Value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record ProviderModelSelector(ComboBox ProviderCombo, ComboBox ModelCombo);

    private sealed record LlmAuthorizationEditors(
        ToggleSwitch Enabled,
        ComboBox ProviderCombo,
        ComboBox ModelCombo,
        NumericUpDown MaxInputTokens,
        NumericUpDown MaxOutputTokens,
        NumericUpDown TimeoutMs,
        NumericUpDown MaxConcurrency,
        ToggleSwitch AllowBackground);

    private enum LlmAuthorizationMode
    {
        None,
        Requested,
        HistoryCompatibility,
        McpCompatibility
    }

    private sealed record AuthorizableRequiredHostCapability(string Id, string? Purpose, bool Required, string? Reason);

    private sealed record AuthorizablePlugin(
        string Id,
        string Name,
        string Description,
        string Kind,
        IReadOnlyList<string> Capabilities,
        IReadOnlyList<AuthorizableRequiredHostCapability> RequiredHostCapabilities)
    {
        public static AuthorizablePlugin FromLoadedPlugin(LoadedPluginInfo pluginInfo) => new(
            pluginInfo.Plugin.Id,
            pluginInfo.Plugin.Name,
            pluginInfo.Plugin.Description,
            LocalPluginKind,
            GetProvidedCapabilities(pluginInfo),
            (pluginInfo.Manifest.RequiredHostCapabilities ?? [])
                .Where(static capability => capability is not null)
                .Select(static capability => new AuthorizableRequiredHostCapability(
                    capability.Id,
                    capability.Purpose,
                    capability.Required,
                    capability.Reason))
                .ToList());

        public static AuthorizablePlugin FromMcpServer(McpServerEntity server) => new(
            server.Id,
            server.Name,
            server.Description,
            McpServerKind,
            [],
            []);

        private static IReadOnlyList<string> GetProvidedCapabilities(LoadedPluginInfo pluginInfo)
        {
            if (pluginInfo.Manifest.ProvidedCapabilities is { Count: > 0 })
            {
                return pluginInfo.Manifest.ProvidedCapabilities;
            }

            if (pluginInfo.Manifest.Capabilities is { Count: > 0 })
            {
                return pluginInfo.Manifest.Capabilities;
            }

            return pluginInfo.Plugin.Capabilities;
        }
    }
}
