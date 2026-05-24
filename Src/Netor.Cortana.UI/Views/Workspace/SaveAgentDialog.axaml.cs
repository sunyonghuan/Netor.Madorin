using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.ViewModels.Workspace;

namespace Netor.Cortana.UI.Views.Workspace;

/// <summary>
/// P2-4：保存常用 Agent 对话框。
/// </summary>
/// <remarks>
/// 由 <c>WorkflowDetailView.axaml.cs</c> 在收到 <c>OnWorkflowTaskCompleted</c> 后弹出，
/// 输入是 <see cref="DynamicAgentRecord"/> 列表 snapshot（必须在 ClearTask 之前拷贝）。
///
/// 重名处理：保存前调用 <c>AgentService.GetByName</c> 预检；命中则在该行 NameError 提示，
/// 用户改名后再次提交。详见 plan §C.2。
/// </remarks>
public partial class SaveAgentDialog : Window
{
    private readonly AvaloniaList<SaveAgentItemVm> _items = [];
    private readonly string _managerProviderId;
    private readonly string _managerModelId;
    private readonly string _taskId;

    /// <summary>
    /// 设计期构造（XAML 预览用）。运行时使用带 records 的重载。
    /// </summary>
    public SaveAgentDialog() : this([], string.Empty, string.Empty, string.Empty) { }

    public SaveAgentDialog(
        IReadOnlyList<SaveAgentItemVm> items,
        string taskId,
        string managerProviderId,
        string managerModelId)
    {
        InitializeComponent();

        _taskId = taskId ?? string.Empty;
        _managerProviderId = managerProviderId ?? string.Empty;
        _managerModelId = managerModelId ?? string.Empty;

        foreach (var item in items ?? [])
        {
            _items.Add(item);
        }

        ItemsList.ItemsSource = _items;
        HeaderHint.Text = $"本任务创建了 {_items.Count} 个临时子智能体，勾选要保存为永久 Agent 的项。" +
                          "保存后可在「智能体设置」中查看与编辑。";
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 1. 收集勾选项
            var selected = _items.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                ShowGlobalError("未选择任何项；如不需要保存请点[全部丢弃]。");
                return;
            }

            // 2. 重置错误状态 + 校验
            HideGlobalError();
            foreach (var item in selected) item.NameError = string.Empty;

            var agentService = App.Services.GetRequiredService<AgentService>();
            var hasError = false;
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in selected)
            {
                var name = item.NewName?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                {
                    item.NameError = "名称不能为空";
                    hasError = true;
                    continue;
                }
                if (!seenNames.Add(name))
                {
                    item.NameError = $"本对话框内有重复名称 '{name}'";
                    hasError = true;
                    continue;
                }
                if (agentService.GetByName(name) is not null)
                {
                    item.NameError = $"已存在同名智能体 '{name}'，请改名";
                    hasError = true;
                }
            }

            if (hasError)
            {
                ShowGlobalError("有命名冲突，请按行内提示改名后再保存。");
                return;
            }

            // 3. 校验通过 → 逐条 Add
            foreach (var item in selected)
            {
                var entity = new AgentEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = (item.NewName ?? item.OriginalName).Trim(),
                    Instructions = item.OriginalInstructions,
                    Description = string.IsNullOrEmpty(_taskId)
                        ? $"由工作流保存的子智能体（{item.OriginalResponsibility}）"
                        : $"由工作流 {_taskId[..Math.Min(8, _taskId.Length)]} 保存的子智能体（{item.OriginalResponsibility}）",
                    DefaultProviderId = _managerProviderId,
                    DefaultModelId = _managerModelId,
                    IsEnabled = true,
                    AllowWorkflowMemory = true,
                };
                agentService.Add(entity);
            }

            Close();
        }
        catch (Exception ex)
        {
            ShowGlobalError($"保存失败：{ex.Message}");
        }
    }

    private void OnDiscardClick(object? sender, RoutedEventArgs e) => Close();

    private void ShowGlobalError(string text)
    {
        GlobalErrorText.Text = text;
        GlobalErrorText.IsVisible = true;
    }

    private void HideGlobalError()
    {
        GlobalErrorText.Text = string.Empty;
        GlobalErrorText.IsVisible = false;
    }
}
