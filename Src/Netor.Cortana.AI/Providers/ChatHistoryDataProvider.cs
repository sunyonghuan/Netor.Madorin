using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// 最大保留的历史消息条数，从数据库读取，回退默认值 15。
    /// </summary>
    private int MaxContentCount => systemSettings.GetValue("ChatHistory.MaxContentCount", 15);

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
        var firstText = context.RequestMessages?.FirstOrDefault()?.Text ?? "";
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
                Id = t.MessageId ?? Guid.NewGuid().ToString("N"),
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
                Id = Guid.NewGuid().ToString("N"),
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
    /// 优先从压缩缓存读取，再补充缓存之后的新消息，避免重复 LLM 调用。
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
            // 1. 读取会话元数据（含压缩缓存）
            var session = dbContext.QueryFirstOrDefault(
                "SELECT * FROM ChatSessions WHERE Id = @Id",
                ReadSessionEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", sessionId));

            var result = new List<AIChatMessage>();

            // 2. 如果存在压缩缓存，反序列化为消息列表
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

                // 3. 补充缓存检查点之后的新消息（OFFSET = CompactedAtCount）
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

            // 4. 无缓存时回退到读取全部消息
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
            && string.Equals(session.StateBag.GetValue<string>(IsNewSessionStateKey), bool.TrueString, StringComparison.OrdinalIgnoreCase)
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
                session!.StateBag.SetValue(IsNewSessionStateKey, bool.FalseString);
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

    private async Task CompactAndReplaceAsync(AIAgent agent, AgentSession session, string sessionId, CancellationToken cancellationToken)
    {
        var modelName = session.StateBag.GetValue<string>("modelid") ?? "Unknown";
        var modelId = session.StateBag.GetValue<string>("modeldbid");
        if (string.IsNullOrEmpty(modelId)) return;
        var client = agent.GetService<IChatClient>();
        if (client is null) return;
        try
        {
            // 1. 读取会话元数据（含压缩检查点）
            var sessionEntity = dbContext.QueryFirstOrDefault(
                "SELECT * FROM ChatSessions WHERE Id = @Id",
                ReadSessionEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", sessionId));
            if (sessionEntity is null) return;

            // 2. 加载该会话全部消息总数
            var totalCount = dbContext.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM ChatMessages WHERE SessionId = @SessionId",
                cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));

            // 3. 仅当新消息数量超过检查点一定阈值时才重新压缩
            var newMessageCount = (int)totalCount - sessionEntity.CompactedAtCount;
            if (newMessageCount < 10) return;

            // 4. 加载全部消息（按时间正序）
            var allEntities = dbContext.Query(
                "SELECT * FROM ChatMessages WHERE SessionId = @SessionId ORDER BY CreatedTimestamp ASC",
                ChatMessageService.ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId))
                .ToList();

            if (allEntities.Count < 10) return;

            // 5. 转为 ChatMessage
            var chatMessages = allEntities
                .Select(t => new AIChatMessage
                {
                    Role = t.ToChatRole(),
                    AuthorName = t.AuthorName,
                    MessageId = t.Id,
                    CreatedAt = t.CreatedAt?.ToLocalTime(),
                    Contents = [new TextContent(t.Content)]
                })
                .ToList();

            // 6. 执行压缩
#pragma warning disable MAAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
            var compactionStrategy = CreateCompactionStrategy(client, modelId);
            var compacted = (await CompactionProvider.CompactAsync(
                compactionStrategy!, chatMessages, cancellationToken: cancellationToken))
                .ToList();
#pragma warning restore MAAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。

            // 7. 如果没有压缩效果，跳过
            if (compacted.Count >= allEntities.Count) return;

            // 8. 将压缩结果序列化写入 CompactedContext，原始消息保持不动
            var compactedMessages = compacted.Select(m => new CompactedMessage
            {
                Role = m.Role.Value,
                AuthorName = m.AuthorName ?? "",
                Content = m.Text ?? ""
            }).ToList();

            var json = JsonSerializer.Serialize(compactedMessages, ChatHistoryJsonContext.Default.ListCompactedMessage);

            dbContext.Execute(
                "UPDATE ChatSessions SET CompactedContext = @CompactedContext, CompactedAtCount = @CompactedAtCount WHERE Id = @Id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@CompactedContext", json);
                    cmd.Parameters.AddWithValue("@CompactedAtCount", allEntities.Count);
                    cmd.Parameters.AddWithValue("@Id", sessionId);
                });

            logger.LogInformation("会话 {SessionId} 压缩缓存已更新：{Total} 条消息 → {Compacted} 条压缩上下文",
                sessionId, allEntities.Count, compacted.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "压缩会话 {SessionId} 历史记录时失败。", sessionId);
        }
    }

#pragma warning disable MAAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。

    private PipelineCompactionStrategy CreateCompactionStrategy(IChatClient client, string modelid)

    {
        var model = modelService.GetById(modelid);
        return new PipelineCompactionStrategy(
            // 1. 最温和：压缩旧工具调用结果
            new ToolResultCompactionStrategy(CompactionTriggers.MessagesExceed(20)),
            // 2. 中等：LLM 摘要旧对话
            new SummarizationCompactionStrategy(client, CompactionTriggers.TokensExceed((int)((model?.ContextLength ?? 128000) * 0.8))),
            // 3. 较激进：只保留最近 N 轮
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(systemSettings.GetValue("ChatHistory.MaxContentCount", 150))),
            // 4. 兜底：强制截断
            new TruncationCompactionStrategy(CompactionTriggers.TokensExceed((int)((model?.ContextLength ?? 128000) * 0.9)))
        );
    }

#pragma warning restore MAAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续
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