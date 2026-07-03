using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Delegation;
using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.Delegation;

[TestClass]
public sealed class DelegatedAgentJobExecutorTests
{
    private static readonly FieldInfo RunningField =
        typeof(DelegatedAgentJobExecutor).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(DelegatedAgentJobExecutor), "_running");

    [TestMethod]
    public async Task StartAsync_WhenRunningJobLimitReached_ThrowsInvalidOperationException()
    {
        var executor = new DelegatedAgentJobExecutor(
            agentFactory: null!,
            providerService: null!,
            modelService: null!,
            jobService: null!,
            toolCatalog: null!,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var running = GetRunningJobs(executor);
        var runningTokens = new List<CancellationTokenSource>();
        try
        {
            for (var i = 1; i <= 50; i++)
            {
                var tokenSource = new CancellationTokenSource();
                runningTokens.Add(tokenSource);
                running[$"job-{i}"] = tokenSource;
            }

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => executor.StartAsync(CreateJob(), CancellationToken.None));

            Assert.Contains("后台委派任务数量已达到上限：50", ex.Message);
        }
        finally
        {
            foreach (var tokenSource in runningTokens)
            {
                tokenSource.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task StartAsync_WhenCallerTokenCancels_TaskKeepsRunning()
    {
        using var db = CreateDatabase();
        using var services = CreateServices();
        var jobService = new DelegatedAgentJobService(db);
        var toolCatalog = new SystemToolCatalogService([], services.GetRequiredService<PluginLoader>());
        var chatClient = new BlockingChatClient();
        var executor = new DelegatedAgentJobExecutor(
            CreateAgentFactory(services, chatClient),
            new AiProviderService(db),
            new AiModelService(db),
            jobService,
            toolCatalog,
            NullLogger<DelegatedAgentJobExecutor>.Instance);

        SeedProviderAndModel(db);
        var job = CreateJob();
        using var cts = new CancellationTokenSource();
        var started = await executor.StartAsync(job, cts.Token);
        Assert.IsTrue(await chatClient.WaitEnteredAsync());

        cts.Cancel();
        var statusAfterCallerCancel = executor.GetStatus(started);

        chatClient.Release();
        Assert.IsTrue(await WaitForStateAsync(executor, started, DelegatedAgentJobStates.Completed));

        Assert.AreEqual(DelegatedAgentJobStates.Running, statusAfterCallerCancel.State);
        Assert.AreEqual(DelegatedAgentJobStates.Completed, jobService.GetById(started)?.State);
    }

    private static ConcurrentDictionary<string, CancellationTokenSource> GetRunningJobs(
        DelegatedAgentJobExecutor executor)
    {
        return (ConcurrentDictionary<string, CancellationTokenSource>?)RunningField.GetValue(executor)
            ?? throw new InvalidOperationException("无法获取后台委派任务运行集合。");
    }

    private static CortanaDbContext CreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), "cortana-delegated-agent-executor-tests", $"{Guid.NewGuid():N}.db");
        return new CortanaDbContext(path);
    }

    private static ServiceProvider CreateServices()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortana-delegated-agent-executor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        return new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .AddSingleton<IAppPaths>(new TestAppPaths(root))
            .AddSingleton<IHttpClientFactory, NoopHttpClientFactory>()
            .AddSingleton<IPluginRuntimeConfigProvider, NoopPluginRuntimeConfigProvider>()
            .AddSingleton<PluginManifestRegistry>()
            .AddSingleton<PluginLoader>()
            .BuildServiceProvider();
    }

    private static AIAgentFactory CreateAgentFactory(
        ServiceProvider services,
        BlockingChatClient chatClient)
    {
        var paths = services.GetRequiredService<IAppPaths>();
        return new AIAgentFactory(
            paths,
            builtInProviders: [],
            services.GetRequiredService<PluginLoader>(),
            new AiProviderDriverRegistry([new TestProviderDriver(chatClient)]),
            services,
            NullLogger<AIAgentFactory>.Instance);
    }

    private static void SeedProviderAndModel(CortanaDbContext db)
    {
        var providerService = new AiProviderService(db);
        providerService.Add(new AiProviderEntity
        {
            Id = "test-provider",
            Name = "Test Provider",
            ProviderType = "TestProvider",
            IsDefault = true,
            IsEnabled = true
        });

        var modelService = new AiModelService(db);
        modelService.Add(new AiModelEntity
        {
            Id = "test-model",
            Name = "test-model",
            DisplayName = "Test Model",
            ProviderId = "test-provider",
            IsDefault = true,
            IsEnabled = true,
        InteractionCapabilities = InteractionCapabilities.FunctionCall
        });
    }

    private static async Task<bool> WaitForStateAsync(
        DelegatedAgentJobExecutor executor,
        string jobId,
        string state)
    {
        for (var i = 0; i < 20; i++)
        {
            if (executor.GetStatus(jobId).State == state)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private static DelegatedAgentJobEntity CreateJob() => new()
    {
        Id = $"chatjob_{Guid.NewGuid():N}",
        ScopeKind = "chat",
        ScopeId = "session-limit",
        ParentTurnId = "turn-1",
        ParentAgentId = "expert",
        ChildName = "child",
        ChildInstructions = "instructions",
        TaskInputJson = JsonSerializer.Serialize(
            new DelegatedAgentTaskInput("执行后台测试任务。", [], null),
            DelegatedAgentJsonContext.Default.DelegatedAgentTaskInput),
        ToolMountsJson = "[]"
    };

    private sealed class TestProviderDriver(BlockingChatClient chatClient) : AiProviderDriverBase
    {
        public override AiProviderDriverDefinition Definition { get; } =
            new("TestProvider", "Test Provider", true);

        public override IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model)
        {
            return chatClient;
        }

        public override ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent)
        {
            return CreateCommonOptions(agent);
        }
    }

    private sealed class BlockingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public async Task<bool> WaitEnteredAsync()
        {
            var completed = await Task.WhenAny(_entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            return completed == _entered.Task;
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory => Path.Combine(root, "workspace");
        public string UserDataDirectory => Path.Combine(root, "userdata");
        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

        [Obsolete("工作区插件目录已废弃，请使用 UserPluginsDirectory。")]
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
        public string BuildPluginConfigJson(string pluginId, PluginManifest manifest) => "{}";

        public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest) => "{}";
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient();
    }
}
