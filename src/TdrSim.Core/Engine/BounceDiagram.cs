using TdrSim.Core.Model;

namespace TdrSim.Core.Engine;

public sealed record BounceOptions
{
    /// <summary>Rays launched after this time are not followed.</summary>
    public required double MaxTimeS { get; init; }

    /// <summary>Rays weaker than this (in rho units) are dropped.</summary>
    public double MinAmplitude { get; init; } = 1e-3;

    /// <summary>Hard cap; hitting it sets <see cref="BounceDiagram.Truncated"/>.</summary>
    public int MaxRays { get; init; } = 2000;
}

/// <summary>A uniform line section of the lattice: nominal Z0 and delay only.</summary>
public sealed record BounceSection(
    string NodeId, bool IsInterface, string Name, double Z0Ohms, double DelayS, double LengthM);

/// <summary>
/// A scattering plane between two sections (index 0 is the instrument port, with the
/// instrument as a semi-infinite Z_ref line on its left; the last one is the far end).
/// Coefficients are for voltage waves: *FromLeft for a wave travelling away from the
/// instrument, *FromRight for one travelling back toward it.
/// </summary>
public sealed record BounceInterface
{
    /// <summary>One-way delay from the port.</summary>
    public required double DelayS { get; init; }

    /// <summary>Physical distance from the port.</summary>
    public required double PositionM { get; init; }

    /// <summary>Document nodes sitting on this plane: the owners of its lumps, or of
    /// the two adjoining sections when it is a bare impedance step.</summary>
    public required IReadOnlyList<string> NodeIds { get; init; }

    public required double RhoFromLeft { get; init; }

    public required double TauFromLeft { get; init; }

    public required double RhoFromRight { get; init; }

    public required double TauFromRight { get; init; }

    /// <summary>A reactive part sits here and was replaced by its DC limit.</summary>
    public required bool IsReactiveApprox { get; init; }

    /// <summary>True for the far end (nothing transmits).</summary>
    public required bool IsTerminal { get; init; }
}

/// <summary>One coefficient in a ray's amplitude product.</summary>
public readonly record struct BounceFactor(bool IsTransmission, int Interface, bool FromRight, double Value)
{
    public override string ToString()
        => $"{(IsTransmission ? 'τ' : 'ρ')}{Interface}{(FromRight ? "′" : "")}";
}

/// <summary>A wave crossing one section. Coincident waves are merged, as on paper.</summary>
public sealed record BounceRay
{
    public required int FromInterface { get; init; }

    public required int ToInterface { get; init; }

    public required double StartS { get; init; }

    public required double EndS { get; init; }

    /// <summary>Wave amplitude relative to the incident step.</summary>
    public required double Amplitude { get; init; }

    /// <summary>How many distinct paths were summed into this ray.</summary>
    public required int PathCount { get; init; }

    /// <summary>The rho/tau product behind <see cref="Amplitude"/>; null when
    /// several paths merged.</summary>
    public required IReadOnlyList<BounceFactor>? Factors { get; init; }
}

/// <summary>A wave emerging back into the instrument: one step on the TDR trace.</summary>
public sealed record BounceArrival(double TimeS, double Amplitude, int PathCount, IReadOnlyList<BounceFactor>? Factors);

/// <summary>
/// The textbook lossless lattice (bounce) diagram of a chain network - deliberately
/// *not* the engine. Cables contribute nominal Z0 and delay only; each interface
/// scatters by the real ABCD product of the resistive lumps sitting on it (shunt G,
/// series R) between the two line impedances; reactive parts use their DC limit and
/// loss is ignored (<see cref="IsApproximate"/>). The gap between this staircase and
/// the simulated trace is the lesson. Nothing is idealized away: automatically
/// inserted connector hardware appears as the short real sections it is.
/// </summary>
public sealed class BounceDiagram
{
    private const double ShortConductance = 1e12;

    private BounceDiagram()
    {
    }

    public required IReadOnlyList<BounceSection> Sections { get; init; }

    /// <summary><see cref="Sections"/>.Count + 1 entries.</summary>
    public required IReadOnlyList<BounceInterface> Interfaces { get; init; }

    public required IReadOnlyList<BounceRay> Rays { get; init; }

    /// <summary>Time-ordered; the running sum is the lattice's rho staircase.</summary>
    public required IReadOnlyList<BounceArrival> PortArrivals { get; init; }

    /// <summary>A lossy cable or a reactive part is present: amplitudes are the
    /// lossless/DC idealization.</summary>
    public required bool IsApproximate { get; init; }

    /// <summary>The ray cap was hit; later rays are missing.</summary>
    public required bool Truncated { get; init; }

    /// <summary>Null when the network is not a simple chain (branches, loops).</summary>
    public static BounceDiagram? Build(
        NetworkModel model,
        IReadOnlyList<ElementOrigin>? origins,
        double zRefOhms,
        BounceOptions options)
    {
        if (zRefOhms <= 0)
            throw new ArgumentOutOfRangeException(nameof(zRefOhms), zRefOhms, "Reference impedance must be > 0.");
        if (origins is not null && origins.Count != model.Elements.Count)
            throw new ArgumentException(
                $"{origins.Count} origins for {model.Elements.Count} elements.", nameof(origins));

        var chain = ChainExtractor.TryExtract(model);
        if (chain is null)
            return null;

        // Records compare by value, so identical parallel elements would collide in
        // an ordinary dictionary: look origins up by reference.
        var originOf = new Dictionary<Element, ElementOrigin>(ReferenceEqualityComparer.Instance);
        if (origins is not null)
        {
            for (int i = 0; i < origins.Count; i++)
                originOf[model.Elements[i]] = origins[i];
        }

        ElementOrigin Origin(Element e) => originOf.GetValueOrDefault(e) ?? new ElementOrigin("", false);

        bool approximate = false;
        var sections = new List<BounceSection>();
        var planes = new List<Plane>();
        var pending = new Plane();

        void AddLoads(IReadOnlyList<Load> loads)
        {
            foreach (var load in loads)
            {
                var (isShort, y) = LoadModel.ShuntAdmittance(load, 0);
                pending.Shunt(isShort ? ShortConductance : y.Real);
                pending.Owners.Add(Origin(load).NodeId);
                bool reactive = load.Kind is LoadKind.Capacitor or LoadKind.Inductor
                    || (load.Kind is LoadKind.SeriesRlc or LoadKind.ParallelRlc
                        && (load.LHenries is not null || load.CFarads is not null));
                pending.Reactive |= reactive;
            }
        }

        foreach (var link in chain.Links)
        {
            AddLoads(link.NearLoads);
            switch (link.TwoPort)
            {
                case Connector connector:
                    pending.Series(connector.Spec.SeriesResistanceOhms);
                    pending.Owners.Add(Origin(connector).NodeId);
                    pending.Reactive |= connector.Spec.SeriesInductanceNh != 0
                        || connector.Spec.ShuntCapacitanceNearPf != 0
                        || connector.Spec.ShuntCapacitanceFarPf != 0;
                    break;

                case CableSegment cable:
                {
                    var origin = Origin(cable);
                    planes.Add(pending);
                    pending = new Plane();
                    sections.Add(new BounceSection(
                        origin.NodeId,
                        origin.IsInterface,
                        cable.Spec.Name,
                        cable.Spec.Z0Ohms,
                        cable.LengthM / (cable.Spec.VelocityFactor * ComplexMath.C0),
                        cable.LengthM));
                    approximate |= cable.Spec.Attenuation is not null
                        || cable.Spec.InsertionLossCoefficients is not null
                        || cable.Spec.RdcCenterOhmsPerKm + cable.Spec.RdcShieldOhmsPerKm > 0;
                    break;
                }
            }
        }

        AddLoads(chain.TerminalLoads);
        planes.Add(pending);

        // Scattering per plane.
        var interfaces = new List<BounceInterface>(planes.Count);
        double delay = 0, position = 0;
        for (int j = 0; j < planes.Count; j++)
        {
            var plane = planes[j];
            bool terminal = j == planes.Count - 1;
            double zLeft = j == 0 ? zRefOhms : sections[j - 1].Z0Ohms;
            double rhoL, tauL, rhoR, tauR;
            if (terminal)
            {
                // One-port: the lumps looking into an open end.
                rhoL = plane.C == 0
                    ? 1
                    : (plane.A / plane.C - zLeft) / (plane.A / plane.C + zLeft);
                tauL = rhoR = tauR = 0;
            }
            else
            {
                double zRight = sections[j].Z0Ohms;
                double zinL = (plane.A * zRight + plane.B) / (plane.C * zRight + plane.D);
                rhoL = (zinL - zLeft) / (zinL + zLeft);
                tauL = (1 + rhoL) / (plane.A + plane.B / zRight);
                double zinR = (plane.D * zLeft + plane.B) / (plane.C * zLeft + plane.A);
                rhoR = (zinR - zRight) / (zinR + zRight);
                tauR = (1 + rhoR) / (plane.D + plane.B / zLeft);
            }

            var owners = plane.Owners.Where(id => id.Length > 0).Distinct().ToList();
            if (owners.Count == 0)
            {
                if (j > 0 && sections[j - 1].NodeId.Length > 0)
                    owners.Add(sections[j - 1].NodeId);
                if (!terminal && sections[j].NodeId.Length > 0 && !owners.Contains(sections[j].NodeId))
                    owners.Add(sections[j].NodeId);
            }

            approximate |= plane.Reactive;
            interfaces.Add(new BounceInterface
            {
                DelayS = delay,
                PositionM = position,
                NodeIds = owners,
                RhoFromLeft = rhoL,
                TauFromLeft = tauL,
                RhoFromRight = rhoR,
                TauFromRight = tauR,
                IsReactiveApprox = plane.Reactive,
                IsTerminal = terminal,
            });

            if (!terminal)
            {
                delay += sections[j].DelayS;
                position += sections[j].LengthM;
            }
        }

        var (rays, arrivals, truncated) = Propagate(sections, interfaces, options);
        return new BounceDiagram
        {
            Sections = sections,
            Interfaces = interfaces,
            Rays = rays,
            PortArrivals = arrivals,
            IsApproximate = approximate,
            Truncated = truncated,
        };
    }

    /// <summary>The lattice's predicted rho at a displayed time: the sum of every
    /// port arrival up to and including it.</summary>
    public double StaircaseAt(double timeS)
    {
        double sum = 0;
        foreach (var arrival in PortArrivals)
        {
            if (arrival.TimeS > timeS)
                break;
            sum += arrival.Amplitude;
        }

        return sum;
    }

    // ---- wavefront propagation ----

    /// <summary>Real ABCD product of the lumps on one plane, in source-to-far order.</summary>
    private sealed class Plane
    {
        public double A = 1, B, C, D = 1;

        public List<string> Owners { get; } = [];

        public bool Reactive { get; set; }

        /// <summary>Right-multiply by a shunt conductance [[1,0],[G,1]].</summary>
        public void Shunt(double g)
        {
            A += B * g;
            C += D * g;
        }

        /// <summary>Right-multiply by a series resistance [[1,R],[0,1]].</summary>
        public void Series(double r)
        {
            B += A * r;
            D += C * r;
        }
    }

    private sealed class Wave
    {
        public double Amplitude;
        public int PathCount;
        public IReadOnlyList<BounceFactor>? Factors;
    }

    private static (List<BounceRay> Rays, List<BounceArrival> Arrivals, bool Truncated) Propagate(
        List<BounceSection> sections, List<BounceInterface> interfaces, BounceOptions options)
    {
        var rays = new List<BounceRay>();
        var arrivals = new List<BounceArrival>();
        bool truncated = false;

        // Coincident arrivals merge: the key quantizes time far below any delay.
        double minDelay = sections.Count > 0 ? sections.Min(s => s.DelayS) : 1e-9;
        double quantum = Math.Max(minDelay * 1e-6, 1e-18);
        var pending = new Dictionary<(int Interface, bool FromRight, long Tick), Wave>();
        var queue = new PriorityQueue<(int Interface, bool FromRight, long Tick), double>();

        void Arrive(int iface, bool fromRight, double time, double amplitude, IReadOnlyList<BounceFactor>? factors, int paths)
        {
            var key = (iface, fromRight, (long)Math.Round(time / quantum));
            if (pending.TryGetValue(key, out var wave))
            {
                wave.Amplitude += amplitude;
                wave.PathCount += paths;
                wave.Factors = null;
            }
            else
            {
                pending[key] = new Wave { Amplitude = amplitude, PathCount = paths, Factors = factors };
                queue.Enqueue(key, time);
            }
        }

        static IReadOnlyList<BounceFactor>? Extend(IReadOnlyList<BounceFactor>? factors, BounceFactor next)
            => factors is null ? null : [.. factors, next];

        // Launch a wave from interface `from` across the adjoining section.
        void Launch(int from, bool rightward, double time, double amplitude, IReadOnlyList<BounceFactor>? factors, int paths)
        {
            if (Math.Abs(amplitude) < options.MinAmplitude || time > options.MaxTimeS)
                return;
            if (rays.Count >= options.MaxRays)
            {
                truncated = true;
                return;
            }

            int section = rightward ? from : from - 1;
            int to = rightward ? from + 1 : from - 1;
            double end = time + sections[section].DelayS;
            rays.Add(new BounceRay
            {
                FromInterface = from,
                ToInterface = to,
                StartS = time,
                EndS = end,
                Amplitude = amplitude,
                PathCount = paths,
                Factors = factors,
            });
            Arrive(to, fromRight: !rightward, end, amplitude, factors, paths);
        }

        void Emerge(double time, double amplitude, IReadOnlyList<BounceFactor>? factors, int paths)
        {
            if (Math.Abs(amplitude) < options.MinAmplitude)
                return;
            arrivals.Add(new BounceArrival(time, amplitude, paths, factors));
        }

        // The unit incident step reaches the port plane from the instrument at t = 0.
        Arrive(0, fromRight: false, 0, 1, [], 1);

        while (queue.TryDequeue(out var key, out double time))
        {
            var wave = pending[key];
            pending.Remove(key);
            var plane = interfaces[key.Interface];
            int j = key.Interface;
            if (!key.FromRight)
            {
                // Travelling away from the instrument: reflect back, transmit onward.
                var reflected = Extend(wave.Factors, new BounceFactor(false, j, false, plane.RhoFromLeft));
                if (j == 0)
                    Emerge(time, wave.Amplitude * plane.RhoFromLeft, reflected, wave.PathCount);
                else
                    Launch(j, rightward: false, time, wave.Amplitude * plane.RhoFromLeft, reflected, wave.PathCount);

                if (!plane.IsTerminal)
                {
                    Launch(j, rightward: true, time, wave.Amplitude * plane.TauFromLeft,
                        Extend(wave.Factors, new BounceFactor(true, j, false, plane.TauFromLeft)), wave.PathCount);
                }
            }
            else
            {
                // Travelling back: reflect away again, transmit toward the instrument.
                Launch(j, rightward: true, time, wave.Amplitude * plane.RhoFromRight,
                    Extend(wave.Factors, new BounceFactor(false, j, true, plane.RhoFromRight)), wave.PathCount);

                var transmitted = Extend(wave.Factors, new BounceFactor(true, j, true, plane.TauFromRight));
                if (j == 0)
                    Emerge(time, wave.Amplitude * plane.TauFromRight, transmitted, wave.PathCount);
                else
                    Launch(j, rightward: false, time, wave.Amplitude * plane.TauFromRight, transmitted, wave.PathCount);
            }
        }

        arrivals.Sort((x, y) => x.TimeS.CompareTo(y.TimeS));
        return (rays, arrivals, truncated);
    }
}
