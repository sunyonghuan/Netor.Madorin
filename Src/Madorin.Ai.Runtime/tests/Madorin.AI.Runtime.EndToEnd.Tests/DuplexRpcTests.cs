using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class DuplexRpcTests
{
    [TestMethod]
    public async Task DuplexPeer_NestedReverseRequest_DoesNotDeadlock()
    {
        var (client, server) = await CreatePeersAsync().ConfigureAwait(false);
        await using var clientScope = client;
        await using var serverScope = server;

        client.SetRequestHandler((request, _) => ValueTask.FromResult(
            new JsonRpcResponse(
                "2.0",
                request.Id,
                ParseElement("""{"answer":"from-client"}"""))));
        server.SetRequestHandler(async (request, ct) =>
        {
            var reverse = await server.SendRequestAsync(
                "host.reverse",
                ParseElement("""{"value":7}"""),
                TimeSpan.FromSeconds(2),
                ct).ConfigureAwait(false);
            return new JsonRpcResponse("2.0", request.Id, reverse.Result);
        });

        var response = await client.SendRequestAsync(
            "runtime.outer",
            ParseElement("""{"value":1}"""),
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.IsNull(response.Error);
        Assert.AreEqual("from-client", response.Result?.GetProperty("answer").GetString());
    }

    [TestMethod]
    public async Task DuplexPeer_ConcurrentRequests_AreCorrelatedById()
    {
        var (client, server) = await CreatePeersAsync().ConfigureAwait(false);
        await using var clientScope = client;
        await using var serverScope = server;

        server.SetRequestHandler(async (request, ct) =>
        {
            var value = request.Params?.GetProperty("value").GetInt32() ?? -1;
            await Task.Delay(value % 3, ct).ConfigureAwait(false);
            return new JsonRpcResponse(
                "2.0",
                request.Id,
                ParseElement($$"""{"value":{{value}}}"""));
        });

        var tasks = Enumerable.Range(0, 32)
            .Select(value => client.SendRequestAsync(
                    "runtime.echo",
                    ParseElement($$"""{"value":{{value}}}"""),
                    TimeSpan.FromSeconds(2))
                .AsTask())
            .ToArray();
        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);

        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, 32).ToArray(),
            responses.Select(response => response.Result!.Value.GetProperty("value").GetInt32())
                .ToArray());
    }

    [TestMethod]
    public async Task DuplexPeer_RequestTimeout_CompletesDeterministically()
    {
        var (client, server) = await CreatePeersAsync().ConfigureAwait(false);
        await using var clientScope = client;
        await using var serverScope = server;

        server.SetRequestHandler(async (request, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return new JsonRpcResponse("2.0", request.Id);
        });

        await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
            await client.SendRequestAsync(
                "runtime.timeout",
                null,
                TimeSpan.FromMilliseconds(50)).ConfigureAwait(false));
    }

    private static async Task<(FramedControlChannel Client, FramedControlChannel Server)>
        CreatePeersAsync()
    {
        var pipeName = $"madorin.tests.duplex.{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTransportTask = NamedPipeTransport.CreateServerAsync(pipeName, timeout.Token);
        var clientTransport = await NamedPipeTransport.ConnectAsync(pipeName, timeout.Token)
            .ConfigureAwait(false);
        var serverTransport = await serverTransportTask.ConfigureAwait(false);
        return (new FramedControlChannel(clientTransport), new FramedControlChannel(serverTransport));
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
