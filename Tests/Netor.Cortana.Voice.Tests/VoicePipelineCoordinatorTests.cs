using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Voice;
using Netor.EventHub;

namespace Netor.Cortana.Voice.Tests;

[TestClass]
public sealed class VoicePipelineCoordinatorTests
{
    private string _root = string.Empty;
    private CortanaDbContext _db = null!;
    private SystemSettingsService _settings = null!;
    private ServiceProvider? _services;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "cortana-voice-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(Path.Combine(_root, "test.db"));
        _settings = new SystemSettingsService(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services?.Dispose();
        _db.Dispose();
        TryDeleteDirectory(_root);
    }

    [TestMethod]
    public async Task StartAsync_WhenKwsDisabled_DoesNotStartKws()
    {
        _settings.SetValue("Voice.Kws.Enabled", "false");
        _settings.SetValue("Voice.Stt.Enabled", "true");
        var kws = new FakeKwsAdapter();
        var coordinator = CreateCoordinator(kws);

        await coordinator.StartAsync(CancellationToken.None);

        Assert.AreEqual(0, kws.StartCount);
        Assert.AreEqual(0, kws.StopCount);
    }

    [TestMethod]
    public async Task ApplySettingsAsync_WhenKwsDisabled_StopsKwsAndStt()
    {
        _settings.SetValue("Voice.Kws.Enabled", "true");
        _settings.SetValue("Voice.Stt.Enabled", "true");
        _settings.SetValue("Voice.Tts.Enabled", "true");
        var kws = new FakeKwsAdapter();
        var stt = new FakeSttAdapter();
        var tts = new FakeTtsAdapter();
        var coordinator = CreateCoordinator(kws, stt, tts);
        await coordinator.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, kws.StartCount);

        _settings.SetValue("Voice.Kws.Enabled", "false");
        await coordinator.ApplySettingsAsync(CancellationToken.None);

        Assert.AreEqual(1, kws.StopCount);
        Assert.AreEqual(1, stt.StopCount);
        Assert.AreEqual(1, tts.StopCount);
    }

    [TestMethod]
    public async Task StartAsync_WhenSttDisabled_DoesNotStartKws()
    {
        _settings.SetValue("Voice.Kws.Enabled", "true");
        _settings.SetValue("Voice.Stt.Enabled", "false");
        var kws = new FakeKwsAdapter();
        var coordinator = CreateCoordinator(kws);

        await coordinator.StartAsync(CancellationToken.None);

        Assert.AreEqual(0, kws.StartCount);
        Assert.AreEqual(0, kws.StopCount);
    }

    [TestMethod]
    public async Task ApplySettingsAsync_WhenSttDisabled_StopsVoiceInputChain()
    {
        _settings.SetValue("Voice.Kws.Enabled", "true");
        _settings.SetValue("Voice.Stt.Enabled", "true");
        _settings.SetValue("Voice.Tts.Enabled", "true");
        var kws = new FakeKwsAdapter();
        var stt = new FakeSttAdapter();
        var tts = new FakeTtsAdapter();
        var coordinator = CreateCoordinator(kws, stt, tts);
        await coordinator.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, kws.StartCount);

        _settings.SetValue("Voice.Stt.Enabled", "false");
        await coordinator.ApplySettingsAsync(CancellationToken.None);

        Assert.AreEqual(1, kws.StopCount);
        Assert.AreEqual(1, stt.StopCount);
        Assert.AreEqual(1, tts.StopCount);
    }

    [TestMethod]
    public async Task ApplySettingsAsync_WhenTtsDisabled_StopsTtsButKeepsInputListening()
    {
        _settings.SetValue("Voice.Kws.Enabled", "true");
        _settings.SetValue("Voice.Stt.Enabled", "true");
        _settings.SetValue("Voice.Tts.Enabled", "true");
        var kws = new FakeKwsAdapter();
        var stt = new FakeSttAdapter();
        var tts = new FakeTtsAdapter();
        var coordinator = CreateCoordinator(kws, stt, tts);
        await coordinator.StartAsync(CancellationToken.None);
        Assert.AreEqual(1, kws.StartCount);

        _settings.SetValue("Voice.Tts.Enabled", "false");
        await coordinator.ApplySettingsAsync(CancellationToken.None);

        Assert.AreEqual(1, tts.StopCount);
        Assert.AreEqual(0, kws.StopCount);
        Assert.AreEqual(1, kws.ConfigureCount);
        Assert.AreEqual(1, stt.ConfigureCount);
    }

    private VoicePipelineCoordinator CreateCoordinator(
        FakeKwsAdapter? kws = null,
        FakeSttAdapter? stt = null,
        FakeTtsAdapter? tts = null)
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();

        return new VoicePipelineCoordinator(
            NullLogger<VoicePipelineCoordinator>.Instance,
            kws ?? new FakeKwsAdapter(),
            stt ?? new FakeSttAdapter(),
            tts ?? new FakeTtsAdapter(),
            new FakeChatEngine(),
            _services.GetRequiredService<ISubscriber>(),
            _settings);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed class FakeKwsAdapter : IKwsPluginAdapter
    {
        public int ConfigureCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task<VoicePluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default)
        {
            ConfigureCount++;
            return Task.FromResult(new VoicePluginToolResult(true, "ok", string.Empty));
        }

        public Task<VoicePluginToolResult> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult(new VoicePluginToolResult(true, "ok", string.Empty));
        }

        public Task<VoicePluginToolResult> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(new VoicePluginToolResult(true, "ok", string.Empty));
        }
    }

    private sealed class FakeSttAdapter : ISttPluginAdapter
    {
        public int ConfigureCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task<VoicePluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default)
        {
            ConfigureCount++;
            return Task.FromResult(new VoicePluginToolResult(true, "ok", string.Empty));
        }

        public Task<VoicePluginToolResult> StartAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.FromResult(new VoicePluginToolResult(true, "ok", string.Empty));
        }

        public Task<VoicePluginToolResult> StopAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.FromResult(new VoicePluginToolResult(true, "ok", string.Empty));
        }
    }

    private sealed class FakeTtsAdapter : ITtsPluginAdapter
    {
        public int StopCount { get; private set; }

        public bool IsAvailable => false;

        public Task EnqueueAsync(string text, string? sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FinishAsync(string? sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<TtsPluginToolResult> ConfigureAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TtsPluginToolResult.Skipped(string.Empty));

        public Task<TtsPluginToolResult> RegenerateGreetingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TtsPluginToolResult.Skipped(string.Empty));

        public Task<TtsPluginToolResult> GreetingPlayAsync(string? sessionId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(TtsPluginToolResult.Skipped(string.Empty));

        public Task StopAsync(string? sessionId = null, CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChatEngine : IAiChatEngine
    {
        public Task SendMessageAsync(string userInput, CancellationToken cancellationToken, List<AttachmentInfo>? attachments = null, List<AgentMention>? mentions = null)
            => Task.CompletedTask;

        public Task NewSessionAsync() => Task.CompletedTask;

        public void Stop()
        {
        }

        public void CancelCurrentTask()
        {
        }

        public Task CancelCurrentTaskAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
