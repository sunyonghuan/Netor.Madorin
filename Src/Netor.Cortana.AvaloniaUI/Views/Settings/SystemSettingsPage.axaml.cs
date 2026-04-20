using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Networks;
using Netor.Cortana.Voice;

using System.Net;
using System.Net.Sockets;

namespace Netor.Cortana.AvaloniaUI.Views.Settings;

public partial class SystemSettingsPage : UserControl
{
    private SystemSettingsService SettingsService => App.Services.GetRequiredService<SystemSettingsService>();

    // 保存每个设置项的输入控件，key = entity.Id
    private readonly Dictionary<string, Control> _editors = [];

    public SystemSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        SettingsContainer.Children.Clear();
        _editors.Clear();

        var allSettings = SettingsService.GetAll();
        var groups = allSettings.GroupBy(s => s.Group).OrderBy(g => g.Min(s => s.SortOrder));

        foreach (var group in groups)
        {
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

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
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

        // 端口冲突检测：保存前验证新端口是否可用
        var portEntry = updates.FirstOrDefault(u => u.Key == "WebSocket.Port");
        if (portEntry != default)
        {
            var oldPort = SettingsService.GetValue<int>("WebSocket.Port", 52841);
            if (int.TryParse(portEntry.Value, out var newPort) && newPort != oldPort)
            {
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

        // 注：修改欢迎语后，应用下次启动时会自动使用新的欢迎语重新生成语音缓存
        // 无需在这里同步更新，避免在后台线程中调用服务导致并发问题

        // 如果端口发生了变化，重启 WebSocket 服务
        if (portEntry != default)
        {
            var currentServer = App.Services.GetRequiredService<WebSocketServerService>();
            var oldPort = currentServer.Port;
            if (int.TryParse(portEntry.Value, out var newPort) && newPort != oldPort)
            {
                await currentServer.StopAsync(CancellationToken.None);
                await currentServer.StartAsync(CancellationToken.None);

                await ShowDialogAsync("端口已修改",
                    $"WebSocket 端口已从 {oldPort} 切换到 {currentServer.Port}，已立即生效。\n已加载的插件可能仍使用旧端口，建议重启软件。");
            }
        }
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
    /// 显示提示对话框。
    /// </summary>
    private async Task ShowDialogAsync(string title, string message)
    {
        try
        {
            var td = new FATaskDialog
            {
                Title = title,
                Content = message,
                Buttons = { FATaskDialogButton.OKButton },
            };

            // 尝试设置 XamlRoot，但如果失败则直接显示
            var root = TopLevel.GetTopLevel(this);
            if (root is not null)
                td.XamlRoot = root;

            await td.ShowAsync();
        }
        catch
        {
            // FATaskDialog 失败时回退到简单提示
            System.Diagnostics.Debug.WriteLine($"{title}: {message}");
        }
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        SettingsService.ResetAllToDefault();
        LoadSettings();
    }
}