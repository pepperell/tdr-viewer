using System.Numerics;
using TdrSim.Core.Catalog;

namespace TdrSim.Core.Engine;

/// <summary>
/// Debug/validation switches for the per-frequency cable evaluation. The non-causal
/// variants exist solely so tests can demonstrate the artifacts they produce
/// (pre-edge precursor); production code always uses <see cref="Default"/>.
/// </summary>
public readonly record struct CableEvalOptions(bool CausalSkin, bool CausalDielectric)
{
    public static CableEvalOptions Default => new(true, true);
}

/// <summary>
/// Per-cable engine coefficients derived once from a <see cref="CableSpec"/>, plus the
/// per-frequency RLGC evaluation.
///
/// Parameter precedence: (Z0, VF) is the primary electrical pair - it determines
/// reflections and timing, which is what a TDR shows. C and L_ext are derived from it;
/// the datasheet capacitance is only a cross-check (UI-level warning, not used here).
/// </summary>
public sealed class CableModel
{
    private readonly double _epsInf;
    private readonly double _epsD;

    private CableModel(double z0, double velocityFactor, double r0PerM, LossCoefficients loss, double dk)
    {
        Z0 = z0;
        VelocityFactor = velocityFactor;
        VelocityMPerS = velocityFactor * ComplexMath.C0;
        CPerM = 1 / (z0 * VelocityMPerS);
        LPerM = z0 / VelocityMPerS;
        R0PerM = r0PerM;
        RsOhmsPerMSqrtHz = 2 * z0 * loss.K1NpPerMSqrtHz;
        TanDelta = loss.K2NpPerMHz / (Math.PI * CPerM * z0);
        Dk = dk;
        (_epsInf, _epsD) = Dielectric.Identify(dk, TanDelta);
    }

    /// <summary>Raw line constants: loss terms are given, not fitted.</summary>
    private CableModel(double z0, double velocityFactor, RlgcParameters rlgc, double dk)
    {
        Z0 = z0;
        VelocityFactor = velocityFactor;
        VelocityMPerS = velocityFactor * ComplexMath.C0;
        CPerM = 1 / (z0 * VelocityMPerS);
        LPerM = z0 / VelocityMPerS;
        R0PerM = rlgc.ROhmsPerM;
        G0PerM = rlgc.GSiemensPerM;
        RsOhmsPerMSqrtHz = rlgc.SkinOhmsPerMSqrtHz;
        TanDelta = rlgc.TanDelta;
        Dk = dk;
        (_epsInf, _epsD) = Dielectric.Identify(dk, TanDelta);
    }

    public double Z0 { get; }
    public double VelocityFactor { get; }
    public double VelocityMPerS { get; }

    /// <summary>Derived from (Z0, VF): C = 1/(Z0*v).</summary>
    public double CPerM { get; }

    /// <summary>Derived from (Z0, VF): L = Z0/v.</summary>
    public double LPerM { get; }

    /// <summary>Loop DC resistance, ohms/m.</summary>
    public double R0PerM { get; }

    /// <summary>DC shunt conductance, S/m; nonzero only for raw-RLGC custom cables.</summary>
    public double G0PerM { get; }

    /// <summary>Skin-effect coefficient: R_skin(f) = Rs*sqrt(f), ohms/(m*sqrt(Hz)).</summary>
    public double RsOhmsPerMSqrtHz { get; }

    /// <summary>Dielectric loss tangent at <see cref="Dielectric.FRefHz"/>.</summary>
    public double TanDelta { get; }

    /// <summary>Real relative permittivity at the reference frequency.</summary>
    public double Dk { get; }

    public static CableModel FromSpec(CableSpec spec)
    {
        double dk = spec.DielectricConstant ?? 1 / (spec.VelocityFactor * spec.VelocityFactor);
        if (spec.Rlgc is { } rlgc)
            return new CableModel(spec.Z0Ohms, spec.VelocityFactor, rlgc, dk);
        double r0 = (spec.RdcCenterOhmsPerKm + spec.RdcShieldOhmsPerKm) / 1000;
        return new CableModel(spec.Z0Ohms, spec.VelocityFactor, r0, LossFit.FromSpec(spec), dk);
    }

    /// <summary>
    /// Per-unit-length series impedance Z'(f) and shunt admittance Y'(f).
    ///
    /// Causal skin effect contributes sqrt(f)*(1+j)*Rs - the imaginary part (internal
    /// inductance) is mandatory; dropping it makes the response non-causal and produces
    /// a precursor artifact ahead of reflected edges.
    ///
    /// Causal dielectric scales jwC by eps(f)/DK (Djordjevic-Sarkar), whose real part
    /// yields G(f) and imaginary part the dispersive C(f). The non-causal fallback is a
    /// constant tan delta.
    /// </summary>
    public (Complex ZPerM, Complex YPerM) EvaluatePerMeter(double fHz, CableEvalOptions options)
    {
        double w = 2 * Math.PI * fHz;
        double rSkin = RsOhmsPerMSqrtHz * Math.Sqrt(fHz);

        Complex z = options.CausalSkin
            ? new Complex(R0PerM + rSkin, rSkin + w * LPerM)
            : new Complex(R0PerM + rSkin, w * LPerM);

        Complex epsNormalized = options.CausalDielectric
            ? Dielectric.Evaluate(_epsInf, _epsD, fHz) / Dk
            : new Complex(1, -TanDelta);

        Complex y = new Complex(0, w * CPerM) * epsNormalized;
        if (G0PerM != 0)
            y += G0PerM;
        return (z, y);
    }
}
