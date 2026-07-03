using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;
using Netor.EventHub;

using System.Net.Http;

namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class AgentOrchestratorTests
{
    private string _root = null!;
    private CortanaDbContext _db = null!;
    private AiProviderService _providerService = null!;
    private AiModelService _modelService = null!;
    private AgentService _agentService = null!;
    private ToolContextVersionService _toolContextVersionService = null!;
    private ServiceProvider _services = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-agent-orchestrator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _db = new CortanaDbContext(Path.Combine(_root, "orchestrator.db"));
        _providerService = new AiProviderService(_db);
        _modelService = new AiModelService(_db);
        _agentService = CreateAgentService(_root, _db);
        _toolContextVersionService = new ToolContextVersionService();
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .AddSingleton(new TestAppPaths(_root))
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
        }
    }

    [TestMethod]
    public async Task BuildAgentAsync_WhenModeIsNone_RecordsEmptyUsedAgents()
    {
        SeedProvider("provider-a", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedAgent("agent-main", "主助手", isDefault: true);

        var orchestrator = CreateOrchestrator();

        var result = await orchestrator.BuildAsync(CreateRequest(AgentOrchestrationMode.None, []));
        var latest = orchestrator.GetLastResult();

        Assert.IsNotNull(result);
        Assert.IsNotNull(latest);
        Assert.AreSame(result, latest);
        Assert.AreEqual(AgentOrchestrationMode.None, result.Mode);
        Assert.IsEmpty(result.UsedAgentIds);
        Assert.IsEmpty(result.Warnings);
    }

    [TestMethod]
    public async Task BuildAgentAsync_WhenModeIsHandoffChat_FallsBackToToolDelegationAndRecordsWarning()
    {
        SeedProvider("provider-a", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedAgent("agent-main", "主助手", isDefault: true);
        SeedAgent("agent-a", "子助手A", isDefault: false);
        SeedAgent("agent-b", "子助手B", isDefault: false);

        var orchestrator = CreateOrchestrator(new AgentOrchestratorOptions
        {
            MaxRounds = 7,
            MaxSubTasks = 9,
        });

        var mentions = new[] { CreateMention("agent-a"), CreateMention("agent-b") };
        var result = await orchestrator.BuildAsync(CreateRequest(AgentOrchestrationMode.HandoffChat, mentions));

        Assert.IsNotNull(result);
        Assert.AreEqual(AgentOrchestrationMode.ToolDelegation, result.Mode);
        CollectionAssert.AreEquivalent(new[] { "agent-a", "agent-b" }, result.UsedAgentIds.ToArray());
        Assert.HasCount(1, result.Warnings);
        StringAssert.Contains(result.Warnings[0], "HandoffChat");
    }

    private AgentOrchestrator CreateOrchestrator(AgentOrchestratorOptions? options = null)
    {
        var resolver = new ChatAgentResolver(
            CreateAgentFactory(),
            _providerService,
            _agentService,
            _modelService,
            _toolContextVersionService,
            NullLogger<ChatAgentResolver>.Instance);
        resolver.LoadDefaults();

        return new AgentOrchestrator(
            resolver,
            options ?? new AgentOrchestratorOptions(),
            NullLogger<AgentOrchestrator>.Instance);
    }

    private AgentOrchestrationRequest CreateRequest(
        AgentOrchestrationMode mode,
        IReadOnlyList<AgentMention> mentions)
    {
        var provider = _providerService.GetAll().First();
        var model = _modelService.GetByProviderId(provider.Id).First();
        var agent = _agentService.GetDefaultOrFirst()!;

        return new AgentOrchestrationRequest(
            mode,
            AgentExecutionStrategy.Sequential,
            agent,
            provider,
            model,
            mentions,
            [],
            "session-1",
            "workspace-1",
            "hello");
    }

    private AIAgentFactory CreateAgentFactory()
    {
        var driver = new TestProviderDriver();
        return new AIAgentFactory(
            _services.GetRequiredService<IAppPaths>(),
            builtInProviders: [],
            _services.GetRequiredService<PluginLoader>(),
            new AiProviderDriverRegistry([driver]),
            _services,
            NullLogger<AIAgentFactory>.Instance);
    }

    private AgentMention CreateMention(string agentId)
    {
        var agent = _agentService.GetByName(agentId);
        Assert.IsNotNull(agent, $"未找到智能体：{agentId}");
        return new AgentMention(agent, 0, agent.Name.Length + 1);
    }

    private void SeedProvider(string id, bool isDefault)
    {
        _providerService.Add(new AiProviderEntity
        {
            Id = id,
            Name = id,
            Url = $"https://{id}.example.com/v1",
            Key = "test-key",
            AuthToken = string.Empty,
            Description = id,
            ProviderType = "TestProvider",
            IsDefault = isDefault,
            IsEnabled = true,
        });
    }

    private void SeedModel(string id, string providerId, bool isDefault)
    {
        _modelService.Add(new AiModelEntity
        {
            Id = id,
            Name = id,
            DisplayName = id,
            Description = id,
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

    private void SeedAgent(string id, string name, bool isDefault)
    {
        _agentService.Add(new AgentEntity
        {
            Id = id,
            Name = name,
            Description = name,
            Instructions = name,
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

    private sealed class NoopPluginRuntimeConfigProvider : IPluginRuntimeConfigProvider
    {
        public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest) => "{}";

        public string BuildPluginConfigJson(string pluginId, PluginManifest manifest) => "{}";
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
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
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
