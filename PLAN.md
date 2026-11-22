# TDR Viewer — Plan

A Windows desktop application that simulates the output of a Time Domain Reflectometer for a
user-drawn network of cables and loads. The user builds the network on a node-graph canvas —
cables with custom or preset parameters, junctions/tees, loops, and arbitrary loads — and the
simulated TDR trace updates live as parameters are dragged.

The physics engine is modeled on what real instruments and signal-integrity tools (Keysight
VNAs, Ansys HFSS, scikit-rf, Simbeor) actually do, so the output is quantitatively accurate:
correct reflection amplitudes and timing, skin-effect edge "slurring" and 1/√t creep on long
cables, dielectric loss, multiple re-reflections, masking, capacitive dips and inductive bumps
with the right time constants, and correct DC settling levels.

---

## 1. Physics foundation

### 1.1 Why frequency-domain synthesis (and not a bounce diagram)

Three candidate methods were researched:

| Method | Lossy lines | Reactive loads | Loops | Verdict |
|---|---|---|---|---|
| Lattice/bounce diagram (ray tracing) | ✗ exact only for lossless, scalar ρ | ✗ needs per-ray convolution | ✗ ray count explodes ~2^bounces | Pedagogical overlay only |
| Time stepping (EMTP/Bergeron, FDTD) | requires vector-fitting of γ(f), Z₀(f) per segment (Marti/ULM models) | ✓ companion models | ✓ nodal | Accurate but much more machinery for the same result |
| **Frequency-domain synthesis** (Γ(f) grid → IFFT) | ✓ exact at every frequency | ✓ exact | ✓ exact via nodal solve | **Chosen core engine** |

Frequency-domain synthesis is exactly how real network analyzers produce "simulated TDR"
(Keysight AN 1287-12; Anritsu 11410-00722A) and how HFSS/scikit-rf compute TDR from
S-parameters. Every physical effect — √f skin loss with its causal internal inductance,
dispersive dielectric, complex loads, re-reflections around loops — is captured exactly by
evaluating the network at each frequency. The hard parts (causality, dispersion) come for free
if the per-frequency models are Kramers-Kronig consistent.

### 1.2 Cable model: causal lossy RLGC

Each cable segment is a uniform transmission line defined by per-unit-length R(f), L(f), G(f),
C(f):

```
γ(f)  = sqrt( (R + jωL)(G + jωC) )          propagation constant [1/m]
Z0(f) = sqrt( (R + jωL)/(G + jωC) )          characteristic impedance [Ω]
```

**Skin effect (conductor loss)** — the W-element convention with the causality-mandated
imaginary part:

```
R(f) + jω·L_int(f) = R0 + √f·(1+j)·Rs
```

- `R0` = DC loop resistance (center + shield), from datasheet.
- `Rs` [Ω/(m·√Hz)] = skin coefficient. The `(1+j)` is not optional: Re and Im of √(jω) are a
  Hilbert pair. Dropping the imaginary part (loss without matched internal inductance) makes
  the model non-causal — the IFFT'd trace grows a precursor foot *before* the incident edge.
  (Zhang & Drewniak, IEEE Trans. EMC 2010; Shlepnev/Simberian app notes.)

**Dielectric loss** — Djordjevic–Sarkar wideband Debye (causal; constant tan δ with constant C
is non-causal):

```
ε_wd(f) = ε_inf + ε_d · ln( (10^m2 + jf)/(10^m1 + jf) ) / ((m2−m1)·ln 10)     m1=4, m2=12
C(f) = C_ref · Re ε_wd(f)/Re ε_wd(f_ref)
G(f) = G0 + 2πf · C_ref · (−Im ε_wd(f))/Re ε_wd(f_ref)
```

Parameters are fully determined by one (DK, tan δ) point at a reference frequency — unit test:
evaluating ε_wd(f_ref) must return DK·(1 − j·tanδ).

**Deriving parameters from datasheets.** Users enter cables either as presets, as custom
"datasheet style" (Z₀, VF, R_dc, attenuation points), or raw RLGC. Datasheet attenuation is fit
to the industry-standard

```
α_dB(f) = k1·√f + k2·f        (f in MHz; dB/100 m)   — 2×2 linear least squares
```

then converted (`k1' = k1/(8.686·100·√1e6)` Np/m/√Hz, `k2'` analogously):

- `Rs = 2·Z0·k1'`, plus `R0` from datasheet DCR
- band tan δ = `(2·k2'/Z0)/(2πC)` → feeds the Djordjevic–Sarkar fit
- `C = √εr/(c·Z0)` or datasheet pF/m; `εr = (1/VF)²`; `L_ext = Z0²·C`

### 1.3 Network solve: nodal admittance (handles branches AND loops)

The network is a graph: electrical nodes, with cables as two-ports between nodes and loads as
one-port shunt admittances. Per frequency, assemble complex Y and solve — this is exactly
SPICE/Qucs AC analysis, so tees, stubs, bridged taps, and rings need zero special-casing.

Cable two-port stamp (exact Pi form, numerically stable at both extremes):

```
Yser = 1/(Z0·sinh(γl))          Ysh = tanh(γl/2)/Z0
Y[i,i] += Yser + Ysh            Y[j,j] += Yser + Ysh
Y[i,j] −= Yser                  Y[j,i] −= Yser
```

- **Large |γl|** (long/lossy line at high f): compute via `coth(x) = (1+e^(−2x))/(1−e^(−2x))`,
  `csch(x) = 2e^(−x)/(1−e^(−2x))` — e^(−2x) underflows harmlessly to 0, giving Yser→0,
  Ysh→1/Z₀ (far end decouples, port sees matched Z₀ — physically correct).
- **Small |γl|** (short segment at low f) and **DC**: use the limiting forms
  `Z0·sinh(γl) → (R+jωL)·l` and `tanh(γl/2)/Z0 → (G+jωC)·l/2`, which follow from
  γ·Z0 = R+jωL and γ/Z0 = G+jωC. At f = 0 this degenerates to exactly a series resistance
  R0·l — the correct DC network — with no 0/0 anywhere.

Loads stamp the diagonal: resistor 1/R, capacitor jωC, inductor 1/(jωL), series/parallel RLC,
custom Z(f). Open stamps nothing; short folds its node into ground (row/column elimination —
best conditioning). Connectors/splices are small two-ports (series R+L, shunt C each side).

Solve `Y·V = e_port` (1 A into the TDR port node) by dense complex LU with partial pivoting
(networks are small, N ≲ 64). Then:

```
Z_in(f) = V_port        Γ(f) = (Z_in − Z_ref)/(Z_in + Z_ref)
```

DC singularity (all-open network ⇒ singular Y at f=0): stamp a tiny leak (1e−12 S) at the port
for the f=0 solve only; Γ(0) → 1 to within 1e−10. Fast path: networks that are a pure chain
(no branching) skip the matrix and use the O(segments) impedance-folding recursion
`Z_in = Z0·(Z_L + Z0·tanh γl)/(Z0 + Z_L·tanh γl)`.

### 1.4 Time-domain synthesis (the VNA low-pass step recipe)

1. **Grid.** Harmonic grid `f_n = n·df`, n = 0..N, `df = 1/T_window`. Time step
   `dt = T_window/M`, FFT size `M = 2N` (power of two). Auto-sizing: `T_window` ≈ 6× the
   longest round-trip delay (user-overridable); `dt ≤ t_rise/5` so the edge is resolved.
2. **Solve** Γ(f_n) at every grid frequency (parallelized across frequencies).
3. **Instrument filter.** Multiply by Gaussian edge response `H(f) = exp(−(ln2/2)·(f/f_3dB)²)`,
   `f_3dB = 0.34/t_rise` (exact 10–90% Gaussian relation 0.3397), plus a Kaiser window
   (β ≈ 6 default, exposed as "ringing vs resolution" advanced setting) to suppress Gibbs
   ringing from band truncation.
4. **Hermitian mirror** (`H[M−n] = conj(H[n])`, Nyquist bin forced real) → **IFFT** → real
   impulse response h[m]. `max|Im|` is a built-in self-check (~1e−12).
5. **Step response** by cumulative sum: `ρ[m] = Σ h[0..m]`. The DFT identity Σh = Γ(0)
   guarantees the trace settles to the exact DC level (e.g., V₀·R_L/(R_L+R_g) voltage divider).
6. **Display trace.** `V(t) = A·(edge(t) + ρ(t))` with the same-filtered incident edge and
   A = step amplitude into Z_ref (e.g., 250 mV). Axis conversions:
   ρ (mrho/div), `Z(t) = Z_ref·(1+ρ)/(1−ρ)` (clamped near ρ=±1), distance `d = v·t/2` using a
   selected velocity factor.
7. **Settling check.** If |h| near the window end exceeds a threshold, show a "trace not
   settled — increase time span" badge (resonant loops ring for a long time; real cables'
   loss floor eventually damps them).

Masking and multi-reflection "ghosts" are *not* bugs to avoid — the exact solve reproduces
them faithfully, which is the point: the Z(t) readout is only trustworthy to the first big
discontinuity, exactly like a real instrument.

### 1.5 Instrument realism

| Preset | Step into 50 Ω | 10–90% rise | Based on |
|---|---|---|---|
| Lab sampling TDR | 250 mV | 23 ps | Tektronix 80E04 |
| Fast lab TDR | 250 mV | 12 ps | Tektronix 80E10B |
| Portable metallic TDR | 300 mV | 200 ps | Tektronix 1502C |
| Cable tester | 2 V | 2 ns | handheld class (rise time approximated) |
| Custom | user | user | — |

Also modeled: source impedance (50/75 Ω selectable — 75 Ω instruments for CATV work), optional
additive Gaussian noise with trace averaging (N-sweep RMS reduction), vertical scale in
mV/div, mrho/div, or Ω/div like the 1502C (0.5–500 mrho/div).

### 1.6 Cable preset library (datasheet-grade, from research)

| Cable | Z₀ | VF | C pF/m | R_dc loop Ω/km | α dB/100 m @100 MHz | Source |
|---|---|---|---|---|---|---|
| RG-58 (Belden 9201) | **52 Ω** | 66% | 93.5 | 33.8 + 23 | 12.5 | Belden datasheet |
| RG-6 (Belden 1694A) | 75 Ω | 82% | 53.1 | 21 + 12 | 6.4 | Belden |
| RG-174 (Belden 8216) | 50 Ω | 66% | 101 | 318 (CCS) + 35 | 27.6 | Belden |
| RG-213 (Belden 8267) | 50 Ω | 66% | 101 | 5.6 + 3.9 | 6.2 | Belden |
| LMR-400 | 50 Ω | 85% | 78.4 | 4.6 + 5.4 | α = 0.4012√f + 0.00085f dB/100 m | Times Microwave |
| Cat5e pair (Belden 1583A) | 100 Ω | 70% | ~46 | 93.8/cond | IL: 1.967√f + 0.023f + 0.05/√f (TIA) | TIA-568-B.2 / Belden |
| Cat6 pair (Belden 2413) | 100 Ω | 72% | ~44 | 78/cond | IL: 1.808√f + 0.017f + 0.2/√f | TIA-568-B.2-1 |

Full attenuation tables from the research are stored in the catalog as (MHz, dB/100 m) points
and fit at load time. Connector presets: BNC barrel (~2 nH series, ~1 pF shunt), F-connector,
generic splice, corroded splice (series R). "Fault" presets: water ingress (short section of
lowered Z₀ + raised G), crush (lowered Z₀ section), resistive bridge, series fault.

---

## 2. Technology stack (verified current, Nov 2026)

| Layer | Choice | Version | Why |
|---|---|---|---|
| Runtime | .NET 10 LTS, `net10.0-windows` | GA Nov 2025 | supported to Nov 2028 |
| UI | WPF | built-in | mature canvas stack; best node-editor ecosystem |
| Node editor | **Nodify** | 7.3.0 | MIT, active, infinite pan/zoom canvas, circuit-style wire routing, arbitrary graphs incl. loops, fully templatable node content, "hundreds of nodes" performance |
| Plot | **ScottPlot.WPF** | 5.1.59 | MIT, OpenGL-accelerated, per-pixel decimation, purpose-built for live/streaming scope traces, crosshair/axis-line cursors |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 | source-generated observable properties/commands |
| FFT | FftFlat | 1.0.1 | pure C#, ~4× faster than Math.NET managed FFT |
| Linear algebra | hand-rolled complex LU (~50 lines, pivoting, buffer-reuse) | — | faster than library dispatch at N ≤ 64; zero deps |

All MIT. Cross-platform later if ever needed: Avalonia + NodifyAvalonia + ScottPlot.Avalonia —
Core and ViewModels port unchanged.

**Performance budget.** Worst realistic case ≈ 32k frequencies × LU of a ~16-node network:
a few ms single-threaded per 1k frequencies, parallelized across cores → **5–30 ms per full
recompute**, well inside the live-update target. Chain-topology fast path is ~10× cheaper.
FFT of 64k points: sub-millisecond (FftFlat).

---

## 3. Application design

### 3.1 Layout

```
┌──────────────────────────────────────────────────────────────────────┐
│ Toolbar: New Open Save │ Instrument preset ▾ │ Rise time ── │ ⚠ badges│
├───────────┬──────────────────────────────────────────┬───────────────┤
│ PALETTE   │  NETWORK CANVAS (Nodify)                 │ INSPECTOR     │
│           │                                          │               │
│ ▸ Cables  │   [TDR]══════(RG-58 30m)══════[Tee]      │ RG-58 30 m    │
│ ▸ Loads   │                        ║         ╚═(RG-6 │ Length 30 m   │
│ ▸ Connect │                   (RG-58 10m)      15m)═ │  ───────●──   │
│ ▸ Faults  │                        ║        [75Ω]    │ Z0  52 Ω      │
│           │                     [Open]               │ VF  66 %      │
│           │                                          │ Loss preset ▾ │
├───────────┴──────────────────────────────────────────┴───────────────┤
│ TDR TRACE (ScottPlot)                    y: mV | mrho | Ω   x: t | m │
│ 300┤      ┌──────                                                    │
│    │______│         ╲____  ..cursor A: 14.2 m, 52 Ω..                │
│   0└──────┴──────────────────────────────────────────────            │
└──────────────────────────────────────────────────────────────────────┘
```

- **Palette** (left): drag cables/loads/connectors/faults onto the canvas.
- **Canvas** (center): Nodify editor. Components are nodes; wires are ideal zero-length
  electrical connections; any number of wires meeting at a point is a junction (tee = 3 wires
  at one node). Loops are just wiring a cycle. Exactly one TDR instrument node (the port).
- **Inspector** (right): properties of the selection — slider + numeric box pairs (length,
  Z₀, VF, R/L/C values, loss coefficients), preset dropdowns. Dragging any slider updates the
  trace live.
- **Trace panel** (bottom): the simulated TDR display. Switchable y-axis (mV / mrho / Ω) and
  x-axis (time / distance with VF choice); two measurement cursors with Δ readout (the classic
  "distance-to-fault" workflow); optional reference-trace overlay (snapshot & compare);
  settling/aliasing warning badge; export CSV/PNG.

### 3.2 Graph semantics (model layer)

```
NetworkModel
├── Nodes: electrical junctions (implicit ground reference)
├── Elements
│   ├── SourcePort     (exactly one; Z_ref, instrument settings)
│   ├── CableSegment   (two-port: length, CableSpec)
│   ├── Connector      (two-port: series R+L, shunt C/side)
│   └── Load           (one-port: Open|Short|R|C|L|SeriesRLC|ParallelRLC|CustomZ(f))
└── CableSpec (preset or custom: Z0, VF, R0, Rs, k1, k2 / DK+tanδ / raw RLGC)
```

The UI graph (Nodify view models) maps 1:1 onto this model; every edit produces a new
immutable `NetworkModel` snapshot handed to the engine (no locking, trivially cancellable).

Validation rules surfaced as canvas badges, not blockers: no TDR source / more than one,
dangling cable end (auto-treated as open — like a real unterminated cable), zero-length
cables, unreachable islands.

### 3.3 Live update pipeline

```
property change → debounce 20 ms → snapshot model → background Task:
   [solve Γ(f) grid (Parallel.For) → filter/window → IFFT → cumsum → axis arrays]
   cancellation checked per frequency block; superseded runs cancelled
→ dispatch to UI thread → update ScottPlot Signal buffers → Refresh()
```

Target: < 50 ms typical edit-to-trace latency; a thin progress shimmer if a recompute exceeds
200 ms (huge windows). Frequency-grid caching: γl and Z₀ arrays per segment are memoized by
(CableSpec, length, grid) so dragging a load slider doesn't re-evaluate cable models.

### 3.4 Extra views (post-MVP, high teaching value)

- **Bounce-diagram panel**: lattice diagram for chain topologies, each ray labeled with its
  ρ/τ product — pedagogically explains *why* each step in the trace exists (exact for the
  lossless case; annotated as approximate otherwise).
- **Annotated trace**: hover a trace feature → highlights the responsible component on the
  canvas (computed from path delays), and vice versa.
- **Impedance-profile (layer peeling) overlay**: run the same dynamic-deconvolution algorithm
  real instruments use (Keysight "TDR peeling") on the simulated trace — shows users both the
  raw trace and the "de-ghosted" Z profile, including where peeling itself fails (loss).

---

## 4. Project structure

```
tdr-viewer/
├── src/
│   ├── TdrSim.Core/            # no UI deps; net8.0 + net10.0
│   │   ├── Model/              # NetworkModel, elements, CableSpec, catalog JSON
│   │   ├── Engine/             # grid builder, cable eval (γ,Z0), stamps, complex LU,
│   │   │                       #   chain fast path, FFT pipeline, trace post-processing
│   │   └── Catalog/            # cable/connector/instrument presets (embedded JSON)
│   └── TdrSim.App/             # net10.0-windows WPF
│       ├── Editor/             # Nodify canvas VMs, palette, inspector
│       ├── Plot/               # ScottPlot trace panel, cursors, axis modes
│       └── Shell/              # main window, toolbar, project save/load (JSON)
├── tests/TdrSim.Core.Tests/    # xUnit analytic validation suite
└── PLAN.md
```

Project files: JSON (System.Text.Json), schema-versioned, round-trips canvas layout + model.

---

## 5. Validation suite (analytic ground truth — written before the UI)

| # | Case | Expected (closed form) |
|---|---|---|
| 1 | Matched load | flat trace; settles to V₀·R_L/(R_L+R_g) |
| 2 | Open / short at l | ±full reflection at t = 2l/v, correct baseline |
| 3 | Resistive mismatch R | plateau at ρ = (R−Z₀)/(R+Z₀) |
| 4 | Capacitive load C | exponential, τ = Z₀C (shorts then opens) |
| 5 | Inductive load L | exponential, τ = L/Z₀ (opens then shorts) |
| 6 | Shunt C mid-line | negative dip, τ = Z₀C/2; **area** = −τ independent of rise time |
| 7 | Series L mid-line | positive bump, τ = L/(2Z₀); area = +τ |
| 8 | Two-segment mismatch | plateau levels = lattice-diagram partial sums (ρ, τ=1+ρ products) |
| 9 | 50 Ω tee (two matched branches) | ρ = −1/3 at the junction |
| 10 | Lossy line, open end | edge = erfc(½√(τ₀/t)), τ₀ = (k1'·l)²/π; 1/√t creep (Wigington–Nahman) |
| 11 | Causality | max|h(t<0)| < 1e−9 with (1+j) model; precursor appears if Im dropped (regression test for the classic bug) |
| 12 | Hermitian self-check | max|Im h| < 1e−12 |
| 13 | Ring/loop | two-path lossless loop vs. hand-computed multi-bounce series |
| 14 | DC settling | long lossy cable + short: trace ramps to R_dc·l divider level, not zero |

---

## 6. Milestones

- **M0 — Scaffold** (small): solution, projects, CI-less build, catalog JSON schema.
  Detailed plan: [plans/M0.md](plans/M0.md).
- **M1 — Engine for chains**: cable eval with causal losses, folding recursion, FFT pipeline,
  tests 1–8, 10–12, 14 green. (This already produces correct single-path TDR traces.)
  Detailed plan: [plans/M1.md](plans/M1.md).
- **M2 — General networks**: nodal assembly with stable stamps, short/open handling, chain
  fast-path switch, tests 9 & 13 green. Performance: ≤ 30 ms worst case verified.
  Detailed plan: [plans/M2.md](plans/M2.md).
- **M3 — Editor shell**: WPF app, Nodify canvas, palette, inspector, model↔VM sync,
  save/load. (Trace panel shows a static computed trace on demand.)
  Detailed plan: [plans/M3.md](plans/M3.md).
- **M4 — Live trace**: debounced background pipeline, ScottPlot panel, axis modes, cursors,
  memoization. The headline experience — drag a slider, watch the reflections move.
  Detailed plan: [plans/M4.md](plans/M4.md).
- **M5 — Realism polish**: instrument presets, noise+averaging, full cable/connector/fault
  catalog, settling & validation badges, reference-trace compare, CSV/PNG export.
  Detailed plan: [plans/M5.md](plans/M5.md).
- **M6 — Teaching extras** (optional): bounce-diagram panel, trace↔canvas cross-highlighting,
  layer-peeling overlay.

Build order rationale: the engine is finished and *proven against closed forms* before any UI
exists, so UI work never debugs physics and physics never debugs bindings.

Improvement backlog beyond the milestones: [TODO.md](TODO.md).

## 7. Risks & mitigations

- **Window/rise-time tension**: a 1 km network at 23 ps rise would need a huge grid. Mitigate:
  cap FFT at 2^18, auto-suggest instrument class for the network scale (real km-range TDRs
  have ns edges for the same reason), warn when dt > t_rise/3.
- **Lossless resonant loops never settle** (circular aliasing): always-on minimum-loss floor
  (real R_dc ensures decay), settling badge, user-extendable window.
- **Nodify models wires as port-to-port visuals, not electrical nets**: solved at the VM layer
  — ports accept multiple connections and the model unions them into one electrical node.
- **Numerical edge cases** (γl overflow, DC singularity, ρ→±1): all have specified stable
  forms (§1.3, §1.4); each gets a dedicated unit test.
- **Single-maintainer deps** (Nodify): MIT + zero-dependency → vendor/fork is trivial.

## 8. Key references

- Keysight AN 1287-12 *Time Domain Analysis Using a Network Analyzer* — the low-pass step
  recipe (harmonic grid, DC extrapolation, Hermitian mirror, windowing, masking).
- Anritsu 11410-00722A — same method, explicit grid indexing; step = ∫ impulse.
- Zhang & Drewniak, *Causal RLGC(f) Models for Transmission Lines*, IEEE Trans. EMC 52(1),
  2010 — the (1+j)√f skin term and causality demonstrations.
- Shlepnev & McMorrow, DesignCon 2010 — Djordjevic–Sarkar wideband Debye identification.
- Wigington & Nahman, *Transient Analysis of Coaxial Cables Considering Skin Effect*, Proc.
  IRE 1957 (+ Chase et al., NIM A 330:228) — erfc step response validation target.
- Dascher, *Measuring Parasitic Capacitance and Inductance Using TDR*, HP Journal Aug 1996 —
  dip/bump time constants and rise-time-invariant area formulas.
- Tektronix 55W-14601-2 *TDR Impedance Measurements* — display conventions, resolution,
  masking; Tek 80E-series & 1502C datasheets — instrument presets.
- Qucs Technical Papers — line Y-parameters and MNA assembly; Pozar via Steer (LibreTexts) —
  ABCD matrices.
- Belden 9201/1694A/8216/8267/1583A/2413 datasheets, Times Microwave LMR-400, TIA-568-B.2 —
  cable catalog values.
- Dommel 1969 / Marti 1982 / ULM 1999 / Gustavsen & Semlyen vector fitting — surveyed
  time-stepping alternative (kept as a documented future option; not needed for v1).
