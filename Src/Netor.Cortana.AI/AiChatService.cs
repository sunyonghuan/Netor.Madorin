using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.Text;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netor.Cortana.AI;

/// <summary>
/// 统一 AI 对话引擎。纯粹的 AI 处理核心，不感知具体输入来源，
/// 通过 <see cref="IAiChatEngine"/> 接口接收输入，
/// 将 AI 流式回复广播到所有活跃的 <see cref="IAiOutputChannel"/> 输出通道。
/// </summary>
public sealed class AiChatHostedService(
    AIAgentFactory factory,
     ChatHistoryDataProvider chatHistoryProvider,
    CortanaDbContext dbContext,
    AiProviderService providerService,
    AgentService agentService,
    AiModelService modelService,
    IEnumerable<IAiOutputChannel> outputChannels,
    IPublisher publisher,
    ISubscriber subscriber,
    ILogger<AiChatHostedService> logger) : IAiChatEngine, IHostedService, IDisposable
{
    private AIAgent? _agent;
    private AgentSession? _session;
    private string _sessionId = string.Empty;
    private CancellationTokenSource? _streamCts;
    private bool _isRunning;
    private bool _disposed;

    // 当前选中的上下文
    private AiProviderEntity? _currentProvider;

    private AgentEntity? _currentAgent;
    private AiModelEntity? _currentModel;

    private bool _eventsSubscribed;
    private CancellationTokenSource? _serviceCts;

    /// <summary>
    /// 当前是否正在进行 AI 对话。
    /// </summary>
    public bool IsRunning => _isRunning;

    // ──────────────────── IHostedService ────────────────────

    /// <summary>
    /// 启动 AI 对话引擎。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        LoadDefaults();

        if (_currentProvider is not null && _currentAgent is not null && _currentModel is not null)
        {
            _agent = factory.Build(_currentAgent, _currentProvider, _currentModel);
            logger.LogInformation("AI 对话服务已初始化：Provider={Provider}, Agent={Agent}, Model={Model}",
                _currentProvider.Name, _currentAgent.Name, _currentModel);
        }
        else
        {
            logger.LogWarning("未找到默认的 AI 提供商/智能体/模型，AI 对话功能不可用");
        }

        SubscribeConfigChangeEvents();
        logger.LogInformation("AI 对话引擎已启动");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 AI 对话引擎。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _serviceCts?.Cancel();
        CancelCurrentTask();
        logger.LogInformation("AI 对话引擎已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 切换 AI 提供商。
    /// </summary>
    public void ChangeProvider(string providerId)
    {
        _currentProvider = providerService.GetById(providerId);
        RebuildAgent();
    }

    /// <summary>
    /// 切换 AI 模型。
    /// </summary>
    public void ChangeModel(string modelId)
    {
        var model = modelService.GetById(modelId);

        if (model is not null)
        {
            _currentModel = model;
            RebuildAgent();
        }
    }

    /// <summary>
    /// 切换智能体。
    /// </summary>
    public void ChangeAgent(string agentId)
    {
        _currentAgent = agentService.GetById(agentId);
        RebuildAgent();
    }

    /// <summary>
    /// 创建新会话。
    /// </summary>
    public async Task NewSessionAsync()
    {
        if (_agent is null)
            RebuildAgent();
        if (_agent is null) return;
        _session = await _agent.CreateSessionAsync(CancellationToken.None);
        _sessionId = Guid.NewGuid().ToString("N");
        _session.StateBag.SetValue("sessionid", _sessionId);

        if (_currentModel is not null)
        {
            _session.StateBag.SetValue("modelid", _currentModel.Name);
            _session.StateBag.SetValue("modeldbid", _currentModel.Id);
        }

        _sessionId = await chatHistoryProvider.CreateNewSessionAsync(_session, _agent);
        publisher.Publish(Events.OnSessionCreated, new SessionCreatedArgs(_sessionId));
    }

    /// <summary>
    /// 恢复指定历史会话。
    /// </summary>
    public async Task ResumeSessionAsync(string sessionId)
    {
        if (_agent is null) return;

        _session = await _agent.CreateSessionAsync(CancellationToken.None);
        _sessionId = sessionId;
        _session.StateBag.SetValue("sessionid", sessionId);

        if (_currentModel is not null)
        {
            _session.StateBag.SetValue("modelid", _currentModel.Name);
            _session.StateBag.SetValue("modeldbid", _currentModel.Id);
        }
    }

    /// <summary>
    /// 中止当前正在进行的流式响应。
    /// </summary>
    public void Stop()
    {
        if (_isRunning)
        {
            _streamCts?.Cancel();
            _isRunning = false;
        }
    }

    /// <summary>
    /// 取消当前正在进行的 AI 对话，并通知所有输出通道清理。
    /// </summary>
    public void CancelCurrentTask()
    {
        _streamCts?.Cancel();
        NotifyChannelsCancelled();
    }

    /// <summary>
    /// 发送用户消息并启动流式响应。支持附件（图片/文件）的多模态消息。
    /// AI 流式回复将广播到所有活跃的输出通道。
    /// </summary>
    public async Task SendMessageAsync(string userInput, CancellationToken cancellationToken, List<AttachmentInfo>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(userInput) && (attachments is null || attachments.Count == 0)) return;

        if (_isRunning)
        {
            logger.LogInformation("当前对话正在进行中，取消旧任务以接受新输入");
            CancelCurrentTask();

            // 等待旧任务的 finally 块完成（释放 _streamCts、重置 _isRunning）
            var spinWait = new SpinWait();
            while (_isRunning)
            {
                spinWait.SpinOnce();
            }
        }

        if (_currentProvider is null || _currentAgent is null || _currentModel is null)
        {
            logger.LogWarning("AI 服务未初始化（缺少默认提供商/智能体/模型），跳过回复");
            return;
        }

        // 确保 Agent 和 Session 已初始化
        _agent ??= factory.Build(_currentAgent, _currentProvider, _currentModel);

        if (_session is null)
        {
            await LoadOrCreateSessionAsync(cancellationToken);
        }

        _isRunning = true;
        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        publisher.Publish(Events.OnAiStarted, new VoiceSignalArgs());

        try
        {
            // 构建多模态消息内容
            var contents = new List<AIContent>();

            if (!string.IsNullOrWhiteSpace(userInput))
            {
                contents.Add(new TextContent(userInput));
            }

            // 读取附件文件并构建对应的 AIContent
            if (attachments is { Count: > 0 })
            {
                foreach (var attachment in attachments)
                {
                    try
                    {
                        if (!File.Exists(attachment.Path))
                        {
                            logger.LogWarning("附件文件不存在：{Path}", attachment.Path);
                            continue;
                        }

                        if (IsImageMimeType(attachment.MimeType))
                        {
                            var imageData = await DataContent.LoadFromAsync(attachment.Path, attachment.MimeType, cancellationToken: _streamCts.Token);
                            contents.Add(imageData);
                            contents.Add(new TextContent($" ![{attachment.Name}]({attachment.Path}) "));
                        }
                        else
                        {
                            contents.Add(new TextContent($" [{attachment.Name}]({attachment.Path}) "));
                        }

                        logger.LogInformation("已加载附件：{Name}（{MimeType}）",
                            attachment.Name, attachment.MimeType);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "读取附件文件失败：{Path}", attachment.Path);
                    }
                }
            }

            if (contents.Count == 0)
            {
                logger.LogWarning("没有有效的消息内容可发送");
                return;
            }

            var msg = new AIChatMessage
            {
                Role = ChatRole.User,
                CreatedAt = DateTimeOffset.UtcNow,
                Contents = contents,
                AuthorName = "用户",
                MessageId = Guid.NewGuid().ToString("N")
            };

            SaveUserMessage(msg.Text);

            var fullResponse = new StringBuilder();

            await foreach (var chunk in _agent!.RunStreamingAsync(msg, _session!, cancellationToken: _streamCts.Token))
            {
                if (chunk.Role is not null && chunk.Role != ChatRole.Assistant) continue;
                if (string.IsNullOrEmpty(chunk.Text)) continue;

                fullResponse.Append(chunk.Text);

                // 向所有活跃的输出通道广播 token
                foreach (var channel in outputChannels)
                {
                    if (!channel.IsActive) continue;
                    try
                    {
                        await channel.OnTokenAsync(chunk.Text, _streamCts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "输出通道 {Channel} 处理 token 失败", channel.Name);
                    }
                }
                //var usage = chunk.Contents.FirstOrDefault(t => t is UsageContent);
                //if (chunk.RawRepresentation is ChatResponseUpdate chatResponse)
                //{
                //    var usege = chatResponse.RawRepresentation;
                //}
            }

            var sessionId = _session?.StateBag.GetValue<string>("sessionid") ?? "";

            // 向所有活跃的输出通道广播完成
            foreach (var channel in outputChannels)
            {
                if (channel.IsActive)
                {
                    try
                    {
                        await channel.OnDoneAsync(sessionId, _streamCts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "输出通道 {Channel} 处理完成事件失败", channel.Name);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(fullResponse.ToString()))
            {
                logger.LogInformation("AI 对话完成，回复长度：{Length}", fullResponse.Length);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("AI 流式响应被取消");
            NotifyChannelsCancelled();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI 流式响应出错");

            foreach (var channel in outputChannels)
            {
                if (channel.IsActive)
                {
                    try
                    {
                        await channel.OnErrorAsync($"AI 对话出错：{ex.Message}");
                    }
                    catch { }
                }
            }
        }
        finally
        {
            _isRunning = false;
            _streamCts?.Dispose();
            _streamCts = null;

            // 无条件通知：AI 推理已结束（无论成功/失败/取消）
            publisher.Publish(Events.OnAiCompleted, new VoiceSignalArgs());
        }
    }

    /// <summary>
    /// 保存用户消息到数据库。
    /// </summary>
    private void SaveUserMessage(string content)
    {
        try
        {
            var sessionId = _session?.StateBag.GetValue<string>("sessionid") ?? "";
            var entity = new ChatMessageEntity
            {
                Role = ChatRole.User.ToString(),
                Content = content,
                AuthorName = "用户",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ModelName = _currentModel?.Name ?? string.Empty
            };

            dbContext.Execute(
                """
                INSERT OR REPLACE INTO ChatMessages
                    (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, TokenCount, ModelName, CreatedAt)
                VALUES
                    (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @TokenCount, @ModelName, @CreatedAt)
                """,
                cmd => ChatMessageService.BindEntity(cmd, entity));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存用户消息失败");
        }
    }

    // ──────────────────── 初始化 ────────────────────

    /// <summary>
    /// 从数据库加载标记为默认的 Provider、Agent 和 Model。
    /// </summary>
    private void LoadDefaults()
    {
        var providers = providerService.GetAll();
        _currentProvider = providers.FirstOrDefault(p => p.IsDefault) ?? providers.FirstOrDefault();

        var agents = agentService.GetAll();
        _currentAgent = agents.FirstOrDefault(a => a.IsDefault) ?? agents.FirstOrDefault();

        if (_currentProvider is not null)
        {
            var models = modelService.GetByProviderId(_currentProvider.Id);
            var defaultModel = models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault();
            _currentModel = defaultModel;
        }
    }

    /// <summary>
    /// 加载最近的 Session 或新建一个，确保对话的上下文连续。
    /// </summary>
    private async Task LoadOrCreateSessionAsync(CancellationToken cancellationToken)
    {
        if (_agent is null) return;

        var recentMessage = dbContext.QueryFirstOrDefault(
            "SELECT * FROM ChatMessages ORDER BY CreatedTimestamp DESC LIMIT 1",
            ChatMessageService.ReadEntity);

        _session = await _agent.CreateSessionAsync(cancellationToken);

        if (recentMessage is not null && !string.IsNullOrEmpty(recentMessage.SessionId))
        {
            _sessionId = recentMessage.SessionId;
            logger.LogDebug("恢复对话 Session：{SessionId}", _sessionId);
        }
        else
        {
            _sessionId = Guid.NewGuid().ToString("N");
            logger.LogDebug("新建对话 Session：{SessionId}", _sessionId);
        }

        _session.StateBag.SetValue("sessionid", _sessionId);

        if (_currentModel is not null)
        {
            _session.StateBag.SetValue("modelid", _currentModel?.Name ?? string.Empty);
            _session.StateBag.SetValue("modeldbid", _currentModel?.Id);
        }
    }

    /// <summary>
    /// 订阅 AI 配置变更事件，在厂商/模型/智能体变更时重置初始化状态。
    /// </summary>
    private void SubscribeConfigChangeEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;

        subscriber.Subscribe<DataChangeArgs>(Events.OnAiProviderChange, (_, _) =>
        {
            ResetInitialization("AI 厂商");
            return Task.FromResult(false);
        });

        subscriber.Subscribe<DataChangeArgs>(Events.OnAiModelChange, (_, _) =>
        {
            ResetInitialization("AI 模型");
            return Task.FromResult(false);
        });

        subscriber.Subscribe<DataChangeArgs>(Events.OnAgentChange, (_, _) =>
        {
            ResetInitialization("智能体");
            return Task.FromResult(false);
        });

        subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, _) =>
        {
            ResetInitialization("工作目录");
            return Task.FromResult(false);
        });
    }

    /// <summary>
    /// 重置初始化状态，下次对话时将重新加载默认配置并重建 Agent。
    /// </summary>
    private void ResetInitialization(string source)
    {
        LoadDefaults();
        _agent = null;
        _session = null;
        logger.LogInformation("{Source}配置已变更，AI 对话服务已重新加载默认配置", source);
    }

    // ──────────────────── 工具方法 ────────────────────

    /// <summary>
    /// 通知所有输出通道：AI 对话已取消，请清理缓冲区并停止进行中的工作。
    /// </summary>
    private void NotifyChannelsCancelled()
    {
        foreach (var channel in outputChannels)
        {
            try
            {
                channel.OnCancelledAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "输出通道 {Channel} 处理取消事件失败", channel.Name);
            }
        }
    }

    /// <summary>
    /// 重新构建 Agent 实例（当提供商、模型或智能体通过前端手动切换时调用）。
    /// </summary>
    private void RebuildAgent()
    {
        if (_currentProvider is null || _currentAgent is null || _currentModel is null)
        {
            return;
        }

        _agent = factory.Build(_currentAgent, _currentProvider, _currentModel);
        _session = null;
    }

    /// <summary>
    /// 判断 MIME 类型是否为图片格式。
    /// </summary>
    private static bool IsImageMimeType(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    // ──────────────────── IDisposable ────────────────────

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        subscriber.Dispose();
    }
}