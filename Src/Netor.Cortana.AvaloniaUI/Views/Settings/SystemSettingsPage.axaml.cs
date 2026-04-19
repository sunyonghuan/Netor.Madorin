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
            Width = 180,
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
            "model" => BuildModelComboBox(entity.Value),
            _ => new TextBox
            {
                Text = entity.Value,
                Width = 250,
                Classes = { "form-input" },
            },
        };

        _editors[entity.Id] = editor;

        var editorRow = new DockPanel { Margin = new Thickness(0, 4) };
        editorRow.Children.Add(label);
        editorRow.Children.Add(editor);

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

    private ComboBox BuildModelComboBox(string currentValue)
    {
        var modelService = App.Services.GetRequiredService<AiModelService>();
        var providerService = App.Services.GetRequiredService<AiProviderService>();

        var cbo = new ComboBox { MinWidth = 280 };
        cbo.Classes.Add("form-combo");
        cbo.Items.Add(new ComboBoxItem { Content = "（跟随当前模型）", Tag = "" });

        var providers = providerService.GetAll();
        int selectedIndex = 0, index = 1;

        foreach (var p in providers)
        {
            foreach (var m in modelService.GetByProviderId(p.Id))
            {
                var displayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Name : m.DisplayName;
                cbo.Items.Add(new ComboBoxItem { Content = $"{p.Name} / {displayName}", Tag = m.Id });
                if (m.Id == currentValue) selectedIndex = index;
                index++;
            }
        }

        cbo.SelectedIndex = selectedIndex;
        return cbo;
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