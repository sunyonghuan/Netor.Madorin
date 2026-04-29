using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// MainWindow — 输入框交互：聚焦视觉、键盘快捷键、#文件 与 @智能体 自动补全。
/// </summary>
public partial class MainWindow
{
    // @智能体提及：名称 → AgentEntity 映射
    private readonly Dictionary<string, AgentEntity> _agentMentions = new(StringComparer.OrdinalIgnoreCase);
    private List<AgentEntity> _currentAgentSuggestions = [];
    private int _agentPopupSelectedIndex = -1;
    private int _currentAgentAtIndex = -1;

    // # 文件补全：文件名 → 完整路径 映射
    private readonly Dictionary<string, string> _fileReferences = new(StringComparer.OrdinalIgnoreCase);

    // ──────── 输入框焦点效果 ────────

    private void OnInputBoxGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!_isDragOver)
        {
            InputBorder.BorderBrush = BorderActive;
        }
    }

    private void OnInputBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!_isDragOver)
        {
            InputBorder.BorderBrush = BorderNormal;
        }
    }

    private void RestoreInputBorder()
    {
        InputBorder.BorderThickness = new Thickness(1);
        InputBorder.Background = BgNormal;
        InputBorder.BorderBrush = InputBox.IsFocused ? BorderActive : BorderNormal;
    }

    // ──────── 输入处理 ────────

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // AgentPopup 键盘导航
        if (AgentPopup.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (_currentAgentSuggestions.Count > 0)
                {
                    _agentPopupSelectedIndex = (_agentPopupSelectedIndex + 1) % _currentAgentSuggestions.Count;
                    HighlightAgentItem(_agentPopupSelectedIndex);
                }
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                if (_currentAgentSuggestions.Count > 0)
                {
                    _agentPopupSelectedIndex = _agentPopupSelectedIndex <= 0
                        ? _currentAgentSuggestions.Count - 1
                        : _agentPopupSelectedIndex - 1;
                    HighlightAgentItem(_agentPopupSelectedIndex);
                }
                return;
            }
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                e.Handled = true;
                if (_agentPopupSelectedIndex >= 0 && _agentPopupSelectedIndex < _currentAgentSuggestions.Count)
                {
                    OnAgentItemSelected(_currentAgentSuggestions[_agentPopupSelectedIndex], _currentAgentAtIndex);
                }
                else if (_currentAgentSuggestions.Count > 0)
                {
                    OnAgentItemSelected(_currentAgentSuggestions[0], _currentAgentAtIndex);
                }
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AgentPopup.IsOpen = false;
                return;
            }
        }

        // FilePopup 键盘导航
        if (FilePopup.IsOpen)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                FilePopup.IsOpen = false;
                return;
            }
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            // 隧道阶段拦截：阻止 TextBox 插入换行，改为发送消息
            e.Handled = true;
            if (FilePopup.IsOpen)
                FilePopup.IsOpen = false;
            if (AgentPopup.IsOpen)
                AgentPopup.IsOpen = false;
            SendMessage();
        }
    }

    // ──────── # 文件补全 ────────

    /// <summary>
    /// 输入框文本变化时检测 # 触发文件补全、@ 触发智能体补全。
    /// </summary>
    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        // 尝试 @ 智能体补全
        if (TryShowAgentPopup(text, caret))
        {
            FilePopup.IsOpen = false;
            return;
        }

        AgentPopup.IsOpen = false;

        // 尝试 # 文件补全
        var hashIndex = text.LastIndexOf('#', Math.Max(0, caret - 1));
        if (hashIndex < 0)
        {
            FilePopup.IsOpen = false;
            return;
        }

        // # 后面到光标之间的内容作为搜索关键字
        var afterHash = text.Substring(hashIndex + 1, caret - hashIndex - 1);

        // 如果包含空格，关闭补全
        if (afterHash.Contains(' ') || afterHash.Contains('\n'))
        {
            FilePopup.IsOpen = false;
            return;
        }

        // 获取工作目录下的文件列表（过滤匹配）
        var appPaths = App.Services.GetRequiredService<IAppPaths>();
        var workDir = appPaths.WorkspaceDirectory;

        if (!Directory.Exists(workDir))
        {
            FilePopup.IsOpen = false;
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories)
                .Select(f => (FullPath: f, RelativePath: Path.GetRelativePath(workDir, f)))
                .Where(f => !f.RelativePath.Contains($"{Path.DirectorySeparatorChar}.", StringComparison.Ordinal)
                         && !f.RelativePath.StartsWith('.'))
                .Where(f => string.IsNullOrEmpty(afterHash)
                         || f.RelativePath.Contains(afterHash, StringComparison.OrdinalIgnoreCase))
                .Take(30)
                .ToList();

            if (files.Count == 0)
            {
                FilePopup.IsOpen = false;
                return;
            }

            FillFileList(files, hashIndex);
            FilePopup.IsOpen = true;
        }
        catch
        {
            FilePopup.IsOpen = false;
        }
    }

    /// <summary>
    /// 填充文件补全列表。
    /// </summary>
    private void FillFileList(List<(string FullPath, string RelativePath)> files, int hashIndex)
    {
        FileList.Items.Clear();

        foreach (var (fullPath, relativePath) in files)
        {
            var fileName = Path.GetFileName(fullPath);
            var dirPart = Path.GetDirectoryName(relativePath) ?? "";

            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 1 };
            sp.Children.Add(new TextBlock
            {
                Text = fileName,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            });
            if (!string.IsNullOrEmpty(dirPart))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = dirPart,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#6a6a6a")),
                });
            }

            var btn = new Button
            {
                Classes = { "selector-item" },
                Content = sp,
                Tag = fullPath,
            };
            btn.Click += (_, _) => OnFileItemSelected(fileName, fullPath, hashIndex);
            FileList.Items.Add(btn);
        }
    }

    /// <summary>
    /// 选中文件后：将 #关键字 替换为文件名，记录文件名→路径映射。
    /// </summary>
    private void OnFileItemSelected(string fileName, string fullPath, int hashIndex)
    {
        FilePopup.IsOpen = false;

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        // 替换 # 到光标之间的内容为 #文件名
        var replacement = $"#{fileName} ";
        var newText = string.Concat(text.AsSpan(0, hashIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = hashIndex + replacement.Length;

        // 记录映射
        _fileReferences[fileName] = fullPath;
    }

    // ──────── @ 智能体补全 ────────

    /// <summary>
    /// 检测 @ 触发智能体补全弹窗。
    /// </summary>
    private bool TryShowAgentPopup(string text, int caret)
    {
        if (caret <= 0) return false;

        var atIndex = text.LastIndexOf('@', Math.Max(0, caret - 1));
        if (atIndex < 0) return false;

        // @ 前面只能是行首或空白字符
        if (atIndex > 0 && !char.IsWhiteSpace(text[atIndex - 1])) return false;

        var afterAt = text.Substring(atIndex + 1, caret - atIndex - 1);

        if (afterAt.Contains(' ') || afterAt.Contains('\n')) return false;

        var agentService = App.Services.GetRequiredService<AgentService>();
        var allAgents = agentService.GetAll();

        var matches = string.IsNullOrEmpty(afterAt)
            ? allAgents
            : allAgents.Where(a => a.Name.Contains(afterAt, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            AgentPopup.IsOpen = false;
            return false;
        }

        _currentAgentSuggestions = matches;
        _currentAgentAtIndex = atIndex;
        _agentPopupSelectedIndex = 0;
        FillAgentList(matches, atIndex);
        HighlightAgentItem(0);
        AgentPopup.IsOpen = true;
        return true;
    }

    /// <summary>
    /// 填充智能体补全列表。
    /// </summary>
    private void FillAgentList(List<AgentEntity> agents, int atIndex)
    {
        AgentList.Items.Clear();

        foreach (var agent in agents)
        {
            var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 1 };
            sp.Children.Add(new TextBlock
            {
                Text = agent.Name,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
            });
            if (!string.IsNullOrWhiteSpace(agent.Description))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = agent.Description.Length > 60 ? agent.Description[..60] + "…" : agent.Description,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#6a6a6a")),
                });
            }

            var btn = new Button
            {
                Classes = { "selector-item" },
                Content = sp,
                Tag = agent,
            };
            btn.Click += (_, _) => OnAgentItemSelected(agent, atIndex);
            AgentList.Items.Add(btn);
        }
    }

    /// <summary>
    /// 选中智能体后：将 @关键字 替换为 @智能体名 并记录提及。
    /// </summary>
    private void OnAgentItemSelected(AgentEntity agent, int atIndex)
    {
        AgentPopup.IsOpen = false;

        var text = InputBox.Text ?? string.Empty;
        var caret = InputBox.CaretIndex;

        var replacement = $"@{agent.Name} ";
        var newText = string.Concat(text.AsSpan(0, atIndex), replacement, text.AsSpan(caret));
        InputBox.Text = newText;
        InputBox.CaretIndex = atIndex + replacement.Length;

        _agentMentions[agent.Name] = agent;
    }

    /// <summary>
    /// 高亮指定索引的智能体列表项。
    /// </summary>
    private void HighlightAgentItem(int index)
    {
        for (var i = 0; i < AgentList.Items.Count; i++)
        {
            if (AgentList.Items[i] is Button btn)
            {
                btn.Background = i == index
                    ? new SolidColorBrush(Color.Parse("#2a2d2e"))
                    : Brushes.Transparent;
            }
        }
    }
}
