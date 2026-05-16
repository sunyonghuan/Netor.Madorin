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

namespace Netor.Cortana.UI.Controls;

public partial class ChatHistoryPanel : UserControl
{
    private const int PageSize = 30;
    private int _currentPage;
    private bool _isLoading;
    private bool _hasMore = true;
    private readonly List<ChatSessionEntity> _loadedSessions = [];
    private readonly HashSet<string> _selectedIds = new(StringComparer.OrdinalIgnoreCase);

    // 阶段 6 Phase 3：会话搜索（决策 6-3-A 标题前缀/子串 LIKE 匹配，不引入 FTS5）。
    // _searchKeyword 是当前生效的过滤词；_searchDebounceTimer 做 200ms 防抖避免逐字符 hammering DB。
    // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #3。
    private string _searchKeyword = string.Empty;
    private DispatcherTimer? _searchDebounceTimer;

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

            // 阶段 6 Phase 3：搜索关键词非空时，加 Title LIKE @kw 过滤（决策 6-3-A）。
            // 用 % 包裹做子串匹配；OrdinalIgnoreCase 由 SQLite 默认 BINARY 比较 + LOWER() 处理。
            var hasSearch = !string.IsNullOrEmpty(_searchKeyword);
            var sql = hasSearch
                ? "SELECT * FROM ChatSessions WHERE IsArchived = 0 AND Categorize = @cat AND LOWER(Title) LIKE LOWER(@kw) ORDER BY IsPinned DESC, LastActiveTimestamp DESC LIMIT @limit OFFSET @offset"
                : "SELECT * FROM ChatSessions WHERE IsArchived = 0 AND Categorize = @cat ORDER BY IsPinned DESC, LastActiveTimestamp DESC LIMIT @limit OFFSET @offset";

            var sessions = db.Query(
                sql,
                ReadSessionEntity,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@cat", categorize);
                    cmd.Parameters.AddWithValue("@limit", PageSize);
                    cmd.Parameters.AddWithValue("@offset", offset);
                    if (hasSearch)
                        cmd.Parameters.AddWithValue("@kw", $"%{_searchKeyword}%");
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

    /// <summary>
    /// 阶段 6 Phase 3：搜索框文本变化处理（200ms 防抖，避免逐字符 hammering DB）。
    /// 在用户停止输入 200ms 后触发 Reload，重新按关键词加载第一页。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #3。
    /// </summary>
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // 取消上一次未触发的防抖定时器
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer?.Stop();
            _searchDebounceTimer = null;

            var newKeyword = (tb.Text ?? string.Empty).Trim();
            if (string.Equals(newKeyword, _searchKeyword, StringComparison.Ordinal)) return;

            _searchKeyword = newKeyword;
            Reload();
        };
        _searchDebounceTimer.Start();
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

        // 界面重设计 B 步骤：右键菜单（3 项：重命名 / 恢复对话 / 删除）。
        // 用户决策：危险操作（删除）放最下 + 弹二次确认对话框。
        // - 重命名：调用 BeginEditTitle 走 inline edit 模式（与双击交互一致）
        // - 恢复对话：复用 OnItemPressed 的 SessionSelected 事件
        // - 删除：弹 InputBoxDialog.ConfirmAsync 二次确认 + 单条 SQL 删除
        var renameItem = new MenuItem { Header = "重命名", Tag = session.Id };
        renameItem.Click += OnContextRenameClick;

        var restoreItem = new MenuItem { Header = "恢复对话", Tag = session.Id };
        restoreItem.Click += OnContextRestoreClick;

        var deleteItem = new MenuItem { Header = "删除", Tag = session.Id };
        deleteItem.Click += OnContextDeleteClick;

        // Items.Add 风格（避免 AOT 模式下混类型数组的潜在分析告警，所有 Avalonia 项目代码统一用法）
        var menu = new ContextMenu();
        menu.Items.Add(renameItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(restoreItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteItem);
        border.ContextMenu = menu;

        return border;
    }

    /// <summary>
    /// 双击标题进入编辑模式（保留原有交互）。委托到 <see cref="BeginEditTitle"/>。
    /// </summary>
    private void OnTitleDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock titleBlock) return;
        BeginEditTitle(titleBlock);
        e.Handled = true;
    }

    /// <summary>
    /// 进入标题 inline edit 模式（B 步骤提取的公共方法，被双击 + 右键"重命名"共同调用）。
    /// 把 TextBlock 替换为 TextBox，注册回车/失焦保存 + ESC 取消。
    /// </summary>
    private void BeginEditTitle(TextBlock titleBlock)
    {
        var parent = titleBlock.Parent as StackPanel;
        if (parent is null) return;

        // 防止重复进入编辑（已经是 TextBox 时直接返回）
        var index = parent.Children.IndexOf(titleBlock);
        if (index < 0) return;

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

        parent.Children[index] = editBox;

        editBox.Focus();
        editBox.SelectAll();
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

    // ──────── 界面重设计 B 步骤：右键菜单 3 个 handler ────────
    // 用户决策：菜单顺序 重命名 → 恢复对话 → 删除（危险操作放最下）+ 删除弹二次确认。
    // 详见 Docs/未来版本策划/界面重设计/05-阶段总结.md §6.1（B 步骤实施记录）。

    /// <summary>
    /// 右键菜单 - 重命名：按 Tag 找到对应列表项的 TextBlock，进入 inline edit 模式。
    /// 与双击交互完全一致（共享 <see cref="BeginEditTitle"/> 公共方法）。
    /// </summary>
    private void OnContextRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string sessionId) return;

        var titleBlock = FindTitleBlock(sessionId);
        if (titleBlock is not null)
            BeginEditTitle(titleBlock);
    }

    /// <summary>
    /// 右键菜单 - 恢复对话：触发 <see cref="SessionSelected"/> 事件，
    /// 与左键单击列表项的行为一致（OnItemPressed 入口）。
    /// </summary>
    private void OnContextRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string sessionId) return;

        var session = _loadedSessions.Find(s => s.Id == sessionId);
        if (session is null) return;

        SessionSelected?.Invoke(session.Id, session.Title);
    }

    /// <summary>
    /// 右键菜单 - 删除（危险操作）：弹二次确认对话框，确认后删除单条会话 + 关联消息。
    /// 与 <see cref="OnDeleteSelectedClick"/>（多选删除）共享 SQL 删除逻辑，但走单条路径。
    /// 删除当前激活会话时触发 <see cref="RequestNewSession"/> 让 MainWindow 创建新会话。
    /// </summary>
    private async void OnContextDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not string sessionId) return;

        var session = _loadedSessions.Find(s => s.Id == sessionId);
        if (session is null) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        var displayTitle = string.IsNullOrWhiteSpace(session.Title) ? "新对话" : session.Title;

        // 二次确认（默认按钮 = 取消，防误删）
        var confirmed = await Netor.Cortana.UI.Views.Workspace.InputBoxDialog.ConfirmAsync(
            owner,
            title: "删除会话",
            message: $"确定要删除会话「{displayTitle}」吗？\n\n该会话的所有消息将一并删除，此操作不可撤销。",
            confirmText: "删除",
            cancelText: "取消");

        if (!confirmed) return;

        var deletedActiveSession = string.Equals(sessionId, CurrentSessionId, StringComparison.OrdinalIgnoreCase);

        try
        {
            var db = App.Services.GetRequiredService<CortanaDbContext>();
            db.Execute(
                "DELETE FROM ChatSessions WHERE Id = @id",
                cmd => cmd.Parameters.AddWithValue("@id", sessionId));
            db.Execute(
                "DELETE FROM ChatMessages WHERE SessionId = @id",
                cmd => cmd.Parameters.AddWithValue("@id", sessionId));

            _selectedIds.Remove(sessionId);
            Reload();

            if (deletedActiveSession)
                RequestNewSession?.Invoke();
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<ChatHistoryPanel>>();
            logger.LogError(ex, "删除单条会话失败 SessionId={SessionId}", sessionId);
        }
    }

    /// <summary>
    /// 按 sessionId 找到列表中对应 Border 内的标题 TextBlock。
    /// CreateSessionItem 的 Border &gt; Grid &gt; StackPanel &gt; TextBlock(标题) 路径。
    /// </summary>
    private TextBlock? FindTitleBlock(string sessionId)
    {
        foreach (var item in HistoryItems.Items)
        {
            if (item is not Border border || border.Tag is not string id) continue;
            if (!string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase)) continue;

            if (border.Child is Grid grid && grid.Children.Count >= 2
                && grid.Children[1] is StackPanel sp && sp.Children.Count >= 1
                && sp.Children[0] is TextBlock titleBlock)
            {
                return titleBlock;
            }
        }
        return null;
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
        return border.Child switch
        {
            Grid grid => grid.Children.OfType<CheckBox>().FirstOrDefault(),
            StackPanel sp => sp.Children.OfType<CheckBox>().FirstOrDefault(),
            CheckBox cb => cb,
            _ => null
        };
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
