using System.Numerics;
using TdrSim.Core.Catalog;
using TdrSim.Core.Engine;

namespace TdrSim.Core.Tests;

public class LineStampsTests
{
    /// <summary>
    /// The property test tying M2's stamps to M1's fold: a Pi stamp terminated with Z_L
    /// and solved as a 2x2 nodal system must give the same input impedance as folding
    /// Z_L through TwoPort.Line - the same physics in two very different formulations,
    /// so a sign or convention bug in either one shows up immediately.
    /// </summary>
    [Fact]
    public void PiStampMatchesTwoPortLineAcrossRegimes()
    {
        // Magnitudes span all three regimes (small-series boundary is |gl| = 1e-4);
        // phases run from distortionless (0 deg) to lossless (90 deg), avoiding exact
        // lossless resonances where the Pi form legitimately loses digits.
        double[] glMags = [1e-6, 3e-5, 1e-3, 0.3, 2, 10, 50];
        double[] glPhasesDeg = [0, 30, 60, 85, 90];
        Complex[] z0s = [new(50, 0), new(50, -8), new(377, 0), new(5, -1)];
        Complex[] zLoads = [new(50, 0), new(1e6, 0), new(1e-3, 0), new(25, 40)];

        var y = new Complex[4];
        var rhs = new Complex[2];
        var pivot = new int[2];
        foreach (double mag in glMags)
        {
            foreach (double phaseDeg in glPhasesDeg)
            {
                foreach (var z0 in z0s)
                {
                    Complex gl = Complex.FromPolarCoordinates(mag, phaseDeg * Math.PI / 180);
                    Complex zTotal = gl * z0;
                    Complex yTotal = gl / z0;
                    var stamp = LineStamps.Line(zTotal, yTotal);
                    Assert.False(stamp.IsIdealShort);
                    var line = TwoPort.Line(zTotal, yTotal);

                    foreach (var zL in zLoads)
                    {
                        var (v, i) = line.Apply(zL, Complex.One);
                        Complex zInFold = v / i;

                        // Ladder reduction of the Pi - numerically benign at any gl -
                        // pins the stamp values themselves to 1e-12.
                        Complex zInPi = 1 / (stamp.ShuntYA
                            + 1 / (1 / stamp.SeriesY + 1 / (stamp.ShuntYB + 1 / zL)));
                        double relPi = (zInPi - zInFold).Magnitude / zInFold.Magnitude;
                        Assert.True(relPi <= 1e-12,
                            $"Pi Z_in off by {relPi:e2} at gl = {mag}@{phaseDeg}deg, Z0 = {z0}, ZL = {zL}.");

                        // The same 2x2 through the LU pathway the nodal solver uses.
                        // A near-transparent segment is inherently ill-conditioned in
                        // nodal form (the load hides in tiny differences between huge
                        // Yser entries), so this tolerance scales as eps*|Yser|*|Z_in|.
                        y[0] = stamp.SeriesY + stamp.ShuntYA;
                        y[1] = -stamp.SeriesY;
                        y[2] = -stamp.SeriesY;
                        y[3] = stamp.SeriesY + stamp.ShuntYB + 1 / zL;
                        rhs[0] = Complex.One;
                        rhs[1] = Complex.Zero;
                        Assert.True(ComplexLu.Factor(y, 2, pivot, out _));
                        ComplexLu.Solve(y, 2, pivot, rhs);
                        double relLu = (rhs[0] - zInFold).Magnitude / zInFold.Magnitude;
                        double tol = Math.Max(1e-12, 1e-13 * stamp.SeriesY.Magnitude * zInFold.Magnitude);
                        Assert.True(relLu <= tol,
                            $"LU Z_in off by {relLu:e2} (tol {tol:e2}) at gl = {mag}@{phaseDeg}deg, Z0 = {z0}, ZL = {zL}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void LosslessLineAtDcIsAnIdealShort()
    {
        var stamp = LineStamps.Line(Complex.Zero, Complex.Zero);
        Assert.True(stamp.IsIdealShort);
        Assert.Equal(Complex.Zero, stamp.ShuntYA);
        Assert.Equal(Complex.Zero, stamp.ShuntYB);
    }

    [Fact]
    public void LossyLineAtDcIsExactlyItsLoopResistance()
    {
        // Y'l = 0, Z'l = R: the stamp must be the pure series-resistance stamp.
        var stamp = LineStamps.Line(new Complex(1.704, 0), Complex.Zero);
        Assert.False(stamp.IsIdealShort);
        Assert.Equal(1 / 1.704, stamp.SeriesY.Real, 1e-15);
        Assert.Equal(0, stamp.SeriesY.Imaginary);
        Assert.Equal(Complex.Zero, stamp.ShuntYA);
    }

    [Fact]
    public void VeryLossyLineDecouplesToMatchedZ0()
    {
        // Re(gamma*l) huge: q1 underflows, Yser -> 0 and each shunt -> 1/Z0.
        Complex z0 = new(75, 0);
        Complex gl = new(900, 300);
        var stamp = LineStamps.Line(gl * z0, gl / z0);
        Assert.False(stamp.IsIdealShort);
        Assert.Equal(0, stamp.SeriesY.Magnitude);
        Assert.Equal(1.0 / 75, stamp.ShuntYA.Real, 1e-15);
        Assert.True(Math.Abs(stamp.ShuntYA.Imaginary) < 1e-15);
    }

    [Fact]
    public void ConnectorStampMapsNearToAAndFarToB()
    {
        var spec = new ConnectorSpec
        {
            Id = "test",
            Name = "test",
            SeriesResistanceOhms = 2,
            SeriesInductanceNh = 10,
            ShuntCapacitanceNearPf = 1,
            ShuntCapacitanceFarPf = 3,
        };

        double f = 100e6;
        double w = 2 * Math.PI * f;
        var stamp = LineStamps.Connector(spec, f);
        Assert.False(stamp.IsIdealShort);
        Assert.Equal(w * 1e-12, stamp.ShuntYA.Imaginary, 1e-18);
        Assert.Equal(w * 3e-12, stamp.ShuntYB.Imaginary, 1e-18);
        Complex expectedSeries = 1 / new Complex(2, w * 10e-9);
        Assert.True((stamp.SeriesY - expectedSeries).Magnitude < 1e-15);
    }

    [Fact]
    public void IdealAndPurelyInductiveConnectorsShortAtDc()
    {
        var ideal = new ConnectorSpec
        {
            Id = "i",
            Name = "i",
            SeriesResistanceOhms = 0,
            SeriesInductanceNh = 0,
            ShuntCapacitanceNearPf = 0,
            ShuntCapacitanceFarPf = 0,
        };
        Assert.True(LineStamps.Connector(ideal, 0).IsIdealShort);
        Assert.True(LineStamps.Connector(ideal, 1e9).IsIdealShort);

        var inductive = ideal with { Id = "l", SeriesInductanceNh = 5 };
        Assert.True(LineStamps.Connector(inductive, 0).IsIdealShort);
        Assert.False(LineStamps.Connector(inductive, 1e6).IsIdealShort);
    }
}
