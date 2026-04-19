using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

using Netor.Cortana.AI;

namespace Netor.Cortana.AvaloniaUI.Views.Settings;

public partial class ProviderSettingsPage : UserControl
{
    private AIAgentFactory AgentFactory => App.Services.GetRequiredService<AIAgentFactory>();
    private AiProviderService ProviderService => App.Services.GetRequiredService<AiProviderService>();
    private AiModelService ModelService => App.Services.GetRequiredService<AiModelService>();
    private IPublisher Publisher => App.Services.GetRequiredService<IPublisher>();

    private string? _editingId;

    public ProviderSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadDriverTypes();
            RefreshList();
        };
    }

    // ──────── 列表视图 ────────

    private void RefreshList()
    {
        ProviderListPanel.Children.Clear();
        var list = ProviderService.GetAll();

        if (list.Count == 0)
        {
            ProviderListPanel.Children.Add(new TextBlock
            {
                Text = "暂无厂商，点击右上角添加",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                Margin = new Thickness(0, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }

        foreach (var p in list)
        {
            var item = BuildListItem(p);
            ProviderListPanel.Children.Add(item);
        }
    }

    private Border BuildListItem(AiProviderEntity entity)
    {
        var driverName = GetDriverDisplayName(entity.ProviderType);
        var name = new TextBlock
        {
            Text = entity.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
        };

        var sub = new TextBlock
        {
            Text = $"{driverName}  ·  {entity.Url}",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(name);
        left.Children.Add(sub);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        if (entity.IsDefault)
        {
            right.Children.Add(new TextBlock
            {
                Text = "默认",
                Foreground = (IBrush)this.FindResource("BlueBrush")!,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

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
        editBtn.Click += (_, _) => EditProvider(entity.Id);
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

    private void LoadDriverTypes()
    {
        CboType.Items.Clear();

        foreach (var definition in AgentFactory.GetDriverDefinitions())
        {
            CboType.Items.Add(new ComboBoxItem
            {
                Content = definition.DisplayName,
                Tag = definition.Id
            });
        }

        if (CboType.ItemCount > 0)
        {
            CboType.SelectedIndex = 0;
        }
    }

    private string GetDriverDisplayName(string driverId)
    {
        var definition = AgentFactory.GetDriverDefinitions()
            .FirstOrDefault(item => string.Equals(item.Id, driverId, StringComparison.OrdinalIgnoreCase));

        return definition?.DisplayName ?? driverId;
    }

    private void SelectDriver(string driverId)
    {
        for (int i = 0; i < CboType.Items.Count; i++)
        {
            if (CboType.Items[i] is ComboBoxItem cbi && cbi.Tag is string tag && tag == driverId)
            {
                CboType.SelectedIndex = i;
                return;
            }
        }

        if (CboType.ItemCount > 0)
        {
            CboType.SelectedIndex = 0;
        }
    }

    private void ClearForm()
    {
        TxtName.Text = string.Empty;
        TxtUrl.Text = string.Empty;
        TxtKey.Text = string.Empty;
        TxtAuthToken.Text = string.Empty;
        TxtDesc.Text = string.Empty;
        if (CboType.ItemCount > 0)
        {
            CboType.SelectedIndex = 0;
        }
        ChkEnabled.IsChecked = true;
        ChkDefault.IsChecked = false;
        BtnDelete.IsVisible = false;
    }

    // ──────── 添加/编辑 ────────

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        ClearForm();
        ShowForm("添加厂商");
    }

    private void EditProvider(string id)
    {
        var entity = ProviderService.GetById(id);
        if (entity is null) return;

        _editingId = id;
        TxtName.Text = entity.Name;
        TxtUrl.Text = entity.Url;
        TxtKey.Text = entity.Key;
        TxtAuthToken.Text = entity.AuthToken;
        TxtDesc.Text = entity.Description;
        ChkEnabled.IsChecked = entity.IsEnabled;
        ChkDefault.IsChecked = entity.IsDefault;
        BtnDelete.IsVisible = true;

        SelectDriver(entity.ProviderType);

        ShowForm("编辑厂商");
    }

    // ──────── 保存/取消/删除 ────────

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = TxtName.Text?.Trim();
        var url = TxtUrl.Text?.Trim();
        var key = TxtKey.Text?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key)) return;

        var providerType = CboType.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t ? t : "OpenAI";
        if (!AgentFactory.IsDriverRegistered(providerType)) return;

        if (_editingId is not null)
        {
            var entity = ProviderService.GetById(_editingId);
            if (entity is null) return;
            entity.Name = name;
            entity.Url = url;
            entity.Key = key;
            entity.AuthToken = TxtAuthToken.Text?.Trim() ?? string.Empty;
            entity.ProviderType = providerType;
            entity.Description = TxtDesc.Text?.Trim() ?? string.Empty;
            entity.IsEnabled = ChkEnabled.IsChecked ?? true;
            entity.IsDefault = ChkDefault.IsChecked ?? false;
            ProviderService.Update(entity);
            if (entity.IsDefault)
                ProviderService.SetDefault(entity.Id);
            Publisher.Publish(Events.OnAiProviderChange, new DataChangeArgs(entity.Id, ChangeType.Update));
        }
        else
        {
            var entity = new AiProviderEntity
            {
                Name = name,
                Url = url,
                Key = key,
                AuthToken = TxtAuthToken.Text?.Trim() ?? string.Empty,
                ProviderType = providerType,
                Description = TxtDesc.Text?.Trim() ?? string.Empty,
                IsEnabled = ChkEnabled.IsChecked ?? true,
                IsDefault = ChkDefault.IsChecked ?? false,
            };
            ProviderService.Add(entity);
            if (entity.IsDefault)
                ProviderService.SetDefault(entity.Id);
            Publisher.Publish(Events.OnAiProviderChange, new DataChangeArgs(entity.Id, ChangeType.Create));
        }

        ShowList();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => ShowList();

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_editingId is null) return;
        ModelService.DeleteByProviderId(_editingId);
        ProviderService.Delete(_editingId);
        Publisher.Publish(Events.OnAiProviderChange, new DataChangeArgs(_editingId, ChangeType.Delete));
        ShowList();
    }
}
