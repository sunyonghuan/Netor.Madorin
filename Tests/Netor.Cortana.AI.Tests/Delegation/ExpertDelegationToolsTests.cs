using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Delegation;
using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Handoff;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Madorin.Plugin;
using Netor.Madorin.Plugin.BuiltIn.FileBrowser;
using Netor.Madorin.Plugin.BuiltIn.PowerShell;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.Delegation;

[TestClass]
public sealed class ExpertDelegationToolsTests
{
    private static readonly FieldInfo RunningField =
        typeof(DelegatedAgentJobExecutor).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(DelegatedAgentJobExecutor), "_running");

    private string _root = null!;
    private CortanaDbContext _db = null!;
    private ServiceProvider _services = null!;
    private RecordingChatClient _chatClient = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-expert-delegation-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _db = new CortanaDbContext(Path.Combine(_root, "delegation-tools.db"));
        var paths = new TestAppPaths(_root);
        _chatClient = new RecordingChatClient();

        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .AddSingleton<IAppPaths>(paths)
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
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [TestMethod]
    public void CreateTools_ReturnsExpectedToolNames()
    {
        var toolCatalog = new SystemToolCatalogService([], _services.GetRequiredService<PluginLoader>());
        var agentFactory = new AIAgentFactory(
            _services.GetRequiredService<IAppPaths>(),
            builtInProviders: [],
            _services.GetRequiredService<PluginLoader>(),
            new AiProviderDriverRegistry([new TestProviderDriver()]),
            _services,
            NullLogger<AIAgentFactory>.Instance);
        var executor = new DelegatedAgentJobExecutor(
            agentFactory,
            new AiProviderService(_db),
            new AiModelService(_db),
            new DelegatedAgentJobService(_db),
            toolCatalog,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var tools = new ExpertDelegationTools(toolCatalog, executor);

        var result = tools.CreateTools(
            new AgentEntity { Id = "expert", Name = "expert" },
            new HandoffRuntimeContext("session-1", "workspace-1", "expert", null, null));

        CollectionAssert.AreEquivalent(
            new[]
            {
                "list_system_tool_catalog",
                "start_autonomous_subagent_task",
                "get_subagent_task_status",
                "read_subagent_task_result",
                "cancel_subagent_task"
            },
            result.Select(static tool => tool.Name).ToArray());
    }

    [TestMethod]
    public async Task DelegationTools_StartStatusReadResult_UsesMountedToolWhitelist()
    {
        var paths = _services.GetRequiredService<IAppPaths>();
        Directory.CreateDirectory(paths.WorkspaceDirectory);

        var builtInProviders = new Microsoft.Agents.AI.AIContextProvider[]
        {
            new FileBrowserProvider(
                NullLogger<FileBrowserProvider>.Instance,
                new FileBrowser(
                    paths,
                    NullLogger<FileBrowser>.Instance)),
            new FileOperationProvider(
                NullLogger<FileOperationProvider>.Instance,
                new FileOperator(
                    paths,
                    NullLogger<FileOperator>.Instance))
        };

        var toolCatalog = new SystemToolCatalogService(
            builtInProviders,
            _services.GetRequiredService<PluginLoader>());
        var providerService = new AiProviderService(_db);
        var modelService = new AiModelService(_db);
        var jobService = new DelegatedAgentJobService(_db);
        var agentFactory = new AIAgentFactory(
            paths,
            builtInProviders,
            _services.GetRequiredService<PluginLoader>(),
            new AiProviderDriverRegistry([new TestProviderDriver(_chatClient)]),
            _services,
            NullLogger<AIAgentFactory>.Instance);
        SeedProviderAndModel(providerService, modelService);

        var executor = new DelegatedAgentJobExecutor(
            agentFactory,
            providerService,
            modelService,
            jobService,
            toolCatalog,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var tools = new ExpertDelegationTools(toolCatalog, executor)
            .CreateTools(
                new AgentEntity { Id = "expert", Name = "expert" },
                new HandoffRuntimeContext("session-1", "workspace-1", "expert", null, null))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var catalogJson = (await tools["list_system_tool_catalog"].InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None))?.ToString() ?? string.Empty;

        StringAssert.Contains(catalogJson, "sys_read_file");
        StringAssert.Contains(catalogJson, "sys_write_file");

        var startJson = (await tools["start_autonomous_subagent_task"].InvokeAsync(
            new AIFunctionArguments
            {
                ["childName"] = "reader",
                ["childInstructions"] = "读取文件并汇报。",
                ["task"] = "读取 README。",
                ["toolMountsJson"] = "[\"sys_read_file\"]",
                ["providerId"] = "test-provider",
                ["modelId"] = "test-model",
                ["attachmentPathsJson"] = "[]",
                ["outputContract"] = "输出一句话。"
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        StringAssert.Contains(startJson, "chatjob_");
        var jobId = ReadJsonString(startJson, "jobId");
        Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));

        var completed = await WaitUntilCompletedAsync(
            jobId,
            tools["get_subagent_task_status"],
            CancellationToken.None);
        Assert.IsTrue(completed);

        var resultJson = (await tools["read_subagent_task_result"].InvokeAsync(
            new AIFunctionArguments
            {
                ["jobId"] = jobId
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        Assert.AreEqual("子任务完成", ReadJsonString(resultJson, "result"));
        CollectionAssert.AreEqual(
            new[] { "sys_read_file" },
            _chatClient.LastToolNames);

        var cancelJson = (await tools["cancel_subagent_task"].InvokeAsync(
            new AIFunctionArguments
            {
                ["jobId"] = jobId
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        StringAssert.Contains(cancelJson, "not_changed");
    }

    [TestMethod]
    public async Task DelegationTools_StartStatusReadResult_UsesMountedPowerShellTool()
    {
        var paths = _services.GetRequiredService<IAppPaths>();
        Directory.CreateDirectory(paths.WorkspaceDirectory);

        var builtInProviders = new Microsoft.Agents.AI.AIContextProvider[]
        {
            new FileBrowserProvider(
                NullLogger<FileBrowserProvider>.Instance,
                new FileBrowser(
                    paths,
                    NullLogger<FileBrowser>.Instance)),
            new FileOperationProvider(
                NullLogger<FileOperationProvider>.Instance,
                new FileOperator(
                    paths,
                    NullLogger<FileOperator>.Instance)),
            new PowerShellProvider(
                new PowerShellExecutor(NullLogger<PowerShellExecutor>.Instance),
                NullLogger<PowerShellProvider>.Instance,
                new SessionRegistry(NullLogger<SessionRegistry>.Instance))
        };

        var toolCatalog = new SystemToolCatalogService(
            builtInProviders,
            _services.GetRequiredService<PluginLoader>());
        var providerService = new AiProviderService(_db);
        var modelService = new AiModelService(_db);
        var jobService = new DelegatedAgentJobService(_db);
        var agentFactory = new AIAgentFactory(
            paths,
            builtInProviders,
            _services.GetRequiredService<PluginLoader>(),
            new AiProviderDriverRegistry([new TestProviderDriver(_chatClient)]),
            _services,
            NullLogger<AIAgentFactory>.Instance);
        SeedProviderAndModel(providerService, modelService);

        var executor = new DelegatedAgentJobExecutor(
            agentFactory,
            providerService,
            modelService,
            jobService,
            toolCatalog,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var tools = new ExpertDelegationTools(toolCatalog, executor)
            .CreateTools(
                new AgentEntity { Id = "expert", Name = "expert" },
                new HandoffRuntimeContext("session-2", "workspace-1", "expert", null, null))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var catalogJson = (await tools["list_system_tool_catalog"].InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None))?.ToString() ?? string.Empty;

        StringAssert.Contains(catalogJson, "sys_execute_powershell");

        var startJson = (await tools["start_autonomous_subagent_task"].InvokeAsync(
            new AIFunctionArguments
            {
                ["childName"] = "powershell",
                ["childInstructions"] = "执行 PowerShell 命令并汇报。",
                ["task"] = "打印当前目录。",
                ["toolMountsJson"] = "[\"sys_execute_powershell\"]",
                ["providerId"] = "test-provider",
                ["modelId"] = "test-model",
                ["attachmentPathsJson"] = "[]",
                ["outputContract"] = "输出一句话。"
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        var jobId = ReadJsonString(startJson, "jobId");
        Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));

        var completed = await WaitUntilCompletedAsync(
            jobId,
            tools["get_subagent_task_status"],
            CancellationToken.None);
        Assert.IsTrue(completed);

        var resultJson = (await tools["read_subagent_task_result"].InvokeAsync(
            new AIFunctionArguments
            {
                ["jobId"] = jobId
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        Assert.AreEqual("子任务完成", ReadJsonString(resultJson, "result"));
        CollectionAssert.AreEqual(
            new[] { "sys_execute_powershell" },
            _chatClient.LastToolNames);
    }

    [TestMethod]
    public async Task DelegationTools_StartStatusReadResult_UsesMountedPluginTool()
    {
        var paths = _services.GetRequiredService<IAppPaths>();
        Directory.CreateDirectory(paths.WorkspaceDirectory);

        var pluginLoader = _services.GetRequiredService<PluginLoader>();
        PluginLoaderTestHarness.AddPlugin(
            pluginLoader,
            _root,
            "test.plugin.delegation",
            "plugin_delegate_report",
            "plugin_delegate_private");

        var builtInProviders = new Microsoft.Agents.AI.AIContextProvider[]
        {
            new FileBrowserProvider(
                NullLogger<FileBrowserProvider>.Instance,
                new FileBrowser(
                    paths,
                    NullLogger<FileBrowser>.Instance)),
            new FileOperationProvider(
                NullLogger<FileOperationProvider>.Instance,
                new FileOperator(
                    paths,
                    NullLogger<FileOperator>.Instance)),
            new PowerShellProvider(
                new PowerShellExecutor(NullLogger<PowerShellExecutor>.Instance),
                NullLogger<PowerShellProvider>.Instance,
                new SessionRegistry(NullLogger<SessionRegistry>.Instance))
        };

        var toolCatalog = new SystemToolCatalogService(
            builtInProviders,
            pluginLoader);
        var providerService = new AiProviderService(_db);
        var modelService = new AiModelService(_db);
        var jobService = new DelegatedAgentJobService(_db);
        var agentFactory = new AIAgentFactory(
            paths,
            builtInProviders,
            pluginLoader,
            new AiProviderDriverRegistry([new TestProviderDriver(_chatClient)]),
            _services,
            NullLogger<AIAgentFactory>.Instance);
        SeedProviderAndModel(providerService, modelService);

        var executor = new DelegatedAgentJobExecutor(
            agentFactory,
            providerService,
            modelService,
            jobService,
            toolCatalog,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var tools = new ExpertDelegationTools(toolCatalog, executor)
            .CreateTools(
                new AgentEntity { Id = "expert", Name = "expert" },
                new HandoffRuntimeContext("session-3", "workspace-1", "expert", null, null))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var catalogJson = (await tools["list_system_tool_catalog"].InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None))?.ToString() ?? string.Empty;

        StringAssert.Contains(catalogJson, "plugin_delegate_report");
        StringAssert.Contains(catalogJson, "plugin_delegate_private");

        var startJson = (await tools["start_autonomous_subagent_task"].InvokeAsync(
            new AIFunctionArguments
            {
                ["childName"] = "plugin-worker",
                ["childInstructions"] = "调用插件工具生成报告。",
                ["task"] = "生成插件报告。",
                ["toolMountsJson"] = "[\"plugin_delegate_report\"]",
                ["providerId"] = "test-provider",
                ["modelId"] = "test-model",
                ["attachmentPathsJson"] = "[]",
                ["outputContract"] = "输出一句话。"
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        var jobId = ReadJsonString(startJson, "jobId");
        Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));

        var completed = await WaitUntilCompletedAsync(
            jobId,
            tools["get_subagent_task_status"],
            CancellationToken.None);
        Assert.IsTrue(completed);

        var resultJson = (await tools["read_subagent_task_result"].InvokeAsync(
            new AIFunctionArguments
            {
                ["jobId"] = jobId
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        Assert.AreEqual("子任务完成", ReadJsonString(resultJson, "result"));
        CollectionAssert.AreEqual(
            new[] { "plugin_delegate_report" },
            _chatClient.LastToolNames);
    }

    [TestMethod]
    public async Task DelegationTools_StartStatusReadResult_UsesMountedMcpTool()
    {
        var paths = _services.GetRequiredService<IAppPaths>();
        Directory.CreateDirectory(paths.WorkspaceDirectory);

        var pluginLoader = _services.GetRequiredService<PluginLoader>();
        await using var mcpServer = await PluginLoaderTestHarness.AddMcpServerAsync(
            pluginLoader,
            "test.mcp.delegation",
            "mcp_delegate_report",
            "mcp_delegate_private");

        var builtInProviders = new Microsoft.Agents.AI.AIContextProvider[]
        {
            new FileBrowserProvider(
                NullLogger<FileBrowserProvider>.Instance,
                new FileBrowser(
                    paths,
                    NullLogger<FileBrowser>.Instance)),
            new FileOperationProvider(
                NullLogger<FileOperationProvider>.Instance,
                new FileOperator(
                    paths,
                    NullLogger<FileOperator>.Instance))
        };

        var toolCatalog = new SystemToolCatalogService(
            builtInProviders,
            pluginLoader);
        var providerService = new AiProviderService(_db);
        var modelService = new AiModelService(_db);
        var jobService = new DelegatedAgentJobService(_db);
        var agentFactory = new AIAgentFactory(
            paths,
            builtInProviders,
            pluginLoader,
            new AiProviderDriverRegistry([new TestProviderDriver(_chatClient)]),
            _services,
            NullLogger<AIAgentFactory>.Instance);
        SeedProviderAndModel(providerService, modelService);

        var executor = new DelegatedAgentJobExecutor(
            agentFactory,
            providerService,
            modelService,
            jobService,
            toolCatalog,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var tools = new ExpertDelegationTools(toolCatalog, executor)
            .CreateTools(
                new AgentEntity { Id = "expert", Name = "expert" },
                new HandoffRuntimeContext("session-mcp", "workspace-1", "expert", null, null))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var catalogJson = (await tools["list_system_tool_catalog"].InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None))?.ToString() ?? string.Empty;

        StringAssert.Contains(catalogJson, "mcp_delegate_report");
        StringAssert.Contains(catalogJson, "mcp_delegate_private");

        var startJson = (await tools["start_autonomous_subagent_task"].InvokeAsync(
            new AIFunctionArguments
            {
                ["childName"] = "mcp-worker",
                ["childInstructions"] = "调用 MCP 工具生成报告。",
                ["task"] = "生成 MCP 报告。",
                ["toolMountsJson"] = "[\"mcp_delegate_report\"]",
                ["providerId"] = "test-provider",
                ["modelId"] = "test-model",
                ["attachmentPathsJson"] = "[]",
                ["outputContract"] = "输出一句话。"
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        var jobId = ReadJsonString(startJson, "jobId");
        Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));

        var completed = await WaitUntilCompletedAsync(
            jobId,
            tools["get_subagent_task_status"],
            CancellationToken.None);
        Assert.IsTrue(completed);

        var resultJson = (await tools["read_subagent_task_result"].InvokeAsync(
            new AIFunctionArguments
            {
                ["jobId"] = jobId
            },
            CancellationToken.None))?.ToString() ?? string.Empty;

        Assert.AreEqual("子任务完成", ReadJsonString(resultJson, "result"));
        CollectionAssert.AreEqual(
            new[] { "mcp_delegate_report" },
            _chatClient.LastToolNames);
    }

    [TestMethod]
    public void DelegationTools_StatusAndResult_WhenJobMissing_ReturnsJsonError()
    {
        var toolCatalog = new SystemToolCatalogService([], _services.GetRequiredService<PluginLoader>());
        var jobService = new DelegatedAgentJobService(_db);
        var executor = new DelegatedAgentJobExecutor(
            agentFactory: null!,
            providerService: null!,
            modelService: null!,
            jobService,
            toolCatalog,
            NullLogger<DelegatedAgentJobExecutor>.Instance);
        var tools = new ExpertDelegationTools(toolCatalog, executor)
            .CreateTools(
                new AgentEntity { Id = "expert", Name = "expert" },
                new HandoffRuntimeContext("session-missing", "workspace-1", "expert", null, null))
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        var statusJson = tools["get_subagent_task_status"].InvokeAsync(
            new AIFunctionArguments { ["jobId"] = "missing-job" },
            CancellationToken.None).GetAwaiter().GetResult()?.ToString() ?? string.Empty;
        var resultJson = tools["read_subagent_task_result"].InvokeAsync(
            new AIFunctionArguments { ["jobId"] = "missing-job" },
            CancellationToken.None).GetAwaiter().GetResult()?.ToString() ?? string.Empty;

        Assert.AreEqual("后台委派任务不存在：missing-job", ReadJsonString(statusJson, "error"));
        Assert.AreEqual("后台委派任务不存在：missing-job", ReadJsonString(resultJson, "error"));
    }

    [TestMethod]
    public async Task DelegationTools_Start_WhenExecutorRejects_ReturnsJsonError()
    {
        var paths = _services.GetRequiredService<IAppPaths>();
        Directory.CreateDirectory(paths.WorkspaceDirectory);

        var builtInProviders = new Microsoft.Agents.AI.AIContextProvider[]
        {
            new FileOperationProvider(
                NullLogger<FileOperationProvider>.Instance,
                new FileOperator(
                    paths,
                    NullLogger<FileOperator>.Instance))
        };

        var toolCatalog = new SystemToolCatalogService(builtInProviders, _services.GetRequiredService<PluginLoader>());
        var executor = new DelegatedAgentJobExecutor(
            agentFactory: null!,
            providerService: null!,
            modelService: null!,
            new DelegatedAgentJobService(_db),
            toolCatalog,
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

            var tools = new ExpertDelegationTools(toolCatalog, executor)
                .CreateTools(
                    new AgentEntity { Id = "expert", Name = "expert" },
                    new HandoffRuntimeContext("session-start-error", "workspace-1", "expert", null, null))
                .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

            var startJson = (await tools["start_autonomous_subagent_task"].InvokeAsync(
                new AIFunctionArguments
                {
                    ["childName"] = "child",
                    ["childInstructions"] = "执行后台任务。",
                    ["task"] = "执行后台任务。",
                    ["toolMountsJson"] = "[\"sys_write_file\"]"
                },
                CancellationToken.None))?.ToString() ?? string.Empty;

            Assert.AreEqual("后台委派任务数量已达到上限：50", ReadJsonString(startJson, "error"));
        }
        finally
        {
            foreach (var tokenSource in runningTokens)
            {
                tokenSource.Dispose();
            }
        }
    }

    private static ConcurrentDictionary<string, CancellationTokenSource> GetRunningJobs(
        DelegatedAgentJobExecutor executor)
    {
        return (ConcurrentDictionary<string, CancellationTokenSource>?)RunningField.GetValue(executor)
            ?? throw new InvalidOperationException("无法获取后台委派任务运行集合。");
    }

    private static async Task<bool> WaitUntilCompletedAsync(
        string jobId,
        AIFunction statusTool,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            var statusJson = (await statusTool.InvokeAsync(
                new AIFunctionArguments
                {
                    ["jobId"] = jobId
                },
                cancellationToken))?.ToString() ?? string.Empty;
            var state = ReadJsonString(statusJson, "state");
            if (state == DelegatedAgentJobStates.Completed)
            {
                return true;
            }

            if (state == DelegatedAgentJobStates.Failed)
            {
                Assert.Fail(statusJson);
            }

            await Task.Delay(50, cancellationToken);
        }

        return false;
    }

    private static string ReadJsonString(string json, string propertyName)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static void SeedProviderAndModel(
        AiProviderService providerService,
        AiModelService modelService)
    {
        providerService.Add(new AiProviderEntity
        {
            Id = "test-provider",
            Name = "Test Provider",
            ProviderType = "TestProvider",
            IsDefault = true,
            IsEnabled = true
        });

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

    private sealed class TestProviderDriver(RecordingChatClient chatClient) : AiProviderDriverBase
    {
        public TestProviderDriver()
            : this(new RecordingChatClient())
        {
        }

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

    private sealed class RecordingChatClient : IChatClient
    {
        public string[] LastToolNames { get; private set; } = [];

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastToolNames = options?.Tools?
                .Select(static tool => tool.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .ToArray() ?? [];

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "子任务完成")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastToolNames = options?.Tools?
                .Select(static tool => tool.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .ToArray() ?? [];

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "子任务完成");
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
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
}
