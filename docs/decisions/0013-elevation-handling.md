# 0013 Elevation handling: detect, warn, gate, restart on request

- **Status:** accepted
- **Date:** 2026-10-13
- **Extends:** ADR 0004 (the player never elevates silently). Nothing in 0004 changes.

## Context
A step with `elevated: true` run from a non-elevated player failed with "Process should have elevated status", which says nothing
about what to do. The player must stay honest about its rights (ADR 0004) but guide the person to a working state.

## Decision
- **Detect:** `IElevationProbe` (Core). The real one is `Environment.IsPrivilegedProcess` (elevated administrator on Windows, root on
  Unix). Tests and the automation channel (`do/elevation?arg=on|off|real`) use a fixed probe.
- **Warn:** opening a guide with `elevated` script steps while not elevated shows a non-blocking banner above the pages ("N of M
  steps need administrator rights") with the action below. Nothing is blocked by the banner.
- **Gate:** `ElevationGate.Check(step.Elevated, probe)` decides per step. A blocked step has its Run button disabled and a notice
  that says why; Skip, Next and the rest are unchanged. The guard is also inside the run command, not only in the button state.
- **Restart as administrator (Windows):** starts the same executable with `UseShellExecute` and the `runas` verb (the standard UAC
  prompt), then closes this instance. If the prompt is cancelled, this instance stays open and says so. The command line is
  `[--data-dir <dir>] <guide path>` (plus the library path when run through `dotnet`), built and quoted by `ElevatedRelaunch`
  and unit tested. **No parameter values** go on the command line (it is visible in the process list); the new instance starts
  from the guide's defaults and the person re-enters what they had typed. Captured values and step progress are not carried over
  either (no run state yet, ADR 0002).
- **Unix:** no restart button; the banner says to start the player with `sudo`.
- The player does not offer to elevate a single step in place: only the whole player is restarted.

## Alternatives considered
- **Elevate only the child process of a step** (a UAC prompt per step): rejected, breaks the output streaming through redirected pipes
  and prompts repeatedly.
- **Carry the parameter values into the new instance** (command line or a temp file): rejected, secrets would leave the process
  boundary in clear text.

## Consequences
- The real UAC relaunch cannot be tested headless; the gating and the arguments are unit tested, the click is manual.
- A restart loses unsaved progress; the guide is reopened at its start.
