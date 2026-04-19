using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Netor.Cortana.AvaloniaUI.Views.Settings;

public partial class AgentSettingsPage : UserControl
{
    private AgentService AgentService => App.Services.GetRequiredService<AgentService>();
    private AiProviderService ProviderService => App.Services.GetRequiredService<AiProviderService>();
    private AiModelService ModelService => App.Services.GetRequiredService<AiModelService>();
    private IPublisher Publisher => App.Services.GetRequiredService<IPublisher>();

    private string? _editingId;
    private List<AiProviderEntity> _providerList = [];
    private List<AiModelEntity> _modelList = [];

    public AgentSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    // ──────── Slider 值显示 ────────

    private void OnTemperatureChanged(object? sender, RangeBaseValueChangedEventArgs e)
    { if (LblTemperature is not null) LblTemperature.Text = e.NewValue.ToString("F2"); }

    private void OnTopPChanged(object? sender, RangeBaseValueChangedEventArgs e)
    { if (LblTopP is not null) LblTopP.Text = e.NewValue.ToString("F2"); }

    private void OnFreqPenaltyChanged(object? sender, RangeBaseValueChangedEventArgs e)
    { if (LblFreqPenalty is not null) LblFreqPenalty.Text = e.NewValue.ToString("F2"); }

    private void OnPresPenaltyChanged(object? sender, RangeBaseValueChangedEventArgs e)
    { if (LblPresPenalty is not null) LblPresPenalty.Text = e.NewValue.ToString("F2"); }

    // ──────── 列表视图 ────────

    private void RefreshList()
    {
        AgentListPanel.Children.Clear();
        var list = AgentService.GetAll();

        if (list.Count == 0)
        {
            AgentListPanel.Children.Add(new TextBlock
            {
                Text = "暂无智能体，点击右上角添加",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                Margin = new Thickness(0, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return;
        }

        foreach (var a in list)
        {
            AgentListPanel.Children.Add(BuildListItem(a));
        }
    }

    private Border BuildListItem(AgentEntity entity)
    {
        var name = new TextBlock
        {
            Text = entity.Name,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
        };

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entity.Description) ? "(无描述)" : entity.Description,
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(name);
        left.Children.Add(desc);

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

        var editBtn = new Button
        {
            Content = "编辑",
            Padding = new Thickness(10, 3),
            FontSize = 12,
        };
        editBtn.Classes.Add("btn-secondary");
        editBtn.Click += (_, _) => EditAgent(entity.Id);
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
        TxtDesc.Text = string.Empty;
        TxtInstructions.Text = string.Empty;
        SldTemperature.Value = 0.7;
        SldTopP.Value = 1.0;
        SldFreqPenalty.Value = 0;
        SldPresPenalty.Value = 0;
        NudMaxTokens.Value = 0;
        NudMaxHistory.Value = 0;
        ChkEnabled.IsChecked = true;
        ChkDefault.IsChecked = false;
        BtnDelete.IsVisible = false;

        PopulateProviderComboBox(string.Empty);
        PopulateModelComboBox(string.Empty, string.Empty);
    }

    // ──────── 添加/编辑 ────────

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        ClearForm();
        ShowForm("添加智能体");
    }

    private void EditAgent(string id)
    {
        var entity = AgentService.GetById(id);
        if (entity is null) return;

        _editingId = id;
        TxtName.Text = entity.Name;
        TxtDesc.Text = entity.Description;
        TxtInstructions.Text = entity.Instructions;
        SldTemperature.Value = entity.Temperature;
        SldTopP.Value = entity.TopP;
        SldFreqPenalty.Value = entity.FrequencyPenalty;
        SldPresPenalty.Value = entity.PresencePenalty;
        NudMaxTokens.Value = entity.MaxTokens;
        NudMaxHistory.Value = entity.MaxHistoryMessages;
        ChkEnabled.IsChecked = entity.IsEnabled;
        ChkDefault.IsChecked = entity.IsDefault;
        BtnDelete.IsVisible = true;

        PopulateProviderComboBox(entity.DefaultProviderId);
        PopulateModelComboBox(entity.DefaultProviderId, entity.DefaultModelId);

        ShowForm("编辑智能体");
    }

    // ──────── 保存/取消/删除 ────────

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = TxtName.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (_editingId is not null)
        {
            var entity = AgentService.GetById(_editingId);
            if (entity is null) return;
            entity.Name = name;
            entity.Description = TxtDesc.Text?.Trim() ?? string.Empty;
            entity.Instructions = TxtInstructions.Text ?? string.Empty;
            entity.Temperature = SldTemperature.Value;
            entity.TopP = SldTopP.Value;
            entity.FrequencyPenalty = SldFreqPenalty.Value;
            entity.PresencePenalty = SldPresPenalty.Value;
            entity.MaxTokens = (int)(NudMaxTokens.Value ?? 0);
            entity.MaxHistoryMessages = (int)(NudMaxHistory.Value ?? 0);
            entity.IsEnabled = ChkEnabled.IsChecked ?? true;
            entity.IsDefault = ChkDefault.IsChecked ?? false;
            entity.DefaultProviderId = GetSelectedProviderId();
            entity.DefaultModelId = GetSelectedModelId();
            AgentService.Update(entity);
            if (entity.IsDefault)
                AgentService.SetDefault(entity.Id);
            Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(entity.Id, ChangeType.Update));
        }
        else
        {
            var entity = new AgentEntity
            {
                Name = name,
                Description = TxtDesc.Text?.Trim() ?? string.Empty,
                Instructions = TxtInstructions.Text ?? string.Empty,
                Temperature = SldTemperature.Value,
                TopP = SldTopP.Value,
                FrequencyPenalty = SldFreqPenalty.Value,
                PresencePenalty = SldPresPenalty.Value,
                MaxTokens = (int)(NudMaxTokens.Value ?? 0),
                MaxHistoryMessages = (int)(NudMaxHistory.Value ?? 0),
                IsEnabled = ChkEnabled.IsChecked ?? true,
                IsDefault = ChkDefault.IsChecked ?? false,
                DefaultProviderId = GetSelectedProviderId(),
                DefaultModelId = GetSelectedModelId(),
            };
            AgentService.Add(entity);
            if (entity.IsDefault)
                AgentService.SetDefault(entity.Id);
            Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(entity.Id, ChangeType.Create));
        }

        ShowList();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => ShowList();

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_editingId is null) return;
        AgentService.Delete(_editingId);
        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(_editingId, ChangeType.Delete));
        ShowList();
    }

    // ──────── 厂商/模型联动 ────────

    private void PopulateProviderComboBox(string selectedId)
    {
        _providerList = ProviderService.GetAll();
        CmbProvider.Items.Clear();
        CmbProvider.Items.Add(new ComboBoxItem { Content = "（跟随会话）", Tag = "" });

        var selectedIndex = 0;
        for (var i = 0; i < _providerList.Count; i++)
        {
            CmbProvider.Items.Add(new ComboBoxItem { Content = _providerList[i].Name, Tag = _providerList[i].Id });
            if (_providerList[i].Id == selectedId)
                selectedIndex = i + 1;
        }
        CmbProvider.SelectedIndex = selectedIndex;
    }

    private void PopulateModelComboBox(string providerId, string selectedModelId)
    {
        CmbModel.Items.Clear();
        CmbModel.Items.Add(new ComboBoxItem { Content = "（跟随会话）", Tag = "" });

        if (string.IsNullOrEmpty(providerId))
        {
            _modelList = [];
            CmbModel.SelectedIndex = 0;
            return;
        }

        _modelList = ModelService.GetByProviderId(providerId);
        var selectedIndex = 0;
        for (var i = 0; i < _modelList.Count; i++)
        {
            var display = string.IsNullOrEmpty(_modelList[i].DisplayName) ? _modelList[i].Name : _modelList[i].DisplayName;
            CmbModel.Items.Add(new ComboBoxItem { Content = display, Tag = _modelList[i].Id });
            if (_modelList[i].Id == selectedModelId)
                selectedIndex = i + 1;
        }
        CmbModel.SelectedIndex = selectedIndex;
    }

    private void OnProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var providerId = GetSelectedProviderId();
        PopulateModelComboBox(providerId, string.Empty);
    }

    private string GetSelectedProviderId()
    {
        if (CmbProvider.SelectedItem is ComboBoxItem item && item.Tag is string id)
            return id;
        return string.Empty;
    }

    private string GetSelectedModelId()
    {
        if (CmbModel.SelectedItem is ComboBoxItem item && item.Tag is string id)
            return id;
        return string.Empty;
    }
}
