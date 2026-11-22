using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

/// <summary>A published live run: the trace, whether a full-resolution pass follows, and the solve time.</summary>
public sealed record LiveTraceResult(TdrTrace Trace, bool IsPreview, TimeSpan Elapsed);

/// <summary>
/// The live recompute pipeline (PLAN section 3.3): Submit on every edit; a short debounce
/// coalesces bursts; the newest submission always wins (an in-flight run is cancelled, a
/// queued one replaced - intermediates are dropped, never queued). Each accepted request
/// first runs a preview with the grid capped at 2^<see cref="PreviewMaxLog2M"/>; when
/// that cap actually bound (the grid came out compromised), a full-resolution pass runs
/// after an idle delay. Immutable snapshots make this lock-free at the model level; one
/// run is in flight at any time.
///
/// Events fire on worker threads - UI callers dispatch. Cancelled intermediates raise no
/// <see cref="RunFailed"/>.
/// </summary>
public sealed class LiveTraceRunner : IDisposable
{
    private readonly SimulationSession _session;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _fullResolutionIdle;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _worker;
    private readonly object _gate = new();

    private Request? _latest;
    private int _version;
    private CancellationTokenSource? _runCts;
    private bool _disposed;

    public LiveTraceRunner(
        SimulationSession session,
        TimeSpan? debounce = null,
        TimeSpan? fullResolutionIdle = null,
        int previewMaxLog2M = 15)
    {
        _session = session;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(20);
        _fullResolutionIdle = fullResolutionIdle ?? TimeSpan.FromMilliseconds(250);
        PreviewMaxLog2M = previewMaxLog2M;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public int PreviewMaxLog2M { get; }

    /// <summary>A trace is ready to display. IsPreview = a full-resolution pass is coming.</summary>
    public event Action<LiveTraceResult>? TraceReady;

    /// <summary>A solve is starting (preview or full) - drives the progress shimmer.</summary>
    public event Action? RunStarted;

    /// <summary>The newest request failed (never raised for superseded/cancelled runs).</summary>
    public event Action<Exception>? RunFailed;

    /// <summary>Queue the newest document state; supersedes anything pending or running.</summary>
    public void Submit(NetworkModel model, InstrumentSettings instrument, SimulationSettings? settings = null)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _latest = new Request(model, instrument, settings ?? SimulationSettings.Default);
            _version++;
            _runCts?.Cancel();
        }

        _signal.Release();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _disposeCts.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Worker surfaced its shutdown cancellation; nothing to do.
        }

        _disposeCts.Dispose();
        _signal.Dispose();
    }

    private async Task WorkerLoopAsync()
    {
        var disposeToken = _disposeCts.Token;
        int lastHandled = 0;
        try
        {
            while (true)
            {
                await _signal.WaitAsync(disposeToken).ConfigureAwait(false);
                if (Volatile.Read(ref _version) == lastHandled)
                    continue; // drained a duplicate signal for work already done

                // Debounce: wait until no newer submission lands within the window.
                while (true)
                {
                    int seen = Volatile.Read(ref _version);
                    await Task.Delay(_debounce, disposeToken).ConfigureAwait(false);
                    if (seen == Volatile.Read(ref _version))
                        break;
                }

                Request request;
                int version;
                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(disposeToken);
                lock (_gate)
                {
                    request = _latest!;
                    version = _version;
                    _runCts = runCts;
                }

                try
                {
                    lastHandled = version;
                    await RunAsync(request, version, runCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    disposeToken.ThrowIfCancellationRequested();
                    // Superseded by a newer Submit: drop silently, loop picks up the newest.
                }
                catch (Exception e)
                {
                    if (Volatile.Read(ref _version) == version)
                        RunFailed?.Invoke(e);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_runCts == runCts)
                            _runCts = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private async Task RunAsync(Request request, int version, CancellationToken token)
    {
        bool IsCurrent() => Volatile.Read(ref _version) == version;

        int previewCap = Math.Min(PreviewMaxLog2M, request.Settings.MaxLog2M);
        var previewSettings = request.Settings with { MaxLog2M = previewCap };

        RunStarted?.Invoke();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var preview = await Task.Run(
            () => TdrSimulator.Simulate(request.Model, request.Instrument, previewSettings, _session, token),
            CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        // The cap only matters when it actually bound: an uncompromised capped grid is
        // bitwise the grid the full run would build, so this preview is the final.
        bool isFinal = previewCap == request.Settings.MaxLog2M || !preview.Grid.Compromised;
        if (!IsCurrent())
            return;
        TraceReady?.Invoke(new LiveTraceResult(preview, !isFinal, stopwatch.Elapsed));
        if (isFinal)
            return;

        await Task.Delay(_fullResolutionIdle, token).ConfigureAwait(false);
        if (!IsCurrent())
            return;

        RunStarted?.Invoke();
        stopwatch.Restart();
        var full = await Task.Run(
            () => TdrSimulator.Simulate(request.Model, request.Instrument, request.Settings, _session, token),
            CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();
        if (IsCurrent())
            TraceReady?.Invoke(new LiveTraceResult(full, false, stopwatch.Elapsed));
    }

    private sealed record Request(NetworkModel Model, InstrumentSettings Instrument, SimulationSettings Settings);
}
