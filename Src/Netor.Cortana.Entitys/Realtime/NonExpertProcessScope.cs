namespace Netor.Cortana.Entitys;

public static class NonExpertProcessScope
{
    private static readonly AsyncLocal<bool> _active = new();

    public static bool Active => _active.Value;

    public static IDisposable Enter()
    {
        var previous = _active.Value;
        _active.Value = true;
        return new Pop(previous);
    }

    private sealed class Pop(bool previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _active.Value = previous;
        }
    }
}
