using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.Entitys.Services;
using Netor.Madorin.Plugin;
using Netor.EventHub;

using System.Net.Http;
using System.Reflection;

namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class ChatSessionServiceTests
{
    private string _root = null!;
    private CortanaDbContext _db = null!;
    private AiProviderService _providerService = null!;
    private AiModelService _modelService = null!;
    private AgentService _agentService = null!;
    private ToolContextVersionService _toolContextVersionService = null!;
    private ServiceProvider _services = null!;
    private TestAppPaths _appPaths = null!;
    private AIAgentFactory _factory = null!;
    private ChatHistoryDataProvider _chatHistoryProvider = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-chat-session-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _db = new CortanaDbContext(Path.Combine(_root, "session-service.db"));
        _providerService = new AiProviderService(_db);
        _modelService = new AiModelService(_db);
        _agentService = CreateAgentService(_root, _db);
        _toolContextVersionService = new ToolContextVersionService();
        _appPaths = new TestAppPaths(_root);

        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .AddSingleton(_appPaths)
            .AddSingleton<IAppPaths>(static sp => sp.GetRequiredService<TestAppPaths>())
            .AddSingleton<SystemSettingsService>(_ => new SystemSettingsService(_db))
            .AddSingleton<IHttpClientFactory, NoopHttpClientFactory>()
            .AddSingleton<IPluginRuntimeConfigProvider, NoopPluginRuntimeConfigProvider>()
            .AddSingleton<PluginManifestRegistry>()
            .AddSingleton<PluginLoader>()
            .AddSingleton<ChatHistoryDataProvider>(sp => new ChatHistoryDataProvider(
                _db,
                sp.GetRequiredService<SystemSettingsService>(),
                sp.GetRequiredService<IAppPaths>(),
                NullLogger<ChatHistoryDataProvider>.Instance,
                sp))
            .BuildServiceProvider();

        _chatHistoryProvider = _services.GetRequiredService<ChatHistoryDataProvider>();
        _factory = new AIAgentFactory(
            _appPaths,
            builtInProviders: [],
            _services.GetRequiredService<PluginLoader>(),
            new AiProviderDriverRegistry([new TestProviderDriver()]),
            _services,
            NullLogger<AIAgentFactory>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
        _db.Dispose();

        try
        {
            DeleteDirectoryWithRetry(_root);
        }
        catch (IOException)
        {
            // 临时目录清理失败不应覆盖真实断言结果。
        }
    }

    [TestMethod]
    public async Task NewSessionAsync_WhenCalled_PublishesSessionCreatedAndTracksCurrentId()
    {
        SeedDefaultSelection();
        SetFactoryLastInputTokens(128);
        var createdEvents = new List<SessionCreatedArgs>();
        Subscribe(createdEvents);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var sessionService = CreateSessionService();
        var agent = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);

        var session = await sessionService.NewSessionAsync(
            agent!,
            resolver.CurrentProvider!,
            resolver.CurrentAgent!,
            resolver.CurrentModel!);

        await WaitForCountAsync(createdEvents, 1);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sessionService.CurrentId));
        Assert.AreEqual(sessionService.CurrentId, session.StateBag.GetValue<string>("sessionid"));
        Assert.AreEqual(sessionService.CurrentId, createdEvents[0].SessionId);
        Assert.AreEqual(0L, _factory.LastInputTokens);
    }

    [TestMethod]
    public async Task ResumeSessionAsync_WhenCalled_SetsCurrentIdAndClearsTokenStats()
    {
        SeedDefaultSelection();
        SetFactoryLastInputTokens(256);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var sessionService = CreateSessionService();
        var agent = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);

        var session = await sessionService.ResumeSessionAsync(
            "existing-session-id",
            agent!,
            resolver.CurrentProvider!,
            resolver.CurrentAgent!,
            resolver.CurrentModel!);

        Assert.AreEqual("existing-session-id", sessionService.CurrentId);
        Assert.AreEqual("existing-session-id", session.StateBag.GetValue<string>("sessionid"));
        Assert.AreEqual(0L, _factory.LastInputTokens);
    }

    [TestMethod]
    public async Task LoadOrCreateSessionAsync_WhenHistoryContainsWorkflowAndMeetingSessions_ResumesOnlyExpertSession()
    {
        SeedDefaultSelection();
        var categorize = _appPaths.WorkspaceDirectory.Md5Encrypt();
        SeedChatSession("archived-session", categorize, "归档会话", 500, isArchived: true);
        SeedChatSession("source-task-session", categorize, "工作来源会话", 600, sourceTaskId: "task-source");
        SeedChatSession("work-linked-session", categorize, "工作关联会话", 700);
        SeedChatSession("meeting-linked-session", categorize, "会议关联会话", 800);
        SeedChatSession("expert-session", categorize, "专家普通会话", 100);
        SeedWorkTask("task-linked", "work-linked-session", categorize);
        SeedMeetingSession("meeting-linked", "meeting-linked-session", categorize);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var sessionService = CreateSessionService();
        var agent = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);

        var visibleSessions = sessionService.GetVisibleExpertSessions(categorize, limit: 20);
        var session = await sessionService.LoadOrCreateSessionAsync(
            agent!,
            resolver.CurrentProvider!,
            resolver.CurrentAgent!,
            resolver.CurrentModel!);

        Assert.HasCount(1, visibleSessions);
        Assert.AreEqual("expert-session", visibleSessions[0].Id);
        Assert.AreEqual("expert-session", sessionService.CurrentId);
        Assert.AreEqual("expert-session", session.StateBag.GetValue<string>("sessionid"));
        Assert.IsTrue(sessionService.IsVisibleExpertSession("expert-session", categorize));
        Assert.IsFalse(sessionService.IsVisibleExpertSession("work-linked-session", categorize));
        Assert.IsFalse(sessionService.IsVisibleExpertSession("meeting-linked-session", categorize));
        Assert.IsFalse(sessionService.IsVisibleExpertSession("source-task-session", categorize));
        Assert.IsFalse(sessionService.IsVisibleExpertSession("archived-session", categorize));
    }

    [TestMethod]
    public async Task EnsureCurrentSessionAsync_AfterResumingNonLatestSession_KeepsResumedSessionInsteadOfLatest()
    {
        // 回归：恢复一个“非最新”专家会话后，下一轮准备（EnsureCurrentSessionAsync）
        // 必须复用被恢复的会话，而不是重解析成“最近会话”，否则 AI 上下文会串到别的会话。
        // 详见 ChatTurnPreparationService.TryPrepareConversationTurnAsync 的修复。
        SeedDefaultSelection();
        var categorize = _appPaths.WorkspaceDirectory.Md5Encrypt();
        SeedChatSession("older-session", categorize, "较早的会话", 100);
        SeedChatSession("latest-session", categorize, "最近的会话", 900);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var sessionService = CreateSessionService();
        var agent = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);

        // 先确认“最近会话”确实是 latest-session（否则本测试前提不成立）。
        Assert.AreEqual("latest-session", sessionService.GetMostRecentVisibleExpertSessionId(categorize));

        // 恢复较早的会话（模拟用户在左侧点击历史对话）。
        await sessionService.ResumeSessionAsync(
            "older-session",
            agent!,
            resolver.CurrentProvider!,
            resolver.CurrentAgent!,
            resolver.CurrentModel!);
        Assert.AreEqual("older-session", sessionService.CurrentId);

        // 下一轮发消息的准备：必须仍然是 older-session，而不是被覆盖成 latest-session。
        var session = await sessionService.EnsureCurrentSessionAsync(
            agent!,
            resolver.CurrentProvider!,
            resolver.CurrentAgent!,
            resolver.CurrentModel!);

        Assert.AreEqual("older-session", sessionService.CurrentId);
        Assert.AreEqual("older-session", session.StateBag.GetValue<string>("sessionid"));
    }

    [TestMethod]
    public async Task EnsureCurrentSessionAsync_WhenNoCurrentSession_FallsBackToMostRecentSession()
    {
        // 冷启动语义保持：没有当前活跃会话时，EnsureCurrentSessionAsync 回退到“最近会话”。
        SeedDefaultSelection();
        var categorize = _appPaths.WorkspaceDirectory.Md5Encrypt();
        SeedChatSession("older-session", categorize, "较早的会话", 100);
        SeedChatSession("latest-session", categorize, "最近的会话", 900);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var sessionService = CreateSessionService();
        var agent = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);

        var session = await sessionService.EnsureCurrentSessionAsync(
            agent!,
            resolver.CurrentProvider!,
            resolver.CurrentAgent!,
            resolver.CurrentModel!);

        Assert.AreEqual("latest-session", sessionService.CurrentId);
        Assert.AreEqual("latest-session", session.StateBag.GetValue<string>("sessionid"));
    }

    [TestMethod]
    public void GetVisibleExpertSessions_WhenSearchKeywordProvided_FiltersWithinExpertSessionsOnly()
    {
        var categorize = _appPaths.WorkspaceDirectory.Md5Encrypt();
        SeedChatSession("expert-match", categorize, "客户复盘", 300);
        SeedChatSession("expert-other", categorize, "普通问题", 200);
        SeedChatSession("work-match", categorize, "客户工作记录", 400);
        SeedWorkTask("task-search", "work-match", categorize);

        var sessionService = CreateSessionService();

        var visibleSessions = sessionService.GetVisibleExpertSessions(
            categorize,
            limit: 20,
            searchKeyword: "客户");

        Assert.HasCount(1, visibleSessions);
        Assert.AreEqual("expert-match", visibleSessions[0].Id);
    }

    private void SeedDefaultSelection()
    {
        SeedProvider("provider-default", "Provider Default", isDefault: true);
        SeedModel("model-default", "provider-default", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);
    }

    private ChatSessionService CreateSessionService()
    {
        return new ChatSessionService(
            _factory,
            _chatHistoryProvider,
            _db,
            _appPaths,
            _services.GetRequiredService<IPublisher>(),
            NullLogger<ChatSessionService>.Instance);
    }

    private ChatAgentResolver CreateResolverWithFactory()
    {
        return new ChatAgentResolver(
            _factory,
            _providerService,
            _agentService,
            _modelService,
            _toolContextVersionService,
            NullLogger<ChatAgentResolver>.Instance);
    }

    private void Subscribe(List<SessionCreatedArgs> sink)
    {
        _services.GetRequiredService<ISubscriber>().Subscribe<SessionCreatedArgs>(
            Events.OnSessionCreated,
            (_, args) =>
            {
                sink.Add(args);
                return Task.FromResult(false);
            });
    }

    private void SetFactoryLastInputTokens(long value)
    {
        var field = typeof(AIAgentFactory).GetField("_lastInputTokens", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "未找到 AIAgentFactory._lastInputTokens");
        field.SetValue(_factory, value);
    }

    private void SeedProvider(string id, string name, bool isDefault)
    {
        _providerService.Add(new AiProviderEntity
        {
            Id = id,
            Name = name,
            Url = $"https://{id}.example.com/v1",
            Key = "test-key",
            AuthToken = string.Empty,
            Description = $"{name} description",
            ProviderType = "TestProvider",
            IsDefault = isDefault,
            IsEnabled = true,
            SortOrder = 0,
        });
    }

    private void SeedChatSession(
        string id,
        string categorize,
        string title,
        long lastActiveTimestamp,
        bool isArchived = false,
        string sourceTaskId = "")
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title, Summary,
                RawDiscription, AgentName, SourceTaskId, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount
            ) VALUES (
                @Id, @Now, @Now, @Categorize, @Title, '', '', 'agent-default', @SourceTaskId,
                @IsArchived, 0, @LastActiveTimestamp, 0, '', 0
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Categorize", categorize);
                cmd.Parameters.AddWithValue("@Title", title);
                cmd.Parameters.AddWithValue("@SourceTaskId", sourceTaskId);
                cmd.Parameters.AddWithValue("@IsArchived", isArchived ? 1 : 0);
                cmd.Parameters.AddWithValue("@LastActiveTimestamp", lastActiveTimestamp);
            });
    }

    private void SeedWorkTask(string taskId, string sessionId, string workspaceId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO WorkTasks (
                Id, SessionId, WorkspaceId, Title, InitialInput, RunId, ManagerRunId, IsActive,
                CurrentPlanJson, PendingRequestId, PendingRequestKind, PendingRequestData,
                CompletedAt, FinalReport, ErrorMessage, Provider, Model, AgentName, MentionsJson,
                SourceTaskId, ParentTaskId, IsOrphaned, OrphanedDetectedAt, HeartbeatAt,
                OrchestratorState, OrchestratorHeartbeatAt, HasPreemption, CreatedAt, UpdatedAt,
                LastActiveAt
            ) VALUES (
                @Id, @SessionId, @WorkspaceId, '工作任务', '初始输入', NULL, NULL, 0,
                NULL, NULL, NULL, NULL, @Now, '完成报告', NULL, 'provider-default',
                'model-default', 'agent-default', NULL, NULL, NULL, 0, NULL, NULL,
                NULL, NULL, 0, @Now, @Now, @Now
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", taskId);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Now", now);
            });
    }

    private void SeedMeetingSession(string meetingId, string sessionId, string workspaceId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO MeetingSessions (
                Id, SessionId, WorkspaceId, Topic, HostAgentId, ParticipantsJson, Status,
                RunId, PendingRequestId, PendingRequestKind, PendingRequestData, FinalSummaryMd,
                Provider, Model, CreatedAt, UpdatedAt, EndedAt
            ) VALUES (
                @Id, @SessionId, @WorkspaceId, '会议主题', 'system-meeting-host', '[]', 2,
                NULL, NULL, NULL, NULL, '会议总结', 'provider-default', 'model-default',
                @Now, @Now, @Now
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", meetingId);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Now", now);
            });
    }

    private void SeedModel(string id, string providerId, bool isDefault)
    {
        _modelService.Add(new AiModelEntity
        {
            Id = id,
            Name = id,
            DisplayName = id,
            Description = $"{id} description",
            ProviderId = providerId,
            IsDefault = isDefault,
            IsEnabled = true,
            ModelType = "chat",
            ContextLength = 128_000,
            InputCapabilities = InputCapabilities.Text,
            OutputCapabilities = OutputCapabilities.Text,
            InteractionCapabilities = InteractionCapabilities.None,
        });
    }

    private void SeedAgent(string id, string displayName, bool isDefault)
    {
        _agentService.Add(new AgentEntity
        {
            Id = id,
            Name = displayName,
            Description = $"{displayName} description",
            Instructions = $"{displayName} prompt",
            IsEnabled = true,
        });

        if (isDefault)
        {
            _agentService.SetDefault(id);
        }
    }

    private static async Task WaitForCountAsync<T>(ICollection<T> collection, int expectedCount)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (collection.Count < expectedCount)
        {
            await Task.Delay(20, cts.Token);
        }
    }

    private static AgentService CreateAgentService(string root, CortanaDbContext db)
    {
        var serializer = new AgentManifestSerializer();
        var validator = new AgentManifestValidator();
        var index = new AgentFileIndex(serializer, validator, NullLogger<AgentFileIndex>.Instance);
        var fileService = new AgentFileService(
            new TestAppPaths(root),
            serializer,
            validator,
            index,
            NullLogger<AgentFileService>.Instance);
        fileService.RebuildIndex();
        return new AgentService(fileService, new SystemSettingsService(db));
    }

    private static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory => Path.Combine(root, "workspace");
        public string UserDataDirectory => Path.Combine(root, "data");
        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");
        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");
        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");
        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");
        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");
        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");
        public string PluginDirectory => Path.Combine(UserDataDirectory, "plugins-bin");
        public string WorkspaceResourcesDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "resources");
        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");
        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }

    private sealed class NoopPluginRuntimeConfigProvider : IPluginRuntimeConfigProvider
    {
        public string BuildPluginConfigJson(string pluginId, PluginManifest manifest)
        {
            return "{}";
        }

        public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest)
        {
            return "{}";
        }
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient();
        }
    }

    private sealed class TestProviderDriver : AiProviderDriverBase
    {
        public override AiProviderDriverDefinition Definition { get; } =
            new("TestProvider", "Test Provider", true);

        public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
        {
            return new TestChatClient();
        }

        public override ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent)
        {
            return CreateCommonOptions(agent);
        }
    }

    private sealed class TestChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("本测试仅验证 session 管理，不发送真实请求。");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
