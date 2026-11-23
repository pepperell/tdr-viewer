using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScottPlot;
using TdrSim.Core.Engine;

namespace TdrSim.App.Plot;

/// <summary>
/// The ScottPlot side of the bounce-diagram tab. Two stacked plots share one time
/// axis (both always-fit, input disabled, left axis width pinned so the data areas
/// line up): on top the simulated rho with the lattice's staircase over it, below the
/// lattice - time to the right, distance from the port downward, so every ray that
/// returns to the top edge sits directly under the step it causes.
/// </summary>
public partial class BouncePanel : UserControl
{
    private const float LeftAxisPx = 78;
    private const int MaxDrawnRays = 700;
    private const int MaxRayLabels = 40;
    private const double HitTestPx = 6;
    private const double MinLabelGapPx = 13;

    private BounceViewModel? _vm;
    private List<BounceRay> _drawnRays = [];
    private ScottPlot.Plottables.LinePlot? _hoverLine;

    public BouncePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        foreach (var control in new[] { TracePlot, LatticePlot })
        {
            control.UserInputProcessor.IsEnabled = false; // always-fit view: no pan/zoom
            control.Plot.Axes.Left.MinimumSize = LeftAxisPx;
            control.Plot.Axes.Left.MaximumSize = LeftAxisPx;
            control.Plot.Axes.Right.MinimumSize = 24;
            control.Plot.Axes.Right.MaximumSize = 24;
        }

        // One time axis: the lattice's tick labels serve both plots.
        TracePlot.Plot.Axes.Bottom.TickLabelStyle.IsVisible = false;
        TracePlot.Plot.Axes.Bottom.MinimumSize = 6;
        TracePlot.Plot.Axes.Bottom.MaximumSize = 6;
        TracePlot.Plot.YLabel("mρ");
        LatticePlot.Plot.YLabel("distance (m)");
        LatticePlot.Plot.XLabel("time (ns)");
        LatticePlot.PreviewMouseMove += OnLatticeMouseMove;
        LatticePlot.MouseLeave += (_, _) => SetHover(null);
        SizeChanged += (_, _) => Replot(); // label de-cluttering depends on pixel size
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
            _vm.Changed -= Replot;
        _vm = e.NewValue as BounceViewModel;
        if (_vm is not null)
            _vm.Changed += Replot;
        Replot();
    }

    private void Replot()
    {
        var tracePlot = TracePlot.Plot;
        var lattice = LatticePlot.Plot;
        tracePlot.Clear();
        lattice.Clear();
        _drawnRays = [];
        _hoverLine = null;

        if (_vm is not { Diagram: { } diagram, Trace: { } trace })
        {
            TracePlot.Refresh();
            LatticePlot.Refresh();
            return;
        }

        double xMax = _vm.MaxTimeS * 1e9;
        double xMin = -0.03 * xMax;

        // ---- top: simulated rho and the lattice's prediction ----
        var simulated = tracePlot.Add.Signal(Scaled(trace.Rho, 1e3), trace.Grid.DtS * 1e9);
        simulated.Data.XOffset = trace.TimeS[0] * 1e9;
        simulated.LineWidth = 2;

        var stepXs = new List<double> { xMin };
        var stepYs = new List<double> { 0 };
        double level = 0;
        foreach (var arrival in diagram.PortArrivals)
        {
            double x = arrival.TimeS * 1e9;
            if (x > xMax)
                break;
            stepXs.Add(x);
            stepYs.Add(level * 1e3);
            level += arrival.Amplitude;
            stepXs.Add(x);
            stepYs.Add(level * 1e3);
        }

        stepXs.Add(xMax);
        stepYs.Add(level * 1e3);
        var staircase = tracePlot.Add.ScatterLine(stepXs.ToArray(), stepYs.ToArray());
        staircase.Color = Colors.OrangeRed;
        staircase.LineWidth = 1.5f;
        staircase.LinePattern = LinePattern.Dashed;

        double yMin = stepYs.Min(), yMax = stepYs.Max();
        int kEnd = Math.Min(trace.Rho.Length - 1,
            (int)((_vm.MaxTimeS + trace.EdgeDelayS) / trace.Grid.DtS));
        for (int k = 0; k <= kEnd; k++)
        {
            yMin = Math.Min(yMin, trace.Rho[k] * 1e3);
            yMax = Math.Max(yMax, trace.Rho[k] * 1e3);
        }

        double pad = Math.Max((yMax - yMin) * 0.1, 5);
        tracePlot.Axes.SetLimits(xMin, xMax, yMin - pad, yMax + pad);

        // ---- bottom: the lattice ----
        double depth = Math.Max(diagram.Interfaces[^1].PositionM, 1e-6);
        double yPad = depth * 0.06;
        double plotHeightPx = Math.Max(LatticePlot.ActualHeight - 50, 60);
        double pxPerMetre = plotHeightPx / (depth + 2 * yPad);

        foreach (var plane in diagram.Interfaces)
        {
            var line = lattice.Add.HorizontalLine(plane.PositionM);
            line.Color = plane.IsReactiveApprox ? Colors.Orange.WithAlpha(0.8) : Colors.Gray.WithAlpha(0.6);
            line.LineWidth = 1;
            if (plane.IsReactiveApprox)
                line.LinePattern = LinePattern.Dashed;
        }

        AddLabels(diagram, xMin, xMax, pxPerMetre);

        // Strongest rays only, weakest drawn first so the strong ones stay on top.
        _drawnRays = [.. diagram.Rays
            .OrderByDescending(r => Math.Abs(r.Amplitude))
            .Take(MaxDrawnRays)];
        for (int i = _drawnRays.Count - 1; i >= 0; i--)
        {
            var ray = _drawnRays[i];
            double magnitude = Math.Min(1, Math.Abs(ray.Amplitude));
            var segment = lattice.Add.Line(
                ray.StartS * 1e9, diagram.Interfaces[ray.FromInterface].PositionM,
                ray.EndS * 1e9, diagram.Interfaces[ray.ToInterface].PositionM);
            segment.Color = (ray.Amplitude >= 0 ? Colors.RoyalBlue : Colors.Crimson)
                .WithAlpha(0.25 + 0.75 * Math.Sqrt(magnitude));
            segment.LineWidth = (float)(1 + 2 * magnitude);
        }

        // Strongest first; a label that would overlap one already placed is skipped.
        double pxPerNs = Math.Max(LatticePlot.ActualWidth - LeftAxisPx - 24, 100) / (xMax - xMin);
        var placed = new List<(double X, double Y)>();
        foreach (var ray in _drawnRays)
        {
            if (placed.Count >= MaxRayLabels)
                break;
            double y = (diagram.Interfaces[ray.FromInterface].PositionM
                + diagram.Interfaces[ray.ToInterface].PositionM) / 2;
            double span = Math.Abs(diagram.Interfaces[ray.FromInterface].PositionM
                - diagram.Interfaces[ray.ToInterface].PositionM);
            if (span * pxPerMetre < MinLabelGapPx)
                continue; // a hairline section has no room for a label
            double px = (ray.StartS + ray.EndS) / 2 * 1e9 * pxPerNs;
            double py = y * pxPerMetre;
            if (placed.Any(p => Math.Abs(p.X - px) < 46 && Math.Abs(p.Y - py) < MinLabelGapPx))
                continue;
            placed.Add((px, py));
            var text = lattice.Add.Text($"{ray.Amplitude:+0.000;−0.000}", (ray.StartS + ray.EndS) / 2 * 1e9, y);
            text.LabelFontSize = 10;
            text.LabelFontColor = ray.Amplitude >= 0 ? Colors.Navy : Colors.DarkRed;
            text.LabelBackgroundColor = Colors.White.WithAlpha(0.75);
            text.LabelAlignment = Alignment.MiddleCenter;
        }

        // Distance grows downward: the port (and the trace above it) is at the top.
        lattice.Axes.SetLimits(xMin, xMax, depth + yPad, -yPad);
        TracePlot.Refresh();
        LatticePlot.Refresh();
    }

    /// <summary>Section names between the interface lines, lump names on them; labels
    /// that would land within a few pixels of the previous one are merged away.</summary>
    private void AddLabels(BounceDiagram diagram, double xMin, double xMax, double pxPerMetre)
    {
        var labels = new List<(double Y, string Text)>();
        for (int j = 0; j < diagram.Interfaces.Count; j++)
        {
            var plane = diagram.Interfaces[j];
            var adjacent = new HashSet<string>();
            if (j > 0)
                adjacent.Add(diagram.Sections[j - 1].NodeId);
            if (j < diagram.Sections.Count)
                adjacent.Add(diagram.Sections[j].NodeId);
            var lumps = plane.NodeIds.Where(id => !adjacent.Contains(id)).Select(_vm!.TitleOf).ToList();
            if (lumps.Count > 0)
                labels.Add((plane.PositionM, string.Join(" + ", lumps) + (plane.IsReactiveApprox ? " (DC limit)" : "")));
            else if (j == 0)
                labels.Add((0, "TDR port"));
            else if (plane.IsTerminal)
                labels.Add((plane.PositionM, "open end"));

            if (j < diagram.Sections.Count)
            {
                var section = diagram.Sections[j];
                string name = section.IsInterface || section.NodeId.Length == 0
                    ? section.Name
                    : _vm.TitleOf(section.NodeId);
                labels.Add((plane.PositionM + section.LengthM / 2, $"{name} · {section.Z0Ohms:0.#} Ω"));
            }
        }

        double lastPx = double.NegativeInfinity;
        double x = xMin + (xMax - xMin) * 0.995;
        foreach (var (y, text) in labels.OrderBy(l => l.Y))
        {
            double px = y * pxPerMetre;
            if (px - lastPx < MinLabelGapPx)
                continue;
            lastPx = px;
            var label = LatticePlot.Plot.Add.Text(text, x, y);
            label.LabelFontSize = 11;
            label.LabelFontColor = Colors.Black.WithAlpha(0.75);
            label.LabelBackgroundColor = Colors.White.WithAlpha(0.7);
            label.LabelAlignment = Alignment.MiddleRight;
        }
    }

    // ---- ray hover ----

    private void OnLatticeMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.Diagram is not { } diagram || _drawnRays.Count == 0)
            return;

        var position = e.GetPosition(LatticePlot);
        double scale = LatticePlot.DisplayScale;
        var mouse = new Pixel((float)(position.X * scale), (float)(position.Y * scale));
        double best = HitTestPx * scale;
        BounceRay? hit = null;
        foreach (var ray in _drawnRays)
        {
            var a = LatticePlot.Plot.GetPixel(new Coordinates(
                ray.StartS * 1e9, diagram.Interfaces[ray.FromInterface].PositionM));
            var b = LatticePlot.Plot.GetPixel(new Coordinates(
                ray.EndS * 1e9, diagram.Interfaces[ray.ToInterface].PositionM));
            double distance = DistanceToSegment(mouse, a, b);
            if (distance < best)
            {
                best = distance;
                hit = ray;
            }
        }

        SetHover(hit);
    }

    private void SetHover(BounceRay? ray)
    {
        if (_vm is null)
            return;
        if (_hoverLine is not null)
        {
            LatticePlot.Plot.Remove(_hoverLine);
            _hoverLine = null;
        }

        if (ray is not null && _vm.Diagram is { } diagram)
        {
            _hoverLine = LatticePlot.Plot.Add.Line(
                ray.StartS * 1e9, diagram.Interfaces[ray.FromInterface].PositionM,
                ray.EndS * 1e9, diagram.Interfaces[ray.ToInterface].PositionM);
            _hoverLine.Color = Colors.Orange;
            _hoverLine.LineWidth = 4;
        }

        _vm.SetHoveredRay(ray);
        LatticePlot.Refresh();
    }

    private static double DistanceToSegment(Pixel p, Pixel a, Pixel b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        double t = lengthSquared == 0
            ? 0
            : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        double cx = a.X + t * dx - p.X, cy = a.Y + t * dy - p.Y;
        return Math.Sqrt(cx * cx + cy * cy);
    }

    private static double[] Scaled(double[] values, double factor)
    {
        var result = new double[values.Length];
        for (int k = 0; k < values.Length; k++)
            result[k] = values[k] * factor;
        return result;
    }
}
