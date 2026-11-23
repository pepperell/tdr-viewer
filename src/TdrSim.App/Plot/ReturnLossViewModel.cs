using CommunityToolkit.Mvvm.ComponentModel;
using TdrSim.Core.Engine;

namespace TdrSim.App.Plot;

/// <summary>
/// Display state for the return-loss tab: |Gamma(f)| in dB of the network on screen,
/// the frequency-domain twin of the TDR trace. The shell hands over each published
/// trace while the tab is visible; the view (ReturnLossPanel.xaml.cs) owns the
/// ScottPlot calls.
/// </summary>
public sealed partial class ReturnLossViewModel : ObservableObject
{
    /// <summary>Shown instead of the plot (nothing simulated yet, broken network).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "Open this tab with a network connected to see its return loss.";

    /// <summary>Worst match in the instrument's band, and what the curve is referenced to.</summary>
    [ObservableProperty]
    private string _caption = "";

    [ObservableProperty]
    private string _hoverReadout = "";

    /// <summary>Logarithmic frequency axis (view-only).</summary>
    [ObservableProperty]
    private bool _logFrequency;

    public bool HasMessage => Message.Length > 0;

    public ReturnLossCurve? Curve { get; private set; }

    public event Action? Changed;

    partial void OnLogFrequencyChanged(bool value) => Changed?.Invoke();

    public void Set(ReturnLossCurve curve)
    {
        Curve = curve;
        Message = "";
        var (worstHz, worstMagnitude) = curve.WorstUpTo(curve.InstrumentBandwidthHz);
        Caption = $"worst match up to the instrument's {FormatFrequency(curve.InstrumentBandwidthHz)} bandwidth: "
            + $"{FormatReturnLoss(worstMagnitude)} return loss at {FormatFrequency(worstHz)}"
            + $" (VSWR {FormatVswr(worstMagnitude)})"
            + $"  ·  referenced to {curve.ZRefOhms:0.##} Ω"
            + "  ·  the network's own Γ(f), before the instrument's edge";
        Changed?.Invoke();
    }

    public void Clear(string message)
    {
        Curve = null;
        Message = message;
        Caption = "";
        HoverReadout = "";
        Changed?.Invoke();
    }

    public void SetHover(double? frequencyHz)
    {
        if (frequencyHz is not { } f || Curve is not { } curve
            || f < 0 || f > curve.FrequencyHz[^1])
        {
            HoverReadout = "";
            return;
        }

        int n = curve.IndexAt(f);
        double magnitude = curve.GammaMagnitude[n];
        HoverReadout = $"f = {FormatFrequency(curve.FrequencyHz[n])}"
            + $"    |Γ| = {magnitude:0.0000}"
            + $"    return loss {FormatReturnLoss(magnitude)}"
            + $"    VSWR {FormatVswr(magnitude)}"
            + $"    mismatch loss {ReturnLoss.MismatchLossDb(Math.Min(magnitude, 0.999999)):0.###} dB";
    }

    public static string FormatFrequency(double hz) => hz switch
    {
        >= 1e9 => $"{hz / 1e9:0.###} GHz",
        >= 1e6 => $"{hz / 1e6:0.###} MHz",
        >= 1e3 => $"{hz / 1e3:0.###} kHz",
        0 => "DC",
        _ => $"{hz:0.###} Hz",
    };

    private static string FormatReturnLoss(double magnitude)
    {
        double db = ReturnLoss.Db(magnitude);
        return db >= ReturnLoss.FloorDb ? $"≥ {ReturnLoss.FloorDb:0} dB" : $"{db:0.0} dB";
    }

    private static string FormatVswr(double magnitude)
    {
        double vswr = ReturnLoss.Vswr(magnitude);
        return vswr > 999 ? "> 999" : $"{vswr:0.00}";
    }
}
