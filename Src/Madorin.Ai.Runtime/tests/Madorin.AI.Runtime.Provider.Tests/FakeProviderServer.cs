using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Madorin.AI.Runtime.Provider.Tests;

internal sealed record FakeProviderRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string ConnectionId);

internal sealed record FakeProviderChunk(string Content, TimeSpan Delay = default);

internal sealed record FakeProviderResponse(
    int StatusCode,
    string ContentType,
    IReadOnlyList<FakeProviderChunk> Chunks,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool AbortAfterChunks = false,
    TimeSpan AbortDelay = default)
{
    public static FakeProviderResponse Error(
        int statusCode,
        string body,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(statusCode, "application/json", [new FakeProviderChunk(body)], headers);

    public static FakeProviderResponse EventStream(params FakeProviderChunk[] chunks) =>
        new(StatusCodes.Status200OK, "text/event-stream", chunks);
}

internal sealed class FakeProviderServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly ConcurrentQueue<FakeProviderRequest> _requests;

    private FakeProviderServer(
        WebApplication application,
        Uri baseAddress,
        ConcurrentQueue<FakeProviderRequest> requests)
    {
        _application = application;
        BaseAddress = baseAddress;
        _requests = requests;
    }

    public Uri BaseAddress { get; }

    public IReadOnlyList<FakeProviderRequest> Requests => [.. _requests];

    public static async Task<FakeProviderServer> StartAsync(
        Func<FakeProviderRequest, FakeProviderResponse> responseFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(responseFactory);

        var requests = new ConcurrentQueue<FakeProviderRequest>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var application = builder.Build();

        application.Run(context => HandleRequestAsync(context, requests, responseFactory));
        await application.StartAsync(ct).ConfigureAwait(false);

        var addresses = application.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = addresses?.SingleOrDefault()
            ?? throw new InvalidOperationException("The fake Provider server did not expose an address.");

        return new FakeProviderServer(application, new Uri(address, UriKind.Absolute), requests);
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task HandleRequestAsync(
        HttpContext context,
        ConcurrentQueue<FakeProviderRequest> requests,
        Func<FakeProviderRequest, FakeProviderResponse> responseFactory)
    {
        using var reader = new StreamReader(
            context.Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        var request = new FakeProviderRequest(
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            context.Request.Headers.ToDictionary(
                static header => header.Key,
                static header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase),
            body,
            context.Connection.Id);
        requests.Enqueue(request);

        var response = responseFactory(request);
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        if (response.Headers is not null)
        {
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }

        foreach (var chunk in response.Chunks)
        {
            if (chunk.Delay > TimeSpan.Zero)
            {
                await Task.Delay(chunk.Delay, context.RequestAborted).ConfigureAwait(false);
            }

            await context.Response.WriteAsync(chunk.Content, context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }

        if (response.AbortAfterChunks)
        {
            if (response.AbortDelay > TimeSpan.Zero)
            {
                await Task.Delay(response.AbortDelay, context.RequestAborted).ConfigureAwait(false);
            }

            context.Abort();
        }
    }
}
