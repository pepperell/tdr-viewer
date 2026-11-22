# TODO — improvement backlog

Prioritized backlog beyond the [PLAN.md](PLAN.md) milestones. Status: M0–M5 complete
plus post-M5 features (examples menu, pulse view, 28-cable catalog with sections,
instrument noise floors, always-fit plots). Workflow for big items stays
"create a plan for Mx" → "complete Mx".

## Top priorities

- [x] **Git init + first commit.** Done 2026-11-22: pushed to
      https://github.com/pepperell/tdr-viewer (main).
- [ ] **M6 — teaching views** (planned in PLAN §6; needs a plans/M6.md):
  - [ ] Bounce-diagram panel for chain topologies, each ray labeled with its ρ/τ
        product (exact for lossless; annotated approximate otherwise).
  - [ ] Trace↔canvas cross-highlighting: hover a trace feature → the responsible
        component lights up (from path delays), and vice versa.
  - [ ] Layer-peeling (dynamic deconvolution) overlay: the "de-ghosted" Z profile,
        including where peeling fails on lossy lines.
- [ ] **Touchstone (.s1p) import/export.** Export Γ(f) as S11 (nearly free — computed
      every run); import a measured .s1p from a real VNA and render its TDR trace via
      the existing reference-overlay machinery. Connects the app to real bench work.
- [ ] **Custom cable entry** (last unfulfilled PLAN §1.2 promise): datasheet-style
      entry (Z₀, VF, R_dc, attenuation points) and raw RLGC, beyond the current Z₀/VF
      overrides. Needs a schema bump to store user-defined specs in the document.
- [ ] **Return-loss view**: a third tab plotting |Γ(f)| in dB — the frequency-domain
      twin of the TDR trace, already computed on every run.

## Smaller, high-value polish

- [ ] Pulse-view parity: cursors + CSV export; persist pulse parameters in the
      document (schema v3); optional second probe overlaid to watch a pulse degrade
      along the chain.
- [ ] Palette filter box (28 cables + connectors/loads/faults make the list long even
      with sections).
- [ ] Examples banner: surface each example's one-line teaching description when it
      loads (currently tooltip-only in the menu).
- [ ] Canvas copy/paste/duplicate (serialize selection → re-id → offset → paste; the
      document layer makes this cheap).
- [ ] App settings persistence (%APPDATA% JSON): window size, reopen last file,
      recent-files menu, live toggle, pulse parameters.
- [ ] Detail-view escape hatch for the always-fit plot, if close-in inspection ever
      pinches (e.g. "span around cursor A" preset or an x-max box) — without
      reintroducing free-form mouse zoom.

## Housekeeping (when it matters)

- [ ] Packaging: app icon, .tdrproj file association (command-line open already
      works), self-contained publish profile, README.
- [ ] Perf: 31-node stress case ~159 ms vs the informational 100 ms target; the M2
      levers (per-frequency plan hoisting, blocked assembly) remain in reserve.
- [ ] Test-suite flakes: timing tests now assert minimums; if flakes return as the
      suite grows, pin perf tests to a serialized xUnit collection.

## Deferred small items (from earlier milestones)

- [ ] Per-division vertical scale presets (PLAN §1.5, 1502C-style mrho/div).
- [ ] Expose window override and Kaiser β as advanced settings (PLAN §1.4).
- [ ] Persist reference traces in the project file (currently session-only).
- [ ] Restore selection/viewport on undo (currently document state only).
