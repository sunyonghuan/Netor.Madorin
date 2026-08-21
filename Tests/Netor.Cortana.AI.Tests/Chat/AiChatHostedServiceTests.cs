using System.Reflection;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Delegation;
using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Madorin.Plugin;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class AiChatHostedServiceTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private AiProviderService _providerService = null!;
    private AiModelService _modelService = null!;
    private AgentService _agentService = null!;
    private ToolContextVersionService _toolContextVersionService = null!;
    private ServiceProvider _services = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-ai-chat-hosted-service-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "chat-hosted-service.db");
        Directory.CreateDirectory(_root);

        _db = new CortanaDbContext(_dbPath);
        _providerService = new AiProviderService(_db);
        _modelService = new AiModelService(_db);
        _agentService = CreateAgentService(_root, _db);
        _toolContextVersionService = new ToolContextVersionService();
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .AddSingleton<IAppPaths>(new TestAppPaths(_root))
            .AddSingleton<IHttpClientFactory, NoopHttpClientFactory>()
            .AddSingleton<IPluginRuntimeConfigProvider, NoopPluginRuntimeConfigProvider>()
            .AddSingleton<PluginManifestRegistry>()
            .AddSingleton<PluginLoader>()
            .BuildServiceProvider();
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
            // 临时目录清理失败不应覆盖断言结果。
        }
    }

    [TestMethod]
    public async Task PublishConversationTurnCompletedOnce_WhenInvokedTwice_OnlyPublishesOnce()
    {
        SeedDefaultSelection();
        var completedEvents = new List<ConversationTurnCompletedArgs>();
        Subscribe(completedEvents);

        var coordinator = CreateTurnLifecycleCoordinator();
        var turnContext = new ChatTurnContext("turn-1", null, null, new CancellationTokenSource())
        {
            Metadata = CreateMetadata("turn-1")
        };

        coordinator.PublishConversationTurnCompletedOnce(
            turnContext,
            ConversationTurnStatus.Succeeded,
            userInput: "你好",
            assistantResponse: "世界",
            errorMessage: null,
            assistantDeltaCount: 2,
            attachmentCount: 1);
        coordinator.PublishConversationTurnCompletedOnce(
            turnContext,
            ConversationTurnStatus.Failed,
            userInput: "不会再次发布",
            assistantResponse: string.Empty,
            errorMessage: "应被去重",
            assistantDeltaCount: 0,
            attachmentCount: 0);

        await WaitForCountAsync(completedEvents, 1);
        Assert.HasCount(1, completedEvents);
        Assert.AreEqual("turn-1", completedEvents[0].TurnId);
        Assert.AreEqual(ConversationTurnStatus.Succeeded, completedEvents[0].Status);
        Assert.AreEqual("你好", completedEvents[0].UserInput);
        Assert.AreEqual("世界", completedEvents[0].AssistantResponse);
    }

    [TestMethod]
    public async Task WaitForCurrentRunToStopAsync_WhenRunDoesNotExit_ForceReleasesStateAndPublishesFailure()
    {
        SeedDefaultSelection();
        var completedEvents = new List<ConversationTurnCompletedArgs>();
        Subscribe(completedEvents);

        var turnRegistry = new ChatTurnCancellationRegistry();
        var coordinator = CreateTurnLifecycleCoordinator(turnRegistry);
        var zombieTurn = new ChatTurnContext(
            "zombie-turn",
            null,
            null,
            new CancellationTokenSource())
        {
            Metadata = CreateMetadata("zombie-turn")
        };
        turnRegistry.Register(zombieTurn);

        var startedAt = DateTimeOffset.UtcNow;
        await coordinator.WaitForCurrentRunToStopAsync(CancellationToken.None);
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        Assert.IsTrue(elapsed >= TimeSpan.FromSeconds(5), $"实际等待时间过短：{elapsed}");
        Assert.IsFalse(turnRegistry.HasActiveTurn());
        Assert.IsNull(turnRegistry.GetCurrentTurn());
        await WaitForCountAsync(completedEvents, 1);
        Assert.HasCount(1, completedEvents);
        Assert.AreEqual("zombie-turn", completedEvents[0].TurnId);
        Assert.AreEqual(ConversationTurnStatus.Failed, completedEvents[0].Status);
        Assert.AreEqual("zombie killed: HTTP/MCP 未响应取消", completedEvents[0].ErrorMessage);
    }

    [TestMethod]
    public void CurrentSessionId_WhenSessionServiceHasCurrentId_ForwardsValue()
    {
        SeedDefaultSelection();
        var sessionService = CreateSessionService();
        SetPrivateField(sessionService, "_currentSessionId", "session-forwarded");

        var service = CreateService(sessionService);

        Assert.AreEqual("session-forwarded", service.CurrentSessionId);
    }

    [TestMethod]
    public async Task StartAsync_WhenWorkspaceChanged_ClearsCurrentSession()
    {
        SeedDefaultSelection();
        var sessionService = CreateSessionService();
        SetPrivateField(sessionService, "_currentSessionId", "session-before-reset");

        var service = CreateService(sessionService);
        await service.StartAsync(CancellationToken.None);

        await _services.GetRequiredService<IPublisher>()
            .PublishAsync(Events.OnWorkspaceChanged, new WorkspaceChangedArgs(Path.Combine(_root, "workspace-2")));

        Assert.IsNull(service.CurrentSessionId);
    }

    [TestMethod]
    public async Task GenerateImageAsync_WhenModelLacksImageCapability_ThrowsExpectedMessage()
    {
        SeedDefaultSelection();
        var service = CreateService();

        try
        {
            await service.GenerateImageAsync("draw a cat", CancellationToken.None);
            Assert.Fail("预期应抛出 InvalidOperationException。");
            return;
        }
        catch (InvalidOperationException exception)
        {
            Assert.AreEqual(
                "当前模型未启用图片输出能力，请先在模型设置中勾选 OutputCapabilities.Image。",
                exception.Message);
        }
    }

    [TestMethod]
    public async Task CancelCurrentTaskAsync_WhenTurnIsActive_CancelsTurnToken()
    {
        SeedDefaultSelection();
        var turnRegistry = new ChatTurnCancellationRegistry();
        var turnContext = new ChatTurnContext("turn-cancel", null, null, new CancellationTokenSource());
        turnRegistry.Register(turnContext);
        var service = CreateService(turnRegistry: turnRegistry);

        await service.CancelCurrentTaskAsync(CancellationToken.None);

        Assert.IsTrue(turnContext.Cts.IsCancellationRequested);
        Assert.AreSame(turnContext, turnRegistry.GetCurrentTurn());
    }

    private static async Task WaitForCountAsync<T>(ICollection<T> collection, int expectedCount)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (collection.Count < expectedCount)
        {
            await Task.Delay(20, cts.Token);
        }
    }

    private void SeedDefaultSelection()
    {
        SeedProvider("provider-default", "Provider Default", isDefault: true);
        SeedModel("model-default", "provider-default", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);
    }

    private AiChatHostedService CreateService(
        ChatSessionService? sessionService = null,
        ChatTurnCancellationRegistry? turnRegistry = null)
    {
        var effectiveTurnRegistry = turnRegistry ?? new ChatTurnCancellationRegistry();
        var effectiveSessionService = sessionService ?? CreateSessionService();
        var resolver = new ChatAgentResolver(
            factory: null!,
            _providerService,
            _agentService,
            _modelService,
            _toolContextVersionService,
            NullLogger<ChatAgentResolver>.Instance);
        resolver.LoadDefaults();

        return new AiChatHostedService(
            agentResolver: resolver,
            messageWriter: new ChatMessageWriter(
                _db,
                NullLogger<ChatMessageWriter>.Instance),
            sessionService: effectiveSessionService,
            turnRegistry: effectiveTurnRegistry,
            turnInterruptionCoordinator: CreateTurnInterruptionCoordinator(effectiveTurnRegistry),
            selectionContextService: new ChatSelectionContextService(
                resolver,
                effectiveSessionService,
                new TestAppPaths(_root)),
            configurationCoordinator: CreateConfigurationCoordinator(resolver, effectiveSessionService),
            turnLifecycleCoordinator: CreateTurnLifecycleCoordinator(effectiveTurnRegistry),
            turnPreparationService: CreateTurnPreparationService(resolver, effectiveSessionService),
            turnExecutor: new ChatTurnExecutor(
                effectiveTurnRegistry,
                new ChatAttachmentLoader(
                    new ChatMessageAssetService(_db),
                    new TestAppPaths(_root),
                    NullLogger<ChatAttachmentLoader>.Instance),
                new ChatMessagePersistence(null!),
                new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
                new ChatRealtimeEventPublisher(null),
                new ChatMessageAssetService(_db),
                new TestAppPaths(_root),
                [],
                _services.GetRequiredService<IPublisher>(),
                NullLogger<ChatTurnExecutor>.Instance),
            imageTurnExecutor: new ChatImageTurnExecutor(
                new AiProviderDriverRegistry([]),
                new ChatGenerationContextBuilder(
                    _db,
                    new TestAppPaths(_root),
                    NullLogger<ChatGenerationContextBuilder>.Instance),
                new ChatGenerationTurnCoordinator(
                    effectiveTurnRegistry,
                    new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
                    new ChatRealtimeEventPublisher(null),
                    _services.GetRequiredService<IPublisher>()),
                new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
                new ChatMessageAssetService(_db),
                new TestAppPaths(_root),
                [],
                NullLogger<ChatImageTurnExecutor>.Instance),
            videoTurnExecutor: new ChatVideoTurnExecutor(
                new AiProviderDriverRegistry([]),
                new ChatGenerationContextBuilder(
                    _db,
                    new TestAppPaths(_root),
                    NullLogger<ChatGenerationContextBuilder>.Instance),
                new ChatGenerationTurnCoordinator(
                    effectiveTurnRegistry,
                    new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
                    new ChatRealtimeEventPublisher(null),
                    _services.GetRequiredService<IPublisher>()),
                new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
                new ChatMessageAssetService(_db),
                new TestAppPaths(_root),
                [],
                NullLogger<ChatVideoTurnExecutor>.Instance),
            publisher: _services.GetRequiredService<IPublisher>(),
            logger: NullLogger<AiChatHostedService>.Instance);
    }

    private ChatConfigurationCoordinator CreateConfigurationCoordinator(
        ChatAgentResolver resolver,
        ChatSessionService sessionService)
    {
        return new ChatConfigurationCoordinator(
            resolver,
            sessionService,
            _services.GetRequiredService<ISubscriber>(),
            NullLogger<ChatConfigurationCoordinator>.Instance);
    }

    private ChatTurnLifecycleCoordinator CreateTurnLifecycleCoordinator(ChatTurnCancellationRegistry? turnRegistry = null)
    {
        return new ChatTurnLifecycleCoordinator(
            turnRegistry ?? new ChatTurnCancellationRegistry(),
            new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
            [],
            NullLogger<ChatTurnLifecycleCoordinator>.Instance);
    }

    private ChatTurnInterruptionCoordinator CreateTurnInterruptionCoordinator(ChatTurnCancellationRegistry? turnRegistry = null)
    {
        var effectiveTurnRegistry = turnRegistry ?? new ChatTurnCancellationRegistry();
        return new ChatTurnInterruptionCoordinator(
            effectiveTurnRegistry,
            CreateTurnLifecycleCoordinator(effectiveTurnRegistry),
            new ChatTurnExecutor(
                effectiveTurnRegistry,
                new ChatAttachmentLoader(
                    new ChatMessageAssetService(_db),
                    new TestAppPaths(_root),
                    NullLogger<ChatAttachmentLoader>.Instance),
                new ChatMessagePersistence(null!),
                new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
                new ChatRealtimeEventPublisher(null),
                new ChatMessageAssetService(_db),
                new TestAppPaths(_root),
                [],
                _services.GetRequiredService<IPublisher>(),
                NullLogger<ChatTurnExecutor>.Instance));
    }

    private ChatTurnPreparationService CreateTurnPreparationService(
        ChatAgentResolver resolver,
        ChatSessionService? sessionService = null)
    {
        var orchestrator = new AgentOrchestrator(
            resolver,
            new AgentOrchestratorOptions(),
            NullLogger<AgentOrchestrator>.Instance);

        return new ChatTurnPreparationService(
            orchestrator,
            sessionService ?? CreateSessionService(),
            null!,
            CreateExpertDelegationTools());
    }

    private ExpertDelegationTools CreateExpertDelegationTools()
    {
        var toolCatalog = new SystemToolCatalogService([], _services.GetRequiredService<PluginLoader>());
        var agentFactory = new AIAgentFactory(
            new TestAppPaths(_root),
            builtInProviders: [],
            _services.GetRequiredService<PluginLoader>(),
            new AiProviderDriverRegistry([new TestProviderDriver()]),
            _services,
            NullLogger<AIAgentFactory>.Instance);

        return new ExpertDelegationTools(
            toolCatalog,
            new DelegatedAgentJobExecutor(
                agentFactory,
                _providerService,
                _modelService,
                new DelegatedAgentJobService(_db),
                toolCatalog,
                NullLogger<DelegatedAgentJobExecutor>.Instance));
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
            throw new NotSupportedException("本测试不发送真实请求。");
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

    private ChatSessionService CreateSessionService()
    {
        return new ChatSessionService(
            factory: null!,
            chatHistoryProvider: null!,
            dbContext: _db,
            appPaths: new TestAppPaths(_root),
            publisher: _services.GetRequiredService<IPublisher>(),
            logger: NullLogger<ChatSessionService>.Instance);
    }

    private void Subscribe(List<ConversationTurnCompletedArgs> sink)
    {
        _services.GetRequiredService<ISubscriber>().Subscribe<ConversationTurnCompletedArgs>(
            Events.OnConversationTurnCompleted,
            (_, args) =>
            {
                sink.Add(args);
                return Task.FromResult(false);
            });
    }

    private ConversationEventMetadata CreateMetadata(string turnId)
    {
        return CreateTurnLifecycleCoordinator().CreateConversationEventMetadata(
            sessionId: "session-1",
            turnId: turnId,
            traceId: "trace-1",
            userMessageId: "user-1",
            assistantMessageId: "assistant-1",
            provider: _providerService.GetAll().Single(static x => x.Id == "provider-default"),
            agentEntity: _agentService.GetAll().Single(static x => x.Id == "agent-default"),
            model: _modelService.GetById("model-default")!,
            orchestrationResult: new AgentOrchestrationResult(
                Agent: null!,
                Mode: AgentOrchestrationMode.ToolDelegation,
                UsedAgentIds: ["agent-a", "agent-b"],
                Warnings: ["fallback"],
                Failures: []));
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"未找到私有字段：{fieldName}");
        field.SetValue(instance, value);
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
            ProviderType = "OpenAI",
            IsDefault = isDefault,
            IsEnabled = true,
            SortOrder = 0,
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
}
