using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class WebSocketInputChannelTests
{
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private ISubscriber _subscriber = null!;
    private FakeChatTransport _transport = null!;
    private FakeChatEngine _chatEngine = null!;
    private WebSocketInputChannel _channel = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();
        _subscriber = _services.GetRequiredService<ISubscriber>();
        _transport = new FakeChatTransport();
        _chatEngine = new FakeChatEngine();
        _channel = new WebSocketInputChannel(
            NullLogger<WebSocketInputChannel>.Instance,
            _transport,
            _chatEngine,
            _publisher,
            new WebSocketRequestContext());

        await _channel.StartAsync(CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_channel is not null)
        {
            await _channel.StopAsync(CancellationToken.None);
            _channel.Dispose();
        }

        _services.Dispose();
    }

    [TestMethod]
    public async Task Send_FromWebSocket_DoesNotPublishImmediateUserEchoButSendsToAi()
    {
        var received = new TaskCompletionSource<WebSocketUserMessageReceivedArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _subscriber.Subscribe<WebSocketUserMessageReceivedArgs>(Events.OnWebSocketUserMessageReceived, (_, args) =>
        {
            received.TrySetResult(args);
            return Task.FromResult(false);
        });

        await _transport.ReceiveAsync(new WebSocketClientMessage(
            "client-1",
            "send",
            "普通 WebSocket 输入",
            [],
            null,
            null,
            null));

        await Task.Delay(150);

        Assert.IsFalse(received.Task.IsCompleted);
        Assert.AreEqual(1, _chatEngine.SendCount);
        Assert.AreEqual("普通 WebSocket 输入", _chatEngine.LastInput);
    }

    [TestMethod]
    public async Task Send_FromPluginSource_DoesNotPublishImmediateUserEchoButSendsToAi()
    {
        var received = new TaskCompletionSource<WebSocketUserMessageReceivedArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _subscriber.Subscribe<WebSocketUserMessageReceivedArgs>(Events.OnWebSocketUserMessageReceived, (_, args) =>
        {
            received.TrySetResult(args);
            return Task.FromResult(false);
        });

        await _transport.ReceiveAsync(new WebSocketClientMessage(
            "client-1",
            "send",
            "定时提醒：测试唤起 AI",
            [],
            null,
            null,
            "order-sync-plugin"));

        await Task.Delay(150);

        Assert.IsFalse(received.Task.IsCompleted);
        Assert.AreEqual(1, _chatEngine.SendCount);
        Assert.AreEqual("定时提醒：测试唤起 AI", _chatEngine.LastInput);
    }

    private sealed class FakeChatTransport : IChatTransport
    {
        public int Port => 12841;

        public event Func<string, string, string, List<AttachmentInfo>, Task>? OnMessageReceived
        {
            add { }
            remove { }
        }

        public event Func<WebSocketClientMessage, Task>? OnClientMessageReceived;

        public Task ReceiveAsync(WebSocketClientMessage message)
            => OnClientMessageReceived?.Invoke(message) ?? Task.CompletedTask;

        public Task SendTokenAsync(string clientId, string token, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendDoneAsync(string clientId, string? sessionId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendErrorAsync(string clientId, string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task BroadcastAsync(string type, string data, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeChatEngine : IAiChatEngine
    {
        public int SendCount { get; private set; }

        public string? LastInput { get; private set; }

        public Task SendMessageAsync(
            string userInput,
            CancellationToken cancellationToken,
            List<AttachmentInfo>? attachments = null,
            List<AgentMention>? mentions = null)
        {
            SendCount++;
            LastInput = userInput;
            return Task.CompletedTask;
        }

        public Task NewSessionAsync()
            => Task.CompletedTask;

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
