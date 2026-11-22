using System.Numerics;
using TdrSim.Core.Catalog;

namespace TdrSim.Core.Engine;

/// <summary>Per-meter Z'(f)/Y'(f) sampled on a frequency grid; index n is f = n*Df.</summary>
public sealed class CableSpectrum
{
    public required Complex[] ZPerM { get; init; }
    public required Complex[] YPerM { get; init; }
}

/// <summary>
/// App-lifetime caches for interactive recomputes. Everything that is identical from one
/// run to the next lives here: CableSpec -&gt; CableModel derivations, the per-meter cable
/// spectra on a given grid (the Djordjevic-Sarkar + skin-effect evaluation that dominates
/// warm runs), and the instrument's reference edge (a full IFFT otherwise recomputed
/// unchanged every run).
///
/// Keys are value-complete: spectra key on the <see cref="CableModel"/> instance - not on
/// (spec, length) - because the per-meter arrays are length-independent, so length edits
/// still hit; grids quantize M to powers of two, so the (M, dt) part of the key only
/// changes on octave jumps or rise-time changes. All methods are thread-safe, and a
/// cancelled fill never inserts a partial entry.
/// </summary>
public sealed class SimulationSession
{
    private const int SpectrumCapacity = 16;
    private const int EdgeCapacity = 8;

    private readonly object _gate = new();
    private readonly Dictionary<CableSpec, CableModel> _models = [];
    private readonly Dictionary<SpectrumKey, CableSpectrum> _spectra = [];
    private readonly Queue<SpectrumKey> _spectrumOrder = new();
    private readonly Dictionary<EdgeKey, TracePipeline.StepResult> _edges = [];
    private readonly Queue<EdgeKey> _edgeOrder = new();

    /// <summary>Spectrum fills performed (misses); test/diagnostic counter.</summary>
    public int SpectrumComputeCount { get; private set; }

    /// <summary>Spectra currently cached; bounded by eviction.</summary>
    public int SpectrumEntryCount
    {
        get
        {
            lock (_gate)
            {
                return _spectra.Count;
            }
        }
    }

    /// <summary>Cable models derived from specs (misses); test/diagnostic counter.</summary>
    public int ModelComputeCount { get; private set; }

    public CableModel GetModel(CableSpec spec)
    {
        lock (_gate)
        {
            if (_models.TryGetValue(spec, out var model))
                return model;
            ModelComputeCount++;
            return _models[spec] = CableModel.FromSpec(spec);
        }
    }

    public CableSpectrum GetSpectrum(
        CableModel model,
        FrequencyGrid grid,
        CableEvalOptions options,
        CancellationToken cancellationToken = default)
    {
        var key = new SpectrumKey(model, grid.M, grid.DtS, options);
        lock (_gate)
        {
            if (_spectra.TryGetValue(key, out var hit))
                return hit;
        }

        // Filled outside the lock so a cancelled fill can never leave a partial entry;
        // a concurrent duplicate fill is benign (first insert wins below).
        int count = grid.FrequencyCount;
        double df = grid.DfHz;
        var z = new Complex[count];
        var y = new Complex[count];
        Parallel.For(
            0,
            count,
            new ParallelOptions { CancellationToken = cancellationToken },
            n => (z[n], y[n]) = model.EvaluatePerMeter(n * df, options));

        lock (_gate)
        {
            if (_spectra.TryAdd(key, new CableSpectrum { ZPerM = z, YPerM = y }))
            {
                SpectrumComputeCount++;
                _spectrumOrder.Enqueue(key);
                while (_spectra.Count > SpectrumCapacity)
                    _spectra.Remove(_spectrumOrder.Dequeue());
            }

            return _spectra[key];
        }
    }

    /// <summary>The instrument's reference edge (Gamma = 1 synthesis), cached per grid/edge shape.</summary>
    internal TracePipeline.StepResult GetEdgeStep(
        FrequencyGrid grid,
        double riseTimeS,
        double kaiserBeta,
        double edgeDelayS,
        CancellationToken cancellationToken)
    {
        var key = new EdgeKey(grid.M, grid.DtS, riseTimeS, kaiserBeta, edgeDelayS);
        lock (_gate)
        {
            if (_edges.TryGetValue(key, out var hit))
                return hit;
        }

        var ones = new Complex[grid.FrequencyCount];
        Array.Fill(ones, Complex.One);
        var step = TracePipeline.Synthesize(ones, grid, riseTimeS, kaiserBeta, edgeDelayS, cancellationToken);

        lock (_gate)
        {
            if (_edges.TryAdd(key, step))
            {
                _edgeOrder.Enqueue(key);
                while (_edges.Count > EdgeCapacity)
                    _edges.Remove(_edgeOrder.Dequeue());
            }

            return _edges[key];
        }
    }

    // CableModel compares by reference: GetModel guarantees one instance per distinct spec.
    private readonly record struct SpectrumKey(CableModel Model, int M, double DtS, CableEvalOptions Options);

    private readonly record struct EdgeKey(int M, double DtS, double RiseTimeS, double KaiserBeta, double EdgeDelayS);
}
