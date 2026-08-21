using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.Madorin.Plugin;
using Netor.Madorin.Plugin.Mcp;
using Netor.Cortana.Store.Views;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Netor.Cortana.UI.Views.Settings;

public partial class PluginManagementPage : UserControl
{
    private AgentService AgentService => App.Services.GetRequiredService<AgentService>();
    private GlobalPluginService GlobalPluginService => App.Services.GetRequiredService<GlobalPluginService>();
    private IAppPaths AppPaths => App.Services.GetRequiredService<IAppPaths>();
    private McpServerService McpServerService => App.Services.GetRequiredService<McpServerService>();
    private IPluginConfigValueProtector PluginConfigValueProtector => App.Services.GetRequiredService<IPluginConfigValueProtector>();
    private PluginLoader PluginLoader => App.Services.GetRequiredService<PluginLoader>();
    private IPublisher Publisher => App.Services.GetRequiredService<IPublisher>();
    private SystemSettingsService SettingsService => App.Services.GetRequiredService<SystemSettingsService>();

    private readonly List<LoadedPluginInfo> _plugins = [];
    private readonly List<McpServerEntity> _mcpServers = [];
    private readonly Dictionary<string, McpServerHost> _activeMcpHosts = new(StringComparer.OrdinalIgnoreCase);
    private LoadedPluginInfo? _selectedPlugin;
    private string? _selectedMcpServerId;
    private DispatcherTimer? _toastTimer;

    public PluginManagementPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshPlugins();
    }

    private void RefreshPlugins()
    {
        _plugins.Clear();
        _plugins.AddRange(PluginLoader.GetLoadedPluginInfos().OrderBy(p => p.Plugin.Name, StringComparer.OrdinalIgnoreCase));

        _mcpServers.Clear();
        _mcpServers.AddRange(McpServerService.GetAll().OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase));

        _activeMcpHosts.Clear();
        foreach (var host in PluginLoader.GetActiveMcpServers())
        {
            _activeMcpHosts[host.Id] = host;
        }

        BuildPluginList();

        if (_selectedPlugin is not null)
        {
            _selectedPlugin = _plugins.FirstOrDefault(p => string.Equals(p.Plugin.Id, _selectedPlugin.Plugin.Id, StringComparison.OrdinalIgnoreCase));
            BuildDetails(_selectedPlugin);
        }
        else if (_selectedMcpServerId is not null)
        {
            BuildMcpDetails(_mcpServers.FirstOrDefault(m => string.Equals(m.Id, _selectedMcpServerId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void BuildPluginList()
    {
        PluginListPanel.Children.Clear();

        var search = TxtSearch.Text?.Trim() ?? string.Empty;
        var filtered = _plugins.Where(plugin => MatchesSearch(plugin, search)).ToList();
        var filteredMcpServers = _mcpServers.Where(server => MatchesSearch(server, search)).ToList();

        if (filtered.Count == 0 && filteredMcpServers.Count == 0)
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

        foreach (var server in filteredMcpServers)
        {
            PluginListPanel.Children.Add(BuildMcpListItem(server));
        }
    }

    private Border BuildPluginListItem(LoadedPluginInfo pluginInfo)
    {
        var plugin = pluginInfo.Plugin;
        var isSelected = _selectedPlugin?.Plugin.Id == plugin.Id;
        var globalText = GlobalPluginService.IsEnabled(plugin.Id)
            ? " · 全局启用"
            : string.Empty;

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
            Text = $"v{plugin.Version} · 全局目录{globalText} · {plugin.Tools.Count} 个工具",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(plugin.Description) ? "(无简介)" : plugin.Description,
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
            _selectedPlugin = pluginInfo;
            _selectedMcpServerId = null;
            BuildPluginList();
            BuildDetails(pluginInfo);
        };

        return border;
    }

    private Border BuildMcpListItem(McpServerEntity server)
    {
        var isSelected = string.Equals(_selectedMcpServerId, server.Id, StringComparison.OrdinalIgnoreCase);
        var isConnected = _activeMcpHosts.ContainsKey(server.Id);
        var statusText = server.IsEnabled
            ? isConnected ? "已连接" : "已启用 · 未连接"
            : "已禁用";

        var title = new TextBlock
        {
            Text = server.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var meta = new TextBlock
        {
            Text = $"MCP 服务 · {server.TransportType} · {statusText}",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.Description) ? "(无简介)" : server.Description,
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
            _selectedPlugin = null;
            _selectedMcpServerId = server.Id;
            BuildPluginList();
            BuildMcpDetails(server);
        };

        return border;
    }

    private void BuildDetails(LoadedPluginInfo? pluginInfo)
    {
        DetailPanel.Children.Clear();
        DetailPanel.RowDefinitions.Clear();
        DetailPanel.ColumnDefinitions.Clear();

        if (pluginInfo is null)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "请选择一个插件",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40)
            });
            return;
        }

        var plugin = pluginInfo.Plugin;
        var isGlobalEnabled = GlobalPluginService.IsEnabled(plugin.Id);

        DetailPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        DetailPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        var header = BuildPluginDetailHeader(pluginInfo, isGlobalEnabled);
        Grid.SetRow(header, 0);
        DetailPanel.Children.Add(header);

        var tabs = new TabControl
        {
            Margin = new Thickness(14, 0, 14, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        tabs.Items.Add(new TabItem { Header = BuildTabHeader("概览"), Content = BuildPluginOverviewTab(pluginInfo, isGlobalEnabled) });
        tabs.Items.Add(new TabItem { Header = BuildTabHeader("配置"), Content = BuildPluginSettings(pluginInfo) });
        tabs.Items.Add(new TabItem { Header = BuildTabHeader("工具"), Content = BuildPluginToolsTab(pluginInfo) });
        tabs.Items.Add(new TabItem { Header = BuildTabHeader("绑定"), Content = BuildPluginBindingsTab(plugin.Id, isGlobalEnabled) });
        Grid.SetRow(tabs, 1);
        DetailPanel.Children.Add(tabs);
    }

    private Control BuildPluginDetailHeader(LoadedPluginInfo pluginInfo, bool isGlobalEnabled)
    {
        var plugin = pluginInfo.Plugin;
        var title = new TextBlock
        {
            Text = plugin.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var globalToggle = new ToggleSwitch
        {
            OnContent = "全局插件",
            OffContent = "全局插件",
            IsChecked = isGlobalEnabled,
            IsEnabled = true,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontWeight = FontWeight.Medium
        };
        globalToggle.IsCheckedChanged += (_, _) =>
        {
            if (_selectedPlugin is null) return;
            GlobalPluginService.SetEnabled(_selectedPlugin.Plugin.Id, globalToggle.IsChecked == true);
            Publisher.Publish(Events.OnPluginsChanged, new VoiceSignalArgs());
            BuildPluginList();
            BuildDetails(_selectedPlugin);
        };

        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(globalToggle, 1);
        top.Children.Add(title);
        top.Children.Add(globalToggle);

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(plugin.Description) ? "(无简介)" : plugin.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            MaxHeight = 42
        };

        var meta = BuildPluginMetaStrip(pluginInfo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(BuildActionButton("卸载", OnUnloadClick, "btn-secondary"));
        buttons.Children.Add(BuildActionButton("重载", OnReloadClick, "btn-primary"));
        buttons.Children.Add(BuildActionButton("打开目录", OnOpenDirectoryClick, "btn-secondary"));
        buttons.Children.Add(BuildActionButton("删除插件", OnDeleteClick, "btn-danger"));

        return new Border
        {
            Padding = new Thickness(14, 14, 14, 10),
            BorderBrush = (IBrush)this.FindResource("Surface1Brush")!,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    top,
                    desc,
                    meta,
                    buttons
                }
            }
        };
    }

    private Control BuildPluginMetaStrip(LoadedPluginInfo pluginInfo)
    {
        var plugin = pluginInfo.Plugin;
        var stack = new StackPanel { Spacing = 6 };
        var chips = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 6,
            LineSpacing = 6
        };

        chips.Children.Add(BuildMetaChip($"v{plugin.Version}"));
        chips.Children.Add(BuildMetaChip(plugin.Id));
        chips.Children.Add(BuildMetaChip(pluginInfo.Manifest.Runtime.ToString()));
        chips.Children.Add(BuildMetaChip("全局目录"));
        chips.Children.Add(BuildMetaChip($"{plugin.Tools.Count} 个工具"));
        stack.Children.Add(chips);

        var pathGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 6
        };
        pathGrid.Children.Add(new TextBlock
        {
            Text = "目录",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });
        var path = new TextBlock
        {
            Text = pluginInfo.DirectoryPath,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(path, 1);
        pathGrid.Children.Add(path);
        stack.Children.Add(pathGrid);

        return stack;
    }

    private Border BuildMetaChip(string text) => new()
    {
        Background = (IBrush)this.FindResource("Surface1Brush")!,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 3),
        Child = new TextBlock
        {
            Text = text,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis
        }
    };

    private static ScrollViewer BuildTabScroll(Control content) => new()
    {
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        Padding = new Thickness(0, 0, 18, 0),
        Content = content
    };

    private TextBlock BuildTabHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Foreground = (IBrush)this.FindResource("TextBrush")!,
        Margin = new Thickness(4, 0)
    };

    private Control BuildPluginOverviewTab(LoadedPluginInfo pluginInfo, bool isGlobalEnabled)
    {
        var plugin = pluginInfo.Plugin;
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(BuildSectionHeader("插件摘要"));
        stack.Children.Add(BuildInfoText($"插件 ID：{plugin.Id}"));
        stack.Children.Add(BuildInfoText($"版本：v{plugin.Version}"));
        stack.Children.Add(BuildInfoText("来源：全局目录"));
        stack.Children.Add(BuildInfoText($"目录：{pluginInfo.DirectoryPath}"));
        stack.Children.Add(BuildInfoText($"工具数量：{plugin.Tools.Count}"));

        if (isGlobalEnabled)
        {
            stack.Children.Add(BuildSectionHeader("绑定状态"));
            stack.Children.Add(BuildInfoText("该插件已启用为全局插件，所有智能体默认可用。已有绑定会保留，关闭全局开关后仍可继续使用。"));
        }
        else
        {
            stack.Children.Add(BuildSectionHeader("绑定状态"));
            stack.Children.Add(BuildInfoText("该插件未设为全局插件，可在“绑定”页为指定智能体启用。"));
        }

        return BuildTabScroll(stack);
    }

    private Control BuildPluginToolsTab(LoadedPluginInfo pluginInfo)
    {
        var plugin = pluginInfo.Plugin;
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(BuildSectionHeader("工具列表"));
        if (plugin.Tools.Count == 0)
        {
            stack.Children.Add(BuildInfoText("该插件未提供工具。"));
        }
        else
        {
            foreach (var tool in plugin.Tools)
            {
                stack.Children.Add(BuildToolListRow(tool));
            }
        }

        return BuildTabScroll(stack);
    }

    private Control BuildToolListRow(Microsoft.Extensions.AI.AITool tool)
    {
        var name = new TextBlock
        {
            Text = tool.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(tool.Description) ? "(无描述)" : tool.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        };

        return new Border
        {
            BorderBrush = (IBrush)this.FindResource("Surface1Brush")!,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    name,
                    desc
                }
            }
        };
    }

    private Control BuildPluginBindingsTab(string pluginId, bool isGlobalEnabled)
    {
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(BuildSectionHeader("绑定智能体"));
        if (isGlobalEnabled)
        {
            stack.Children.Add(BuildInfoText("该插件已启用为全局插件，所有智能体默认可用，无需单独绑定。"));
        }
        else
        {
            stack.Children.Add(BuildInfoText("勾选后会立即保存当前插件和智能体的绑定关系。"));
            AddAgentBindings(stack, pluginId);
        }

        return BuildTabScroll(stack);
    }

    private Control BuildPluginSettings(LoadedPluginInfo pluginInfo)
    {
        var settings = (pluginInfo.Manifest.SettingsSchema ?? [])
            .Where(static setting => setting is not null)
            .Where(static setting => !string.IsNullOrWhiteSpace(setting.Key))
            .ToList();
        if (settings.Count == 0)
        {
            return BuildTabScroll(new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0),
                Children =
                {
                    BuildSectionHeader("插件配置"),
                    BuildInfoText("该插件未声明可配置项。")
                }
            });
        }

        var editors = new List<PluginSettingEditor>();
        var settingsStack = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(0, 10, 0, 24)
        };

        foreach (var descriptor in settings)
        {
            var key = GetPluginConfigKey(pluginInfo.Plugin.Id, descriptor.Key);
            var storedValue = SettingsService.GetValue(key);
            var defaultValue = GetDefaultValueText(descriptor);
            var currentValue = storedValue is not null && !IsSensitiveSetting(descriptor)
                ? PluginConfigValueProtector.GetEffectiveValue(storedValue)
                : defaultValue;
            var editor = BuildPluginSettingEditor(descriptor, currentValue, storedValue is not null);
            editors.Add(new PluginSettingEditor(descriptor, editor, storedValue is not null));
            settingsStack.Children.Add(BuildSettingsFormItem(descriptor, editor));
        }

        var saveButton = new Button
        {
            Content = "保存插件配置",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        saveButton.Classes.Add("btn-primary");
        saveButton.Click += async (_, _) => await SavePluginSettingsAsync(pluginInfo.Plugin.Id, editors);

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(BuildSectionHeader("插件配置"));
        stack.Children.Add(BuildInfoText("保存后会重新加载插件；标记为重启生效的配置会保留到下次启动。"));
        stack.Children.Add(settingsStack);

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto")
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 18, 0),
            Content = stack
        };
        var footer = new Border
        {
            BorderBrush = (IBrush)this.FindResource("Surface1Brush")!,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 18, 0),
            Child = saveButton
        };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(footer, 1);
        layout.Children.Add(scroll);
        layout.Children.Add(footer);
        return layout;
    }

    private Control BuildPluginSettingEditor(PluginSettingDescriptor descriptor, string value, bool hasStoredValue)
    {
        if (IsSensitiveSetting(descriptor))
        {
            return new TextBox
            {
                Text = string.Empty,
                PasswordChar = '*',
                PlaceholderText = hasStoredValue ? "已保存，留空不修改" : string.Empty,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Classes = { "form-input" }
            };
        }

        switch (NormalizeSettingType(descriptor.Type))
        {
            case "boolean":
                return new ToggleSwitch
                {
                    IsChecked = bool.TryParse(value, out var boolValue) && boolValue,
                    OnContent = "开",
                    OffContent = "关",
                    HorizontalAlignment = HorizontalAlignment.Left
                };

            case "number":
                return new NumericUpDown
                {
                    Value = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var numberValue)
                        ? numberValue
                        : 0,
                    FormatString = "0.##",
                    Width = 160,
                    MinHeight = 36,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    TextAlignment = TextAlignment.Left,
                    Background = (IBrush)this.FindResource("Surface0Brush")!,
                    Foreground = (IBrush)this.FindResource("TextBrush")!
                };

            case "enum":
                return BuildPluginSettingCombo(descriptor, value);

            case "json":
                return new TextBox
                {
                    Text = value,
                    AcceptsReturn = true,
                    MinHeight = 84,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "form-input" }
                };

            default:
                return new TextBox
                {
                    Text = value,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Classes = { "form-input" }
                };
        }
    }

    private ComboBox BuildPluginSettingCombo(PluginSettingDescriptor descriptor, string value)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Classes.Add("form-combo");

        var selectedIndex = 0;
        var options = descriptor.Options is { Count: > 0 } ? descriptor.Options : [];
        for (var i = 0; i < options.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = options[i], Tag = options[i] });
            if (string.Equals(options[i], value, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
        }

        if (combo.Items.Count == 0)
        {
            combo.Items.Add(new ComboBoxItem { Content = "未声明选项", Tag = value });
            combo.IsEnabled = false;
        }

        combo.SelectedIndex = selectedIndex;
        return combo;
    }

    private Control BuildSettingsFormItem(PluginSettingDescriptor descriptor, Control editor)
    {
        if (editor is not NumericUpDown and not ToggleSwitch)
        {
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        var labelStack = new StackPanel { Spacing = 3 };
        labelStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(descriptor.Label) ? descriptor.Key : descriptor.Label,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });

        if (!string.IsNullOrWhiteSpace(descriptor.Description))
        {
            labelStack.Children.Add(new TextBlock
            {
                Text = descriptor.Description,
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            });
        }

        var item = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                labelStack,
                editor
            }
        };

        return item;
    }

    private async Task SavePluginSettingsAsync(string pluginId, IReadOnlyList<PluginSettingEditor> editors)
    {
        try
        {
            var validationError = ValidatePluginSettings(editors);
            if (validationError is not null)
            {
                ShowPageToast(validationError, TimeSpan.FromMilliseconds(2200));
                return;
            }

            var changed = false;
            var requiresRestart = false;
            var changes = new List<PluginSettingChange>();

            foreach (var editor in editors)
            {
                var descriptor = editor.Descriptor;
                var key = GetPluginConfigKey(pluginId, descriptor.Key);
                var newValue = GetPluginSettingEditorValue(editor.Control, descriptor);
                if (newValue is null)
                {
                    continue;
                }

                var oldValue = SettingsService.GetValue(key);
                var effectiveOldValue = oldValue is not null
                    ? PluginConfigValueProtector.GetEffectiveValue(oldValue)
                    : null;
                if (string.Equals(effectiveOldValue, newValue, StringComparison.Ordinal))
                {
                    continue;
                }

                var storedValue = IsSensitiveSetting(descriptor)
                    ? PluginConfigValueProtector.Protect(newValue)
                    : newValue;
                changes.Add(new PluginSettingChange(descriptor, key, storedValue));
                changed = true;
                requiresRestart |= descriptor.RestartRequired;
            }

            if (!changed)
            {
                ShowPageToast("配置没有变化", TimeSpan.FromMilliseconds(1600));
                return;
            }

            foreach (var change in changes)
            {
                SettingsService.EnsureSetting(
                    change.Key,
                    "插件配置",
                    string.IsNullOrWhiteSpace(change.Descriptor.Label) ? change.Descriptor.Key : change.Descriptor.Label!,
                    change.Descriptor.Description ?? string.Empty,
                    GetDefaultValueText(change.Descriptor),
                    NormalizeSettingType(change.Descriptor.Type),
                    0);
            }

            SettingsService.SaveBatch(changes.Select(change => (change.Key, change.Value)));

            if (!requiresRestart)
            {
                await ReloadPluginAfterSettingsSaveAsync(pluginId);
                return;
            }

            ShowPageToast("配置已保存，下次启动生效", TimeSpan.FromMilliseconds(1800));
        }
        catch (CryptographicException ex)
        {
            ShowPageToast($"敏感配置保护失败：{ex.Message}", TimeSpan.FromMilliseconds(2600));
        }
        catch (Exception ex)
        {
            ShowPageToast($"配置保存失败：{ex.Message}", TimeSpan.FromMilliseconds(2600));
        }
    }

    private async Task ReloadPluginAfterSettingsSaveAsync(string pluginId)
    {
        try
        {
            var loaded = await PluginLoader.ReloadPluginByIdAsync(pluginId);
            RefreshPlugins();
            var message = loaded
                ? "配置已保存，插件已重载"
                : "配置已保存，但插件重载未成功";
            ShowPageToast(message, TimeSpan.FromMilliseconds(1800));
        }
        catch (Exception ex)
        {
            ShowPageToast($"配置已保存，但插件重载失败：{ex.Message}", TimeSpan.FromMilliseconds(2600));
            return;
        }
    }

    private static string? ValidatePluginSettings(IReadOnlyList<PluginSettingEditor> editors)
    {
        foreach (var editor in editors)
        {
            var descriptor = editor.Descriptor;
            var label = GetSettingLabel(descriptor);
            var value = GetPluginSettingEditorValue(editor.Control, descriptor);
            var type = NormalizeSettingType(descriptor.Type);

            if (value is null)
            {
                if (descriptor.Required && !editor.HasStoredValue)
                {
                    return $"{label} 为必填项";
                }

                continue;
            }

            if (descriptor.Required && string.IsNullOrWhiteSpace(value))
            {
                return $"{label} 为必填项";
            }

            if (type == "number" && !ValidateNumberSetting(descriptor, value, label, out var numberError))
            {
                return numberError;
            }

            if (type == "json" && !ValidateJsonSetting(value, label, out var jsonError))
            {
                return jsonError;
            }

            if (IsTextLikeSettingType(type) && !ValidateTextLengthSetting(descriptor, value, label, out var lengthError))
            {
                return lengthError;
            }

            if (!ValidatePatternSetting(descriptor, value, label, out var patternError))
            {
                return patternError;
            }

            var options = descriptor.Options ?? [];
            if (type == "enum" && options.Count > 0
                && !options.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return $"{label} 的选项无效";
            }
        }

        return null;
    }

    private static bool ValidateNumberSetting(
        PluginSettingDescriptor descriptor,
        string value,
        string label,
        out string? error)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            error = $"{label} 必须是数字";
            return false;
        }

        if (TryGetValidationNumber(descriptor.Validation, "min", out var min) && number < min)
        {
            error = $"{label} 不能小于 {min.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (TryGetValidationNumber(descriptor.Validation, "max", out var max) && number > max)
        {
            error = $"{label} 不能大于 {max.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateTextLengthSetting(
        PluginSettingDescriptor descriptor,
        string value,
        string label,
        out string? error)
    {
        if (string.IsNullOrEmpty(value))
        {
            error = null;
            return true;
        }

        if (TryGetValidationNonNegativeInteger(descriptor.Validation, "minLength", out var minLength)
            && value.Length < minLength)
        {
            error = $"{label} 长度不能小于 {minLength.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (TryGetValidationNonNegativeInteger(descriptor.Validation, "maxLength", out var maxLength)
            && value.Length > maxLength)
        {
            error = $"{label} 长度不能大于 {maxLength.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ValidateJsonSetting(string value, string label, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = null;
            return true;
        }

        try
        {
            using var _ = JsonDocument.Parse(value);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = $"{label} 必须是有效 JSON";
            return false;
        }
    }

    private static bool ValidatePatternSetting(
        PluginSettingDescriptor descriptor,
        string value,
        string label,
        out string? error)
    {
        if (string.IsNullOrEmpty(value)
            || !TryGetValidationString(descriptor.Validation, "pattern", out var pattern)
            || string.IsNullOrWhiteSpace(pattern))
        {
            error = null;
            return true;
        }

        try
        {
            if (Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(250)))
            {
                error = null;
                return true;
            }

            error = $"{label} 格式不符合要求";
            return false;
        }
        catch (ArgumentException)
        {
            error = $"{label} 的格式校验规则无效";
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            error = $"{label} 的格式校验超时";
            return false;
        }
    }

    private static string? GetPluginSettingEditorValue(Control editor, PluginSettingDescriptor descriptor)
    {
        switch (editor)
        {
            case ToggleSwitch toggle:
                return (toggle.IsChecked ?? false).ToString().ToLowerInvariant();
            case NumericUpDown number:
                return (number.Value ?? 0).ToString(CultureInfo.InvariantCulture);
            case ComboBox combo:
                return combo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : string.Empty;
            case TextBox textBox when IsSensitiveSetting(descriptor)
                && string.IsNullOrEmpty(textBox.Text):
                return null;
            case TextBox textBox:
                return textBox.Text ?? string.Empty;
            default:
                return string.Empty;
        }
    }

    private static string GetPluginConfigKey(string pluginId, string key) => $"Plugin:{pluginId}:Config:{key}";

    private static bool IsSensitiveSetting(PluginSettingDescriptor descriptor)
    {
        return descriptor.Sensitive
            || string.Equals(NormalizeSettingType(descriptor.Type), "secret", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextLikeSettingType(string type)
    {
        return type is "string" or "path" or "secret";
    }

    private static string GetSettingLabel(PluginSettingDescriptor descriptor)
    {
        return string.IsNullOrWhiteSpace(descriptor.Label) ? descriptor.Key : descriptor.Label!;
    }

    private static string NormalizeSettingType(string? type)
    {
        return string.IsNullOrWhiteSpace(type)
            ? "string"
            : type.Trim().ToLowerInvariant();
    }

    private static string GetDefaultValueText(PluginSettingDescriptor descriptor)
    {
        if (descriptor.DefaultValue is not { } defaultValue)
        {
            return string.Empty;
        }

        return defaultValue.ValueKind switch
        {
            JsonValueKind.String => defaultValue.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => defaultValue.GetRawText(),
            JsonValueKind.Object or JsonValueKind.Array => defaultValue.GetRawText(),
            _ => string.Empty
        };
    }

    private static bool TryGetValidationNumber(JsonElement? validation, string propertyName, out decimal value)
    {
        value = 0;
        return validation is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(propertyName, out var property)
            && property.TryGetDecimal(out value);
    }

    private static bool TryGetValidationString(JsonElement? validation, string propertyName, out string value)
    {
        value = string.Empty;
        if (validation is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetValidationNonNegativeInteger(JsonElement? validation, string propertyName, out int value)
    {
        value = 0;
        return validation is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out value)
            && value >= 0;
    }

    private void BuildMcpDetails(McpServerEntity? server)
    {
        DetailPanel.Children.Clear();
        DetailPanel.RowDefinitions.Clear();
        DetailPanel.ColumnDefinitions.Clear();

        if (server is null)
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "请选择一个插件或 MCP 服务",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40)
            });
            return;
        }

        var isConnected = _activeMcpHosts.TryGetValue(server.Id, out var host);
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(14) };

        stack.Children.Add(new TextBlock
        {
            Text = server.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        });

        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(server.Description) ? "(无简介)" : server.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(BuildInfoText("类型：MCP 服务"));
        stack.Children.Add(BuildInfoText($"服务 ID：{server.Id}"));
        stack.Children.Add(BuildInfoText($"传输：{server.TransportType}"));
        stack.Children.Add(BuildInfoText($"状态：{(server.IsEnabled ? isConnected ? "已连接" : "已启用但未连接" : "已禁用")}"));
        stack.Children.Add(BuildInfoText($"地址：{GetMcpAddress(server)}"));
        stack.Children.Add(BuildInfoText($"工具数量：{(isConnected ? host!.Tools.Count : 0)}"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(BuildActionButton("重连", OnReconnectMcpClick, "btn-primary"));
        stack.Children.Add(buttons);

        stack.Children.Add(BuildSectionHeader("工具列表"));
        if (!isConnected || host!.Tools.Count == 0)
        {
            stack.Children.Add(BuildInfoText("该 MCP 服务当前没有可用工具。"));
        }
        else
        {
            stack.Children.Add(BuildInfoText(string.Join("，", host.Tools.Select(tool => tool.Name))));
        }

        stack.Children.Add(BuildSectionHeader("绑定智能体"));
        stack.Children.Add(BuildInfoText("勾选后会立即保存当前 MCP 服务和智能体的绑定关系。"));
        AddMcpAgentBindings(stack, server.Id);

        DetailPanel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = stack
        });
    }

    private void AddAgentBindings(Panel target, string pluginId)
    {
        var agents = AgentService.GetAll();
        if (agents.Count == 0)
        {
            target.Children.Add(BuildInfoText("当前没有可用智能体。"));
            return;
        }

        var bindingsGrid = CreateTwoColumnGrid();

        for (var index = 0; index < agents.Count; index++)
        {
            var agent = agents[index];
            var check = new CheckBox
            {
                Content = agent.Name,
                IsChecked = agent.BoundPlugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase),
                Foreground = (IBrush)this.FindResource("TextBrush")!,
                Tag = agent.Id,
                VerticalAlignment = VerticalAlignment.Center
            };
            check.IsCheckedChanged += (_, _) => SaveAgentBinding(agent.Id, pluginId, check.IsChecked == true);

            AddTwoColumnGridChild(bindingsGrid, index, new Border
            {
                Background = (IBrush)this.FindResource("Surface1Brush")!,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6),
                Margin = GetTwoColumnItemMargin(index),
                Child = check
            });
        }

        target.Children.Add(bindingsGrid);
    }

    private void AddMcpAgentBindings(Panel target, string serverId)
    {
        var agents = AgentService.GetAll();
        if (agents.Count == 0)
        {
            target.Children.Add(BuildInfoText("当前没有可用智能体。"));
            return;
        }

        var bindingsGrid = CreateTwoColumnGrid();

        for (var index = 0; index < agents.Count; index++)
        {
            var agent = agents[index];
            var check = new CheckBox
            {
                Content = agent.Name,
                IsChecked = agent.BoundMcp.Contains(serverId, StringComparer.OrdinalIgnoreCase),
                Foreground = (IBrush)this.FindResource("TextBrush")!,
                Tag = agent.Id,
                VerticalAlignment = VerticalAlignment.Center
            };
            check.IsCheckedChanged += (_, _) => SaveMcpAgentBinding(agent.Id, serverId, check.IsChecked == true);

            AddTwoColumnGridChild(bindingsGrid, index, new Border
            {
                Background = (IBrush)this.FindResource("Surface1Brush")!,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6),
                Margin = GetTwoColumnItemMargin(index),
                Child = check
            });
        }

        target.Children.Add(bindingsGrid);
    }

    private static Grid CreateTwoColumnGrid() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("*,*"),
        RowDefinitions = new RowDefinitions(),
        Margin = new Thickness(0, 2, 0, 0)
    };

    private static void AddTwoColumnGridChild(Grid grid, int index, Control child)
    {
        var row = index / 2;
        var column = index % 2;

        while (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static Thickness GetTwoColumnItemMargin(int index)
    {
        return index % 2 == 0
            ? new Thickness(0, 0, 6, 8)
            : new Thickness(6, 0, 0, 8);
    }

    private TextBlock BuildInfoText(string text) => new()
    {
        Text = text,
        Foreground = (IBrush)this.FindResource("SubtextBrush")!,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12
    };

    private TextBlock BuildSectionHeader(string text) => new()
    {
        Text = text,
        Foreground = (IBrush)this.FindResource("TextBrush")!,
        FontWeight = FontWeight.SemiBold,
        FontSize = 12,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private Button BuildActionButton(string text, EventHandler<RoutedEventArgs> handler, string cssClass)
    {
        var button = new Button { Content = text };
        button.Classes.Add(cssClass);
        button.Click += handler;
        return button;
    }

    private void ShowPageToast(string message, TimeSpan duration)
    {
        Netor.Cortana.UI.UiPromptService.ShowInlineToast(ToastBorder, ToastText, message, ref _toastTimer, duration);
    }

    private void SaveAgentBinding(string agentId, string pluginId, bool shouldBind)
    {
        var agent = AgentService.GetByName(agentId);
        if (agent is null) return;

        var hasBinding = agent.BoundPlugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
        if (shouldBind == hasBinding) return;

        if (shouldBind)
        {
            agent.BoundPlugins = [.. agent.BoundPlugins, pluginId];
        }
        else
        {
            agent.BoundPlugins = [.. agent.BoundPlugins.Where(id => !string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase))];
        }

        AgentService.Update(agent);
        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(agent.Id, ChangeType.Update));
    }

    private void SaveMcpAgentBinding(string agentId, string serverId, bool shouldBind)
    {
        var agent = AgentService.GetByName(agentId);
        if (agent is null) return;

        var hasBinding = agent.BoundMcp.Contains(serverId, StringComparer.OrdinalIgnoreCase);
        if (shouldBind == hasBinding) return;

        if (shouldBind)
        {
            agent.BoundMcp = [.. agent.BoundMcp, serverId];
        }
        else
        {
            agent.BoundMcp = [.. agent.BoundMcp.Where(id => !string.Equals(id, serverId, StringComparison.OrdinalIgnoreCase))];
        }

        AgentService.Update(agent);
        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(agent.Id, ChangeType.Update));
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => BuildPluginList();

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => RefreshPlugins();

    private void OnOpenGlobalDirectoryClick(object? sender, RoutedEventArgs e) => OpenDirectory(AppPaths.UserPluginsDirectory);

    private void OnOpenStoreClick(object? sender, RoutedEventArgs e)
    {
        var storeWindow = App.Services.GetRequiredService<StoreWindow>();
        storeWindow.Show();
        storeWindow.Activate();
    }

    private void OnOpenDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedPlugin is null) return;
        OpenDirectory(_selectedPlugin.DirectoryPath);
    }

    private void OnUnloadClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedPlugin is null) return;
        PluginLoader.UnloadPlugin(_selectedPlugin.DirectoryName);
        RefreshPlugins();
    }

    private async void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedPlugin is null) return;
        try
        {
            var loaded = await PluginLoader.ReloadPluginAsync(_selectedPlugin.DirectoryName);
            RefreshPlugins();
            if (!loaded)
            {
                ShowPageToast("插件重载未成功", TimeSpan.FromMilliseconds(1800));
            }
        }
        catch (Exception ex)
        {
            ShowPageToast($"插件重载失败：{ex.Message}", TimeSpan.FromMilliseconds(2200));
        }
    }

    private async void OnReconnectMcpClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedMcpServerId is null) return;
        await PluginLoader.ReconnectMcpAsync(_selectedMcpServerId, McpServerService);
        RefreshPlugins();
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedPlugin is null) return;

        var pluginInfo = _selectedPlugin;
        PluginLoader.UnloadPlugin(pluginInfo.DirectoryName);
        TryDeletePluginDirectory(pluginInfo.DirectoryPath);
        GlobalPluginService.Remove(pluginInfo.Plugin.Id);
        RemovePluginBindingFromAllAgents(pluginInfo.Plugin.Id);
        _selectedPlugin = null;
        RefreshPlugins();
    }

    private void RemovePluginBindingFromAllAgents(string pluginId)
    {
        foreach (var agent in AgentService.GetAll())
        {
            if (!agent.BoundPlugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase)) continue;

            agent.BoundPlugins = [.. agent.BoundPlugins.Where(id => !string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase))];
            AgentService.Update(agent);
            Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(agent.Id, ChangeType.Update));
        }
    }

    private void TryDeletePluginDirectory(string directoryPath)
    {
        if (!IsPluginDirectory(directoryPath)) return;
        if (Directory.Exists(directoryPath))
            Directory.Delete(directoryPath, recursive: true);
    }

    private bool IsPluginDirectory(string directoryPath)
    {
        var fullPath = NormalizeDirectory(directoryPath);
        var userRoot = NormalizeDirectory(AppPaths.UserPluginsDirectory);

        return fullPath.StartsWith(userRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static bool MatchesSearch(LoadedPluginInfo pluginInfo, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var plugin = pluginInfo.Plugin;
        return plugin.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || plugin.Description.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSearch(McpServerEntity server, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        return server.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || server.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || server.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            || server.TransportType.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMcpAddress(McpServerEntity server)
    {
        return server.TransportType == "stdio"
            ? string.Join(" ", new[] { server.Command }.Concat(server.Arguments).Where(value => !string.IsNullOrWhiteSpace(value)))
            : server.Url;
    }

    private static void OpenDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        Process.Start(new ProcessStartInfo(directoryPath) { UseShellExecute = true });
    }

    private sealed record PluginSettingEditor(PluginSettingDescriptor Descriptor, Control Control, bool HasStoredValue);

    private sealed record PluginSettingChange(PluginSettingDescriptor Descriptor, string Key, string Value);
}
