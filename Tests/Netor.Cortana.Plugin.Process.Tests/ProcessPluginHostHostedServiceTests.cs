using System.Text.Json;

using Netor.Cortana.Plugin.Process.Debugging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Plugin.Process.Hosting;
using Netor.Cortana.Plugin.Process.Protocol;
using Netor.Cortana.Plugin.Process.Settings;

namespace Netor.Cortana.Plugin.Process.Tests;

[TestClass]
public sealed class ProcessPluginHostHostedServiceTests
{
    [TestMethod]
    public async Task RunAsync_InitThenDestroy_StartsAndStopsHostedServices()
    {
        var state = new HostedServiceProbeState();
        using var input = new StringReader(string.Join(Environment.NewLine,
            CreateInitRequest(),
            CreateRequest("destroy")));
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>(),
            (services, settings) => ConfigureHostedService(services, settings, state),
            _ => { },
            input,
            output);

        Assert.AreEqual(1, state.ConfigureCount);
        Assert.AreEqual("data", state.DataDirectoryAtConfigure);
        Assert.AreEqual(1, state.StartCount);
        Assert.AreEqual(1, state.StopCount);
        Assert.AreEqual("data", state.DataDirectoryAtStart);
        Assert.IsTrue(state.WasStartedBeforeStop);

        var responses = ReadResponses(output);
        Assert.HasCount(2, responses);
        Assert.IsTrue(responses[0].Success);
        Assert.IsTrue(responses[1].Success);
    }

    [TestMethod]
    public async Task RunAsync_WhenInputClosesAfterInit_StopsHostedServices()
    {
        var state = new HostedServiceProbeState();
        using var input = new StringReader(CreateInitRequest());
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>(),
            (services, settings) => ConfigureHostedService(services, settings, state),
            _ => { },
            input,
            output);

        Assert.AreEqual(1, state.ConfigureCount);
        Assert.AreEqual(1, state.StartCount);
        Assert.AreEqual(1, state.StopCount);
        Assert.IsTrue(state.WasStartedBeforeStop);

        var responses = ReadResponses(output);
        Assert.HasCount(1, responses);
        Assert.IsTrue(responses[0].Success);
    }

    [TestMethod]
    public async Task RunAsync_ProvidesHostApplicationLifetime_AndSignalsLifecycle()
    {
        var state = new HostedServiceProbeState();
        using var input = new StringReader(string.Join(Environment.NewLine,
            CreateInitRequest(),
            CreateRequest("destroy")));
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>(),
            (services, _) =>
            {
                services.AddSingleton(state);
                services.AddSingleton<IHostedService, LifetimeProbe>();
            },
            _ => { },
            input,
            output);

        Assert.IsTrue(state.LifetimeResolved);
        Assert.IsFalse(state.StartedWasSignaledDuringStart);
        Assert.IsTrue(state.StartedSignaled);
        Assert.IsTrue(state.StoppingWasSignaledDuringStop);
        Assert.IsTrue(state.StoppingSignaled);
        Assert.IsTrue(state.StoppedSignaled);

        var responses = ReadResponses(output);
        Assert.HasCount(2, responses);
        Assert.IsTrue(responses[0].Success);
        Assert.IsTrue(responses[1].Success);
    }

    [TestMethod]
    public async Task RunAsync_UsesHostedLifecycleServiceOrder()
    {
        var state = new HostedServiceProbeState();
        using var input = new StringReader(string.Join(Environment.NewLine,
            CreateInitRequest(),
            CreateRequest("destroy")));
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>(),
            (services, _) =>
            {
                services.AddSingleton(state);
                services.AddSingleton<IHostedService, LifecycleProbe>();
            },
            _ => { },
            input,
            output);

        CollectionAssert.AreEqual(new[]
        {
            "starting",
            "start",
            "started",
            "application-started",
            "application-stopping",
            "stopping",
            "stop",
            "stopped",
            "application-stopped"
        }, state.Events);

        var responses = ReadResponses(output);
        Assert.HasCount(2, responses);
        Assert.IsTrue(responses[0].Success);
        Assert.IsTrue(responses[1].Success);
    }

    [TestMethod]
    public async Task RunAsync_ApplicationStartedCallbackCanUseLoggerFactoryAcrossRepeatedRuns()
    {
        var state = new HostedServiceProbeState();

        for (var i = 0; i < 3; i++)
        {
            using var input = new StringReader(string.Join(Environment.NewLine,
                CreateInitRequest(),
                CreateRequest("destroy")));
            using var output = new StringWriter();

            await ProcessPluginHost.RunAsync(
                CreatePluginInfo(),
                new Dictionary<string, ToolInvoker>(),
                (services, _) =>
                {
                    services.AddSingleton(state);
                    services.AddSingleton<IHostedService, LoggerFactoryStartedProbe>();
                },
                _ => { },
                input,
                output);

            var responses = ReadResponses(output);
            Assert.HasCount(2, responses);
            Assert.IsTrue(responses[0].Success);
            Assert.IsTrue(responses[1].Success);
        }

        Assert.AreEqual(3, state.LoggerFactoryCallbackCount);
        Assert.AreEqual(0, state.LoggerFactoryCallbackFailureCount);
    }

    [TestMethod]
    public async Task PluginDebugger_CreateInitDisposeRepeatedly_DoesNotReuseDisposedLoggerFactory()
    {
        var state = new HostedServiceProbeState();

        for (var i = 0; i < 3; i++)
        {
            await using var debugger = new ProbeDebugger(state);
            await debugger.InitAsync().ConfigureAwait(false);
        }

        Assert.AreEqual(3, state.LoggerFactoryCallbackCount);
        Assert.AreEqual(0, state.LoggerFactoryCallbackFailureCount);
    }

    [TestMethod]
    public async Task RunAsync_WhenInitializationFails_LogsFailureWithInitLogger()
    {
        var state = new HostedServiceProbeState();
        using var input = new StringReader(CreateInitRequest());
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>(),
            (services, _) =>
            {
                services.AddSingleton(state);
                services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(state));
                services.AddSingleton<IHostedService, FailingHostedService>();
            },
            _ => { },
            input,
            output);

        Assert.AreEqual("ProcessPluginHost.Init", state.InitFailureLoggerName);
        Assert.AreEqual("插件初始化失败", state.InitFailureMessage);
        Assert.IsInstanceOfType<InvalidOperationException>(state.InitFailureException);

        var responses = ReadResponses(output);
        Assert.HasCount(1, responses);
        Assert.IsFalse(responses[0].Success);
        StringAssert.Contains(responses[0].Error, nameof(InvalidOperationException));
        StringAssert.Contains(responses[0].Error, "init boom");
    }

    [TestMethod]
    public async Task RunAsync_ProvidesProcessPluginRuntimeContext()
    {
        var state = new HostedServiceProbeState();
        using var input = new StringReader(string.Join(Environment.NewLine,
            CreateInitRequest(),
            CreateRequest("destroy")));
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>(),
            (services, _) =>
            {
                services.AddSingleton(state);
                services.AddSingleton<IHostedService, RuntimeContextProbe>();
            },
            _ => { },
            input,
            output);

        Assert.IsFalse(string.IsNullOrWhiteSpace(state.RuntimeContextInstanceId));
        Assert.AreEqual(32, state.RuntimeContextInstanceId!.Length);
        Assert.AreEqual("data", state.RuntimeContextDataDirectory);
        Assert.IsTrue(state.RuntimeContextSettingsIsSameInstance);

        var responses = ReadResponses(output);
        Assert.HasCount(2, responses);
        Assert.IsTrue(responses[0].Success);
        Assert.IsTrue(responses[1].Success);
    }

    [TestMethod]
    public async Task RunAsync_InvokeBeforeInit_ReturnsNotInitializedFailure()
    {
        using var input = new StringReader(CreateRequest("invoke", "{}", "test.echo"));
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>
            {
                ["test.echo"] = (_, _) => ValueTask.FromResult("ok")
            },
            null,
            _ => { },
            input,
            output);

        var responses = ReadResponses(output);
        Assert.HasCount(1, responses);
        Assert.IsFalse(responses[0].Success);
        Assert.AreEqual("插件尚未初始化，请先调用 init", responses[0].Error);
    }

    [TestMethod]
    public async Task RunAsync_GetInfoBeforeInit_DoesNotConfigureServices()
    {
        var state = new HostedServiceProbeState();
        using var input = new StringReader(string.Join(Environment.NewLine,
            CreateRequest("get_info"),
            CreateRequest("destroy")));
        using var output = new StringWriter();

        await ProcessPluginHost.RunAsync(
            CreatePluginInfo(),
            new Dictionary<string, ToolInvoker>(),
            (services, settings) => ConfigureHostedService(services, settings, state),
            _ => { },
            input,
            output);

        Assert.AreEqual(0, state.ConfigureCount);
        Assert.AreEqual(0, state.StartCount);
        Assert.AreEqual(0, state.StopCount);

        var responses = ReadResponses(output);
        Assert.HasCount(2, responses);
        Assert.IsTrue(responses[0].Success);
        Assert.IsTrue(responses[1].Success);
    }

    private static void ConfigureHostedService(
        IServiceCollection services,
        PluginSettings settings,
        HostedServiceProbeState state)
    {
        state.ConfigureCount++;
        state.DataDirectoryAtConfigure = settings.DataDirectory;
        services.AddSingleton(state);
        services.AddSingleton<IHostedService, HostedServiceProbe>();
    }

    private static PluginInfoData CreatePluginInfo()
        => new()
        {
            Id = "test.plugin",
            Name = "Test Plugin",
            Version = "1.0.0",
            Description = "Test plugin",
            Tools = []
        };

    private static string CreateInitRequest()
    {
        var args = JsonSerializer.Serialize(
            new InitConfig
            {
                DataDirectory = "data",
                WorkspaceDirectory = "workspace",
                PluginDirectory = "plugin",
                WsPort = 12841
            },
            ProcessProtocolJsonContext.Default.InitConfig);

        return CreateRequest("init", args);
    }

    private static string CreateRequest(string method, string? args = null, string? toolName = null)
        => JsonSerializer.Serialize(
            new HostRequest { Method = method, Args = args, ToolName = toolName },
            ProcessProtocolJsonContext.Default.HostRequest);

    private static List<HostResponse> ReadResponses(StringWriter output)
        => output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize(line, ProcessProtocolJsonContext.Default.HostResponse))
            .Select(response => response ?? throw new InvalidOperationException("响应反序列化失败。"))
            .ToList();

    private sealed class ProbeDebugger : PluginDebugger
    {
        public ProbeDebugger(HostedServiceProbeState state)
            : base((input, output) => ProcessPluginHost.RunAsync(
                CreatePluginInfo(),
                new Dictionary<string, ToolInvoker>(),
                (services, _) =>
                {
                    services.AddSingleton(state);
                    services.AddSingleton<IHostedService, LoggerFactoryStartedProbe>();
                },
                _ => { },
                input,
                output))
        {
        }
    }

    private sealed class HostedServiceProbe(HostedServiceProbeState state, PluginSettings settings) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            state.StartCount++;
            state.DataDirectoryAtStart = settings.DataDirectory;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            state.StopCount++;
            state.WasStartedBeforeStop = state.StartCount > 0;
            return Task.CompletedTask;
        }
    }

    private sealed class LifetimeProbe(
        HostedServiceProbeState state,
        IHostApplicationLifetime applicationLifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            state.LifetimeResolved = true;
            state.StartedWasSignaledDuringStart = applicationLifetime.ApplicationStarted.IsCancellationRequested;
            applicationLifetime.ApplicationStarted.Register(static value =>
                ((HostedServiceProbeState)value!).StartedSignaled = true, state);
            applicationLifetime.ApplicationStopping.Register(static value =>
                ((HostedServiceProbeState)value!).StoppingSignaled = true, state);
            applicationLifetime.ApplicationStopped.Register(static value =>
                ((HostedServiceProbeState)value!).StoppedSignaled = true, state);

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            state.StoppingWasSignaledDuringStop = applicationLifetime.ApplicationStopping.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleProbe(
        HostedServiceProbeState state,
        IHostApplicationLifetime applicationLifetime) : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken)
        {
            state.Events.Add("starting");
            applicationLifetime.ApplicationStarted.Register(static value =>
                ((HostedServiceProbeState)value!).Events.Add("application-started"), state);
            applicationLifetime.ApplicationStopping.Register(static value =>
                ((HostedServiceProbeState)value!).Events.Add("application-stopping"), state);
            applicationLifetime.ApplicationStopped.Register(static value =>
                ((HostedServiceProbeState)value!).Events.Add("application-stopped"), state);
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            state.Events.Add("start");
            return Task.CompletedTask;
        }

        public Task StartedAsync(CancellationToken cancellationToken)
        {
            state.Events.Add("started");
            return Task.CompletedTask;
        }

        public Task StoppingAsync(CancellationToken cancellationToken)
        {
            state.Events.Add("stopping");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            state.Events.Add("stop");
            return Task.CompletedTask;
        }

        public Task StoppedAsync(CancellationToken cancellationToken)
        {
            state.Events.Add("stopped");
            return Task.CompletedTask;
        }
    }

    private sealed class LoggerFactoryStartedProbe(
        HostedServiceProbeState state,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            applicationLifetime.ApplicationStarted.Register(static value =>
            {
                var (probeState, factory) = ((HostedServiceProbeState, ILoggerFactory))value!;
                try
                {
                    factory.CreateLogger("QuartzLikeProbe").LogInformation("ApplicationStarted callback completed.");
                    probeState.LoggerFactoryCallbackCount++;
                }
                catch (ObjectDisposedException)
                {
                    probeState.LoggerFactoryCallbackFailureCount++;
                }
            }, (state, loggerFactory));

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FailingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("init boom");

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RuntimeContextProbe(
        HostedServiceProbeState state,
        ProcessPluginRuntimeContext context,
        PluginSettings settings) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            state.RuntimeContextInstanceId = context.InstanceId;
            state.RuntimeContextDataDirectory = context.Settings.DataDirectory;
            state.RuntimeContextSettingsIsSameInstance = ReferenceEquals(context.Settings, settings);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider(HostedServiceProbeState state) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(state, categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly HostedServiceProbeState _probeState;
        private readonly string _categoryName;

        public CapturingLogger(HostedServiceProbeState probeState, string categoryName)
        {
            _probeState = probeState;
            _categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState scopeState)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (_categoryName != "ProcessPluginHost.Init" || logLevel != LogLevel.Error)
            {
                return;
            }

            _probeState.InitFailureLoggerName = _categoryName;
            _probeState.InitFailureMessage = formatter(state, exception);
            _probeState.InitFailureException = exception;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class HostedServiceProbeState
    {
        public int ConfigureCount { get; set; }

        public string? DataDirectoryAtConfigure { get; set; }

        public int StartCount { get; set; }

        public int StopCount { get; set; }

        public string? DataDirectoryAtStart { get; set; }

        public bool WasStartedBeforeStop { get; set; }

        public bool LifetimeResolved { get; set; }

        public bool StartedWasSignaledDuringStart { get; set; }

        public bool StartedSignaled { get; set; }

        public bool StoppingWasSignaledDuringStop { get; set; }

        public bool StoppingSignaled { get; set; }

        public bool StoppedSignaled { get; set; }

        public List<string> Events { get; } = [];

        public int LoggerFactoryCallbackCount { get; set; }

        public int LoggerFactoryCallbackFailureCount { get; set; }

        public string? InitFailureLoggerName { get; set; }

        public string? InitFailureMessage { get; set; }

        public Exception? InitFailureException { get; set; }

        public string? RuntimeContextInstanceId { get; set; }

        public string? RuntimeContextDataDirectory { get; set; }

        public bool RuntimeContextSettingsIsSameInstance { get; set; }
    }
}
