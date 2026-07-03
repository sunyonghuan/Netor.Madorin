using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class VoiceEventBridgeTests
{
    private ServiceProvider _services = null!;
    private ISubscriber _subscriber = null!;
    private VoiceEventBridge _bridge = null!;

    [TestInitialize]
    public void Setup()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _subscriber = _services.GetRequiredService<ISubscriber>();
        _bridge = new VoiceEventBridge(
            _services.GetRequiredService<IPublisher>(),
            NullLogger.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
    }

    [TestMethod]
    public async Task V1_KwsDetected_PublishesOnWakeWordDetected()
    {
        var tcs = new TaskCompletionSource<VoiceSignalArgs>();
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnWakeWordDetected, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{}}""");
        _bridge.Bridge("voice.kws.detected.v1", doc.RootElement);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task V2_SttPartial_PublishesWithText()
    {
        var tcs = new TaskCompletionSource<VoiceTextArgs>();
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnSttPartial, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{"text":"正在识别"}}""");
        _bridge.Bridge("voice.stt.partial.v1", doc.RootElement);

        var args = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("正在识别", args.Text);
    }

    [TestMethod]
    public async Task V3_SttFinal_PublishesWithText()
    {
        var tcs = new TaskCompletionSource<VoiceTextArgs>();
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnSttFinal, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{"text":"识别完成"}}""");
        _bridge.Bridge("voice.stt.final.v1", doc.RootElement);

        var args = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("识别完成", args.Text);
    }

    [TestMethod]
    public async Task V4_SttStopped_PublishesSignal()
    {
        var tcs = new TaskCompletionSource<VoiceSignalArgs>();
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnSttStopped, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{}}""");
        _bridge.Bridge("voice.stt.stopped.v1", doc.RootElement);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task V5_TtsStarted_PublishesSignal()
    {
        var tcs = new TaskCompletionSource<VoiceSignalArgs>();
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsStarted, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{}}""");
        _bridge.Bridge("voice.tts.started.v1", doc.RootElement);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task V6_TtsSubtitle_PublishesWithText()
    {
        var tcs = new TaskCompletionSource<VoiceTextArgs>();
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnTtsSubtitle, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{"text":"当前播放句子"}}""");
        _bridge.Bridge("voice.tts.subtitle.v1", doc.RootElement);

        var args = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("当前播放句子", args.Text);
    }

    [TestMethod]
    public async Task V7_TtsCompleted_PublishesSignal()
    {
        var tcs = new TaskCompletionSource<VoiceSignalArgs>();
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsCompleted, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{}}""");
        _bridge.Bridge("voice.tts.completed.v1", doc.RootElement);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public void V8_UnmappedVoiceOp_DoesNotThrow()
    {
        using var doc = JsonDocument.Parse("""{"payload":{}}""");
        _bridge.Bridge("voice.foo.v1", doc.RootElement);
    }

    [TestMethod]
    public void V9_GenericErrorSuffix_DoesNotPublishSignal()
    {
        var fired = false;
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsCompleted, (_, _) =>
        {
            fired = true;
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{"text":"模型加载失败"}}""");
        _bridge.Bridge("voice.kws.error.v1", doc.RootElement);

        Assert.IsFalse(fired);
    }

    [TestMethod]
    public void V10_TtsGreetingReady_DoesNotPublishSignal()
    {
        var fired = false;
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsStarted, (_, _) =>
        {
            fired = true;
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsCompleted, (_, _) =>
        {
            fired = true;
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{}}""");
        _bridge.Bridge("voice.tts.greeting_ready.v1", doc.RootElement);

        Assert.IsFalse(fired);
    }

    [TestMethod]
    public void V11_TtsErrorV1_DoesNotPublishSignal()
    {
        var fired = false;
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnTtsCompleted, (_, _) =>
        {
            fired = true;
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{"text":"模型加载失败"}}""");
        _bridge.Bridge("voice.tts.error.v1", doc.RootElement);

        Assert.IsFalse(fired);
    }

    [TestMethod]
    public async Task V12_LegacyOpWithoutVersion_NormalizedAndBridged()
    {
        var tcs = new TaskCompletionSource<VoiceTextArgs>();
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnTtsSubtitle, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"payload":{"text":"旧版三件套发包"}}""");
        _bridge.Bridge("voice.tts.subtitle", doc.RootElement);

        var args = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("旧版三件套发包", args.Text);
    }

    [TestMethod]
    public void NormalizeVoiceOp_NoSuffix_AppendsV1()
    {
        Assert.AreEqual("voice.tts.subtitle.v1", VoiceEventBridge.NormalizeVoiceOp("voice.tts.subtitle"));
    }

    [TestMethod]
    public void NormalizeVoiceOp_HasV1Suffix_ReturnsAsIs()
    {
        Assert.AreEqual("voice.tts.subtitle.v1", VoiceEventBridge.NormalizeVoiceOp("voice.tts.subtitle.v1"));
    }

    [TestMethod]
    public void NormalizeVoiceOp_HasV2Suffix_ReturnsAsIs()
    {
        Assert.AreEqual("voice.tts.subtitle.v2", VoiceEventBridge.NormalizeVoiceOp("voice.tts.subtitle.v2"));
    }
}
