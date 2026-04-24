using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using OpenAI.Chat;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 聊天历史数据持久化提供商，将对话消息保存到 SQLite 数据库。
/// 继承自 <see cref="ChatHistoryProvider"/>，在 Agent 执行完成后自动保存请求和响应消息。
/// </summary>
public sealed class ChatHistoryDataProvider(
    CortanaDbContext dbContext,
    SystemSettingsService systemSettings,
    IAppPaths appPaths,
    ILogger<ChatHistoryDataProvider> logger,
    IServiceProvider services) : ChatHistoryProvider, IDisposable

{
    internal const string NewSessionTitle = "新会话";

    private const string IsNewSessionStateKey = "isnewsession";

    /// <summary>
    /// 创建新的聊天会话记录，初始标题固定为“新会话”。
    /// </summary>
    public Task<string> CreateNewSessionAsync(AgentSession session, AIAgent agent)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agent);

        session.StateBag.SetValue(IsNewSessionStateKey, bool.TrueString);

        return AddOrUpdateSessionAsync(session, NewSessionTitle, agent);
    }

    /// <summary>
    /// Agent 执行完成后保存聊天历史到数据库。
    /// 确保工具调用链条完整（assistant tool_calls → tool result），每条消息独立 ID，按原序入库。
    /// </summary>
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelName = context.Session?.StateBag.GetValue<string>("modelid") ?? "Unknown";
        var modelDbId = context.Session?.StateBag.GetValue<string>("modeldbid");
        var modelService = services.GetRequiredService<Netor.Cortana.Entitys.Services.AiModelService>();
        var modelEntity = string.IsNullOrEmpty(modelDbId) ? null : modelService.GetById(modelDbId);
        var reasoningEnabled = modelEntity?.InteractionCapabilities.HasFlag(Netor.Cortana.Entitys.InteractionCapabilities.Reasoning) == true;
        var firstText = context.RequestMessages?.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var sessionId = await AddOrUpdateSessionAsync(context.Session, firstText.Truncate(32), context.Agent, accumulateInputTokens: true);

        // ──── 完整工具链条落库：保证消息顺序，每条消息独立 ID ────
        var messages = (context.ResponseMessages ?? [])
            .Select((t, index) => new ChatMessageEntity
            {
                Role = t.Role.ToString(),
            Content = ChatMessageExtensions.BuildPersistedContent(t.Text, FilterContents(t.Contents, reasoningEnabled)),
            ContentsJson = ChatMessageExtensions.BuildContentsJson(FilterContents(t.Contents, reasoningEnabled)),
                AuthorName = GetChatRoleText(t.Role, context.Agent.Name ?? "未知"),
                CreatedAt = t.CreatedAt ?? DateTimeOffset.UtcNow,
                CreatedTimestamp = t.CreatedAt?.ToLocalTime().ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                // ── 关键修复 1：每条消息独立 ID，不覆盖 ──
                // 优先 t.MessageId（OpenAI 兼容接口返回的真实消息 ID），兜底才生成新 Guid
                // 不再把 assistant 消息强行统一为同一个 assistantMessageId，避免链条断裂
                Id = t.MessageId ?? Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ModelName = modelName
            })
            .ToList(); // 立即物化，确保顺序固定且能多次遍历

        try
        {
            dbContext.ExecuteInTransaction(conn =>
            {
                // ── 关键修复 2：按原序逐条入库，保证链条不乱序 ──
                foreach (var message in messages)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO ChatMessages
                            (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, ContentsJson, TokenCount, ModelName, CreatedAt)
                        VALUES
                            (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @ContentsJson, @TokenCount, @ModelName, @CreatedAt)
                        """;
                    ChatMessageService.BindEntity(cmd, message);
                    cmd.ExecuteNonQuery();
                }

                // ── 关键修复 3：验证工具链完整性（日志诊断） ──
                // 检查本轮是否存在"孤立 tool 消息"（无对应 assistant tool_calls）
                var toolMessages = messages.Where(m => m.Role == ChatRole.Tool.ToString()).ToList();
                if (toolMessages.Count > 0)
                {
                    var assistantToolCallMessages = messages.Where(m => 
                        m.Role == ChatRole.Assistant.ToString() && 
                        !string.IsNullOrEmpty(m.ContentsJson) && 
                        (m.ContentsJson.Contains("functionCall") || m.ContentsJson.Contains("toolCall"))).ToList();

                    if (assistantToolCallMessages.Count == 0)
                    {
                        logger.LogWarning("⚠️ 工具链条警告：检测到 {ToolCount} 条 tool 消息，但无对应的 assistant tool_calls 消息。可能导致历史恢复时报错。", 
                            toolMessages.Count);
                    }
                    else
                    {
                        logger.LogInformation("✓ 工具链条完整：{ToolCallCount} 条 assistant tool_calls + {ToolCount} 条 tool 结果已同步入库。", 
                            assistantToolCallMessages.Count, toolMessages.Count);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存聊天历史到数据库时失败。");
        }

        static IList<AIContent>? FilterContents(IList<AIContent>? contents, bool enableReasoning)
        {
            if (enableReasoning) return contents;
            if (contents is null || contents.Count == 0) return contents;
            var filtered = contents.Where(c => c is not TextReasoningContent).ToList();
            return filtered.Count == 0 ? null : filtered;
        }

        // ---- 保存 AI 生成的多媒体资源（图片/音频/视频等）到资源库 ----
        // 注意：用户上传的附件已在 AiChatHostedService.SendMessageAsync 中处理（图片拷贝+入表，
        // 文档类直接引用原始路径）。此处仅处理"AI 返回的"多媒体内容（AssetKind=generated）。
        try
        {
            var assistantPersistedIds = messages
                .Where(m => m.Role == ChatRole.Assistant.ToString())
                .Select(m => m.Id)
                .ToList();
            await SaveGeneratedAssetsAsync(context, sessionId, assistantPersistedIds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存 AI 生成资源失败。");
        }

        // ---- 新增：压缩后回写 ----
        if (context.Session is null) return;
        await CompactAndReplaceAsync(context.Agent, context.Session, sessionId, cancellationToken);

        // ---- 新会话首次对话：异步生成 AI 摘要标题 ----
        if (context.Session.StateBag.GetValue<string>(IsNewSessionStateKey) == bool.TrueString)
        {
            context.Session.StateBag.SetValue(IsNewSessionStateKey, bool.FalseString);
            var aiResponse = context.ResponseMessages?.FirstOrDefault(m => m.Role == ChatRole.Assistant)?.Text ?? "";
            _ = GenerateAndUpdateTitleAsync(context.Agent, sessionId, firstText, aiResponse);
        }
    }

    /// <summary>
    /// 保存部分响应到数据库。用于处理用户取消时已输出但未完成的部分。
    /// </summary>
    public async Task SavePartialResponseAsync(
        string partialResponse,
        AgentSession? session,
        AIAgent? agent,
        string modelName = "Unknown",
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(partialResponse) || session is null || agent is null)
        {
            return;
        }

        try
        {
            var sessionId = session.StateBag.GetValue<string>("sessionid") ?? "";
            if (string.IsNullOrEmpty(sessionId))
            {
                // 如果会话ID不存在，先创建会话记录
                sessionId = await AddOrUpdateSessionAsync(session, "取消的对话", agent);
            }

            // 创建部分响应消息
            var partialContents = new List<AIContent> { new TextContent(partialResponse) };
            var message = new ChatMessageEntity
            {
                Role = ChatRole.Assistant.ToString(),
                Content = ChatMessageExtensions.BuildPersistedContent(partialResponse, partialContents),
                ContentsJson = ChatMessageExtensions.BuildContentsJson(partialContents),
                AuthorName = GetChatRoleText(ChatRole.Assistant, agent.Name ?? "未知"),
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedTimestamp = DateTimeOffset.UtcNow.ToLocalTime().ToUnixTimeMilliseconds(),
                Id = messageId ?? Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ModelName = modelName
            };

            // 保存到数据库
            dbContext.ExecuteInTransaction(conn =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR REPLACE INTO ChatMessages
                        (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, ContentsJson, TokenCount, ModelName, CreatedAt)
                    VALUES
                        (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @ContentsJson, @TokenCount, @ModelName, @CreatedAt)
                    """;
                ChatMessageService.BindEntity(cmd, message);
                cmd.ExecuteNonQuery();
            });

            logger.LogInformation("已保存取消的部分响应，长度：{Length}", partialResponse.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存部分响应时失败。");
        }
    }

    /// <summary>
    /// 返回指定会话的聊天历史记录，供 Agent 上下文使用。
    /// 优先加载压缩段落摘要 + 尾部原始消息，兼容老版 CompactedContext 缓存。
    /// </summary>
    protected override async ValueTask<IEnumerable<AIChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("N");

        if (context.Session is not null)
        {
            sessionId = context.Session.StateBag.GetValue<string>("sessionid") ?? sessionId;
        }

        try
        {
            var segmentService = services.GetRequiredService<CompactionSegmentService>();
            var segments = segmentService.GetBySessionId(sessionId);
            var result = new List<AIChatMessage>();

            // ── 新系统：段落摘要 + 尾部原始消息 ──
            if (segments.Count > 0)
            {
                var maxDisplay = systemSettings.GetValue("Compaction.MaxDisplaySegments", 15);

                // 滑动窗口：只加载最近 N 个段落（旧段落不删除，只是不再加载）
                var displaySegments = segments.Count > maxDisplay
                    ? segments.Skip(segments.Count - maxDisplay).ToList()
                    : segments;

                foreach (var seg in displaySegments)
                {
                    result.Add(new AIChatMessage(ChatRole.System,
                        $"[对话摘要 #{seg.SegmentIndex + 1}]\n{seg.Summary}"));
                }

                // 加载最后一个段落之后的所有原始消息
                var lastSegment = segments[^1];
                var tailOffset = lastSegment.EndMessageIndex + 1;

                var tailMessages = dbContext.Query(
                    "SELECT * FROM ChatMessages WHERE SessionId = @SessionId ORDER BY CreatedTimestamp ASC LIMIT -1 OFFSET @Offset",
                    ChatMessageService.ReadEntity,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@SessionId", sessionId);
                        cmd.Parameters.AddWithValue("@Offset", tailOffset);
                    });

                result.AddRange(tailMessages.Select(t => new AIChatMessage
                {
                    Role = t.ToChatRole(),
                    AuthorName = t.AuthorName,
                    MessageId = t.Id,
                    CreatedAt = t.CreatedAt?.ToLocalTime(),
                    Contents = BuildContentsWithAssets(t)
                }));

                return result;
            }

            // ── 老版兼容：CompactedContext 缓存（只读回退） ──
            var session = dbContext.QueryFirstOrDefault(
                "SELECT * FROM ChatSessions WHERE Id = @Id",
                ReadSessionEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", sessionId));

            if (session is not null && !string.IsNullOrEmpty(session.CompactedContext))
            {
                var cached = JsonSerializer.Deserialize(session.CompactedContext, ChatHistoryJsonContext.Default.ListCompactedMessage);

                // 检测摘要是否实际生成成功（防止 LLM 失败时仍标记已压缩导致数据丢失）
                var hasSummaryFailed = cached is null
                    || cached.Count == 0
                    || cached.All(m => string.IsNullOrWhiteSpace(m.Content)
                        || m.Content.Contains("[Summary unavailable]", StringComparison.OrdinalIgnoreCase));

                if (!hasSummaryFailed)
                {
                    result.AddRange(cached!.Select(m => new AIChatMessage
                    {
                        Role = new ChatRole(m.Role),
                        AuthorName = m.AuthorName,
                        Contents = [new TextContent(m.Content)]
                    }));

                    var newer = dbContext.Query(
                        "SELECT * FROM ChatMessages WHERE SessionId = @SessionId ORDER BY CreatedTimestamp ASC LIMIT -1 OFFSET @Offset",
                        ChatMessageService.ReadEntity,
                        cmd =>
                        {
                            cmd.Parameters.AddWithValue("@SessionId", sessionId);
                            cmd.Parameters.AddWithValue("@Offset", session.CompactedAtCount);
                        });

                    result.AddRange(newer.Select(t => new AIChatMessage
                    {
                        Role = t.ToChatRole(),
                        AuthorName = t.AuthorName,
                        MessageId = t.Id,
                        CreatedAt = t.CreatedAt?.ToLocalTime(),
                        Contents = BuildContentsWithAssets(t)
                    }));

                    return result;
                }

                // 摘要无效 → 跳过老版缓存，回退到加载全部消息
                logger.LogWarning("会话 {SessionId} 的老版压缩摘要无效（Summary unavailable），回退到加载全部消息。", sessionId);
            }

            // ── 无任何压缩：加载全部消息 ──
            var messages = dbContext.Query(
                "SELECT * FROM ChatMessages WHERE SessionId = @SessionId ORDER BY CreatedTimestamp ASC",
                ChatMessageService.ReadEntity,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@SessionId", sessionId);
                })
                .Select(t => new AIChatMessage
                {
                    Role = t.ToChatRole(),
                    AuthorName = t.AuthorName,
                    MessageId = t.Id,
                    CreatedAt = t.CreatedAt?.ToLocalTime(),
                    Contents = BuildContentsWithAssets(t)
                });
            return messages;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "从数据库加载聊天历史时失败。");
            return [];
        }
    }

    /// <summary>
    /// 创建或更新聊天会话记录。
    /// </summary>
    /// <param name="accumulateInputTokens">
    /// 是否把当前 <see cref="AIAgentFactory.LastInputTokens"/> 累加到会话的 TotalTokenCount。
    /// 仅应在真实对话保存路径（<see cref="StoreChatHistoryAsync"/>）置为 true，
    /// 新建会话/取消对话等其他路径不应累加，避免重复计算或污染统计。
    /// </param>
    private async Task<string> AddOrUpdateSessionAsync(AgentSession? session, string title, AIAgent agent, bool accumulateInputTokens = false)
    {
        var sessionId = session?.StateBag.GetValue<string>("sessionid") ?? Guid.NewGuid().ToString("N");
        JsonElement? sessionJson = null;
        var shouldUpdateNewSessionTitle = session is not null
            && !string.IsNullOrWhiteSpace(title)
            && !string.Equals(title, NewSessionTitle, StringComparison.Ordinal);

        if (session is not null)
        {
            sessionJson = await agent.SerializeSessionAsync(session);
        }

        try
        {
            var sessionEntity = dbContext.QueryFirstOrDefault(
                "SELECT * FROM ChatSessions WHERE Id = @Id",
                ReadSessionEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", sessionId));

            sessionEntity ??= new ChatSessionEntity
            {
                Id = sessionId,
                Title = title,
                CreatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                RawDiscription = sessionJson?.ToString() ?? "",
                Categorize = appPaths.WorkspaceDirectory.Md5Encrypt()
            };

            if (shouldUpdateNewSessionTitle && (string.IsNullOrWhiteSpace(sessionEntity.Title) || sessionEntity.Title == NewSessionTitle))
            {
                sessionEntity.Title = title;
            }

            if (sessionJson is not null)
            {
                sessionEntity.RawDiscription = sessionJson.Value.ToString();
            }

            sessionEntity.UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            sessionEntity.LastActiveTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (accumulateInputTokens)
            {
                sessionEntity.TotalTokenCount += services.GetRequiredService<AIAgentFactory>().LastInputTokens;
            }

            dbContext.Execute(
                """
                INSERT OR REPLACE INTO ChatSessions
                    (Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title, Summary, RawDiscription, AgentId, IsArchived, IsPinned, LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount)
                VALUES
                    (@Id, @CreatedTimestamp, @UpdatedTimestamp, @Categorize, @Title, @Summary, @RawDiscription, @AgentId, @IsArchived, @IsPinned, @LastActiveTimestamp, @TotalTokenCount, @CompactedContext, @CompactedAtCount)
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Id", sessionEntity.Id);
                    cmd.Parameters.AddWithValue("@CreatedTimestamp", sessionEntity.CreatedTimestamp);
                    cmd.Parameters.AddWithValue("@UpdatedTimestamp", sessionEntity.UpdatedTimestamp);
                    cmd.Parameters.AddWithValue("@Categorize", sessionEntity.Categorize);
                    cmd.Parameters.AddWithValue("@Title", sessionEntity.Title);
                    cmd.Parameters.AddWithValue("@Summary", sessionEntity.Summary);
                    cmd.Parameters.AddWithValue("@RawDiscription", sessionEntity.RawDiscription);
                    cmd.Parameters.AddWithValue("@AgentId", sessionEntity.AgentId);
                    cmd.Parameters.AddWithValue("@IsArchived", sessionEntity.IsArchived ? 1 : 0);
                    cmd.Parameters.AddWithValue("@IsPinned", sessionEntity.IsPinned ? 1 : 0);
                    cmd.Parameters.AddWithValue("@LastActiveTimestamp", sessionEntity.LastActiveTimestamp);
                    cmd.Parameters.AddWithValue("@TotalTokenCount", sessionEntity.TotalTokenCount);
                    cmd.Parameters.AddWithValue("@CompactedContext", sessionEntity.CompactedContext);
                    cmd.Parameters.AddWithValue("@CompactedAtCount", sessionEntity.CompactedAtCount);
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "创建或更新会话记录时失败。");
        }

        return sessionId;
    }

    /// <summary>
    /// 将 ChatRole 转换为中文显示文本。
    /// </summary>
    private static string GetChatRoleText(ChatRole role, string agentName)
    {
        if (role == ChatRole.User) return "用户";
        if (role == ChatRole.System) return "系统";
        if (role == ChatRole.Assistant) return agentName;
        if (role == ChatRole.Tool) return "工具";

        return "未知";
    }

    /// <summary>
    /// 从 SqliteDataReader 映射 ChatSessionEntity。
    /// </summary>
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
            TotalTokenCount = r.GetInt32(r.GetOrdinal("TotalTokenCount")),
            CompactedContext = r.GetString(r.GetOrdinal("CompactedContext")),
            CompactedAtCount = r.GetInt32(r.GetOrdinal("CompactedAtCount"))
        };
    }

    /// <summary>
    /// 异步调用 AI 为新会话生成摘要标题，更新数据库后通过事件通知 UI。
    /// </summary>
    private async Task GenerateAndUpdateTitleAsync(AIAgent agent, string sessionId, string userMessage, string aiResponse)
    {
        try
        {
            var client = ResolveCompactionClient(agent);
            if (client is null) return;

            var prompt = new List<AIChatMessage>
            {
                new(ChatRole.System, "你是标题生成助手。根据用户问题和AI回复，提取关键词并总结成一个简短的对话标题。要求：不超过20个字，不要引号，不要标点，直接输出标题文本。"),
                new(ChatRole.User, $"用户：{userMessage.Truncate(200)}\nAI：{aiResponse.Truncate(500)}")
            };

            // 若借用主对话的 TokenTrackingChatClient，抑制其 usage 上报，避免污染主进度条
            using var _ = (client as TokenTrackingChatClient)?.SuppressUsage();
            var completion = await client.GetResponseAsync(prompt);
            var title = completion?.Text?.Trim();

            if (string.IsNullOrWhiteSpace(title)) return;

            // 清理可能的引号和多余空白
            title = title.Trim('"', '"', '"', '「', '」', '\'').Truncate(32);

            dbContext.Execute(
                "UPDATE ChatSessions SET Title = @Title, UpdatedTimestamp = @Updated WHERE Id = @Id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Title", title);
                    cmd.Parameters.AddWithValue("@Updated", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("@Id", sessionId);
                });

            var publisher = services.GetRequiredService<IPublisher>();
            publisher.Publish(Events.OnSessionTitleUpdated, new SessionTitleUpdatedArgs(sessionId, title));

            logger.LogInformation("会话 {SessionId} 标题已由 AI 生成：{Title}", sessionId, title);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "为会话 {SessionId} 生成 AI 标题失败，保留原标题。", sessionId);
        }
    }

    /// <summary>
    /// 段落式压缩：将超出尾部保留范围的最早一批未摘要消息生成不可变段落。
    /// 每次最多生成一个段落，段落一旦创建永不修改，避免递归信息丢失。
    /// </summary>
    private async Task CompactAndReplaceAsync(AIAgent agent, AgentSession session, string sessionId, CancellationToken cancellationToken)
    {
        var modelId = session.StateBag.GetValue<string>("modeldbid");
        if (string.IsNullOrEmpty(modelId)) return;
        var client = ResolveCompactionClient(agent);
        if (client is null) return;
        try
        {
            var segmentSize = systemSettings.GetValue("Compaction.SegmentSize", 30);
            var rawTailSize = systemSettings.GetValue("Compaction.RawTailSize", 20);

            // 1. 获取消息总数
            var totalCount = (int)dbContext.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM ChatMessages WHERE SessionId = @SessionId",
                cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));

            // 2. 获取已被段落覆盖的消息数量
            var segmentService = services.GetRequiredService<CompactionSegmentService>();
            var coveredCount = segmentService.GetMaxCoveredMessageIndex(sessionId) + 1;

            // 3. 计算未摘要消息数
            var unsummarizedCount = totalCount - coveredCount;

            // 4. 仅当未摘要消息超过 段落大小 + 尾部保留数 时触发
            if (unsummarizedCount <= segmentSize + rawTailSize) return;

            // 5. 加载最早的 SegmentSize 条未摘要消息
            var messagesToSummarize = dbContext.Query(
                "SELECT * FROM ChatMessages WHERE SessionId = @SessionId ORDER BY CreatedTimestamp ASC LIMIT @Limit OFFSET @Offset",
                ChatMessageService.ReadEntity,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@SessionId", sessionId);
                    cmd.Parameters.AddWithValue("@Limit", segmentSize);
                    cmd.Parameters.AddWithValue("@Offset", coveredCount);
                });

            if (messagesToSummarize.Count < segmentSize) return;

            // 6. 构建摘要请求
            var summaryPrompt = new List<AIChatMessage>
            {
                new(ChatRole.System, """
                    你是对话摘要助手。请将以下对话片段压缩成结构化的摘要。
                    
                    ## 必须完整保留的内容
                    - 代码块（包括完整代码、不要截断）、Shell 命令、SQL 语句
                    - 文件路径、URL、API 端点
                    - 工具调用的名称、输入参数和返回结果
                    - 所有数值型数据：配置项的值、阈值、端口号、版本号
                    - 错误信息和异常堆栈的关键行
                    - 用户明确的决策和结论
                    
                    ## 可以精简的内容
                    - 寒暄、确认、重复的问答
                    - 冗余的解释和过渡语句
                    - 已被后续修正覆盖的中间尝试（仅保留最终结果）
                    
                    ## 输出格式
                    - 使用 Markdown 标题分隔不同话题
                    - 代码块使用 ``` 围栏并标注语言
                    - 关键决策使用 **粗体** 标注
                    - 按时间顺序组织，使用第三人称叙述
                    - 摘要长度应为原文的 20%~40%，宁多勿少，确保不丢失可操作信息
                    """)
            };

            summaryPrompt.AddRange(messagesToSummarize.Select(t => new AIChatMessage
            {
                Role = t.ToChatRole(),
                AuthorName = t.AuthorName,
                Contents = [new TextContent(t.Content)]
            }));

            summaryPrompt.Add(new AIChatMessage(ChatRole.User, "请基于以上对话片段生成摘要。务必保留所有代码、命令、文件路径和数值数据，不要遗漏任何可操作的技术细节。"));

            // 7. 调用 LLM 生成摘要（若借用主对话 ChatClient，抑制其 usage 上报避免进度条跳变）
            using var _ = (client as TokenTrackingChatClient)?.SuppressUsage();
            var completion = await client.GetResponseAsync(summaryPrompt, cancellationToken: cancellationToken);
            var summaryText = completion?.Text;

            if (string.IsNullOrWhiteSpace(summaryText)) return;

            // 8. 创建不可变段落
            var nextIndex = segmentService.GetMaxSegmentIndex(sessionId) + 1;
            var segment = new CompactionSegmentEntity
            {
                SessionId = sessionId,
                SegmentIndex = nextIndex,
                StartMessageIndex = coveredCount,
                EndMessageIndex = coveredCount + segmentSize - 1,
                Summary = summaryText,
                OriginalMessageCount = segmentSize,
                ModelName = session.StateBag.GetValue<string>("modelid") ?? "Unknown"
            };

            segmentService.Add(segment);

            logger.LogInformation(
                "会话 {SessionId} 新增压缩段落 #{Index}：消息 [{Start}..{End}] → 摘要 {Len} 字符",
                sessionId, nextIndex, coveredCount, coveredCount + segmentSize - 1, summaryText.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "压缩会话 {SessionId} 历史记录时失败。", sessionId);
        }
    }

    /// <summary>
    /// 解析缩略专用模型。若 Compaction.ModelId 已配置且有效，则通过 <see cref="ModelPurposeResolver"/>
    /// 复用缓存的 <see cref="IChatClient"/>；否则回退到当前 Agent 的 ChatClient。
    /// </summary>
    private IChatClient? ResolveCompactionClient(AIAgent agent)
    {
        var resolver = services.GetRequiredService<ModelPurposeResolver>();
        return resolver.TryResolve("Compaction.ModelId") ?? agent.GetService<IChatClient>();
    }

    /// <summary>
    /// 释放本 Provider 持有的资源。用途级客户端统一由 <see cref="ModelPurposeResolver"/> 托管。
    /// </summary>
    public void Dispose()
    {
        // 压缩客户端缓存已迁移到 ModelPurposeResolver，此处无需释放。
    }

    // ──────────────────── 资源加载 / 保存辅助 ────────────────────

    /// <summary>
    /// 构造一条历史消息的 Contents 列表：优先从 <c>ContentsJson</c> 还原结构化内容（工具调用/结果）；
    /// 否则退化为文本快照。无论走哪条路径，都会追加 <c>ChatMessageAssets</c> 表中的图片资源。
    /// 非图片资源（文档/音视频）不回灌到 AI 上下文，避免占用 token；它们通过消息 Content 中的
    /// 链接引用（用户附件）或由 UI 侧的 ResourceCardPanel 独立渲染。
    /// </summary>
    private IList<AIContent> BuildContentsWithAssets(ChatMessageEntity entity)
    {
        List<AIContent> contents;

        var structured = ChatMessageExtensions.ParseContentsJson(entity.ContentsJson);
        if (structured is { Count: > 0 })
        {
            contents = [.. structured];
        }
        else
        {
            contents = [new TextContent(entity.Content ?? string.Empty)];
        }

        if (string.IsNullOrEmpty(entity.Id)) return contents;

        try
        {
            var assetService = services.GetRequiredService<ChatMessageAssetService>();
            var assets = assetService.GetByMessageId(entity.Id);
            foreach (var asset in assets)
            {
                if (asset.AssetGroup != "images") continue; // 仅图片回灌给 AI
                var fullPath = Path.Combine(appPaths.WorkspaceResourcesDirectory, asset.RelativePath);
                if (!File.Exists(fullPath)) continue;

                var bytes = File.ReadAllBytes(fullPath);
                contents.Add(new DataContent(bytes, asset.MimeType));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载消息 {MessageId} 的历史资源失败。", entity.Id);
        }
        return contents;
    }

    /// <summary>
    /// 遍历 AI 响应中的所有 <see cref="DataContent"/>，将其持久化到资源库并写入索引。
    /// 对应 <c>AssetKind="generated"</c> / <c>SourceType="generated"</c> / <c>Role="assistant"</c>。
    /// 纯 URI 引用（无内联 bytes）直接跳过——运行时下载超出本次职责。
    /// </summary>
    private async Task SaveGeneratedAssetsAsync(InvokedContext context, string sessionId, IReadOnlyList<string> assistantPersistedIds)
    {
        if (context.ResponseMessages is null) return;

        var assetService = services.GetRequiredService<ChatMessageAssetService>();
        var generatedEntities = new List<ChatMessageAssetEntity>();
        var sortOrder = 0;
        var assistantIndex = 0; // 与入库时的 assistant 顺序对齐

        foreach (var msg in context.ResponseMessages)
        {
            if (msg.Role != ChatRole.Assistant) continue;
            if (msg.Contents is null || msg.Contents.Count == 0) continue;

            // 与 ChatMessages 表已入库的 assistant Id 一一对应，确保资源归属稳定
            var messageId = assistantIndex < assistantPersistedIds.Count
                ? assistantPersistedIds[assistantIndex]
                : (msg.MessageId ?? Guid.NewGuid().ToString("N"));
            assistantIndex++;

            foreach (var content in msg.Contents)
            {
                if (content is not DataContent dc) continue;
                if (dc.Data.Length == 0) continue; // URI-only、无内联二进制 → 跳过

                try
                {
                    var mediaType = string.IsNullOrEmpty(dc.MediaType) ? "application/octet-stream" : dc.MediaType;
                    var assetGroup = ResolveAssetGroupForMime(mediaType);
                    var extension = InferExtensionFromMediaType(mediaType);
                    var fileName = $"{Guid.NewGuid():N}{extension}";
                    var relativePath = Path.Combine("histories", sessionId, assetGroup, messageId, fileName);
                    var absolutePath = Path.Combine(appPaths.WorkspaceResourcesDirectory, relativePath);

                    var targetDir = Path.GetDirectoryName(absolutePath)!;
                    if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                    await File.WriteAllBytesAsync(absolutePath, dc.Data.ToArray());

                    var sha256 = Convert.ToHexStringLower(SHA256.HashData(dc.Data.Span));
                    generatedEntities.Add(new ChatMessageAssetEntity
                    {
                        SessionId = sessionId,
                        MessageId = messageId,
                        Role = "assistant",
                        AssetGroup = assetGroup,
                        AssetKind = "generated",
                        MimeType = mediaType,
                        OriginalName = fileName,
                        RelativePath = relativePath,
                        FileSizeBytes = dc.Data.Length,
                        Sha256 = sha256,
                        SortOrder = sortOrder++,
                        SourceType = "generated",
                    });

                    logger.LogInformation("已保存 AI 生成资源：{RelativePath}（{MimeType}，{Bytes} 字节）",
                        relativePath, mediaType, dc.Data.Length);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "保存单个 AI 生成资源失败。");
                }
            }
        }

        if (generatedEntities.Count > 0)
        {
            try { assetService.BatchInsert(generatedEntities); }
            catch (Exception ex) { logger.LogError(ex, "批量写入 AI 生成资源索引失败。"); }
        }
    }

    private static string ResolveAssetGroupForMime(string mimeType)
    {
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "images";
        if (mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "audio";
        if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        return "files";
    }

    private static string InferExtensionFromMediaType(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "audio/aac" => ".aac",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "application/pdf" => ".pdf",
        "application/json" => ".json",
        "text/plain" => ".txt",
        _ => string.Empty,
    };
}

/// <summary>
/// 压缩消息的轻量 DTO，用于 JSON 序列化存储到 CompactedContext。
/// </summary>
internal sealed class CompactedMessage
{
    public string Role { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// AOT 安全的 JSON 序列化上下文，用于压缩消息的序列化/反序列化。
/// </summary>
[JsonSerializable(typeof(List<CompactedMessage>))]
internal sealed partial class ChatHistoryJsonContext : JsonSerializerContext
{
}