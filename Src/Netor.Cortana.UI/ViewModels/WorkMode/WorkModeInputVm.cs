using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Netor.Cortana.AI;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.ViewModels.Common;
using Netor.EventHub;

namespace Netor.Cortana.UI.ViewModels.WorkModeVm;

/// <summary>
/// 工作模式输入框 ViewModel。
/// 将用户输入路由到 WorkflowExecutor 而非 AiChatHostedService。
/// </summary>
public sealed class WorkModeInputVm : IInputVm
{
    private readonly WorkflowExecutor _executor;
    private readonly WorkTaskService _taskService;
    private readonly ICurrentSessionResolver _sessionResolver;
    private readonly AiChatHostedService _chatService;
    private readonly IPublisher _publisher;
    private readonly AgentService _agentService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly WorkPendingInputService _pendingInputService;
    private readonly RunningStepInterruptService _runningStepInterruptService;

    private string _initialInput = string.Empty;
    private AgentEntity? _selectedAgent;
    private AiProviderEntity? _selectedProvider;
    private AiModelEntity? _selectedModel;
    private bool _isRunning;
    private bool _isTaskActive;
    private string? _validationError;
    private List<AgentMention>? _pendingMentions;
    private string? _selectedTaskId;

    public WorkModeInputVm(
        WorkflowExecutor executor,
        WorkTaskService taskService,
        ICurrentSessionResolver sessionResolver,
        AiChatHostedService chatService,
        IPublisher publisher,
        AgentService agentService,
        AiProviderService providerService,
        AiModelService modelService,
        WorkPendingInputService pendingInputService,
        RunningStepInterruptService runningStepInterruptService,
        ISubscriber subscriber)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _pendingInputService = pendingInputService ?? throw new ArgumentNullException(nameof(pendingInputService));
        _runningStepInterruptService = runningStepInterruptService ?? throw new ArgumentNullException(nameof(runningStepInterruptService));

        LoadAvailableAgents();
        LoadAvailableProviders();
        RestoreDefaultSelections();

        subscriber.Subscribe<WorkTaskCreatedArgs>(Events.OnWorkTaskCreated, (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BindCurrentTaskIfInWorkspace(args.TaskId);
                IsTaskActive = true;
                IsRunning = true;
                OnPropertyChanged(nameof(IsStepExecutionPhase));
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<WorkTaskCompletedArgs>(Events.OnWorkTaskCompleted, (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BindCurrentTaskIfInWorkspace(args.TaskId);
                IsRunning = false;
                IsTaskActive = IsSelectedTaskActive();
                OnPropertyChanged(nameof(IsStepExecutionPhase));
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<WorkTaskFailedArgs>(Events.OnWorkTaskFailed, (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BindCurrentTaskIfInWorkspace(args.TaskId);
                IsRunning = false;
                IsTaskActive = IsSelectedTaskActive();
                OnPropertyChanged(nameof(IsStepExecutionPhase));
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<WorkTaskCancelledArgs>(Events.OnWorkTaskCancelled, (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BindCurrentTaskIfInWorkspace(args.TaskId);
                IsRunning = false;
                IsTaskActive = IsSelectedTaskActive();
                OnPropertyChanged(nameof(IsStepExecutionPhase));
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<WorkTaskPausedArgs>(Events.OnWorkTaskPaused, (context, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BindCurrentTaskIfInWorkspace(args.TaskId);
                IsRunning = false;
                IsTaskActive = IsSelectedTaskActive();
                OnPropertyChanged(nameof(IsStepExecutionPhase));
            });
            _ = HandlePausedTaskAsync(args.TaskId, args.Reason);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<WorkStepStartedArgs>(Events.OnWorkStepStarted, (_, args) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BindCurrentTaskIfInWorkspace(args.TaskId);
                IsTaskActive = true;
                IsRunning = true;
                OnPropertyChanged(nameof(IsStepExecutionPhase));
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<WorkAskUserRequestedArgs>(Events.OnWorkAskUserRequested, (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsTaskActive = true;
                IsRunning = false;
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<WorkApprovalRequestedArgs>(Events.OnWorkApprovalRequested, (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsTaskActive = true;
                IsRunning = false;
            });
            return Task.FromResult(false);
        });
    }

    public string InitialInput
    {
        get => _initialInput;
        set { _initialInput = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSubmit)); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsStepExecutionPhase));
            OnPropertyChanged(nameof(InputPlaceholderText));
        }
    }

    public bool IsIdle => !_isRunning;

    public bool IsStepExecutionPhase
        => IsTaskInStepExecutionPhase(GetSelectedTaskInCurrentWorkspace());

    public bool IsTaskActive
    {
        get => _isTaskActive;
        private set
        {
            if (_isTaskActive == value) return;
            _isTaskActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsStepExecutionPhase));
            OnPropertyChanged(nameof(InputPlaceholderText));
        }
    }
    public bool CanSubmit => _selectedModel is { IsEnabled: true }
        && (!string.IsNullOrWhiteSpace(_initialInput) || Attachments.Count > 0);

    public string? ValidationError
    {
        get => _validationError;
        private set { _validationError = value; OnPropertyChanged(); }
    }

    public string InputPlaceholderText => _isRunning
        ? "任务执行中，可继续补充要求（# 引用文件，@ 提及智能体，/end 关闭当前工作）"
        : "描述你想完成的任务（Enter 发送，Shift+Enter 换行，# 引用文件，@ 提及智能体，/end 关闭当前工作）";

    public AgentEntity? SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (SetField(ref _selectedAgent, value))
            {
                OnPropertyChanged(nameof(SelectedAgentName));
                if (value is not null)
                {
                    SyncProviderModelFromAgent(value);
                }
            }
        }
    }

    public string SelectedAgentName => _selectedAgent?.Name ?? "智能体";

    public AiProviderEntity? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetField(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(SelectedProviderName));
                if (value is not null)
                {
                    LoadModelsForProvider(value.Id);
                }
            }
        }
    }

    public string SelectedProviderName => _selectedProvider?.Name ?? "厂商";

    public AiModelEntity? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetField(ref _selectedModel, value))
            {
                OnPropertyChanged(nameof(SelectedModelName));
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    public string SelectedModelName
        => _selectedModel is null
            ? "模型"
            : !string.IsNullOrWhiteSpace(_selectedModel.DisplayName)
                ? _selectedModel.DisplayName!
                : _selectedModel.Name;

    public ObservableCollection<AttachmentInfo> Attachments { get; } = new();
    public ObservableCollection<AgentEntity> AvailableAgents { get; } = [];
    public ObservableCollection<AiProviderEntity> AvailableProviders { get; } = [];
    public ObservableCollection<AiModelEntity> AvailableModels { get; } = [];

    /// <summary>用户消息已提交（UI 用于追加用户气泡）。</summary>
    public event Action<string>? UserMessageSubmitted;

    public void SetPendingMentions(List<AgentMention>? mentions)
    {
        _pendingMentions = mentions is { Count: > 0 } ? mentions : null;
    }

    public void SetSelectedTask(string? taskId)
    {
        _selectedTaskId = string.IsNullOrWhiteSpace(taskId) ? null : taskId;
    }

    public void PrepareNewTask()
    {
        CloseSelectedTaskForNewWork();
        _selectedTaskId = null;
        _pendingMentions = null;
        ValidationError = null;
        InitialInput = string.Empty;
        Attachments.Clear();
        IsTaskActive = false;
        IsRunning = false;
        OnPropertyChanged(nameof(CanSubmit));
    }

    public void LoadAvailableAgents()
    {
        var selectedId = _selectedAgent?.Id;
        AvailableAgents.Clear();
        foreach (var agent in _agentService.GetSelectable())
        {
            AvailableAgents.Add(agent);
        }

        var defaultAgent = _agentService.GetDefaultOrFirst();
        _selectedAgent = AvailableAgents.FirstOrDefault(a => a.Id == selectedId)
            ?? AvailableAgents.FirstOrDefault(a => a.Id == defaultAgent?.Id)
            ?? AvailableAgents.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedAgent));
        OnPropertyChanged(nameof(SelectedAgentName));
    }

    public void LoadAvailableProviders()
    {
        var selectedId = _selectedProvider?.Id;
        AvailableProviders.Clear();
        foreach (var provider in _providerService.GetAll())
        {
            AvailableProviders.Add(provider);
        }

        _selectedProvider = AvailableProviders.FirstOrDefault(p => p.Id == selectedId)
            ?? AvailableProviders.FirstOrDefault(p => p.IsDefault)
            ?? AvailableProviders.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderName));

        if (_selectedProvider is not null)
        {
            LoadModelsForProvider(_selectedProvider.Id);
        }
    }

    public void LoadModelsForProvider(string providerId)
    {
        var selectedId = _selectedModel?.Id;
        AvailableModels.Clear();
        foreach (var model in _modelService.GetByProviderId(providerId))
        {
            AvailableModels.Add(model);
        }

        _selectedModel = AvailableModels.FirstOrDefault(m => m.Id == selectedId)
            ?? AvailableModels.FirstOrDefault(m => m.IsDefault)
            ?? AvailableModels.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedModelName));
        OnPropertyChanged(nameof(CanSubmit));
    }

    public async Task SubmitAsync(CancellationToken cancellationToken = default)
    {
        var text = _initialInput?.Trim();
        if (_selectedModel is null || !_selectedModel.IsEnabled)
        {
            ValidationError = "当前没有可用的 AI 模型。";
            return;
        }
        if (string.IsNullOrWhiteSpace(text)) return;

        ValidationError = null;

        // 清空输入框
        _initialInput = string.Empty;
        OnPropertyChanged(nameof(InitialInput));
        OnPropertyChanged(nameof(CanSubmit));

        if (IsEndCommand(text))
        {
            await HandleEndCommandAsync(cancellationToken);
            return;
        }

        var mentions = _pendingMentions;
        _pendingMentions = null;

        // 通知 UI 追加用户气泡
        UserMessageSubmitted?.Invoke(text);

        try
        {
            // 获取当前会话信息，没有则自动创建
            var sessionId = _sessionResolver.GetCurrentSessionId();
            if (sessionId is null)
            {
                await _chatService.NewSessionAsync();
                sessionId = _sessionResolver.GetCurrentSessionId();
                if (sessionId is null)
                {
                    ValidationError = "无法创建会话，请检查 AI 配置";
                    return;
                }
            }

            var selectedTask = GetSelectedTaskInCurrentWorkspace();
            if (selectedTask is not null)
            {
                if (!selectedTask.IsActive)
                {
                    ValidationError = "当前工作记录已关闭，请点击加号开始新的工作。";
                    return;
                }

                await SubmitToExistingTaskAsync(selectedTask, text, mentions, cancellationToken);
                return;
            }

            // 检查是否有活跃任务
            var activeTask = _taskService.GetActiveTask(sessionId);

            if (activeTask is null)
            {
                // 新建任务
                var agent = _selectedAgent ?? _chatService.CurrentAgent;
                var provider = _selectedProvider ?? _chatService.CurrentProvider;
                var model = _selectedModel;

                if (agent is null || provider is null || model is null)
                {
                    ValidationError = "AI 配置不完整";
                    return;
                }

                var taskId = Guid.NewGuid().ToString("N");
                var entity = new WorkTaskEntity
                {
                    Id = taskId,
                    SessionId = sessionId,
                    WorkspaceId = _chatService.CurrentWorkspaceId ?? string.Empty,
                    Title = text.Length > 50 ? text[..50] + "..." : text,
                    InitialInput = text,
                    IsActive = true,
                    Provider = provider.Id,
                    Model = model.Id,
                    AgentName = agent.Id,
                    MentionsJson = SerializeMentions(mentions),
                };
                _taskService.Create(entity);
                _selectedTaskId = taskId;

                IsTaskActive = true;
                StartWorkflowRun(
                    taskId,
                    () => _executor.ExecuteAsync(taskId, agent, provider, model, text, mentions, cancellationToken),
                    cancellationToken);
            }
            else
            {
                await SubmitToExistingTaskAsync(activeTask, text, mentions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            ValidationError = $"发送失败：{ex.Message}";
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        // 乐观即时恢复：UI 立刻切回空闲态，无需等待后台确认。
        // 工作流 finally 中的 IsRunning = keepRunning 仍是最终真相，幂等不冲突。
        IsRunning = false;

        var selectedTask = GetSelectedTaskInCurrentWorkspace();
        if (selectedTask is not null)
        {
            if (IsTaskInStepExecutionPhase(selectedTask))
            {
                await _executor.CancelAsync(selectedTask.Id, cancellationToken);
            }

            return;
        }

        var sessionId = _sessionResolver.GetCurrentSessionId();
        if (sessionId is null)
        {
            return;
        }

        var activeTask = _taskService.GetActiveTask(sessionId);
        if (activeTask is null)
        {
            return;
        }

        if (IsTaskInStepExecutionPhase(activeTask))
        {
            await _executor.CancelAsync(activeTask.Id, cancellationToken);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void StartWorkflowRun(string taskId, Func<Task> executeAsync, CancellationToken cancellationToken)
    {
        _selectedTaskId = taskId;
        IsRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await executeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ValidationError = $"发送失败：{ex.Message}";
                });
            }
            finally
            {
                var keepRunning = ShouldKeepRunningAfterWorkflowRun(taskId);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => IsRunning = keepRunning);
            }
        }, cancellationToken);
    }

    private bool ShouldKeepRunningAfterWorkflowRun(string taskId)
    {
        var task = _taskService.GetById(taskId);
        return task is { IsActive: true }
            && string.Equals(task.OrchestratorState, WorkTaskOrchestratorStates.Running, StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(task.PendingRequestId);
    }

    private async Task SubmitToExistingTaskAsync(
        WorkTaskEntity task,
        string text,
        List<AgentMention>? mentions,
        CancellationToken cancellationToken)
    {
        _selectedTaskId = task.Id;
        IsTaskActive = true;
        var isPendingRequest = !string.IsNullOrWhiteSpace(task.PendingRequestId);
        if (IsCancelCommand(text))
        {
            await _executor.CancelAsync(task.Id, cancellationToken);
            return;
        }

        if (IsRunning)
        {
            if (IsTaskInStepExecutionPhase(task))
            {
                var interrupted = await _runningStepInterruptService.InterruptCurrentStepAsync(task.Id, text, cancellationToken);
                if (!interrupted)
                {
                    _pendingInputService.Enqueue(task.Id, text);
                    _taskService.SetPreemption(task.Id, true);
                }
            }
            else if (isPendingRequest)
            {
                ValidationError = "当前确认正在处理中，请等待本轮处理完成。";
            }
            else
            {
                ValidationError = "当前回复正在处理中，请等待本轮处理完成。";
            }

            return;
        }

        StartWorkflowRun(
            task.Id,
            () => isPendingRequest
                ? _executor.ResumeAsync(task.Id, text, cancellationToken)
                : _executor.ContinueAsync(task.Id, text, mentions, cancellationToken),
            cancellationToken);
    }

    private WorkTaskEntity? GetSelectedTaskInCurrentWorkspace()
    {
        if (string.IsNullOrWhiteSpace(_selectedTaskId))
        {
            return null;
        }

        var task = _taskService.GetById(_selectedTaskId);
        var currentWorkspaceId = _chatService.CurrentWorkspaceId ?? _sessionResolver.GetCurrentWorkspaceId() ?? string.Empty;
        return task is not null
            && string.Equals(task.WorkspaceId, currentWorkspaceId, StringComparison.Ordinal)
            ? task
            : null;
    }

    private static bool IsTaskInStepExecutionPhase(WorkTaskEntity? task)
        => string.Equals(task?.OrchestratorState, WorkTaskOrchestratorStates.Running, StringComparison.Ordinal);

    private void BindCurrentTaskIfInWorkspace(string taskId)
    {
        var task = _taskService.GetById(taskId);
        var currentWorkspaceId = _chatService.CurrentWorkspaceId ?? _sessionResolver.GetCurrentWorkspaceId() ?? string.Empty;
        if (task is not null && string.Equals(task.WorkspaceId, currentWorkspaceId, StringComparison.Ordinal))
        {
            _selectedTaskId = taskId;
        }
    }

    private bool IsSelectedTaskActive()
        => GetSelectedTaskInCurrentWorkspace()?.IsActive == true;

    private void CloseSelectedTaskForNewWork()
    {
        var task = GetSelectedTaskInCurrentWorkspace();
        if (task is { IsActive: true })
        {
            _taskService.CloseTaskRecord(task.Id, "用户开始新的工作。");
            _ = _executor.CancelAsync(task.Id);
        }
    }

    private async Task HandleEndCommandAsync(CancellationToken cancellationToken)
    {
        _pendingMentions = null;

        var task = GetSelectedTaskInCurrentWorkspace();
        if (task is null)
        {
            var sessionId = _sessionResolver.GetCurrentSessionId();
            task = sessionId is null ? null : _taskService.GetActiveTask(sessionId);
        }

        if (task is null || !task.IsActive)
        {
            ValidationError = "当前没有可关闭的工作。";
            return;
        }

        await _executor.CancelAsync(task.Id, cancellationToken);
        _taskService.CloseTaskRecord(task.Id, "用户输入 /end 关闭当前工作。");
        await _publisher.PublishAsync(Events.OnWorkTaskCancelled, new WorkTaskCancelledArgs(task.Id));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private async Task HandlePausedTaskAsync(string taskId, string reason)
    {
        if (!string.Equals(reason, "user_preemption", StringComparison.Ordinal))
        {
            return;
        }

        var task = _taskService.GetById(taskId);
        if (task is null || !task.IsActive)
        {
            return;
        }

        var currentWorkspaceId = _chatService.CurrentWorkspaceId ?? _sessionResolver.GetCurrentWorkspaceId() ?? string.Empty;
        if (!string.Equals(task.WorkspaceId, currentWorkspaceId, StringComparison.Ordinal))
        {
            return;
        }

        var pendingInputs = _pendingInputService.Drain(taskId);
        if (pendingInputs.Count == 0)
        {
            return;
        }

        var mergedInput = string.Join(Environment.NewLine, pendingInputs.Where(static text => !string.IsNullOrWhiteSpace(text)).Select(static text => text.Trim()));
        if (string.IsNullOrWhiteSpace(mergedInput))
        {
            return;
        }

        try
        {
            StartWorkflowRun(
                taskId,
                () => _executor.ContinueAsync(taskId, mergedInput, cancellationToken: CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ValidationError = $"继续执行失败：{ex.Message}";
            });
        }
    }

    private static string? SerializeMentions(List<AgentMention>? mentions)
    {
        if (mentions is not { Count: > 0 })
        {
            return null;
        }

        var dto = mentions
            .Select(m => new AgentMentionDto(m.Agent.Id, m.Agent.Name))
            .ToList();
        return JsonSerializer.Serialize(dto, WorkModeJsonContext.Default.ListAgentMentionDto);
    }

    private void RestoreDefaultSelections()
    {
        var defaultAgent = _agentService.GetDefaultOrFirst();
        _selectedAgent = AvailableAgents.FirstOrDefault(a => a.Id == defaultAgent?.Id)
            ?? AvailableAgents.FirstOrDefault()
            ?? _chatService.CurrentAgent;

        _selectedProvider = AvailableProviders.FirstOrDefault(p => p.IsDefault)
            ?? AvailableProviders.FirstOrDefault()
            ?? _chatService.CurrentProvider;

        if (_selectedProvider is not null)
        {
            LoadModelsForProvider(_selectedProvider.Id);
        }

        _selectedModel = AvailableModels.FirstOrDefault(m => m.IsDefault)
            ?? AvailableModels.FirstOrDefault();

        OnPropertyChanged(nameof(SelectedAgent));
        OnPropertyChanged(nameof(SelectedAgentName));
        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedModelName));
        OnPropertyChanged(nameof(CanSubmit));
    }

    private void SyncProviderModelFromAgent(AgentEntity agent)
    {
    }

    private static bool IsCancelCommand(string text)
    {
        var normalized = text.Trim();
        return normalized is "取消" or "停止" or "停下" or "不要继续" or "别继续" or "终止" or "中止"
            || normalized.Contains("取消任务", StringComparison.Ordinal)
            || normalized.Contains("停止任务", StringComparison.Ordinal)
            || normalized.Contains("终止任务", StringComparison.Ordinal)
            || normalized.Contains("不要继续", StringComparison.Ordinal);
    }

    private static bool IsEndCommand(string text)
        => string.Equals(text.Trim(), "/end", StringComparison.OrdinalIgnoreCase);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
