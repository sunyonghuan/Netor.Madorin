using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

using OpenAI.Chat;

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
    AiModelService modelService,
    IAppPaths appPaths,
    ILogger<ChatHistoryDataProvider> logger,
    IServiceProvider services) : ChatHistoryProvider

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
    /// </summary>
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelName = context.Session?.StateBag.GetValue<string>("modelid") ?? "Unknown";
        var assistantMessageId = context.Session?.StateBag.GetValue<string>("assistantmessageid");
        var firstText = context.RequestMessages?.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        var sessionId = await AddOrUpdateSessionAsync(context.Session, firstText.Truncate(32), context.Agent);

        // 确保 ResponseMessages 不为 null 再进行 Select
        var messages = (context.ResponseMessages ?? [])
            .Select(t => new ChatMessageEntity
            {
                Role = t.Role.ToString(),
                Content = t.Text,
                AuthorName = GetChatRoleText(t.Role, context.Agent.Name ?? "未知"),
                CreatedAt = t.CreatedAt ?? DateTimeOffset.UtcNow,
                CreatedTimestamp = t.CreatedAt?.ToLocalTime().ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Id = (t.Role == ChatRole.Assistant ? assistantMessageId : null) ?? t.MessageId ?? Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ModelName = modelName
            });

        try
        {
            dbContext.ExecuteInTransaction(conn =>
            {
                foreach (var message in messages)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO ChatMessages
                            (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, TokenCount, ModelName, CreatedAt)
                        VALUES
                            (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @TokenCount, @ModelName, @CreatedAt)
                        """;
                    ChatMessageService.BindEntity(cmd, message);
                    cmd.ExecuteNonQuery();
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "保存聊天历史到数据库时失败。");
        }
        // ---- 新增：压缩后回写 ----
        if (context.Session is null) return;
        await CompactAndReplaceAsync(context.Agent, context.Session, sessionId, cancellationToken);
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
            var message = new ChatMessageEntity
            {
                Role = ChatRole.Assistant.ToString(),
                Content = partialResponse,
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
                        (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, TokenCount, ModelName, CreatedAt)
                    VALUES
                        (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @TokenCount, @ModelName, @CreatedAt)
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
                    Contents = [new TextContent(t.Content)]
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
                if (cached is not null)
                {
                    result.AddRange(cached.Select(m => new AIChatMessage
                    {
                        Role = new ChatRole(m.Role),
                        AuthorName = m.AuthorName,
                        Contents = [new TextContent(m.Content)]
                    }));
                }

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
                    Contents = [new TextContent(t.Content)]
                }));

                return result;
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
                    Contents = [new TextContent(t.Content)]
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
    private async Task<string> AddOrUpdateSessionAsync(AgentSession? session, string title, AIAgent agent)
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
            sessionEntity.TotalTokenCount += services.GetRequiredService<AIAgentFactory>().ChatClient?.LastInputTokens ?? 0;

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
                    你是对话摘要助手。请将以下对话片段压缩成简洁的摘要：
                    - 完整保留代码块、命令、文件路径等技术细节
                    - 保留工具调用的输入参数和返回结果
                    - 保留用户的关键需求和 AI 的关键决策
                    - 保留所有重要的数值、配置项、错误信息
                    - 使用第三人称叙述，按时间顺序组织
                    - 摘要长度控制在原文的 30% 以内
                    """)
            };

            summaryPrompt.AddRange(messagesToSummarize.Select(t => new AIChatMessage
            {
                Role = t.ToChatRole(),
                AuthorName = t.AuthorName,
                Contents = [new TextContent(t.Content)]
            }));

            summaryPrompt.Add(new AIChatMessage(ChatRole.User, "请基于以上对话片段生成摘要，保留所有技术细节。"));

            // 7. 调用 LLM 生成摘要
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
    /// 解析缩略专用模型。若 Compaction.ModelId 已配置且有效，则创建独立 IChatClient；否则回退到当前 Agent 的 ChatClient。
    /// </summary>
    private IChatClient? ResolveCompactionClient(AIAgent agent)
    {
        var compactionModelId = systemSettings.GetValue("Compaction.ModelId");
        if (!string.IsNullOrEmpty(compactionModelId))
        {
            var model = modelService.GetById(compactionModelId);
            if (model is not null)
            {
                var providerService = services.GetRequiredService<AiProviderService>();
                var provider = providerService.GetById(model.ProviderId);
                if (provider is not null)
                {
                    var registry = services.GetRequiredService<AiProviderDriverRegistry>();
                    var driver = registry.Resolve(provider);
                    return driver.CreateChatClient(provider, model);
                }
            }
        }

        return agent.GetService<IChatClient>();
    }
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