# TODO — improvement backlog

Prioritized backlog beyond the [PLAN.md](PLAN.md) milestones. Status: M0–M6 complete
plus post-M5 features (examples menu, pulse view, 28-cable catalog with sections,
instrument noise floors, always-fit plots, custom cables, return-loss view). Workflow for big items stays
"create a plan for Mx" → "complete Mx".

## Top priorities

- [x] **Git init + first commit.** Done 2026-11-22: pushed to
      https://github.com/pepperell/tdr-viewer (main).
- [x] **M6 — teaching views** (detailed plan: [plans/M6.md](plans/M6.md)):
  - [x] Bounce-diagram panel for chain topologies, each ray labeled with its ρ/τ
        product (exact for lossless; annotated approximate otherwise).
  - [x] Trace↔canvas cross-highlighting: hover a trace feature → the responsible
        component lights up (from path delays), and vice versa.
  - [x] Layer-peeling (dynamic deconvolution) overlay: the "de-ghosted" Z profile,
        including where peeling fails on lossy lines.
- [x] **Custom cable entry** (the last PLAN §1.2 promise): datasheet-style entry
      (Z₀, VF, R_dc, attenuation points) and raw RLGC, in a manager dialog; stored in
      the document (schema v4).
- [x] **Return-loss view**: a fourth tab plotting |Γ(f)| in dB — the frequency-domain
      twin of the TDR trace, already computed on every run.

## Smaller, high-value polish

- [ ] Pulse-view parity: cursors + CSV export; persist pulse parameters in the
      document (schema v3); optional second probe overlaid to watch a pulse degrade
      along the chain.
- [x] **M7 — component bar** (detailed plan: [plans/M7.md](plans/M7.md)): the long
      palette list is replaced by square buttons with drop-downs; this also retires the
      palette filter box idea. The TDR node is permanent.
- [ ] Examples banner: surface each example's one-line teaching description when it
      loads (currently tooltip-only in the menu).
- [ ] Canvas copy/paste/duplicate (serialize selection → re-id → offset → paste; the
      document layer makes this cheap).
- [x] **App settings persistence** (%APPDATA%\TdrViewer\settings.json): window
      size/maximized, reopen last file (toggle lives in the Recent menu), recent-files
      menu, live toggle, pulse parameters. Best-effort IO: a missing or corrupt file
      means defaults, never an error.
- [ ] Trace x-extent on lossy lines: the always-fit scan follows slow loss creep, so
      a lossy chain's view runs far past its last echo (the bounce tab frames itself
      on its own arrivals instead). Consider the same activity-based extent for the
      trace.
- [ ] Bounce diagram: mm-scale connector sections make the zero-rise-time staircase
      spike at joints (correct, but reads like a glitch) - optionally smooth the
      staircase by the instrument rise time.
- [ ] Custom cables: a per-user library shared across projects (they are per-document
      today), and keeping a copied preset's separate dielectric constant.
- [ ] Return-loss view: cursors/CSV export, and the reference-trace overlay.
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
