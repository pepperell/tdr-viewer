using System.Diagnostics;
using TdrSim.Core.Engine;
using TdrSim.Core.Model;
using static TdrSim.Core.Tests.TestNetworks;

namespace TdrSim.Core.Tests;

/// <summary>
/// M4 step 3: the live state machine. Debounce, latest-wins supersession, preview/final
/// staging, and clean failure semantics - all on tiny lossless networks so every solve
/// is milliseconds.
/// </summary>
public sealed class LiveTraceRunnerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Small line: the full grid fits under the 2^15 preview cap (single-run final).</summary>
    private static NetworkModel ShortLine(double lengthM) => Network(
        new SourcePort(N(0)),
        new CableSegment(N(0), N(1), Lossless(50, 0.66), lengthM),
        new Load(N(1), LoadKind.Resistor, ROhms: 75));

    /// <summary>Long line: the full grid needs more than 2^15 samples (preview + final).</summary>
    private static NetworkModel LongLine(double lengthM) => Network(
        new SourcePort(N(0)),
        new CableSegment(N(0), N(1), Lossless(50, 0.66), lengthM),
        new Load(N(1), LoadKind.Resistor, ROhms: 25));

    private static bool SameTrace(TdrTrace a, TdrTrace b)
    {
        if (!a.Grid.Equals(b.Grid) || a.Rho.Length != b.Rho.Length)
            return false;
        for (int k = 0; k < a.Rho.Length; k++)
        {
            if (BitConverter.DoubleToInt64Bits(a.Rho[k]) != BitConverter.DoubleToInt64Bits(b.Rho[k]))
                return false;
        }

        return true;
    }

    private sealed class Collector
    {
        private readonly object _lock = new();
        private readonly List<LiveTraceResult> _results = [];
        private readonly List<Exception> _failures = [];
        private readonly SemaphoreSlim _arrived = new(0);

        public Collector(LiveTraceRunner runner)
        {
            runner.TraceReady += r =>
            {
                lock (_lock)
                {
                    _results.Add(r);
                }

                _arrived.Release();
            };
            runner.RunFailed += e =>
            {
                lock (_lock)
                {
                    _failures.Add(e);
                }

                _arrived.Release();
            };
        }

        public IReadOnlyList<LiveTraceResult> Results
        {
            get
            {
                lock (_lock)
                {
                    return [.. _results];
                }
            }
        }

        public IReadOnlyList<Exception> Failures
        {
            get
            {
                lock (_lock)
                {
                    return [.. _failures];
                }
            }
        }

        public LiveTraceResult WaitForResult(Func<LiveTraceResult, bool> predicate)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (_lock)
                {
                    var hit = _results.FirstOrDefault(predicate);
                    if (hit is not null)
                        return hit;
                }

                var remaining = Timeout - sw.Elapsed;
                Assert.True(remaining > TimeSpan.Zero,
                    $"Timed out waiting for a trace; got {Results.Count} result(s), {Failures.Count} failure(s)"
                    + (Failures.Count > 0 ? $" (first: {Failures[0].Message})" : "") + ".");
                _arrived.Wait(remaining);
            }
        }

        public Exception WaitForFailure()
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (_lock)
                {
                    if (_failures.Count > 0)
                        return _failures[0];
                }

                var remaining = Timeout - sw.Elapsed;
                Assert.True(remaining > TimeSpan.Zero, "Timed out waiting for RunFailed.");
                _arrived.Wait(remaining);
            }
        }
    }

    [Fact]
    public void RapidFireSubmitsEndWithTheLastRequestFinal()
    {
        using var runner = new LiveTraceRunner(
            new SimulationSession(),
            debounce: TimeSpan.FromMilliseconds(10),
            fullResolutionIdle: TimeSpan.FromMilliseconds(15));
        var collector = new Collector(runner);

        for (int k = 0; k < 10; k++)
            runner.Submit(ShortLine(5 + k), Tdr1502C);

        var expected = TdrSimulator.Simulate(ShortLine(14), Tdr1502C);
        collector.WaitForResult(r => !r.IsPreview && SameTrace(r.Trace, expected));
        Thread.Sleep(200); // quiescence: nothing newer may arrive after the last request's final

        var results = collector.Results;
        Assert.Empty(collector.Failures);
        Assert.False(results[^1].IsPreview);
        Assert.True(SameTrace(results[^1].Trace, expected),
            "A stale run published after the newest request's final trace.");
    }

    [Fact]
    public void PreviewPrecedesFinalAndBothMatchDirectSimulate()
    {
        using var runner = new LiveTraceRunner(
            new SimulationSession(),
            debounce: TimeSpan.FromMilliseconds(1),
            fullResolutionIdle: TimeSpan.FromMilliseconds(10));
        var collector = new Collector(runner);

        var model = LongLine(150);
        runner.Submit(model, Tdr1502C);
        collector.WaitForResult(r => !r.IsPreview);

        var results = collector.Results;
        Assert.Empty(collector.Failures);
        Assert.Equal(2, results.Count);

        // Previews are real physics on a coarser grid - bitwise what a direct capped
        // call computes - and the final is bitwise the direct full-resolution call.
        var expectedPreview = TdrSimulator.Simulate(
            model, Tdr1502C, SimulationSettings.Default with { MaxLog2M = 15 });
        var expectedFinal = TdrSimulator.Simulate(model, Tdr1502C);
        Assert.True(results[0].IsPreview);
        Assert.True(results[0].Trace.Grid.Compromised); // the cap actually bound
        Assert.True(SameTrace(results[0].Trace, expectedPreview), "Preview differs from direct capped Simulate.");
        Assert.False(results[1].IsPreview);
        Assert.True(SameTrace(results[1].Trace, expectedFinal), "Final differs from direct Simulate.");
    }

    [Fact]
    public void SmallGridsSkipTheRedundantFullResolutionPass()
    {
        using var runner = new LiveTraceRunner(
            new SimulationSession(),
            debounce: TimeSpan.FromMilliseconds(1),
            fullResolutionIdle: TimeSpan.FromMilliseconds(5));
        var collector = new Collector(runner);

        runner.Submit(ShortLine(5), Tdr1502C);
        collector.WaitForResult(r => !r.IsPreview);
        Thread.Sleep(150);

        // The capped grid was not compromised, so it IS the full grid: one run, one event.
        var results = collector.Results;
        Assert.Single(results);
        Assert.False(results[0].IsPreview);
        Assert.False(results[0].Trace.Grid.Compromised);
    }

    [Fact]
    public void SubmitDuringTheIdleWaitSupersedesTheFullResolutionPass()
    {
        using var runner = new LiveTraceRunner(
            new SimulationSession(),
            debounce: TimeSpan.FromMilliseconds(1),
            fullResolutionIdle: TimeSpan.FromMilliseconds(400));
        var collector = new Collector(runner);

        var first = LongLine(150);
        var second = LongLine(170);
        runner.Submit(first, Tdr1502C);
        collector.WaitForResult(r => r.IsPreview); // first's preview is out; idle wait running
        runner.Submit(second, Tdr1502C);

        var expectedSecondFinal = TdrSimulator.Simulate(second, Tdr1502C);
        collector.WaitForResult(r => !r.IsPreview && SameTrace(r.Trace, expectedSecondFinal));

        // The first request's full-resolution pass must have been cancelled, silently.
        var expectedFirstFinal = TdrSimulator.Simulate(first, Tdr1502C);
        Assert.Empty(collector.Failures);
        Assert.DoesNotContain(collector.Results, r => !r.IsPreview && SameTrace(r.Trace, expectedFirstFinal));
    }

    [Fact]
    public void InvalidNetworkRaisesRunFailedNotACrash()
    {
        using var runner = new LiveTraceRunner(
            new SimulationSession(), debounce: TimeSpan.FromMilliseconds(1));
        var collector = new Collector(runner);

        runner.Submit(Network(new Load(N(0), LoadKind.Resistor, ROhms: 50)), Tdr1502C);
        var failure = collector.WaitForFailure();

        Assert.IsType<ArgumentException>(failure);
        Assert.Empty(collector.Results);
    }

    [Fact]
    public void DisposeStopsCleanlyMidRun()
    {
        var runner = new LiveTraceRunner(
            new SimulationSession(), debounce: TimeSpan.FromMilliseconds(1));
        var collector = new Collector(runner);

        runner.Submit(LongLine(150), Tdr1502C);
        runner.Dispose(); // likely lands mid-debounce or mid-preview; must not hang or throw
        runner.Dispose(); // idempotent
        runner.Submit(ShortLine(5), Tdr1502C); // no-op after dispose, must not throw

        Assert.Empty(collector.Failures);
    }
}
