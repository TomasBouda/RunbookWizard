# 0006 Player behaviour and UI

- **Status:** accepted
- **Date:** 2026-10-13

## Context
The player turns a guide into a sequence of steps a person works through. The rules for ordering, confirmation and
state decide how safe and predictable it feels, and the app has to follow the personal desktop UI standards.

## Decision
- **Ordered, blocking steps.** A step is blocked while any earlier step is pending, running, or failed without
  `continueOnFailure`. A step may be skipped explicitly. `when` conditions are re-evaluated whenever a parameter or
  capture changes; a false condition marks the step skipped (shown as "skipped - condition").
- **One click runs, two for destructive steps.** Run executes a script step. A step with `destructive: true` first shows
  its warning and the risk findings, and runs only after a second click ("Yes, run it"). The command shown is always
  the exact text that runs, with secrets masked.
- **Stay on the result.** After a script run the player stays on the step to show the verdict and output; the person
  clicks "Next step". Info and manual steps advance after Finish or Skip.
- **No run-state persistence.** Closing a guide discards its progress (see ADR 0002). Only settings and the recent list persist.
- **PowerShell runs with `-ExecutionPolicy Bypass` for that one process only** (ADR 0004 clarification): it affects no
  machine or user policy and is needed because the script is a temp `.ps1` file. The script is still shown before it runs.
- **Style.** The IISBlitz/AppShelf "ops console": `Styles/Tokens.axaml` (teal accent) and `Styles/Controls.axaml`,
  custom title bar, command palette (Ctrl+K), System/Light/Dark theme, Logs page, version in the header.
- **UI verification** goes through TomLabs.UiAutomation with the fixture in `tests/ui-fixture`.

## Alternatives considered
- Free navigation with no blocking: rejected, procedures are order-dependent and skipping a prerequisite silently is the failure the tool exists to prevent.
- Single click for destructive steps: rejected, the warning would be easy to miss.
- Persisting run state: deferred, tracked as a backlog item (needs a decision on where it lives and how it relates to a changed guide).

## Consequences
The player is predictable and safe by default but cannot resume an interrupted session yet. Controls.axaml still carries
unused AppShelf styles to trim later.
