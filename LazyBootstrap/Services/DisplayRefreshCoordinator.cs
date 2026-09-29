using System;
using System.Threading;
using System.Threading.Tasks;

namespace LazyBootstrap.Services
{
    // All native discovery, mode probes and display transactions share this gate.
    internal sealed class DisplayRefreshCoordinator : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _queryStateLock = new();
        private CancellationTokenSource _queryGeneration = new();
        private bool _queriesPaused;
        private bool _configurationEnabled;

        public DisplayRefreshCoordinator(bool configurationEnabled = true)
        {
            _configurationEnabled = configurationEnabled;
        }

        public bool IsDisposed => _lifetime.IsCancellationRequested;
        public bool IsPaused { get { lock (_queryStateLock) return _queriesPaused; } }

        public void SetConfigurationEnabled(bool enabled)
        {
            lock (_queryStateLock)
            {
                if (IsDisposed || enabled == _configurationEnabled) return;
                _configurationEnabled = enabled;
                UpdateQueryGeneration();
            }
        }

        public bool SetLaunchState(bool isLaunching, bool isGameRunning, bool isWorkflowActive)
        {
            lock (_queryStateLock)
            {
                bool paused = isLaunching || isGameRunning || isWorkflowActive;
                if (IsDisposed || paused == _queriesPaused) return false;
                _queriesPaused = paused;
                UpdateQueryGeneration();
                return true;
            }
        }

        private void UpdateQueryGeneration()
        {
            if (_queriesPaused || !_configurationEnabled)
            {
                // Cancel queued reads and invalidate results of native calls already in progress.
                _queryGeneration.Cancel();
            }
            else if (_queryGeneration.IsCancellationRequested)
            {
                _queryGeneration.Dispose();
                _queryGeneration = new CancellationTokenSource();
            }
        }

        public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
        {
            CancellationTokenSource linkedSource;
            lock (_queryStateLock)
            {
                if (!_configurationEnabled || _queriesPaused)
                    throw new OperationCanceledException("Display queries are disabled or paused.");
                linkedSource = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken, _queryGeneration.Token);
            }
            using var linked = linkedSource;
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var result = await Task.Run(operation, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally { _gate.Release(); }
        }

        public async Task<IDisposable> EnterTransactionAsync(CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            return new Lease(_gate);
        }

        // The caller holds a transaction lease; launch validation is allowed while list refreshes are paused.
        public async Task<DisplayDiscoveryResult> DiscoverForTransactionAsync(Func<DisplayDiscoveryResult> discover, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            var result = await Task.Run(discover, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }

        public async Task WaitForQueriesToFinishAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            _gate.Release();
        }

        public T RunSynchronous<T>(Func<T> operation)
        {
            _gate.Wait();
            try { return operation(); }
            finally { _gate.Release(); }
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            _lifetime.Cancel();
            // In-flight P/Invokes cannot be interrupted. Keep the gate alive until their leases end.
        }

        private sealed class Lease(SemaphoreSlim gate) : IDisposable
        {
            private SemaphoreSlim _gate = gate;
            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
