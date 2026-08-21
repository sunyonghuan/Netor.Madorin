using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Reflection;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Netor.Cortana.Entitys;
using Netor.Madorin.Plugin;
using Netor.Madorin.Plugin.Mcp;
using Netor.Madorin.Plugin.Native;

namespace Netor.Cortana.AI.Tests.Delegation;

internal static class PluginLoaderTestHarness
{
    private static readonly FieldInfo NativeHostsField =
        typeof(PluginLoader).GetField("_nativeHosts", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(PluginLoader), "_nativeHosts");

    private static readonly FieldInfo McpHostsField =
        typeof(PluginLoader).GetField("_mcpHosts", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(PluginLoader), "_mcpHosts");

    private static readonly FieldInfo PluginsField =
        typeof(ExternalProcessPluginHostBase).GetField("_plugins", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(ExternalProcessPluginHostBase), "_plugins");

    private static readonly FieldInfo McpClientField =
        typeof(McpServerHost).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(McpServerHost), "_client");

    private static readonly FieldInfo McpToolsField =
        typeof(McpServerHost).GetField("_tools", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(nameof(McpServerHost), "_tools");

    public static TestPlugin AddPlugin(
        PluginLoader pluginLoader,
        string root,
        string pluginId,
        params string[] toolNames)
    {
        var plugin = new TestPlugin(pluginId, toolNames);
        AddPlugin(pluginLoader, root, plugin);
        return plugin;
    }

    public static async Task<TestMcpServer> AddMcpServerAsync(
        PluginLoader pluginLoader,
        string mcpId,
        params string[] toolNames)
    {
        var server = await TestMcpServer.CreateAsync(pluginLoader, mcpId, toolNames);

        var mcpHosts = (ConcurrentDictionary<string, McpServerHost>?)McpHostsField.GetValue(pluginLoader)
            ?? throw new InvalidOperationException("无法获取测试插件加载器的 MCP 宿主集合。");
        mcpHosts[mcpId] = server.Host;

        return server;
    }

    private static void AddPlugin(PluginLoader pluginLoader, string root, IPlugin plugin)
    {
        var pluginDirectory = Path.Combine(root, "plugins", plugin.Id);
        Directory.CreateDirectory(pluginDirectory);

        var manifest = new PluginManifest
        {
            Id = plugin.Id,
            Name = plugin.Name,
            Version = plugin.Version.ToString(),
            Description = plugin.Description,
            Runtime = PluginRuntime.Native,
            LibraryName = "test-plugin.dll"
        };

        var host = new NativePluginHost(
            pluginDirectory,
            manifest,
            NullLogger<NativePluginHost>.Instance,
            new NoopPluginRuntimeConfigProvider());

        var plugins = (List<IPlugin>?)PluginsField.GetValue(host)
            ?? throw new InvalidOperationException("无法获取测试插件宿主的插件列表。");
        plugins.Add(plugin);

        var nativeHosts = (ConcurrentDictionary<string, NativePluginHost>?)NativeHostsField.GetValue(pluginLoader)
            ?? throw new InvalidOperationException("无法获取测试插件加载器的 Native 宿主集合。");
        nativeHosts[pluginDirectory] = host;
    }

    private static void InitializeMcpHost(McpServerHost host, McpClient client, IList<McpClientTool> tools)
    {
        McpClientField.SetValue(host, client);
        McpToolsField.SetValue(host, tools);
    }

    private static void RemoveMcpServer(PluginLoader pluginLoader, string mcpId)
    {
        var mcpHosts = (ConcurrentDictionary<string, McpServerHost>?)McpHostsField.GetValue(pluginLoader)
            ?? throw new InvalidOperationException("无法获取测试插件加载器的 MCP 宿主集合。");
        mcpHosts.TryRemove(mcpId, out _);
    }

    private sealed class NoopPluginRuntimeConfigProvider : IPluginRuntimeConfigProvider
    {
        public string BuildPluginConfigJson(string pluginId, PluginManifest manifest) => "{}";

        public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest) => "{}";
    }

    public sealed class TestMcpServer : IAsyncDisposable
    {
        private readonly PluginLoader _pluginLoader;
        private readonly CancellationTokenSource _cts;
        private readonly McpServer _server;
        private readonly Task _serverTask;
        private bool _disposed;

        private TestMcpServer(
            PluginLoader pluginLoader,
            string id,
            McpServerHost host,
            McpServer server,
            Task serverTask,
            CancellationTokenSource cts)
        {
            _pluginLoader = pluginLoader;
            Id = id;
            Host = host;
            _server = server;
            _serverTask = serverTask;
            _cts = cts;
        }

        public string Id { get; }

        public McpServerHost Host { get; }

        public static async Task<TestMcpServer> CreateAsync(
            PluginLoader pluginLoader,
            string id,
            IReadOnlyList<string> toolNames)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var loggerFactory = NullLoggerFactory.Instance;

            var serverTransport = new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                $"Test MCP {id}",
                loggerFactory);

            var toolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.OrdinalIgnoreCase);
            foreach (var toolName in toolNames)
            {
                toolCollection.Add(McpServerTool.Create(
                    (Func<string>)(() => "ok"),
                    new McpServerToolCreateOptions
                    {
                        Name = toolName,
                        Description = $"测试 MCP 工具 {toolName}。"
                    }));
            }

            var server = McpServer.Create(
                serverTransport,
                new McpServerOptions
                {
                    ServerInfo = new Implementation
                    {
                        Name = $"Test MCP {id}",
                        Version = "1.0.0"
                    },
                    Capabilities = new ServerCapabilities
                    {
                        Tools = new ToolsCapability()
                    },
                    ToolCollection = toolCollection
                },
                loggerFactory,
                serviceProvider: null);
            var serverTask = Task.Run(() => server.RunAsync(cts.Token), CancellationToken.None);

            var clientTransport = new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                loggerFactory);
            var client = await McpClient.CreateAsync(
                clientTransport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = "Netor.Cortana.Tests",
                        Version = "1.0.0"
                    }
                },
                loggerFactory,
                cts.Token);
            var tools = await client.ListToolsAsync(cancellationToken: cts.Token);

            var host = new McpServerHost(
                new McpServerEntity
                {
                    Id = id,
                    Name = $"Test MCP {id}",
                    TransportType = "stdio",
                    Description = "用于验证后台子智能体 MCP 工具挂载。"
                },
                loggerFactory);
            InitializeMcpHost(host, client, tools.ToList());

            return new TestMcpServer(pluginLoader, id, host, server, serverTask, cts);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RemoveMcpServer(_pluginLoader, Id);

            try
            {
                _cts.Cancel();
            }
            catch
            {
            }

            await Host.DisposeAsync();
            await _server.DisposeAsync();

            try
            {
                await Task.WhenAny(_serverTask, Task.Delay(TimeSpan.FromSeconds(1)));
            }
            catch
            {
            }

            _cts.Dispose();
        }
    }
}

internal sealed class TestPlugin : IPlugin
{
    public TestPlugin(string id, params string[] toolNames)
    {
        Id = id;
        Name = $"Test Plugin {id}";
        Tools = toolNames
            .Select(static toolName => AIFunctionFactory.Create(
                name: toolName,
                method: () => "ok",
                description: $"测试插件工具 {toolName}。"))
            .ToArray();
    }

    public string Id { get; }

    public string Name { get; }

    public Version Version { get; } = new(1, 0, 0);

    public string Description => "用于验证后台子智能体插件工具挂载。";

    public string? Instructions => "这是测试插件指令。";

    public IReadOnlyList<string> Tags => [];

    public IReadOnlyList<string> Capabilities => [];

    public IReadOnlyList<AITool> Tools { get; }

    public Task InitializeAsync(IPluginContext context) => Task.CompletedTask;
}
