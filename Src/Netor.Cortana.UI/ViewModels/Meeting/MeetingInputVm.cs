using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Avalonia.Threading;

using Netor.Cortana.AI;
using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Controls.Meeting;
using Netor.Cortana.UI.ViewModels.Common;

namespace Netor.Cortana.UI.ViewModels.Meeting;

/// <summary>
/// 会议模式输入 ViewModel，负责会议创建、参会者多选、回复、插话和取消。
/// </summary>
public sealed class MeetingInputVm : IInputVm
{
    private const int MinParticipants = 2;

    private readonly MeetingSessionService _sessions;
    private readonly MeetingAttachmentService _meetingAttachments;
    private readonly MeetingExecutor _executor;
    private readonly ICurrentSessionResolver _sessionResolver;
    private readonly AiChatHostedService _chatService;
    private readonly AgentService _agentService;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly IPublisher _publisher;
    private readonly MeetingAttachmentImporter _attachmentImporter;

    private readonly HashSet<string> _selectedAgentIds = new(StringComparer.Ordinal);
    private MeetingViewController? _controller;
    private string _initialInput = string.Empty;
    private string? _activeMeetingId;
    private AiProviderEntity? _selectedProvider;
    private AiModelEntity? _selectedModel;
    private bool _isRunning;
    private bool _isTaskActive;
    private string? _validationError;

    public MeetingInputVm(
        MeetingSessionService sessions,
        MeetingAttachmentService meetingAttachments,
        MeetingExecutor executor,
        ICurrentSessionResolver sessionResolver,
        AiChatHostedService chatService,
        AgentService agentService,
        AiProviderService providerService,
        AiModelService modelService,
        IPublisher publisher,
        MeetingAttachmentImporter attachmentImporter,
        ISubscriber subscriber)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _meetingAttachments = meetingAttachments ?? throw new ArgumentNullException(nameof(meetingAttachments));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
        _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _attachmentImporter = attachmentImporter ?? throw new ArgumentNullException(nameof(attachmentImporter));

        LoadAvailableAgents();
        LoadAvailableProviders();
        RestoreDefaultSelections();
        SubscribeMeetingEvents(subscriber);
    }

    public event Action<string>? UserMessageSubmitted;

    public string InitialInput
    {
        get => _initialInput;
        set
        {
            if (SetField(ref _initialInput, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanSubmit));
                OnPropertyChanged(nameof(InputPlaceholderText));
            }
        }
    }

    public bool IsIdle => !_isRunning;

    public bool IsTaskActive
    {
        get => _isTaskActive;
        private set
        {
            if (SetField(ref _isTaskActive, value))
            {
                OnPropertyChanged(nameof(CanSubmit));
                OnPropertyChanged(nameof(InputPlaceholderText));
            }
        }
    }

    public bool CanSubmit => _selectedModel is { IsEnabled: true }
        && (!string.IsNullOrWhiteSpace(_initialInput) || Attachments.Count > 0);

    public string? ValidationError
    {
        get => _validationError;
        private set => SetField(ref _validationError, value);
    }

    public string InputPlaceholderText => _activeMeetingId is null
        ? "输入会议主题..."
        : IsRunning
            ? "会议进行中，可输入插话或 /end 结束..."
            : "可继续讨论、要求总结，或输入 /end 散会...";

    public ObservableCollection<AttachmentInfo> Attachments { get; } = [];

    public ObservableCollection<AgentEntity> AvailableAgents { get; } = [];

    public ObservableCollection<AiProviderEntity> AvailableProviders { get; } = [];

    public ObservableCollection<AiModelEntity> AvailableModels { get; } = [];

    public IReadOnlySet<string> SelectedAgentIds => _selectedAgentIds;

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

    public string SelectedAgentsLabel
        => _selectedAgentIds.Count == 0 ? "智能体" : $"智能体 ({_selectedAgentIds.Count})";

    public bool HasActiveMeeting => !string.IsNullOrWhiteSpace(_activeMeetingId);

    public string? ActiveMeetingId => _activeMeetingId;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void BindController(MeetingViewController controller)
    {
        _controller = controller;
    }

    public void SetActiveMeeting(string? meetingId)
    {
        _activeMeetingId = string.IsNullOrWhiteSpace(meetingId) ? null : meetingId;
        IsTaskActive = _activeMeetingId is not null;
        OnPropertyChanged(nameof(ActiveMeetingId));
        OnPropertyChanged(nameof(HasActiveMeeting));
        OnPropertyChanged(nameof(InputPlaceholderText));
    }

    public void ToggleAgent(string agentId, bool selected)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        if (selected)
        {
            _selectedAgentIds.Add(agentId);
        }
        else
        {
            _selectedAgentIds.Remove(agentId);
        }

        OnPropertyChanged(nameof(SelectedAgentsLabel));
    }

    public void ShowSelectionLockedNotice()
    {
        if (!HasActiveMeeting)
        {
            return;
        }

        ValidationError = "当前会议的厂商和模型已锁定，新选择将在下次会议生效";
    }

    public void LoadAvailableAgents()
    {
        var selectedIds = _selectedAgentIds.ToHashSet(StringComparer.Ordinal);
        AvailableAgents.Clear();
        foreach (var agent in _agentService.GetSelectable())
        {
            AvailableAgents.Add(agent);
        }

        _selectedAgentIds.Clear();
        foreach (var agent in AvailableAgents.Where(a => selectedIds.Contains(a.Id)))
        {
            _selectedAgentIds.Add(agent.Id);
        }

        foreach (var agent in AvailableAgents.OrderBy(a => a.SortOrder))
        {
            if (_selectedAgentIds.Count >= MinParticipants)
            {
                break;
            }

            _selectedAgentIds.Add(agent.Id);
        }

        OnPropertyChanged(nameof(SelectedAgentsLabel));
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
        var text = _initialInput.Trim();
        if (_selectedModel is null || !_selectedModel.IsEnabled)
        {
            ValidationError = "当前没有可用的 AI 模型。";
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ValidationError = null;

        if (_activeMeetingId is not null)
        {
            ClearInput();
            UserMessageSubmitted?.Invoke(text);
            if (_controller is not null)
            {
                await _controller.SubmitUserTextAsync(text, cancellationToken);
            }
            return;
        }

        if (_selectedAgentIds.Count < MinParticipants)
        {
            ValidationError = $"会议至少需要 {MinParticipants} 位参会者(当前 {_selectedAgentIds.Count} 位)";
            return;
        }

        var meetingId = await CreateMeetingAsync(text, cancellationToken);
        ClearInput();
        SetActiveMeeting(meetingId);
        UserMessageSubmitted?.Invoke(text);

        IsTaskActive = true;
        IsRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _executor.ExecuteAsync(
                    meetingId,
                    _selectedProvider?.Id,
                    _selectedModel?.Id,
                    cancellationToken);
            }
            finally
            {
                Dispatcher.UIThread.Post(() => IsRunning = false);
            }
        }, cancellationToken);
    }

    private void ClearInput()
    {
        _initialInput = string.Empty;
        OnPropertyChanged(nameof(InitialInput));
        OnPropertyChanged(nameof(CanSubmit));
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_activeMeetingId is null)
        {
            return;
        }

        // 乐观即时恢复：UI 立刻切回空闲态，无需等待后台暂停完成。
        // MarkMeetingIdle / MarkMeetingRunning 事件仍是最终真相，幂等不冲突。
        IsRunning = false;

        await _executor.PauseAsync(_activeMeetingId, cancellationToken);
    }

    public void MarkMeetingRunning(string meetingId, bool isRunning)
    {
        if (!string.Equals(_activeMeetingId, meetingId, StringComparison.Ordinal))
        {
            return;
        }

        IsTaskActive = true;
        IsRunning = isRunning;
    }

    public void MarkMeetingIdle(string meetingId)
    {
        if (!string.Equals(_activeMeetingId, meetingId, StringComparison.Ordinal))
        {
            return;
        }

        IsRunning = false;
        IsTaskActive = true;
    }

    private async Task<string> CreateMeetingAsync(string topic, CancellationToken cancellationToken)
    {
        var provider = _selectedProvider ?? throw new InvalidOperationException("请选择会议主持人厂商");
        var model = _selectedModel ?? throw new InvalidOperationException("请选择会议主持人模型");
        var participants = AvailableAgents
            .Where(a => _selectedAgentIds.Contains(a.Id))
            .Select((a, index) => new MeetingParticipantDto(a.Id, a.Name, index))
            .ToList();
        var workspaceId = _sessionResolver.GetCurrentWorkspaceId() ?? _chatService.CurrentWorkspaceId ?? string.Empty;
        var sessionId = _sessions.CreateBackingChatSession(
            workspaceId,
            participants.FirstOrDefault()?.AgentId ?? string.Empty);
        var meeting = new MeetingSessionEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            Topic = topic,
            ParticipantsJson = JsonSerializer.Serialize(participants, MeetingJsonContext.Default.ListMeetingParticipantDto),
            Status = 0,
            Provider = provider.Id,
            Model = model.Id,
        };

        var meetingId = _sessions.Create(meeting);
        foreach (var attachment in Attachments.ToList())
        {
            var imported = _attachmentImporter.Import(meetingId, attachment);
            _meetingAttachments.Add(new MeetingAttachmentEntity
            {
                MeetingId = meetingId,
                FileName = imported.FileName,
                StoredPath = imported.RelativePath,
                MimeType = attachment.MimeType,
                SizeBytes = imported.SizeBytes,
            });
        }
        Attachments.Clear();

        await _publisher.PublishAsync(
            Events.OnMeetingCreated,
            new MeetingCreatedArgs(meetingId, sessionId, topic, participants));
        return meetingId;
    }

    private void RestoreDefaultSelections()
    {
        _selectedProvider = AvailableProviders.FirstOrDefault(p => p.IsDefault) ?? AvailableProviders.FirstOrDefault();
        if (_selectedProvider is not null)
        {
            LoadModelsForProvider(_selectedProvider.Id);
        }

        _selectedModel = AvailableModels.FirstOrDefault(m => m.IsDefault) ?? AvailableModels.FirstOrDefault();
        foreach (var agent in AvailableAgents.OrderBy(a => a.SortOrder).Take(MinParticipants))
        {
            _selectedAgentIds.Add(agent.Id);
        }

        OnPropertyChanged(nameof(SelectedProviderName));
        OnPropertyChanged(nameof(SelectedModelName));
        OnPropertyChanged(nameof(SelectedAgentsLabel));
    }

    private void SubscribeMeetingEvents(ISubscriber subscriber)
    {
        subscriber.Subscribe<MeetingCreatedArgs>(Events.OnMeetingCreated, (_, args) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                SetActiveMeeting(args.MeetingId);
                IsRunning = true;
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<MeetingSpeakerChangedArgs>(Events.OnMeetingSpeakerChanged, (_, args) =>
        {
            MarkMeetingActivity(args.MeetingId);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<MeetingMessageDeltaArgs>(Events.OnMeetingMessageDelta, (_, args) =>
        {
            MarkMeetingActivity(args.MeetingId);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<MeetingThinkingDeltaArgs>(Events.OnMeetingThinkingDelta, (_, args) =>
        {
            MarkMeetingActivity(args.MeetingId);
            return Task.FromResult(false);
        });
        subscriber.Subscribe<MeetingAskUserRequestedArgs>(Events.OnMeetingAskUserRequested, (_, args) =>
        {
            if (!string.Equals(_activeMeetingId, args.MeetingId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                IsTaskActive = true;
                IsRunning = false;
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<MeetingCompletedArgs>(Events.OnMeetingCompleted, (_, args) =>
        {
            if (!string.Equals(_activeMeetingId, args.MeetingId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                MarkMeetingIdle(args.MeetingId);
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<MeetingPausedArgs>(Events.OnMeetingPaused, (_, args) =>
        {
            if (!string.Equals(_activeMeetingId, args.MeetingId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                IsTaskActive = true;
                IsRunning = false;
            });
            return Task.FromResult(false);
        });
        subscriber.Subscribe<MeetingCancelledArgs>(Events.OnMeetingCancelled, (_, args) =>
        {
            if (!string.Equals(_activeMeetingId, args.MeetingId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                IsRunning = false;
                IsTaskActive = false;
                SetActiveMeeting(null);
            });
            return Task.FromResult(false);
        });
    }

    private void MarkMeetingActivity(string meetingId)
    {
        if (!string.Equals(_activeMeetingId, meetingId, StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            IsTaskActive = true;
            IsRunning = true;
        });
    }

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
