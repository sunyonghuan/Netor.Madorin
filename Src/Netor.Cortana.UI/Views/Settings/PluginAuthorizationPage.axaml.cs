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
            Text = $"{plugin.Kind} · {(llmEnabled ? "大模型已授权" : "大模型未授权")}",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
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

        EnsureAuthorizationSettings(_selectedPlugin.Id);
        AuthorizationPanel.Children.Add(BuildLlmAuthorizationCard(_selectedPlugin));
    }

    private Border BuildLlmAuthorizationCard(AuthorizablePlugin plugin)
    {
        var keyPrefix = GetCapabilityPrefix(plugin.Id);
        var enabled = new ToggleSwitch
        {
            IsChecked = SettingsService.GetValue($"{keyPrefix}:Enabled", false),
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
            Text = "大模型授权",
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(BuildInfoText("授权插件拥有使用大模型的权利，可调用大模型完成相应的工作。"));
        stack.Children.Add(grid);
        stack.Children.Add(buttons);

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

    private static bool MatchesSearch(AuthorizablePlugin plugin, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        return plugin.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Kind.Contains(search, StringComparison.OrdinalIgnoreCase);
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

    private sealed record AuthorizablePlugin(string Id, string Name, string Description, string Kind)
    {
        public static AuthorizablePlugin FromLoadedPlugin(LoadedPluginInfo pluginInfo) => new(
            pluginInfo.Plugin.Id,
            pluginInfo.Plugin.Name,
            pluginInfo.Plugin.Description,
            "本地插件");

        public static AuthorizablePlugin FromMcpServer(McpServerEntity server) => new(
            server.Id,
            server.Name,
            server.Description,
            "MCP 服务");
    }
}
