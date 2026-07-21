namespace Madorin.AI.Runtime.Transport.Abstractions;

public interface IBlobChannel
{
    public ValueTask<Stream> OpenReadAsync(
        string blobId,
        CancellationToken cancellationToken = default);

    public ValueTask<string> WriteAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default);
}
