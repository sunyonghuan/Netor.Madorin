using System.Collections.Concurrent;

namespace Madorin.AI.Runtime.Server;

internal enum RunCapacityResult
{
    Acquired,
    GlobalCapacityReached,
    ProviderCapacityReached,
    SessionCapacityReached
}

internal sealed class RunRegistry : IDisposable
{
    private readonly SemaphoreSlim _globalCapacity;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerCapacities =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionCapacities =
        new(StringComparer.Ordinal);
    private readonly int _maxConcurrentRunsPerProvider;
    private readonly int _maxConcurrentRunsPerSession;
    private readonly TimeSpan _runTimeout;
    private readonly CancellationToken _shutdownToken;
    private int _disposed;

    public RunRegistry(
        int maxConcurrentRuns,
        int maxConcurrentRunsPerProvider,
        int maxConcurrentRunsPerSession,
        TimeSpan runTimeout,
        CancellationToken shutdownToken)
    {
        _globalCapacity = new SemaphoreSlim(maxConcurrentRuns, maxConcurrentRuns);
        _maxConcurrentRunsPerProvider = maxConcurrentRunsPerProvider;
        _maxConcurrentRunsPerSession = maxConcurrentRunsPerSession;
        _runTimeout = runTimeout;
        _shutdownToken = shutdownToken;
    }

    public RunLease? TryAcquire(string providerId, out RunCapacityResult result)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (!_globalCapacity.Wait(0))
        {
            result = RunCapacityResult.GlobalCapacityReached;
            return null;
        }

        var providerCapacity = _providerCapacities.GetOrAdd(
            providerId,
            _ => new SemaphoreSlim(
                _maxConcurrentRunsPerProvider,
                _maxConcurrentRunsPerProvider));
        if (!providerCapacity.Wait(0))
        {
            _globalCapacity.Release();
            result = RunCapacityResult.ProviderCapacityReached;
            return null;
        }

        try
        {
            result = RunCapacityResult.Acquired;
            return new RunLease(
                this,
                providerId,
                _runTimeout,
                _shutdownToken);
        }
        catch
        {
            providerCapacity.Release();
            _globalCapacity.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _globalCapacity.Dispose();
        foreach (var providerCapacity in _providerCapacities.Values)
        {
            providerCapacity.Dispose();
        }

        foreach (var sessionCapacity in _sessionCapacities.Values)
        {
            sessionCapacity.Dispose();
        }

        _providerCapacities.Clear();
        _sessionCapacities.Clear();
    }

    private bool TryAcquireSession(string sessionId)
    {
        var sessionCapacity = _sessionCapacities.GetOrAdd(
            sessionId,
            _ => new SemaphoreSlim(
                _maxConcurrentRunsPerSession,
                _maxConcurrentRunsPerSession));
        return sessionCapacity.Wait(0);
    }

    private void Release(string providerId, string? sessionId)
    {
        if (sessionId is not null
            && _sessionCapacities.TryGetValue(sessionId, out var sessionCapacity))
        {
            sessionCapacity.Release();
        }

        if (_providerCapacities.TryGetValue(providerId, out var providerCapacity))
        {
            providerCapacity.Release();
        }

        _globalCapacity.Release();
    }

    internal sealed class RunLease : IDisposable
    {
        private readonly RunRegistry _owner;
        private readonly string _providerId;
        private readonly CancellationTokenSource _cancellation;
        private readonly Timer _timeoutTimer;
        private string? _sessionId;
        private int _cancelRequested;
        private int _timedOut;
        private int _disposed;

        public RunLease(
            RunRegistry owner,
            string providerId,
            TimeSpan timeout,
            CancellationToken shutdownToken)
        {
            _owner = owner;
            _providerId = providerId;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _timeoutTimer = new Timer(
                static state => ((RunLease)state!).CancelForTimeout(),
                this,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool IsTimedOut => Volatile.Read(ref _timedOut) != 0;

        public bool Cancel()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) != 0
                || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            try
            {
                _cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public bool TryBindSession(string sessionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (Volatile.Read(ref _sessionId) is not null)
            {
                throw new InvalidOperationException("The Run lease is already bound to a Session.");
            }

            if (!_owner.TryAcquireSession(sessionId))
            {
                return false;
            }

            _sessionId = sessionId;
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _timeoutTimer.Dispose();
            _cancellation.Dispose();
            _owner.Release(_providerId, _sessionId);
        }

        private void CancelForTimeout()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _timedOut, 1);
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
