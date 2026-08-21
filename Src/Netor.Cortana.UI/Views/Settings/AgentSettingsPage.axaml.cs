using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using Netor.Madorin.Plugin;

namespace Netor.Cortana.UI.Views.Settings;

public partial class AgentSettingsPage : UserControl
{
    private AgentFileService AgentFileService => App.Services.GetRequiredService<AgentFileService>();
    private AgentManifestValidator ManifestValidator => App.Services.GetRequiredService<AgentManifestValidator>();
    private GlobalPluginService GlobalPluginService => App.Services.GetRequiredService<GlobalPluginService>();
    private McpServerService McpServerService => App.Services.GetRequiredService<McpServerService>();
    private PluginLoader PluginLoader => App.Services.GetRequiredService<PluginLoader>();
    private SystemSettingsService SettingsService => App.Services.GetRequiredService<SystemSettingsService>();
    private IPublisher Publisher => App.Services.GetRequiredService<IPublisher>();

    private const string DefaultAgentSettingKey = "Agent.DefaultName";

    private readonly Dictionary<string, CheckBox> _pluginChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _mcpChecks = new(StringComparer.OrdinalIgnoreCase);
    private string? _editingName;
    private string? _editingAgentDirectory;
    private string _editingKind = AgentManifestKinds.Agent;
    private string _avatarPath = string.Empty;
    private string? _pendingAvatarSourcePath;
    private IBrush? _nameBorderBrush;
    private IBrush? _displayNameBorderBrush;
    private IBrush? _descriptionBorderBrush;

    public AgentSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        AgentListPanel.Children.Clear();
        var list = AgentFileService.GetAll();

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

        foreach (var record in list)
        {
            AgentListPanel.Children.Add(BuildListItem(record));
        }
    }

    private Border BuildListItem(AgentFileRecord record)
    {
        var manifest = record.Manifest;
        var title = new TextBlock
        {
            Text = manifest.DisplayName,
            Foreground = (IBrush)this.FindResource("TextBrush")!,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
        };

        var desc = new TextBlock
        {
            Text = $"{manifest.Name} · {(string.IsNullOrWhiteSpace(manifest.Description) ? "(无描述)" : manifest.Description)}",
            Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        left.Children.Add(desc);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var defaultName = SettingsService.GetValue(DefaultAgentSettingKey, "default");
        if (string.Equals(manifest.Name, defaultName, StringComparison.Ordinal))
        {
            right.Children.Add(BuildBadge("默认", "AccentBrush"));
        }

        if (!manifest.Enabled)
        {
            right.Children.Add(BuildBadge("停用", "SubtextBrush"));
        }

        if (string.Equals(manifest.Kind, AgentManifestKinds.BuiltinSystem, StringComparison.Ordinal))
        {
            right.Children.Add(BuildBadge("系统", "SubtextBrush"));
        }

        var editBtn = new Button
        {
            Content = "编辑",
            Padding = new Thickness(10, 3),
            FontSize = 12,
        };
        editBtn.Classes.Add("btn-secondary");
        editBtn.Click += (_, _) => EditAgent(manifest.Name);
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
            Tag = manifest.Name,
        };

        border.PointerEntered += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface1Brush")!;
        border.PointerExited += (s, _) => ((Border)s!).Background = (IBrush)this.FindResource("Surface0Brush")!;

        return border;
    }

    private TextBlock BuildBadge(string text, string brushResource) => new()
    {
        Text = text,
        Foreground = (IBrush)this.FindResource(brushResource)!,
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void ShowForm(string title)
    {
        FormTitle.Text = title;
        ListView.IsVisible = false;
        FormView.IsVisible = true;
    }

    private void ShowList()
    {
        _editingName = null;
        _editingAgentDirectory = null;
        FormView.IsVisible = false;
        ListView.IsVisible = true;
        ClearForm();
        RefreshList();
    }

    private void ClearForm()
    {
        _editingName = null;
        _editingAgentDirectory = null;
        _editingKind = AgentManifestKinds.Agent;
        _avatarPath = string.Empty;
        _pendingAvatarSourcePath = null;
        ValidationText.IsVisible = false;
        ClearValidationState();

        TxtName.Text = string.Empty;
        TxtName.IsReadOnly = false;
        TxtDisplayName.Text = string.Empty;
        TxtDesc.Text = string.Empty;
        TxtInstructions.Text = string.Empty;
        ChkEnabled.IsChecked = true;
        ChkEnabled.IsEnabled = true;
        ChkDefault.IsChecked = false;
        ChkAllowWorkflowMemory.IsChecked = true;
        BtnDelete.IsVisible = false;
        BtnDelete.IsEnabled = true;

        SetAvatarPreview(null);
        BuildBindingSelectors([], []);
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        ClearForm();
        ShowForm("添加智能体");
    }

    private void EditAgent(string name)
    {
        var record = AgentFileService.GetByName(name);
        if (record is null) return;

        var manifest = record.Manifest;
        _editingName = manifest.Name;
        _editingAgentDirectory = record.DirectoryPath;
        _editingKind = manifest.Kind;
        _avatarPath = manifest.Avatar;
        _pendingAvatarSourcePath = null;
        var isBuiltin = string.Equals(manifest.Kind, AgentManifestKinds.BuiltinSystem, StringComparison.Ordinal);

        TxtName.Text = manifest.Name;
        TxtName.IsReadOnly = true;
        TxtDisplayName.Text = manifest.DisplayName;
        TxtDesc.Text = manifest.Description;
        TxtInstructions.Text = record.Prompt;
        ChkEnabled.IsChecked = manifest.Enabled;
        ChkEnabled.IsEnabled = !isBuiltin;
        ChkDefault.IsChecked = string.Equals(SettingsService.GetValue(DefaultAgentSettingKey, "default"), manifest.Name, StringComparison.Ordinal);
        ChkAllowWorkflowMemory.IsChecked = manifest.AllowWorkflowMemory;

        BtnDelete.IsVisible = true;
        BtnDelete.IsEnabled = !isBuiltin;
        ToolTip.SetTip(BtnDelete, isBuiltin ? "builtin/system Agent 不允许删除。" : null);

        LoadAvatarPreview(manifest.Avatar);
        BuildBindingSelectors(manifest.BoundPlugins, manifest.BoundMcp);

        ShowForm("编辑智能体");
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var name = TxtName.Text?.Trim() ?? string.Empty;
        var displayName = TxtDisplayName.Text?.Trim() ?? string.Empty;
        var description = TxtDesc.Text?.Trim() ?? string.Empty;

        var manifest = new AgentManifest
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Kind = _editingName is null ? AgentManifestKinds.Agent : _editingKind,
            Avatar = _avatarPath,
            Enabled = string.Equals(_editingKind, AgentManifestKinds.BuiltinSystem, StringComparison.Ordinal) || (ChkEnabled.IsChecked ?? true),
            SortOrder = AgentFileService.GetByName(name)?.Manifest.SortOrder ?? 100,
            AllowWorkflowMemory = ChkAllowWorkflowMemory.IsChecked ?? true,
            BoundPlugins = GetSelectedIds(_pluginChecks),
            BoundMcp = GetSelectedIds(_mcpChecks),
        };

        var validation = ManifestValidator.Validate(manifest, _editingName ?? name);
        if (!validation.IsValid)
        {
            ShowValidation(validation.Error ?? "manifest 校验失败。");
            MarkInvalidRequiredFields(name, displayName, description);
            return;
        }

        if (!TryImportPendingAvatar(name, out var avatarError))
        {
            ShowValidation(avatarError ?? "头像导入失败。");
            return;
        }

        AgentFileService.Save(manifest, TxtInstructions.Text ?? string.Empty);

        if (ChkDefault.IsChecked == true)
        {
            SettingsService.SetValue(DefaultAgentSettingKey, manifest.Name);
        }
        else if (string.Equals(SettingsService.GetValue(DefaultAgentSettingKey, "default"), manifest.Name, StringComparison.Ordinal))
        {
            SettingsService.SetValue(DefaultAgentSettingKey, "default");
        }

        var changeType = _editingName is null ? ChangeType.Create : ChangeType.Update;
        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(manifest.Name, changeType));
        ShowList();
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.IsVisible = true;
    }

    private void ClearValidationState()
    {
        if (_nameBorderBrush is null)
        {
            _nameBorderBrush = TxtName.BorderBrush;
            _displayNameBorderBrush = TxtDisplayName.BorderBrush;
            _descriptionBorderBrush = TxtDesc.BorderBrush;
        }

        TxtName.BorderBrush = _nameBorderBrush;
        TxtDisplayName.BorderBrush = _displayNameBorderBrush;
        TxtDesc.BorderBrush = _descriptionBorderBrush;
    }

    private void MarkInvalidRequiredFields(string name, string displayName, string description)
    {
        ClearValidationState();

        var errorBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));
        if (string.IsNullOrWhiteSpace(name))
        {
            TxtName.BorderBrush = errorBrush;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            TxtDisplayName.BorderBrush = errorBrush;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            TxtDesc.BorderBrush = errorBrush;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => ShowList();

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_editingName is null) return;

        AgentFileService.SoftDelete(_editingName);
        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(_editingName, ChangeType.Delete));
        ShowList();
    }

    private void BuildBindingSelectors(IReadOnlyList<string> boundPlugins, IReadOnlyList<string> boundMcp)
    {
        _pluginChecks.Clear();
        _mcpChecks.Clear();
        PluginBindingsPanel.Children.Clear();
        McpBindingsPanel.Children.Clear();

        var loadedPlugins = PluginLoader.GetLoadedPluginInfos()
            .Select(info => (Id: info.Plugin.Id, Label: info.Plugin.Name, Installed: true, Enabled: GlobalPluginService.IsEnabled(info.Plugin.Id)))
            .Concat(boundPlugins.Select(id => (Id: id, Label: id, Installed: false, Enabled: false)))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Installed).First())
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (loadedPlugins.Count == 0)
        {
            PluginBindingsPanel.Children.Add(BuildEmptyBindingText("当前没有可绑定插件。"));
        }
        else
        {
            foreach (var plugin in loadedPlugins)
            {
                var label = plugin.Installed
                    ? plugin.Enabled ? plugin.Label : $"{plugin.Label}（已禁用）"
                    : $"{plugin.Label}（未安装）";
                var check = BuildBindingCheckBox(label, plugin.Id, boundPlugins.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase), plugin.Installed && plugin.Enabled);
                _pluginChecks[plugin.Id] = check;
                PluginBindingsPanel.Children.Add(check);
            }
        }

        var mcpServers = McpServerService.GetAll()
            .Select(server => (Id: server.Id, Label: server.Name, Installed: true, Enabled: server.IsEnabled))
            .Concat(boundMcp.Select(id => (Id: id, Label: id, Installed: false, Enabled: false)))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Installed).First())
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (mcpServers.Count == 0)
        {
            McpBindingsPanel.Children.Add(BuildEmptyBindingText("当前没有可绑定 MCP。"));
        }
        else
        {
            foreach (var server in mcpServers)
            {
                var label = server.Installed
                    ? server.Enabled ? server.Label : $"{server.Label}（已禁用）"
                    : $"{server.Label}（未安装）";
                var check = BuildBindingCheckBox(label, server.Id, boundMcp.Contains(server.Id, StringComparer.OrdinalIgnoreCase), server.Installed && server.Enabled);
                _mcpChecks[server.Id] = check;
                McpBindingsPanel.Children.Add(check);
            }
        }
    }

    private CheckBox BuildBindingCheckBox(string label, string id, bool isChecked, bool isAvailable) => new()
    {
        Content = label,
        Tag = id,
        IsChecked = isChecked,
        Foreground = isAvailable ? (IBrush)this.FindResource("TextBrush")! : (IBrush)this.FindResource("SubtextBrush")!,
    };

    private TextBlock BuildEmptyBindingText(string text) => new()
    {
        Text = text,
        Foreground = (IBrush)this.FindResource("SubtextBrush")!,
        FontSize = 12,
    };

    private static List<string> GetSelectedIds(Dictionary<string, CheckBox> checks) =>
        checks
            .Where(static pair => pair.Value.IsChecked == true)
            .Select(static pair => pair.Key)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async void OnPickAvatarClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择头像图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] },
            ]
        });

        if (files.Count == 0) return;

        var srcPath = files[0].Path.LocalPath;
        var ext = Path.GetExtension(srcPath);
        _pendingAvatarSourcePath = srcPath;
        _avatarPath = Path.Combine("assets", $"avatar-{Guid.NewGuid():N}{ext}").Replace('\\', '/');
        LoadAvatarPreview(_avatarPath);
    }

    private void OnClearAvatarClick(object? sender, RoutedEventArgs e)
    {
        _avatarPath = string.Empty;
        _pendingAvatarSourcePath = null;
        SetAvatarPreview(null);
    }

    private void LoadAvatarPreview(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            SetAvatarPreview(null);
            return;
        }

        var fullPath = ResolveAvatarPath(relativePath);
        if (fullPath is null || !File.Exists(fullPath))
        {
            SetAvatarPreview(null);
            return;
        }

        SetAvatarPreview(new Bitmap(fullPath));
    }

    private string? ResolveAvatarPath(string relativePath)
    {
        if (!string.IsNullOrWhiteSpace(_pendingAvatarSourcePath) &&
            string.Equals(relativePath, _avatarPath, StringComparison.Ordinal))
        {
            return _pendingAvatarSourcePath;
        }

        if (string.IsNullOrWhiteSpace(_editingAgentDirectory))
        {
            return null;
        }

        return Path.Combine(_editingAgentDirectory, relativePath);
    }

    private bool TryImportPendingAvatar(string agentName, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(_pendingAvatarSourcePath))
        {
            return true;
        }

        try
        {
            var agentDirectory = Path.Combine(AgentFileService.AgentsDirectory, agentName);
            var targetPath = Path.GetFullPath(Path.Combine(agentDirectory, _avatarPath));
            var agentRoot = Path.GetFullPath(agentDirectory);
            var rootPrefix = agentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "avatar 路径必须位于 Agent 目录内。";
                return false;
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                error = "avatar 路径无效。";
                return false;
            }

            Directory.CreateDirectory(targetDirectory);
            File.Copy(_pendingAvatarSourcePath, targetPath, overwrite: true);
            _editingAgentDirectory = agentDirectory;
            _pendingAvatarSourcePath = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"头像导入失败：{ex.Message}";
            return false;
        }
    }

    private void SetAvatarPreview(Bitmap? bitmap)
    {
        AvatarPreview.Child = bitmap is null
            ? new TextBlock
            {
                Text = "无",
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            }
            : new Image
            {
                Source = bitmap,
                Width = 48,
                Height = 48,
                Stretch = Stretch.UniformToFill,
            };
    }
}
