using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.Cortana.Plugin;

namespace Netor.Cortana.AvaloniaUI.Views.Settings;

public partial class McpServerSettingsPage : UserControl
{
    private McpServerService McpService => App.Services.GetRequiredService<McpServerService>();
    private PluginLoader PluginLoader => App.Services.GetRequiredService<PluginLoader>();

    private string? _editingId;

    public McpServerSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    // ──────── 传输类型切换 ────────

    private void OnTransportChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboTransport is null || StdioFields is null || HttpFields is null) return;
        if (CboTransport.SelectedItem is ComboBoxItem { Tag: string transport })
        {
            StdioFields.IsVisible = transport == "stdio";
            HttpFields.IsVisible = transport != "stdio";
        }
    }

    // ──────── 列表视图 ────────

    private void RefreshList()
    {
        McpListPanel.Children.Clear();
        var list = McpService.GetAll();

        if (list.Count == 0)
        {
            McpListPanel.Children.Add(new TextBlock
            {
                Text = "暂无 MCP 服务，点击右上角添加",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                Margin = new Thickness(0, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }

        foreach (var m in list)
        {
            McpListPanel.Children.Add(BuildListItem(m));
        }
    }

    private Border BuildListItem(McpServerEntity entity)
    {
        var name = new TextBlock
        {
            Text = entity.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
        };

        var sub = new TextBlock
        {
            Text = entity.TransportType + (entity.TransportType == "stdio" ? $"  ·  {entity.Command}" : $"  ·  {entity.Url}"),
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(name);
        left.Children.Add(sub);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        var statusText = entity.IsEnabled ? "已启用" : "已禁用";
        var statusColor = entity.IsEnabled
            ? (IBrush)this.FindResource("GreenBrush")!
            : (IBrush)this.FindResource("SubtextBrush")!;
        right.Children.Add(new TextBlock
        {
            Text = statusText,
            Foreground = statusColor,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var editBtn = new Button
        {
            Content = "编辑",
            Padding = new Thickness(10, 3),
            FontSize = 12,
        };
        editBtn.Classes.Add("btn-secondary");
        editBtn.Click += (_, _) => EditMcp(entity.Id);
        right.Children.Add(editBtn);

        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        var border = new Border
        {
            Background = (IBrush)this.FindResource("Surface0Brush")!,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 8),
            Child = grid,
            Tag = entity.Id,
        };

        border.PointerEntered += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface1Brush")!;
        border.PointerExited += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface0Brush")!;

        return border;
    }

    // ──────── 视图切换 ────────

    private void ShowForm(string title)
    {
        FormTitle.Text = title;
        ListView.IsVisible = false;
        FormView.IsVisible = true;
    }

    private void ShowList()
    {
        _editingId = null;
        FormView.IsVisible = false;
        ListView.IsVisible = true;
        ClearForm();
        RefreshList();
    }

    private void ClearForm()
    {
        TxtName.Text = string.Empty;
        CboTransport.SelectedIndex = 0;
        TxtCommand.Text = string.Empty;
        TxtArguments.Text = string.Empty;
        TxtEnvVars.Text = string.Empty;
        TxtUrl.Text = string.Empty;
        TxtApiKey.Text = string.Empty;
        TxtDesc.Text = string.Empty;
        ChkEnabled.IsChecked = true;
        BtnDelete.IsVisible = false;
        LblTestResult.Text = string.Empty;
    }

    // ──────── 添加/编辑 ────────

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        ClearForm();
        ShowForm("添加 MCP 服务");
    }

    private void EditMcp(string id)
    {
        var entity = McpService.GetById(id);
        if (entity is null) return;

        _editingId = id;
        TxtName.Text = entity.Name;
        TxtDesc.Text = entity.Description;
        ChkEnabled.IsChecked = entity.IsEnabled;
        BtnDelete.IsVisible = true;

        // 选中传输类型
        for (int i = 0; i < CboTransport.Items.Count; i++)
        {
            if (CboTransport.Items[i] is ComboBoxItem cbi && cbi.Tag is string tag && tag == entity.TransportType)
            {
                CboTransport.SelectedIndex = i;
                break;
            }
        }

        // 填充对应字段
        TxtCommand.Text = entity.Command;
        TxtArguments.Text = string.Join(",", entity.Arguments);
        TxtEnvVars.Text = string.Join("\n", entity.EnvironmentVariables.Select(kv => $"{kv.Key}={kv.Value}"));
        TxtUrl.Text = entity.Url;
        TxtApiKey.Text = entity.ApiKey;

        ShowForm("编辑 MCP 服务");
    }

    // ──────── 保存/取消/删除/测试 ────────

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = TxtName.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var transport = CboTransport.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t ? t : "stdio";
        McpServerEntity? entity;

        if (_editingId is not null)
        {
            entity = McpService.GetById(_editingId);
            if (entity is null) return;
            FillEntity(entity, name, transport);
            McpService.Update(entity);
        }
        else
        {
            entity = new McpServerEntity();
            FillEntity(entity, name, transport);
            McpService.Add(entity);
        }

        if (entity is null)
            return;

        try
        {
            if (entity.IsEnabled)
            {
                await PluginLoader.AddMcpServerAsync(entity);
            }
            else
            {
                await PluginLoader.RemoveMcpServerAsync(entity.Id);
            }
        }
        catch (Exception ex)
        {
            LblTestResult.Text = $"保存成功，但接入运行时失败: {ex.Message}";
            LblTestResult.Foreground = (IBrush)this.FindResource("RedBrush")!;
            return;
        }

        ShowList();
    }

    private void FillEntity(McpServerEntity entity, string name, string transport)
    {
        entity.Name = name;
        entity.TransportType = transport;
        entity.Description = TxtDesc.Text?.Trim() ?? string.Empty;
        entity.IsEnabled = ChkEnabled.IsChecked ?? true;

        if (transport == "stdio")
        {
            entity.Command = TxtCommand.Text?.Trim() ?? string.Empty;
            entity.Arguments = (TxtArguments.Text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            entity.EnvironmentVariables = ParseEnvVars(TxtEnvVars.Text ?? string.Empty);
            entity.Url = string.Empty;
            entity.ApiKey = string.Empty;
        }
        else
        {
            entity.Url = TxtUrl.Text?.Trim() ?? string.Empty;
            entity.ApiKey = TxtApiKey.Text?.Trim() ?? string.Empty;
            entity.Command = string.Empty;
            entity.Arguments = [];
            entity.EnvironmentVariables = [];
        }
    }

    private static Dictionary<string, string?> ParseEnvVars(string text)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
            {
                dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }
        return dict;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => ShowList();

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_editingId is null) return;

        await PluginLoader.RemoveMcpServerAsync(_editingId);
        McpService.Delete(_editingId);
        ShowList();
    }

    private void OnTestClick(object? sender, RoutedEventArgs e)
    {
        // 用表单当前值构造临时实体来测试
        var transport = CboTransport.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t ? t : "stdio";
        var entity = new McpServerEntity();
        FillEntity(entity, TxtName.Text?.Trim() ?? "test", transport);

        LblTestResult.Text = "测试中...";
        LblTestResult.Foreground = (IBrush)this.FindResource("SubtextBrush")!;

        _ = Task.Run(async () =>
        {
            try
            {
                var loggerFactory = App.Services.GetRequiredService<ILoggerFactory>();
                var host = new McpServerHost(entity, loggerFactory);
                await host.ConnectAsync(CancellationToken.None);
                var toolCount = host.Tools.Count;
                await host.DisposeAsync();

                Dispatcher.UIThread.Post(() =>
                {
                    LblTestResult.Text = $"连接成功，发现 {toolCount} 个工具";
                    LblTestResult.Foreground = (IBrush)this.FindResource("GreenBrush")!;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    LblTestResult.Text = $"连接失败: {ex.Message}";
                    LblTestResult.Foreground = (IBrush)this.FindResource("RedBrush")!;
                });
            }
        });
    }
}
