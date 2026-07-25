using System.Text;

namespace Madorin.AI.Runtime.Cli.Commands;

internal sealed class AtomicOutputFile : IAsyncDisposable
{
    private readonly string _targetPath;
    private readonly string _temporaryPath;
    private bool _committed;
    private bool _writerDisposed;

    private AtomicOutputFile(string targetPath)
    {
        _targetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(_targetPath)
            ?? throw new ArgumentException("The output file must have a parent directory.", nameof(targetPath));
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Output directory not found: {directory}");
        }

        if (Directory.Exists(_targetPath))
        {
            throw new IOException($"The output path is a directory: {_targetPath}");
        }

        var fileName = Path.GetFileName(_targetPath);
        _temporaryPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                _temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            Writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            stream?.Dispose();
            TryDeleteTemporaryFile(_temporaryPath);
            throw;
        }
    }

    public TextWriter Writer { get; }

    public static AtomicOutputFile Create(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return new AtomicOutputFile(targetPath);
    }

    public async ValueTask CommitAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_writerDisposed, this);
        await Writer.FlushAsync(ct).ConfigureAwait(false);
        await Writer.DisposeAsync().ConfigureAwait(false);
        _writerDisposed = true;
        ct.ThrowIfCancellationRequested();
        File.Move(_temporaryPath, _targetPath, overwrite: true);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_writerDisposed)
        {
            await Writer.DisposeAsync().ConfigureAwait(false);
            _writerDisposed = true;
        }

        if (!_committed)
        {
            TryDeleteTemporaryFile(_temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort and must not hide the original output failure.
        }
    }
}
