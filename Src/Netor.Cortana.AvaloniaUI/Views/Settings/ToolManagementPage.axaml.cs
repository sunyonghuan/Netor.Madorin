using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using Netor.Cortana.Plugin;
using Netor.Cortana.Plugin.Abstractions;

namespace Netor.Cortana.AvaloniaUI.Views.Settings;

public partial class ToolManagementPage : UserControl
{
    private AgentService AgentService => App.Services.GetRequiredService<AgentService>();
    private PluginLoader PluginLoader => App.Services.GetRequiredService<PluginLoader>();

    private string? _selectedAgentId;

    // 记录当前勾选的插件ID和MCP服务器ID
    private readonly HashSet<string> _enabledPluginIds = [];
    private readonly HashSet<string> _enabledMcpServerIds = [];

    public ToolManagementPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadAgents();
    }

    // ──────── 智能体下拉 ────────

    private void LoadAgents()
    {
        CboAgent.Items.Clear();
        CboAgent.Items.Add(new ComboBoxItem { Content = "— 请选择 —", Tag = "" });
        foreach (var a in AgentService.GetAll())
        {
            CboAgent.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a.Id });
        }
        CboAgent.SelectedIndex = 0;
    }

    private void OnAgentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboAgent is null || ToolsContainer is null) return;
        if (CboAgent.SelectedItem is ComboBoxItem { Tag: string id } && !string.IsNullOrEmpty(id))
        {
            _selectedAgentId = id;
            var agent = AgentService.GetById(id);
            if (agent is null) return;

            _enabledPluginIds.Clear();
            _enabledMcpServerIds.Clear();
            foreach (var pid in agent.EnabledPluginIds) _enabledPluginIds.Add(pid);
            foreach (var mid in agent.EnabledMcpServerIds) _enabledMcpServerIds.Add(mid);

            BuildToolsList();
            BtnSave.IsVisible = true;
        }
        else
        {
            _selectedAgentId = null;
            ToolsContainer.Children.Clear();
            BtnSave.IsVisible = false;
        }
    }

    // ──────── 工具列表构建 ────────

    private void BuildToolsList()
    {
        ToolsContainer.Children.Clear();

        // 本地插件
        var plugins = PluginLoader.GetActivePlugins();
        if (plugins.Count > 0)
        {
            ToolsContainer.Children.Add(BuildSectionHeader("本地插件"));
            foreach (var plugin in plugins)
            {
                ToolsContainer.Children.Add(BuildPluginGroup(plugin));
            }
        }

        // MCP 服务器
        var mcpServers = PluginLoader.GetActiveMcpServers();
        if (mcpServers.Count > 0)
        {
            ToolsContainer.Children.Add(BuildSectionHeader("MCP 服务器"));
            foreach (var mcp in mcpServers)
            {
                ToolsContainer.Children.Add(BuildMcpGroup(mcp));
            }
        }

        if (plugins.Count == 0 && mcpServers.Count == 0)
        {
            ToolsContainer.Children.Add(new TextBlock
            {
                Text = "暂无可用插件或 MCP 服务器",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20),
            });
        }
    }

    private static TextBlock BuildSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Gray, // will be overridden by resource at runtime
            Margin = new Thickness(0, 8, 0, 4),
        };
    }

    private Border BuildPluginGroup(IPlugin plugin)
    {
        var isEnabled = _enabledPluginIds.Contains(plugin.Id);

        var chk = new CheckBox
        {
            Content = $"{plugin.Name}  ({plugin.Tools.Count} 个工具)",
            IsChecked = isEnabled,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontWeight = FontWeight.Medium,
            Tag = plugin.Id,
        };
        chk.IsCheckedChanged += (_, _) =>
        {
            if (chk.IsChecked == true) _enabledPluginIds.Add(plugin.Id);
            else _enabledPluginIds.Remove(plugin.Id);
        };

        var desc = new TextBlock
        {
            Text = plugin.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 4),
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(chk);
        if (!string.IsNullOrWhiteSpace(plugin.Description)) stack.Children.Add(desc);

        var border = new Border
        {
            Background = (IBrush)this.FindResource("Surface0Brush")!,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 8),
            Child = stack,
        };

        border.PointerEntered += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface1Brush")!;
        border.PointerExited += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface0Brush")!;

        return border;
    }

    private Border BuildMcpGroup(McpServerHost mcp)
    {
        var isEnabled = _enabledMcpServerIds.Contains(mcp.Id);

        var chk = new CheckBox
        {
            Content = $"{mcp.Name}  ({mcp.Tools.Count} 个工具)",
            IsChecked = isEnabled,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontWeight = FontWeight.Medium,
            Tag = mcp.Id,
        };
        chk.IsCheckedChanged += (_, _) =>
        {
            if (chk.IsChecked == true) _enabledMcpServerIds.Add(mcp.Id);
            else _enabledMcpServerIds.Remove(mcp.Id);
        };

        var desc = new TextBlock
        {
            Text = mcp.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 0, 0, 4),
        };

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(chk);
        if (!string.IsNullOrWhiteSpace(mcp.Description)) stack.Children.Add(desc);

        var border = new Border
        {
            Background = (IBrush)this.FindResource("Surface0Brush")!,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 8),
            Child = stack,
        };

        border.PointerEntered += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface1Brush")!;
        border.PointerExited += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface0Brush")!;

        return border;
    }

    // ──────── 保存 ────────

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedAgentId is null) return;

        var agent = AgentService.GetById(_selectedAgentId);
        if (agent is null) return;

        agent.EnabledPluginIds = [.. _enabledPluginIds];
        agent.EnabledMcpServerIds = [.. _enabledMcpServerIds];
        AgentService.Update(agent);
    }
}
