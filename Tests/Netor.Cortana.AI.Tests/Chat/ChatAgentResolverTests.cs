using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;
using Netor.EventHub;
using System.Net.Http;

namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class ChatAgentResolverTests
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
        _root = Path.Combine(Path.GetTempPath(), $"cortana-chat-agent-resolver-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "resolver.db");
        Directory.CreateDirectory(_root);

        _db = new CortanaDbContext(_dbPath);
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
            // 临时测试目录清理失败不应覆盖真实断言结果。
        }
    }

    [TestMethod]
    public void LoadDefaults_PicksDefaultProviderAgentAndModel()
    {
        SeedProvider("provider-a", "Provider A", isDefault: false);
        SeedProvider("provider-b", "Provider B", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedModel("model-b1", "provider-b", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);
        SeedAgent("agent-other", "其它助手", isDefault: false);

        var resolver = CreateResolver();

        resolver.LoadDefaults();

        Assert.AreEqual("provider-b", resolver.CurrentProvider?.Id);
        Assert.AreEqual("model-b1", resolver.CurrentModel?.Id);
        Assert.AreEqual("agent-default", resolver.CurrentAgent?.Id);
    }

    [TestMethod]
    public void ChangeProvider_WhenProviderChanges_UpdatesCurrentModelToProviderDefault()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedProvider("provider-b", "Provider B", isDefault: false);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedModel("model-b1", "provider-b", isDefault: true);
        SeedModel("model-b2", "provider-b", isDefault: false);
        SeedAgent("agent-default", "默认助手", isDefault: true);

        var resolver = CreateResolver();
        resolver.LoadDefaults();

        resolver.ChangeProvider("provider-b");

        Assert.AreEqual("provider-b", resolver.CurrentProvider?.Id);
        Assert.AreEqual("model-b1", resolver.CurrentModel?.Id);
    }

    [TestMethod]
    public void ChangeModel_WhenModelBelongsToAnotherProvider_SwitchesProviderTogether()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedProvider("provider-b", "Provider B", isDefault: false);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedModel("model-b1", "provider-b", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);

        var resolver = CreateResolver();
        resolver.LoadDefaults();

        resolver.ChangeModel("model-b1");

        Assert.AreEqual("provider-b", resolver.CurrentProvider?.Id);
        Assert.AreEqual("model-b1", resolver.CurrentModel?.Id);
    }

    [TestMethod]
    public void InvalidateAgent_WhenAgentFileChanged_RefreshesCurrentAgentSnapshot()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);

        var resolver = CreateResolver();
        resolver.LoadDefaults();
        Assert.AreEqual("默认助手", resolver.CurrentAgent?.Name);

        _agentService.Update(new AgentEntity
        {
            Id = "agent-default",
            Name = "默认助手-已更新",
            Description = "更新后的说明",
            Instructions = "更新后的提示词",
            IsEnabled = true,
        });

        resolver.InvalidateAgent("测试");

        Assert.AreEqual("默认助手-已更新", resolver.CurrentAgent?.Name);
    }

    [TestMethod]
    public void BumpToolContextVersion_WhenCalled_IncrementsVersionAndRefreshesAgentSnapshot()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);

        var resolver = CreateResolver();
        resolver.LoadDefaults();

        _agentService.Update(new AgentEntity
        {
            Id = "agent-default",
            Name = "默认助手-v2",
            Description = "更新后的说明",
            Instructions = "更新后的提示词",
            IsEnabled = true,
        });

        var version = resolver.BumpToolContextVersion("插件/MCP 工具");

        Assert.AreEqual(1L, version);
        Assert.AreEqual(1L, _toolContextVersionService.Current);
        Assert.AreEqual("默认助手-v2", resolver.CurrentAgent?.Name);
    }

    [TestMethod]
    public void BuildAgentForTurn_WhenSelectionAndToolVersionUnchanged_ReusesCurrentAgent()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();

        var built = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);
        var reused = resolver.BuildAgentForTurn(built, mentions: []);

        Assert.IsNotNull(built);
        Assert.AreSame(built, reused);
    }

    [TestMethod]
    public void BuildAgentForTurn_WhenDefaultSelectionChanges_RebuildsAgentInsteadOfReusingCurrentAgent()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedProvider("provider-b", "Provider B", isDefault: false);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedModel("model-b1", "provider-b", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var original = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);

        resolver.ChangeProvider("provider-b");
        var rebuilt = resolver.BuildAgentForTurn(original, mentions: []);

        Assert.IsNotNull(original);
        Assert.IsNotNull(rebuilt);
        Assert.AreNotSame(original, rebuilt);
        Assert.AreEqual("provider-b", resolver.CurrentProvider?.Id);
        Assert.AreEqual("model-b1", resolver.CurrentModel?.Id);
    }

    [TestMethod]
    public void BuildAgentForTurn_WhenToolContextVersionChanges_RebuildsAgentInsteadOfReusingCurrentAgent()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var original = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);

        _toolContextVersionService.Bump();
        var rebuilt = resolver.BuildAgentForTurn(original, mentions: []);

        Assert.IsNotNull(original);
        Assert.IsNotNull(rebuilt);
        Assert.AreNotSame(original, rebuilt);
    }

    [TestMethod]
    public void BuildAgentForTurn_WhenHasMention_RebuildsAgentEvenIfCurrentAgentExists()
    {
        SeedProvider("provider-a", "Provider A", isDefault: true);
        SeedModel("model-a1", "provider-a", isDefault: true);
        SeedAgent("agent-default", "默认助手", isDefault: true);
        SeedAgent("agent-sub", "子助手", isDefault: false);

        var resolver = CreateResolverWithFactory();
        resolver.LoadDefaults();
        var original = resolver.BuildAgentForTurn(currentAgent: null, mentions: []);
        var mention = CreateMention("agent-sub");

        var rebuilt = resolver.BuildAgentForTurn(original, mentions: [mention]);

        Assert.IsNotNull(original);
        Assert.IsNotNull(rebuilt);
        Assert.AreNotSame(original, rebuilt);
    }

    private ChatAgentResolver CreateResolver()
    {
        return new ChatAgentResolver(
            factory: null!,
            _providerService,
            _agentService,
            _modelService,
            _toolContextVersionService,
            NullLogger<ChatAgentResolver>.Instance);
    }

    private ChatAgentResolver CreateResolverWithFactory()
    {
        return new ChatAgentResolver(
            CreateAgentFactory(),
            _providerService,
            _agentService,
            _modelService,
            _toolContextVersionService,
            NullLogger<ChatAgentResolver>.Instance);
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

    private AgentMention CreateMention(string agentId)
    {
        var agent = _agentService.GetByName(agentId);
        Assert.IsNotNull(agent, $"未找到智能体：{agentId}");
        return new AgentMention(agent, 0, agent.Name.Length + 1);
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

        // 测试结果不应因临时目录清理失败而被覆盖。
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
            throw new NotSupportedException("本测试仅验证 agent 构建与复用，不发送真实请求。");
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
