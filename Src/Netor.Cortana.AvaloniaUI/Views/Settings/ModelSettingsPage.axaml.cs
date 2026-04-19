using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Netor.Cortana.AvaloniaUI.Views.Settings;

public partial class ModelSettingsPage : UserControl
{
    private AiProviderService ProviderService => App.Services.GetRequiredService<AiProviderService>();
    private AiModelService ModelService => App.Services.GetRequiredService<AiModelService>();
    private IPublisher Publisher => App.Services.GetRequiredService<IPublisher>();

    private string? _editingId;
    private string? _selectedProviderId;

    public ModelSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadProviders();
    }

    // ──────── 厂商下拉 ────────

    private void LoadProviders()
    {
        CboProvider.Items.Clear();
        CboProvider.Items.Add(new ComboBoxItem { Content = "— 选择厂商 —", Tag = "" });
        foreach (var p in ProviderService.GetAll())
        {
            CboProvider.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        }
        CboProvider.SelectedIndex = 0;
    }

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CboProvider is null || ModelListPanel is null) return;
        if (CboProvider.SelectedItem is ComboBoxItem { Tag: string id } && !string.IsNullOrEmpty(id))
        {
            _selectedProviderId = id;
            RefreshList();
        }
        else
        {
            _selectedProviderId = null;
            ModelListPanel.Children.Clear();
            ModelListPanel.Children.Add(new TextBlock
            {
                Text = "请先选择一个 AI 厂商",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20),
            });
        }
    }

    // ──────── 远程拉取 ────────

    private void OnFetchModelsClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedProviderId is null) return;
        var provider = ProviderService.GetById(_selectedProviderId);
        if (provider is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var fetcher = App.Services.GetRequiredService<AiModelFetcherService>();
                await fetcher.FetchAndSaveModelsAsync(provider);
                Dispatcher.UIThread.Post(RefreshList);
            }
            catch { }
        });
    }

    // ──────── 列表视图 ────────

    private void RefreshList()
    {
        ModelListPanel.Children.Clear();
        if (_selectedProviderId is null) return;

        var list = ModelService.GetByProviderId(_selectedProviderId);
        if (list.Count == 0)
        {
            ModelListPanel.Children.Add(new TextBlock
            {
                Text = "该厂商下暂无模型",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20),
            });
            return;
        }

        foreach (var m in list)
        {
            ModelListPanel.Children.Add(BuildListItem(m));
        }
    }

    private Border BuildListItem(AiModelEntity entity)
    {
        var displayName = string.IsNullOrWhiteSpace(entity.DisplayName) ? entity.Name : entity.DisplayName;
        var name = new TextBlock
        {
            Text = displayName,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
        };

        var sub = new TextBlock
        {
            Text = $"{entity.ModelType}  ·  {entity.Name}  ·  ctx:{entity.ContextLength}",
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

        var editBtn = new Button
        {
            Content = "编辑",
            Padding = new Thickness(10, 3),
            FontSize = 12,
        };
        editBtn.Classes.Add("btn-secondary");
        editBtn.Click += (_, _) => EditModel(entity.Id);
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
        TxtDisplayName.Text = string.Empty;
        TxtDesc.Text = string.Empty;
        CboModelType.SelectedIndex = 0;
        NudContext.Value = 0;
        ChkEnabled.IsChecked = true;
        ChkDefault.IsChecked = false;
        BtnDelete.IsVisible = false;

        // 能力默认值
        ChkInText.IsChecked = true;
        ChkInImage.IsChecked = false;
        ChkInAudio.IsChecked = false;
        ChkInVideo.IsChecked = false;
        ChkInFile.IsChecked = false;
        ChkOutText.IsChecked = true;
        ChkOutImage.IsChecked = false;
        ChkOutAudio.IsChecked = false;
        ChkOutVideo.IsChecked = false;
        ChkFuncCall.IsChecked = false;
        ChkStreaming.IsChecked = false;
        ChkSysPrompt.IsChecked = false;
        ChkJsonMode.IsChecked = false;
    }

    // ──────── 添加/编辑 ────────

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedProviderId is null) return;
        ClearForm();
        ShowForm("添加模型");
    }

    private void EditModel(string id)
    {
        var entity = ModelService.GetById(id);
        if (entity is null) return;

        _editingId = id;
        TxtName.Text = entity.Name;
        TxtDisplayName.Text = entity.DisplayName;
        TxtDesc.Text = entity.Description;
        NudContext.Value = entity.ContextLength;
        ChkEnabled.IsChecked = entity.IsEnabled;
        ChkDefault.IsChecked = entity.IsDefault;
        BtnDelete.IsVisible = true;

        for (int i = 0; i < CboModelType.Items.Count; i++)
        {
            if (CboModelType.Items[i] is ComboBoxItem cbi && cbi.Tag is string tag && tag == entity.ModelType)
            {
                CboModelType.SelectedIndex = i;
                break;
            }
        }

        // 加载能力标志
        ChkInText.IsChecked = entity.InputCapabilities.HasFlag(InputCapabilities.Text);
        ChkInImage.IsChecked = entity.InputCapabilities.HasFlag(InputCapabilities.Image);
        ChkInAudio.IsChecked = entity.InputCapabilities.HasFlag(InputCapabilities.Audio);
        ChkInVideo.IsChecked = entity.InputCapabilities.HasFlag(InputCapabilities.Video);
        ChkInFile.IsChecked = entity.InputCapabilities.HasFlag(InputCapabilities.File);
        ChkOutText.IsChecked = entity.OutputCapabilities.HasFlag(OutputCapabilities.Text);
        ChkOutImage.IsChecked = entity.OutputCapabilities.HasFlag(OutputCapabilities.Image);
        ChkOutAudio.IsChecked = entity.OutputCapabilities.HasFlag(OutputCapabilities.Audio);
        ChkOutVideo.IsChecked = entity.OutputCapabilities.HasFlag(OutputCapabilities.Video);
        ChkFuncCall.IsChecked = entity.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall);
        ChkStreaming.IsChecked = entity.InteractionCapabilities.HasFlag(InteractionCapabilities.Streaming);
        ChkSysPrompt.IsChecked = entity.InteractionCapabilities.HasFlag(InteractionCapabilities.SystemPrompt);
        ChkJsonMode.IsChecked = entity.InteractionCapabilities.HasFlag(InteractionCapabilities.JsonMode);

        ShowForm("编辑模型");
    }

    // ──────── 保存/取消/删除 ────────

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = TxtName.Text?.Trim();
        if (string.IsNullOrEmpty(name) || _selectedProviderId is null) return;

        var modelType = CboModelType.SelectedItem is ComboBoxItem cbi && cbi.Tag is string t ? t : "chat";

        var inputCaps = ReadInputCapabilities();
        var outputCaps = ReadOutputCapabilities();
        var interCaps = ReadInteractionCapabilities();

        if (_editingId is not null)
        {
            var entity = ModelService.GetById(_editingId);
            if (entity is null) return;
            entity.Name = name;
            entity.DisplayName = TxtDisplayName.Text?.Trim() ?? string.Empty;
            entity.ModelType = modelType;
            entity.ContextLength = (int)(NudContext.Value ?? 0);
            entity.Description = TxtDesc.Text?.Trim() ?? string.Empty;
            entity.IsEnabled = ChkEnabled.IsChecked ?? true;
            entity.IsDefault = ChkDefault.IsChecked ?? false;
            entity.InputCapabilities = inputCaps;
            entity.OutputCapabilities = outputCaps;
            entity.InteractionCapabilities = interCaps;
            ModelService.Update(entity);
            if (entity.IsDefault)
                ModelService.SetDefault(entity.Id);
            Publisher.Publish(Events.OnAiModelChange, new DataChangeArgs(entity.Id, ChangeType.Update));
        }
        else
        {
            var entity = new AiModelEntity
            {
                Name = name,
                DisplayName = TxtDisplayName.Text?.Trim() ?? string.Empty,
                ProviderId = _selectedProviderId,
                ModelType = modelType,
                ContextLength = (int)(NudContext.Value ?? 0),
                Description = TxtDesc.Text?.Trim() ?? string.Empty,
                IsEnabled = ChkEnabled.IsChecked ?? true,
                IsDefault = ChkDefault.IsChecked ?? false,
                InputCapabilities = inputCaps,
                OutputCapabilities = outputCaps,
                InteractionCapabilities = interCaps,
            };
            ModelService.Add(entity);
            if (entity.IsDefault)
                ModelService.SetDefault(entity.Id);
            Publisher.Publish(Events.OnAiModelChange, new DataChangeArgs(entity.Id, ChangeType.Create));
        }

        ShowList();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => ShowList();

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_editingId is null) return;
        ModelService.Delete(_editingId);
        Publisher.Publish(Events.OnAiModelChange, new DataChangeArgs(_editingId, ChangeType.Delete));
        ShowList();
    }

    // ──────── 能力标志读取 ────────

    private InputCapabilities ReadInputCapabilities()
    {
        var caps = InputCapabilities.None;
        if (ChkInText.IsChecked == true) caps |= InputCapabilities.Text;
        if (ChkInImage.IsChecked == true) caps |= InputCapabilities.Image;
        if (ChkInAudio.IsChecked == true) caps |= InputCapabilities.Audio;
        if (ChkInVideo.IsChecked == true) caps |= InputCapabilities.Video;
        if (ChkInFile.IsChecked == true) caps |= InputCapabilities.File;
        return caps;
    }

    private OutputCapabilities ReadOutputCapabilities()
    {
        var caps = OutputCapabilities.None;
        if (ChkOutText.IsChecked == true) caps |= OutputCapabilities.Text;
        if (ChkOutImage.IsChecked == true) caps |= OutputCapabilities.Image;
        if (ChkOutAudio.IsChecked == true) caps |= OutputCapabilities.Audio;
        if (ChkOutVideo.IsChecked == true) caps |= OutputCapabilities.Video;
        return caps;
    }

    private InteractionCapabilities ReadInteractionCapabilities()
    {
        var caps = InteractionCapabilities.None;
        if (ChkFuncCall.IsChecked == true) caps |= InteractionCapabilities.FunctionCall;
        if (ChkStreaming.IsChecked == true) caps |= InteractionCapabilities.Streaming;
        if (ChkSysPrompt.IsChecked == true) caps |= InteractionCapabilities.SystemPrompt;
        if (ChkJsonMode.IsChecked == true) caps |= InteractionCapabilities.JsonMode;
        return caps;
    }
}
