using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.EventHub;

namespace Netor.Cortana.AvaloniaUI.Controls;

public partial class ChatHistoryPanel : UserControl
{
    private const int PageSize = 30;
    private int _currentPage;
    private bool _isLoading;
    private bool _hasMore = true;
    private readonly List<ChatSessionEntity> _loadedSessions = [];
    private readonly HashSet<string> _selectedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 点击某条历史记录时触发，参数为 (sessionId, title)。
    /// </summary>
    public event Action<string, string>? SessionSelected;

    /// <summary>
    /// 删除了当前活跃会话后触发，要求创建新会话。
    /// </summary>
    public event Action? RequestNewSession;

    /// <summary>
    /// 当前活跃的会话 ID，由 MainWindow 维护。
    /// </summary>
    public string CurrentSessionId { get; set; } = string.Empty;

    public ChatHistoryPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var subscriber = App.Services.GetRequiredService<ISubscriber>();
        subscriber.Subscribe<SessionCreatedArgs>(Events.OnSessionCreated, (_, _) =>
        {
            Dispatcher.UIThread.Post(Reload);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<SessionTitleUpdatedArgs>(Events.OnSessionTitleUpdated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() => UpdateSessionTitle(args.SessionId, args.Title));
            return Task.FromResult(false);
        });
    }

    /// <summary>
    /// 加载第一页数据（切换工作目录或打开面板时调用）。
    /// </summary>
    public void Reload()
    {
        _currentPage = 0;
        _hasMore = true;
        _loadedSessions.Clear();
        _selectedIds.Clear();
        HistoryItems.Items.Clear();
        SelectAllCheckBox.IsChecked = false;
        LoadNextPage();
    }

    private void LoadNextPage()
    {
        if (_isLoading || !_hasMore) return;
        _isLoading = true;

        try
        {
            var db = App.Services.GetRequiredService<CortanaDbContext>();
            var categorize = App.WorkspaceDirectory.Md5Encrypt();
            var offset = _currentPage * PageSize;

            var sessions = db.Query(
                "SELECT * FROM ChatSessions WHERE IsArchived = 0 AND Categorize = @cat ORDER BY IsPinned DESC, LastActiveTimestamp DESC LIMIT @limit OFFSET @offset",
                ReadSessionEntity,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@cat", categorize);
                    cmd.Parameters.AddWithValue("@limit", PageSize);
                    cmd.Parameters.AddWithValue("@offset", offset);
                });

            if (sessions.Count < PageSize)
                _hasMore = false;

            foreach (var session in sessions)
            {
                _loadedSessions.Add(session);
                HistoryItems.Items.Add(CreateSessionItem(session));
            }

            _currentPage++;
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<ChatHistoryPanel>>();
            logger.LogError(ex, "加载历史记录失败");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private Border CreateSessionItem(ChatSessionEntity session)
    {
        var title = string.IsNullOrWhiteSpace(session.Title) ? "新对话" : session.Title;
        var updatedTime = DateTimeOffset.FromUnixTimeMilliseconds(session.LastActiveTimestamp).LocalDateTime;
        var createdTime = DateTimeOffset.FromUnixTimeMilliseconds(session.CreatedTimestamp).LocalDateTime;

        var checkBox = new CheckBox
        {
            Classes = { "history-check" },
            Tag = session.Id,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
            IsChecked = _selectedIds.Contains(session.Id),
        };
        checkBox.Click += OnItemCheckClick;

        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            FontSize = 12,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        titleBlock.DoubleTapped += OnTitleDoubleTapped;

        var textPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                titleBlock,
                new TextBlock
                {
                    Text = $"更新: {updatedTime:MM-dd HH:mm}  创建: {createdTime:MM-dd HH:mm}",
                    Foreground = new SolidColorBrush(Color.Parse("#6a6a6a")),
                    FontSize = 10,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                },
            }
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { checkBox, textPanel },
        };
        Grid.SetColumn(checkBox, 0);
        Grid.SetColumn(textPanel, 1);

        var border = new Border
        {
            Classes = { "history-item" },
            Tag = session.Id,
            Child = row,
        };
        border.PointerPressed += OnItemPressed;

        return border;
    }

    /// <summary>
    /// 双击标题进入编辑模式。
    /// </summary>
    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock titleBlock) return;
        var parent = titleBlock.Parent as StackPanel;
        if (parent is null) return;

        var editBox = new TextBox
        {
            Text = titleBlock.Text,
            FontSize = 12,
            Padding = new Avalonia.Thickness(2, 0),
            MinHeight = 0,
            Tag = titleBlock, // 保存原始 TextBlock 引用
        };

        editBox.KeyDown += OnTitleEditKeyDown;
        editBox.LostFocus += OnTitleEditLostFocus;

        var index = parent.Children.IndexOf(titleBlock);
        parent.Children[index] = editBox;

        editBox.Focus();
        editBox.SelectAll();
        e.Handled = true;
    }

    private void OnTitleEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox editBox) return;

        if (e.Key == Key.Enter)
        {
            CommitTitleEdit(editBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelTitleEdit(editBox);
            e.Handled = true;
        }
    }

    private void OnTitleEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox editBox)
            CommitTitleEdit(editBox);
    }

    private void CommitTitleEdit(TextBox editBox)
    {
        editBox.KeyDown -= OnTitleEditKeyDown;
        editBox.LostFocus -= OnTitleEditLostFocus;

        var originalBlock = editBox.Tag as TextBlock;
        if (originalBlock is null) return;

        var parent = editBox.Parent as StackPanel;
        if (parent is null) return;

        var newTitle = editBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
            newTitle = originalBlock.Text; // 空输入则还原

        originalBlock.Text = newTitle;
        var index = parent.Children.IndexOf(editBox);
        parent.Children[index] = originalBlock;

        // 查找所属 sessionId 并更新数据库
        var border = parent.Parent is Grid grid ? grid.Parent as Border : null;
        if (border?.Tag is string sessionId && newTitle != null)
        {
            var session = _loadedSessions.Find(s => s.Id == sessionId);
            if (session is not null)
                session.Title = newTitle;

            try
            {
                var db = App.Services.GetRequiredService<CortanaDbContext>();
                db.Execute(
                    "UPDATE ChatSessions SET Title = @Title, UpdatedTimestamp = @Updated WHERE Id = @Id",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@Title", newTitle);
                        cmd.Parameters.AddWithValue("@Updated", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                        cmd.Parameters.AddWithValue("@Id", sessionId);
                    });

                var publisher = App.Services.GetRequiredService<IPublisher>();
                publisher.Publish(Events.OnSessionTitleUpdated, new SessionTitleUpdatedArgs(sessionId, newTitle));
            }
            catch (Exception ex)
            {
                var logger = App.Services.GetRequiredService<ILogger<ChatHistoryPanel>>();
                logger.LogError(ex, "修改会话标题失败");
            }
        }
    }

    private void CancelTitleEdit(TextBox editBox)
    {
        editBox.KeyDown -= OnTitleEditKeyDown;
        editBox.LostFocus -= OnTitleEditLostFocus;

        var originalBlock = editBox.Tag as TextBlock;
        if (originalBlock is null) return;

        var parent = editBox.Parent as StackPanel;
        if (parent is null) return;

        var index = parent.Children.IndexOf(editBox);
        parent.Children[index] = originalBlock;
    }

    // ──────── 事件处理 ────────

    /// <summary>
    /// 更新指定会话在列表中的标题文本。
    /// </summary>
    private void UpdateSessionTitle(string sessionId, string newTitle)
    {
        var session = _loadedSessions.Find(s => s.Id == sessionId);
        if (session is not null)
            session.Title = newTitle;

        foreach (var item in HistoryItems.Items)
        {
            if (item is Border border && border.Tag is string id
                && string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                // 第一个 TextBlock 是标题
                if (border.Child is Grid grid && grid.Children[1] is StackPanel sp && sp.Children[0] is TextBlock tb)
                    tb.Text = newTitle;
                break;
            }
        }
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not string sessionId) return;

        // 忽略点击在 CheckBox 上的事件
        if (e.Source is CheckBox or Border { Name: "NormalRectangle" }) return;

        var session = _loadedSessions.Find(s => s.Id == sessionId);
        if (session is null) return;

        SessionSelected?.Invoke(session.Id, session.Title);
    }

    private void OnItemCheckClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string id) return;

        if (cb.IsChecked == true)
            _selectedIds.Add(id);
        else
            _selectedIds.Remove(id);

        UpdateSelectAllState();
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        var isChecked = SelectAllCheckBox.IsChecked == true;
        _selectedIds.Clear();

        foreach (var item in HistoryItems.Items)
        {
            if (item is not Border border) continue;
            var cb = FindCheckBox(border);
            if (cb is null) continue;

            cb.IsChecked = isChecked;
            if (isChecked && cb.Tag is string id)
                _selectedIds.Add(id);
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        Reload();
    }

    private void OnDeleteSelectedClick(object? sender, RoutedEventArgs e)
    {
        if (_selectedIds.Count == 0) return;

        var deletedActiveSession = _selectedIds.Contains(CurrentSessionId);

        try
        {
            var db = App.Services.GetRequiredService<CortanaDbContext>();

            foreach (var id in _selectedIds)
            {
                db.Execute(
                    "DELETE FROM ChatSessions WHERE Id = @id",
                    cmd => cmd.Parameters.AddWithValue("@id", id));
                db.Execute(
                    "DELETE FROM ChatMessages WHERE SessionId = @id",
                    cmd => cmd.Parameters.AddWithValue("@id", id));
            }

            _selectedIds.Clear();
            Reload();

            if (deletedActiveSession)
                RequestNewSession?.Invoke();
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<ChatHistoryPanel>>();
            logger.LogError(ex, "删除会话失败");
        }
    }

    /// <summary>
    /// 由 MainWindow 调用：注册滚动到底部时加载下一页。
    /// </summary>
    public void AttachScrollHandler()
    {
        HistoryScroller.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = HistoryScroller;
        if (sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 50)
        {
            LoadNextPage();
        }
    }

    // ──────── 辅助方法 ────────

    private void UpdateSelectAllState()
    {
        if (_loadedSessions.Count == 0)
        {
            SelectAllCheckBox.IsChecked = false;
            return;
        }

        SelectAllCheckBox.IsChecked = _selectedIds.Count == _loadedSessions.Count;
    }

    private static CheckBox? FindCheckBox(Border border)
    {
        if (border.Child is StackPanel sp)
        {
            foreach (var child in sp.Children)
            {
                if (child is CheckBox cb) return cb;
            }
        }
        return null;
    }

    private static ChatSessionEntity ReadSessionEntity(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        return new ChatSessionEntity
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Categorize = r.GetString(r.GetOrdinal("Categorize")),
            Title = r.GetString(r.GetOrdinal("Title")),
            Summary = r.GetString(r.GetOrdinal("Summary")),
            RawDiscription = r.GetString(r.GetOrdinal("RawDiscription")),
            AgentId = r.GetString(r.GetOrdinal("AgentId")),
            IsArchived = r.GetInt64(r.GetOrdinal("IsArchived")) != 0,
            IsPinned = r.GetInt64(r.GetOrdinal("IsPinned")) != 0,
            LastActiveTimestamp = r.GetInt64(r.GetOrdinal("LastActiveTimestamp")),
            TotalTokenCount = r.GetInt32(r.GetOrdinal("TotalTokenCount"))
        };
    }
}
