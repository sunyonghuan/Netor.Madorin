using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Plugin;
using Netor.EventHub;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class PluginBusBroadcastDispatcherTests
{
    private ServiceProvider _services = null!;
    private ISubscriber _subscriber = null!;
    private IPublisher _publisher = null!;
    private PluginBusSubscriptionRegistry _subscriptions = null!;
    private PluginManifestRegistry _manifests = null!;
    private VoiceEventBridge _voiceBridge = null!;
    private WebSocketConnectionManager _connections = null!;
    private PluginBusBroadcastDispatcher _dispatcher = null!;
    private ConcurrentBag<(string ClientId, string Payload)> _sent = null!;
    private ConcurrentDictionary<string, Func<CancellationToken, Task>> _sendOverrides = null!;

    [TestInitialize]
    public void Setup()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _subscriber = _services.GetRequiredService<ISubscriber>();
        _publisher = _services.GetRequiredService<IPublisher>();

        _subscriptions = new PluginBusSubscriptionRegistry();
        _manifests = new PluginManifestRegistry(NullLogger<PluginManifestRegistry>.Instance);
        _voiceBridge = new VoiceEventBridge(_publisher, NullLogger.Instance);
        _connections = new WebSocketConnectionManager(
            2048,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(2),
            (_, _) => Task.CompletedTask);
        _sent = new ConcurrentBag<(string, string)>();
        _sendOverrides = new ConcurrentDictionary<string, Func<CancellationToken, Task>>(StringComparer.Ordinal);
        _dispatcher = new PluginBusBroadcastDispatcher(
            _subscriptions,
            _manifests,
            _voiceBridge,
            _connections,
            SendCaptureAsync,
            NullLogger.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
    }

    private async Task SendCaptureAsync(string clientId, string payload, CancellationToken ct)
    {
        if (_sendOverrides.TryGetValue(clientId, out var slow))
        {
            await slow(ct).ConfigureAwait(false);
        }
        _sent.Add((clientId, payload));
    }

    [TestMethod]
    public async Task T1_NormalForward_TwoSubscribersReceiveOnce()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });
        _subscriptions.Subscribe("sub-b", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(2, _sent.Count);
        Assert.IsTrue(_sent.Any(s => s.ClientId == "sub-a"));
        Assert.IsTrue(_sent.Any(s => s.ClientId == "sub-b"));
        Assert.IsFalse(_sent.Any(s => s.ClientId == "publisher"));
    }

    [TestMethod]
    public async Task T2_SelfLoopExcluded_PublisherDoesNotReceiveOwn()
    {
        _subscriptions.Subscribe("publisher", new[] { "voice" });
        _subscriptions.Subscribe("sub-b", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(1, _sent.Count);
        Assert.IsTrue(_sent.Any(s => s.ClientId == "sub-b"));
        Assert.IsFalse(_sent.Any(s => s.ClientId == "publisher"));
    }

    [TestMethod]
    public async Task T3_HopCountAtLimit_FrameDropped()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","hopCount":8,"payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(0, _sent.Count);
    }

    [TestMethod]
    public async Task T4_SubscribedOpsFilter_OtherOpsNotDelivered()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });
        _subscriptions.SetSubscribedOps("sub-a", new[] { "voice.tts.subtitle.v1" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(0, _sent.Count);
    }

    [TestMethod]
    public async Task T5_VoiceBridgeInvoked_PublisherFiredOnSttPartial()
    {
        var tcs = new TaskCompletionSource<VoiceTextArgs>();
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnSttPartial, (_, args) =>
        {
            tcs.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.stt.partial.v1","payload":{"text":"hi"}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);

        var args = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("hi", args.Text);
    }

    [TestMethod]
    public async Task T6_SourceHostRejected_NoForward()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","source":"host","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(0, _sent.Count);
    }

    [TestMethod]
    public async Task T7_OpFallbackToEventType_LegacyForwarded()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","eventType":"voice.kws.detected.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(1, _sent.Count);
    }

    [TestMethod]
    public async Task T8_OpWinsOverEventType_VoiceBridgeUsesOp()
    {
        var sttFired = false;
        var kwsFired = new TaskCompletionSource<VoiceSignalArgs>();
        _subscriber.Subscribe<VoiceTextArgs>(Events.OnSttPartial, (_, _) =>
        {
            sttFired = true;
            return Task.FromResult(false);
        });
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnWakeWordDetected, (_, args) =>
        {
            kwsFired.TrySetResult(args);
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","eventType":"voice.stt.partial.v1","payload":{"text":"x"}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);

        await kwsFired.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(sttFired);
    }

    [TestMethod]
    public async Task T9_HopCountRewritten_SubscriberReceivesIncremented()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","hopCount":3,"payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(1, _sent.Count);
        using var received = JsonDocument.Parse(_sent.First().Payload);
        Assert.AreEqual(4, received.RootElement.GetProperty("hopCount").GetInt32());
    }

    [TestMethod]
    public async Task T10_WeakDeclarationMismatch_LogsButForwards()
    {
        _manifests.Register("voice_kws_sherpa", new[] { "voice.kws.other.v1" }, Array.Empty<string>());
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","sourcePluginId":"voice_kws_sherpa","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(1, _sent.Count);
    }

    [TestMethod]
    public async Task T11_DefaultSubscribedOps_ReceivesAllTopicOps()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(1, _sent.Count);
    }

    [TestMethod]
    public async Task T12_NonVoiceTopic_DoesNotInvokeVoiceBridge()
    {
        _subscriptions.Subscribe("sub-a", new[] { "memory" });
        var voiceFired = false;
        _subscriber.Subscribe<VoiceSignalArgs>(Events.OnWakeWordDetected, (_, _) =>
        {
            voiceFired = true;
            return Task.FromResult(false);
        });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"memory.event.indexUpdated.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(1, _sent.Count);
        Assert.IsFalse(voiceFired);
    }

    [TestMethod]
    public async Task T13_OpMissing_DropsWithoutThrowing()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(0, _sent.Count);
    }

    [TestMethod]
    public async Task T14_BackgroundDelivery_HandleAsyncReturnsImmediately()
    {
        _subscriptions.Subscribe("fast", new[] { "voice" });
        _subscriptions.Subscribe("slow", new[] { "voice" });
        _sendOverrides["slow"] = ct => Task.Delay(TimeSpan.FromSeconds(3), ct);

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","payload":{}}""");
        var sw = Stopwatch.StartNew();
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        sw.Stop();

        Assert.IsTrue(sw.ElapsedMilliseconds < 200, $"HandleAsync took {sw.ElapsedMilliseconds}ms");

        await Task.Delay(300);
        Assert.IsTrue(_sent.Any(s => s.ClientId == "fast"));
        Assert.IsFalse(_sent.Any(s => s.ClientId == "slow"));
    }

    [TestMethod]
    public async Task T15_JsonElementLifetime_BackgroundUsesOwnedString()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });
        var gate = new TaskCompletionSource();
        _sendOverrides["sub-a"] = async _ => await gate.Task;

        var json = """{"type":"event","op":"voice.kws.detected.v1","hopCount":3,"payload":{"text":"hello"}}""";

        using (var doc = JsonDocument.Parse(json))
        {
            await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        }

        gate.SetResult();
        await Task.Delay(200);

        Assert.AreEqual(1, _sent.Count);
        using var received = JsonDocument.Parse(_sent.First().Payload);
        Assert.AreEqual(4, received.RootElement.GetProperty("hopCount").GetInt32());
        Assert.AreEqual("hello", received.RootElement.GetProperty("payload").GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task T16_OpEventTypeConflict_OpIsAuthority()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","eventType":"voice.tts.subtitle.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(1, _sent.Count);
        using var received = JsonDocument.Parse(_sent.First().Payload);
        Assert.AreEqual("voice.kws.detected.v1", received.RootElement.GetProperty("op").GetString());
    }

    [TestMethod]
    public async Task T17_ProtectedChatOp_RejectedNoBackground()
    {
        _subscriptions.Subscribe("sub-a", new[] { "conversation" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"conversation.chat.message.send","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(0, _sent.Count);
    }

    [TestMethod]
    public async Task T17b_ProtectedMemoryRpcOp_Rejected()
    {
        _subscriptions.Subscribe("sub-a", new[] { "memory" });

        using var doc = JsonDocument.Parse("""{"type":"event","op":"memory.context.supply.request","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(0, _sent.Count);
    }

    [TestMethod]
    public async Task T18_LargePayload_RejectedNoBackground()
    {
        _subscriptions.Subscribe("sub-a", new[] { "voice" });
        var bigData = new string('x', 1024 * 1024 + 100);
        var json = "{\"type\":\"event\",\"op\":\"voice.kws.detected.v1\",\"payload\":{\"data\":\"" + bigData + "\"}}";

        using var doc = JsonDocument.Parse(json);
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);
        await Task.Delay(150);

        Assert.AreEqual(0, _sent.Count);
    }

    [TestMethod]
    public async Task T19_DeadLinkCleanup_TimeoutRemovesSubscription()
    {
        _subscriptions.Subscribe("dead-sub", new[] { "voice" });
        _sendOverrides["dead-sub"] = ct => Task.Delay(TimeSpan.FromSeconds(5), ct);

        using var doc = JsonDocument.Parse("""{"type":"event","op":"voice.kws.detected.v1","payload":{}}""");
        await _dispatcher.HandleAsync("publisher", doc.RootElement, CancellationToken.None);

        // PerSubscriberTimeoutMs = 1000, give cleanup buffer
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.IsFalse(_subscriptions.GetSubscribers("voice").Contains("dead-sub"));
    }
}
