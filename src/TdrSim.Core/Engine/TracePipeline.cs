using System.Numerics;
using FftFlat;

namespace TdrSim.Core.Engine;

/// <summary>
/// Frequency-domain step synthesis (the VNA "low-pass step" method): shape Gamma(f) with
/// the instrument's Gaussian edge and a Kaiser window, Hermitian-mirror onto the full
/// grid, IFFT to the impulse response, and integrate to the step response.
///
/// The DFT identity sum(h) = X[0] = Gamma(0) guarantees the trace settles at the exact
/// DC reflection; a dedicated unit test pins FftFlat's 1/N inverse normalization, which
/// this relies on.
/// </summary>
internal static class TracePipeline
{
    public sealed record StepResult(double[] Rho, double MaxImagRatio, bool Settled);

    /// <param name="gammaHalf">Gamma at f_n = n*Df, n = 0..M/2.</param>
    /// <param name="edgeDelayS">
    /// Linear-phase delay applied to everything so the zero-phase (acausal-looking)
    /// instrument edge sits fully inside the window instead of wrapping around.
    /// </param>
    public static StepResult Synthesize(
        Complex[] gammaHalf,
        FrequencyGrid grid,
        double riseTimeS,
        double kaiserBeta,
        double edgeDelayS,
        CancellationToken cancellationToken = default)
    {
        int m = grid.M;
        int half = m / 2;
        if (gammaHalf.Length != half + 1)
            throw new ArgumentException($"Expected {half + 1} frequency samples, got {gammaHalf.Length}.");

        cancellationToken.ThrowIfCancellationRequested();

        double f3Db = 0.34 / riseTimeS;
        double gaussCoeff = Math.Log(2) / 2 / (f3Db * f3Db);

        var x = new Complex[m];
        for (int n = 0; n <= half; n++)
        {
            double f = n * grid.DfHz;
            double magnitude = Math.Exp(-gaussCoeff * f * f) * ComplexMath.Kaiser((double)n / half, kaiserBeta);
            x[n] = gammaHalf[n] * Complex.FromPolarCoordinates(magnitude, -2 * Math.PI * f * edgeDelayS);
        }

        // Hermitian symmetry; DC and Nyquist bins must be purely real.
        x[0] = new Complex(x[0].Real, 0);
        x[half] = new Complex(x[half].Real, 0);
        for (int n = 1; n < half; n++)
            x[m - n] = Complex.Conjugate(x[n]);

        cancellationToken.ThrowIfCancellationRequested();
        new FastFourierTransform(m).Inverse(x);
        cancellationToken.ThrowIfCancellationRequested();

        double maxRe = 0, maxIm = 0;
        for (int k = 0; k < m; k++)
        {
            maxRe = Math.Max(maxRe, Math.Abs(x[k].Real));
            maxIm = Math.Max(maxIm, Math.Abs(x[k].Imaginary));
        }

        var rho = new double[m];
        double sum = 0;
        for (int k = 0; k < m; k++)
        {
            sum += x[k].Real;
            rho[k] = sum;
        }

        // Settled when the impulse response has died away by the last tenth of the window.
        double tailMax = 0;
        for (int k = m - m / 10; k < m; k++)
            tailMax = Math.Max(tailMax, Math.Abs(x[k].Real));
        bool settled = tailMax <= Math.Max(1e-12, 1e-4 * maxRe);

        return new StepResult(rho, maxRe > 0 ? maxIm / maxRe : 0, settled);
    }
}
