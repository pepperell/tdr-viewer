# TDR Viewer

A Windows desktop application that simulates the output of a Time Domain Reflectometer (TDR)
for a user-drawn network of cables and loads. Build a network of cables, junctions, loops, and
loads on a node-graph canvas; the simulated TDR trace updates live as parameters change.

The physics engine uses frequency-domain synthesis (the same method network analyzers use for
"simulated TDR"): a per-frequency nodal admittance solve of the whole network with causal lossy
transmission-line models, followed by IFFT to the time domain. See [PLAN.md](PLAN.md) for the
full design and [plans/](plans/) for per-milestone plans.

## Build

Requires the .NET 10 SDK (pinned via `global.json`).

```
dotnet build TdrViewer.slnx
dotnet test TdrViewer.slnx
dotnet run --project src/TdrSim.App
```

## Layout

- `src/TdrSim.Core` - simulation engine, network model, cable/instrument catalog (no UI deps)
- `src/TdrSim.App` - WPF application (Nodify canvas + ScottPlot trace)
- `tests/TdrSim.Core.Tests` - engine and catalog unit tests
