using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.EventHub;

namespace Netor.Cortana.AI;

/// <summary>
/// 管理专家模式聊天会话的当前 sessionId、新建/恢复会话与最近会话装载。
/// </summary>
public sealed class ChatSessionService(
    AIAgentFactory factory,
    ChatHistoryDataProvider chatHistoryProvider,
    CortanaDbContext dbContext,
    IAppPaths appPaths,
    IPublisher publisher,
    ILogger<ChatSessionService> logger)
{
    private string _currentSessionId = string.Empty;
    private AgentSession? _currentSession;

    /// <summary>
    /// 当前活跃会话 ID。
    /// </summary>
    public string? CurrentId => string.IsNullOrWhiteSpace(_currentSessionId) ? null : _currentSessionId;

    /// <summary>
    /// 构造专家模式普通对话可见性条件。普通对话必须排除工作模式、会议模式和归档支撑会话。
    /// </summary>
    public static string BuildVisibleExpertSessionPredicate(string sessionAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionAlias);

        return $"""
               {sessionAlias}.IsArchived = 0
                 AND IFNULL({sessionAlias}.SourceTaskId, '') = ''
                 AND NOT EXISTS (
                     SELECT 1 FROM MeetingSessions ms
                     WHERE ms.SessionId = {sessionAlias}.Id
                 )
                 AND NOT EXISTS (
                     SELECT 1 FROM WorkTasks wt
                     WHERE wt.SessionId = {sessionAlias}.Id
                 )
               """;
    }

    /// <summary>
    /// 分页查询专家模式可见的普通对话会话。
    /// </summary>
    public List<ChatSessionEntity> GetVisibleExpertSessions(
        string categorize,
        int limit,
        int offset = 0,
        string? searchKeyword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categorize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var hasSearch = !string.IsNullOrWhiteSpace(searchKeyword);
        var predicate = BuildVisibleExpertSessionPredicate("ChatSessions");
        var sql = hasSearch
            ? $"""
               SELECT * FROM ChatSessions
               WHERE Categorize = @Categorize
                 AND {predicate}
                 AND LOWER(Title) LIKE LOWER(@Keyword)
               ORDER BY IsPinned DESC, LastActiveTimestamp DESC
               LIMIT @Limit OFFSET @Offset
               """
            : $"""
               SELECT * FROM ChatSessions
               WHERE Categorize = @Categorize
                 AND {predicate}
               ORDER BY IsPinned DESC, LastActiveTimestamp DESC
               LIMIT @Limit OFFSET @Offset
               """;

        return dbContext.Query(
            sql,
            ReadSessionEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Categorize", categorize);
                cmd.Parameters.AddWithValue("@Limit", limit);
                cmd.Parameters.AddWithValue("@Offset", offset);
                if (hasSearch)
                {
                    cmd.Parameters.AddWithValue("@Keyword", $"%{searchKeyword!.Trim()}%");
                }
            });
    }

    /// <summary>
    /// 获取最近一个专家模式可见的普通对话会话 ID。
    /// </summary>
    public string? GetMostRecentVisibleExpertSessionId(string categorize)
    {
        return GetVisibleExpertSessions(categorize, limit: 1).FirstOrDefault()?.Id;
    }

    /// <summary>
    /// 判断指定会话是否允许在专家模式历史中恢复。
    /// </summary>
    public bool IsVisibleExpertSession(string sessionId, string categorize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(categorize);

        var predicate = BuildVisibleExpertSessionPredicate("ChatSessions");
        var count = dbContext.ExecuteScalar<long>(
            $"""
             SELECT COUNT(1) FROM ChatSessions
             WHERE Id = @SessionId
               AND Categorize = @Categorize
               AND {predicate}
             """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@Categorize", categorize);
            });

        return count > 0;
    }

    public async Task<AgentSession> NewSessionAsync(
        AIAgent agent,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _currentSessionId = Guid.NewGuid().ToString("N");
        session.StateBag.SetValue("sessionid", _currentSessionId);
        ApplySelectionState(session, provider, agentEntity, model);

        _currentSessionId = await chatHistoryProvider.CreateNewSessionAsync(session, agent).ConfigureAwait(false);
        session.StateBag.SetValue("sessionid", _currentSessionId);
        _currentSession = session;
        publisher.Publish(Events.OnSessionCreated, new SessionCreatedArgs(_currentSessionId));

        factory.ResetTokenStats();
        logger.LogDebug("已创建新聊天会话：{SessionId}", _currentSessionId);
        return session;
    }

    public async Task<AgentSession> ResumeSessionAsync(
        string sessionId,
        AIAgent agent,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(agent);

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _currentSessionId = sessionId;
        session.StateBag.SetValue("sessionid", sessionId);
        ApplySelectionState(session, provider, agentEntity, model);
        _currentSession = session;

        factory.ResetTokenStats();
        logger.LogDebug("已恢复聊天会话：{SessionId}", _currentSessionId);
        return session;
    }

    public async Task<AgentSession> LoadOrCreateSessionAsync(
        AIAgent agent,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var categorize = appPaths.WorkspaceDirectory.Md5Encrypt();
        var recentSessionId = GetMostRecentVisibleExpertSessionId(categorize);

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var isNewSession = string.IsNullOrWhiteSpace(recentSessionId);
        if (!isNewSession)
        {
            _currentSessionId = recentSessionId!;
            logger.LogDebug("恢复对话 Session：{SessionId}", _currentSessionId);
        }
        else
        {
            _currentSessionId = Guid.NewGuid().ToString("N");
            logger.LogDebug("新建对话 Session：{SessionId}", _currentSessionId);
        }

        session.StateBag.SetValue("sessionid", _currentSessionId);
        ApplySelectionState(session, provider, agentEntity, model);

        if (isNewSession)
        {
            // 立即落库：避免 AI 回复中断/失败时首轮内容丢失，
            // 也保证工作模式转交等下游能通过 ChatSessions.Id 外键引用。
            _currentSessionId = await chatHistoryProvider.CreateNewSessionAsync(session, agent).ConfigureAwait(false);
            session.StateBag.SetValue("sessionid", _currentSessionId);
            publisher.Publish(Events.OnSessionCreated, new SessionCreatedArgs(_currentSessionId));
        }

        _currentSession = session;
        return session;
    }

    public async Task<AgentSession> EnsureCurrentSessionAsync(
        AIAgent agent,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (_currentSession is not null)
        {
            ApplySelectionState(_currentSession, provider, agentEntity, model);
            return _currentSession;
        }

        return await LoadOrCreateSessionAsync(
            agent,
            provider,
            agentEntity,
            model,
            cancellationToken).ConfigureAwait(false);
    }

    public void ClearCurrentSession()
    {
        _currentSession = null;
        _currentSessionId = string.Empty;
    }

    internal static void ApplySelectionState(
        AgentSession session,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model)
    {
        session.StateBag.SetValue("providerid", provider.Id);
        session.StateBag.SetValue("providername", provider.Name);
        session.StateBag.SetValue("agentid", agentEntity.Id);
        session.StateBag.SetValue("agentname", agentEntity.Name);
        session.StateBag.SetValue("modelid", model.Name);
        session.StateBag.SetValue("modeldbid", model.Id);
    }

    private static ChatSessionEntity ReadSessionEntity(SqliteDataReader r)
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
            AgentName = r.GetString(r.GetOrdinal("AgentName")),
            SourceTaskId = r.GetString(r.GetOrdinal("SourceTaskId")),
            IsArchived = r.GetInt64(r.GetOrdinal("IsArchived")) != 0,
            IsPinned = r.GetInt64(r.GetOrdinal("IsPinned")) != 0,
            LastActiveTimestamp = r.GetInt64(r.GetOrdinal("LastActiveTimestamp")),
            TotalTokenCount = r.GetInt64(r.GetOrdinal("TotalTokenCount")),
            CompactedContext = r.GetString(r.GetOrdinal("CompactedContext")),
            CompactedAtCount = r.GetInt32(r.GetOrdinal("CompactedAtCount"))
        };
    }
}
