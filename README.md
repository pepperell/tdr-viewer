# TDR Viewer

A Windows desktop application that simulates the output of a Time Domain Reflectometer
(TDR) for a user-drawn network of transmission lines and loads. Build a network of
cables, PCB traces, connectors, junctions, loops, faults, and loads on a node-graph
canvas; the simulated TDR trace updates live as you drag sliders. A second view drives
the same network with a repetitive pulse train and shows the voltage waveform at any
probe point — at the load, at a junction, anywhere.

The output is meant to be quantitatively right, not just plausible: correct reflection
amplitudes and timing, skin-effect edge slurring and 1/√t creep on long cables,
dielectric loss, multiple re-reflections and masking, capacitive dips and inductive
bumps with the right time constants, correct DC settling levels, and
instrument-realistic noise floors. Every physics claim is pinned by a closed-form unit
test (224 tests at last count).

## Features

- **Node-graph editor** (Nodify): drag cables/connectors/loads/faults from a sectioned
  palette, wire them freely — tees are just three wires on a port, loops are legal.
- **Live TDR trace** (ScottPlot): debounced background recompute on every edit, with a
  fast preview grid followed by a full-resolution pass; axis modes mV / mρ / Ω and
  time / distance (with a display velocity-factor choice); two draggable measurement
  cursors with Δ readout in both units.
- **Pulse view**: periodic pulse-train stimulus with independent rise time, fall time,
  repetition rate and duty cycle, observed at any probe point; a "# of pulses
  displayed" view setting tiles the steady-state waveform.
- **Catalog**: 28 cables with datasheet-grade parameters — coax from RG-178 to
  LMR-600, twisted pair (Cat3…Cat6A, RS-485, 300 Ω twin-lead), PCB traces (FR-4
  microstrip/stripline, 100 Ω differential pair, Rogers RO4350B, polyimide flex), and
  ideal lossless teaching lines; connector and fault presets; five instrument presets
  with per-class noise floors.
- **Faults**: water-ingress / crushed sections (dielectric change at constant L),
  resistive bridges, series faults — all compiled onto exact line models.
- **Realism**: additive Gaussian noise at the selected instrument's floor with N-sweep
  averaging (σ/√N), settling and grid-resolution warnings.
- **Workflow**: 15 built-in examples in the menu, schema-versioned `.tdrproj` project
  files (open via double-click/command line), undo/redo, reference-trace overlay,
  CSV/PNG export.

## Build & run

Requires the .NET 10 SDK (pinned via `global.json`); Windows (WPF).

```
dotnet build TdrViewer.slnx
dotnet test  TdrViewer.slnx
dotnet run --project src/TdrSim.App
dotnet run --project src/TdrSim.App -- path\to\project.tdrproj
```

---

# Theory of operation — the TDR trace

## Why frequency-domain synthesis

The engine computes the port reflection coefficient Γ(f) of the whole network on a
dense frequency grid and inverse-FFTs it into the time domain. This is exactly how
real vector network analyzers produce "simulated TDR" (Keysight AN 1287-12, Anritsu
11410-00722A) and how field solvers compute TDR from S-parameters. The alternatives
lose: a lattice/bounce diagram is exact only for lossless lines with scalar
reflections and its ray count explodes with loop topologies, while time stepping
(EMTP/Bergeron) needs vector-fitted line models for the same answer with much more
machinery. In the frequency domain, every physical effect — √f conductor loss with
its causality-mandated internal inductance, dispersive dielectrics, complex loads,
re-reflections around loops — is captured *exactly* by evaluating the network at each
frequency, provided the per-frequency models are Kramers–Kronig consistent.

## The cable model: causal lossy RLGC

Each line is a uniform transmission line described by per-unit-length R(f), L(f),
G(f), C(f), from which

```
γ(f)  = sqrt( (R + jωL)(G + jωC) )      propagation constant
Z0(f) = sqrt( (R + jωL)/(G + jωC) )     characteristic impedance
```

**Conductor (skin) loss** uses the W-element convention:

```
R(f) + jω·L_int(f) = R0 + sqrt(f)·(1 + j)·Rs
```

The `(1+j)` is not optional. The real and imaginary parts of √(jω) are a Hilbert
pair; dropping the imaginary part (loss without the matched internal inductance)
makes the model non-causal, and the synthesized trace grows a precursor foot *before*
the incident edge. A regression test deliberately drops it and asserts the artifact
appears.

**Dielectric loss** uses the Djordjevic–Sarkar wideband Debye model — a constant
tan δ with constant C is also non-causal:

```
ε_wd(f) = ε_inf + ε_d · ln( (10^12 + jf)/(10^4 + jf) ) / (8·ln 10)
C(f) = C_ref · Re ε_wd(f) / Re ε_wd(f_ref)
G(f) = 2πf · C_ref · (−Im ε_wd(f)) / Re ε_wd(f_ref)
```

Its two parameters are identified from a single (DK, tan δ) point; a unit test pins
that evaluating the model at the reference frequency returns DK·(1 − j·tan δ).

**From datasheets.** Catalog entries are datasheet-style: Z₀, velocity factor, DC
resistance, and either attenuation points (MHz, dB/100 m) or TIA insertion-loss
coefficients. Attenuation is least-squares fitted to the industry form

```
α(f) = k1·sqrt(f) + k2·f        (dB/100 m, f in MHz)
```

where k1 captures skin effect and k2 dielectric loss; then `Rs = 2·Z0·k1'` and tan δ
follows from k2'. The primary electrical pair is (Z₀, VF) — what a TDR actually
shows — and `C = 1/(Z0·v)`, `L = Z0/v` are derived from it (the datasheet pF/m is
kept only as a cross-check, enforced within 15 % by a test over the whole catalog).
PCB traces reuse the identical machinery with VF = 1/√ε_eff and loss tables built
from ε_eff and tan δ — FR-4's ~0.02 floor and Rogers' 0.0037 are recovered from the
tables by the fit, which a test asserts.

## Solving the network

Two solvers produce the same Γ(f); tests pin them bitwise against each other on
chain topologies.

**Chain fast path.** Unbranched networks fold from the termination back to the source
in homogeneous (V, I) coordinates — an open is (1, 0), a short is (0, 1), so there
are no infinities anywhere — renormalizing each step so nothing overflows, then
`Γ = (V − Zref·I)/(V + Zref·I)`.

**General nodal solve.** Anything with branches or loops assembles a complex
admittance matrix per frequency (SPICE-style AC analysis). Lines stamp the exact Pi
equivalent

```
Yser = 1/(Z0·sinh γl)         Ysh = tanh(γl/2)/Z0
```

with numerically stable forms at both extremes: for large |γl| the exponentials
underflow harmlessly to the physically-correct matched condition; for small |γl| and
DC the limits reduce to the exact series impedance `(R+jωL)·l` and shunt
`(G+jωC)·l/2` with no 0/0. Ideal shorts never enter the matrix — they merge nodes by
union-find, mirroring the chain solver's homogeneous coordinates. A 1e−12 S leak on
every diagonal keeps the matrix nonsingular for DC-floating nodes and exact lossless
resonances (~1e−10 relative error in Γ — far below physics tolerances). The system
is solved by dense complex LU with partial pivoting; injecting 1 A at the port gives
`Z_in = V_port` and `Γ = (Z_in − Zref)/(Z_in + Zref)`.

## From Γ(f) to the screen

1. **Grid.** Harmonic grid `f_n = n·Δf`, n = 0…M/2, with the time window auto-sized
   to `max(6 × round trip, 40 × rise time, 10 ns)` so re-reflections settle in view,
   and `dt = t_rise/5` to resolve the edge. M is the next power of two (capped at
   2^18 by default; if the cap binds, dt stretches and the trace is flagged
   *grid compromised*).
2. **Instrument edge.** Γ(f) is multiplied by a Gaussian edge response
   `H(f) = exp(−(ln2/2)·(f/f_3dB)²)` with `f_3dB = 0.34/t_rise` (the exact Gaussian
   10–90 % relation), plus a Kaiser window (β = 6) to suppress Gibbs ringing from
   band truncation, plus a linear-phase delay of 10·t_rise so the zero-phase edge's
   leading tail sits inside the window (displayed time is measured from the edge).
3. **Synthesis.** Hermitian-mirror onto the full grid (DC and Nyquist bins forced
   real), IFFT to the real impulse response h[m], then cumulative sum to the step
   response ρ[m]. The DFT identity `Σ h = Γ(0)` guarantees the trace settles at the
   *exact* DC reflection — e.g. the resistive divider including cable DC resistance,
   which is why a long line into a short ramps to the R_dc level instead of −1.
4. **Display.** `V(t) = A·(edge(t) + ρ(t))` with A the step amplitude into Zref;
   `Z(t) = Zref·(1+ρ)/(1−ρ)` with ρ clamped at ±(1−1e−9) (a real instrument reads
   "open", so the Ω cursor readout shows ∞ past 100 kΩ); distance `d = VF·c·t/2`
   using a display-only velocity factor.
5. **Self-checks.** `max|Im h| / max|Re h|` must be at rounding level (Hermitian/
   causality check, asserted on every test trace); if the impulse response has not
   died out by the window's last tenth, the trace is flagged *not settled* (resonant
   loops ring; the loss floor eventually damps them).

Masking and multi-reflection ghosts are not bugs to avoid — the exact solve
reproduces them faithfully, which is the point: the Z(t) readout is only trustworthy
up to the first big discontinuity, exactly like a real instrument.

## Live pipeline and caching

Every edit compiles the canvas into an immutable network snapshot and submits it to a
latest-wins background runner (20 ms debounce, cancellation checked per frequency).
Because the default demo needs M = 2^17, live runs first publish a preview with the
grid capped at 2^15 (~20 ms warm), then a full-resolution pass after 250 ms of idle.
A session cache memoizes each cable model's per-meter Z'(f)/Y'(f) spectra keyed on
(model, grid) — the arrays are length-independent, so dragging a *length* slider hits
the cache. Cached and uncached runs are asserted bitwise-identical.

## Instrument noise

Each instrument preset carries a display-referred single-sweep noise floor (0.4 mV
for a lab sampling head, 2 mV for a portable metallic TDR, 10 mV for a handheld
tester). Noise is applied strictly at display time — never inside the solver or its
caches — as seeded Gaussian noise with RMS σ/√N for N-sweep averaging, consistently
across the ρ/V/Ω representations. A blank noise field means the preset's floor; an
explicit 0 turns it off. Each published trace re-rolls, like a live repetitive sweep.

---

# Theory of operation — the pulse view

## Periodic steady state, computed exactly

A pulse train is periodic, and that makes the frequency-domain approach *stronger*
here, not weaker: the engine solves the network only at the harmonics of the
repetition rate, `f_k = k/T` for k = 0…M/2, with `M·dt = T` exactly (M a power of
two, dt ≤ min(t_rise, t_fall)/5, same cap-and-flag discipline as the TDR grid). The
synthesized waveform is the exact periodic steady state: there is no windowing
heuristic and no settling question, because reflections that outlive one repetition
period wrap into the next — which is precisely what they do on a real repetitive
sweep. Circular wrap-around is the physics, not an artifact.

## The stimulus spectrum

The generator is modeled as a trapezoid-like pulse with *independent* Gaussian rise
and fall edges: a smoothed rising step at t = 0 minus a smoothed falling step a
pulse-width later, W = duty·T. Writing `H_r(f)`, `H_f(f)` for the Gaussian edge
filters of the rise and fall times (same `f_3dB = 0.34/t_r` relation as the TDR
edge), the Fourier-series coefficients of the periodic stimulus are closed-form:

```
c_0 = EMF · duty
c_k = EMF · ( H_r(f_k) − H_f(f_k)·e^(−j2πf_k·W) ) / ( j·2π·f_k·T ),   k ≥ 1
```

`EMF = 2·A` so that a matched line sees the instrument's amplitude convention (A
into Zref). A Kaiser window (β = 6) shapes the truncated harmonic series, and a T/8
linear-phase offset keeps the leading edge clear of the display wrap. Because the two
edge filters are independent, rise and fall times are genuinely independent — a test
measures ~1 ns up and ~5 ns down on the same pulse.

## Probing anywhere: transfer functions

The TDR view needs only the port reflection; the pulse view needs the voltage at an
arbitrary net. The nodal solver has a second mode for this: the source is stamped as
its Norton equivalent (admittance 1/Zref at the port, current Vs/Zref injected), and
the solve returns the transfer function `T_n(f) = V(node n)/Vs` per frequency — at
the port itself this reduces to the familiar `(1 + Γ)/2`. The displayed waveform is
then the Hermitian IFFT of `c_k · T_n(f_k)`.

Probe points are the TDR port, every load, every bridge fault, and every junction.
Junctions are electrically invisible (they merge nets and emit nothing), so *dropping
a junction anywhere is the probe mechanism* — wire one mid-span and it appears in the
probe dropdown. Degenerate cases stay exact: a probe on a shorted net reads
identically zero, volts at an open end double, the waveform mean equals
amplitude × duty × the DC transfer — all pinned by tests.

## Display

The one-period steady state is tiled "# of pulses displayed" times. The instrument's
noise floor is rolled over the *whole tiled array*, not the single period — otherwise
every repetition would show the same squiggle, an obviously fake repeating pattern;
independent noise per repetition is what distinct acquisitions look like on a real
screen. Both views are always auto-fitted (no mouse zoom), and the pulse view
re-ranges when the pulse count changes.

---

# Validation

The engine was finished and proven against closed forms before any UI existed, so UI
work never debugs physics. The suite (224 tests) includes, among others:

| Case | Expected (closed form) |
|---|---|
| Matched / resistive loads | exact divider levels, plateau at (R−Z₀)/(R+Z₀) |
| Open/short at l | ±full reflection at t = 2l/v |
| Shunt C / series L mid-line | dip/bump with τ = Z₀C/2, L/2Z₀; area = ∓τ, rise-time-invariant |
| Two-segment mismatch, ring loops | lattice-diagram partial sums, multi-bounce climb 1 − 4·3^(−k) |
| 50 Ω tee | ρ = −1/3 at the junction |
| Lossy open line | Wigington–Nahman erfc edge, 1/√t creep |
| Causality | precursor < 1e−9 with (1+j) skin model; appears when Im dropped |
| DC settling | long lossy line + short ramps to the R_dc divider, not −1 |
| Faults | series R/(R+2Z₀), bridge −Z₀/(2R+Z₀), section dip level & v′ = s·VF timing |
| Pulse view | delay, open-end doubling, duty-cycle mean, independent rise/fall |
| Caching | cold vs. warm runs bitwise identical on both solver paths |

Plus serializer round-trips, document-compiler validation (issues, not exceptions),
and every built-in example compiling with zero issues.

# Layout

- `src/TdrSim.Core` — engine, network model, project documents, catalog (no UI deps)
- `src/TdrSim.App` — WPF app: `Editor/` (Nodify canvas), `Plot/` (trace + pulse
  panels), `Shell/` (window, toolbar, live pipeline)
- `tests/TdrSim.Core.Tests` — the analytic validation suite
- `PLAN.md` — full design; `plans/` — per-milestone plans; `TODO.md` — backlog

# Key references

- Keysight AN 1287-12, *Time Domain Analysis Using a Network Analyzer* — the
  low-pass step recipe this engine follows.
- Zhang & Drewniak, *Causal RLGC(f) Models for Transmission Lines*, IEEE Trans. EMC
  52(1), 2010 — the (1+j)√f skin term.
- Djordjevic, Sarkar et al. — wideband Debye dielectric; Shlepnev & McMorrow,
  DesignCon 2010 — its identification.
- Wigington & Nahman, Proc. IRE 1957 — the erfc skin-effect step response used as a
  validation target.
- Dascher (HP Journal, 1996) — parasitic L/C dip-bump time constants and areas.
- Belden / Times Microwave / TIA-568 datasheets and specs for the catalog values.
