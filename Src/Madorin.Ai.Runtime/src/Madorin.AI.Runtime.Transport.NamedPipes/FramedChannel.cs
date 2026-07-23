using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

/// <summary>JSON helper over the length-prefixed transport.</summary>
public sealed class FramedChannel(NamedPipeTransport transport)
{
    public ValueTask SendJsonAsync<T>(T value, CancellationToken ct = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), RuntimeJsonContext.Default);
        return transport.SendFrameAsync(bytes, ct);
    }

    public async ValueTask<T?> ReceiveJsonAsync<T>(JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        var bytes = await transport.ReceiveFrameAsync(ct).ConfigureAwait(false);
        return bytes.Length == 0 ? default : JsonSerializer.Deserialize(bytes, typeInfo);
    }
}
