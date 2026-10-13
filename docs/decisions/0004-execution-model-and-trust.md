# 0004 Execution model: nothing runs without a click, exact command shown, guides are untrusted

- **Status:** accepted
- **Date:** 2026-10-13

## Context
A guide may be written by an AI or downloaded from anywhere, and it contains commands that run with the user's
rights. The player must therefore never be a way to run something the person has not seen and chosen.

## Decision
- Loading a guide executes nothing. A script step runs only when the person presses its Run button.
- The command preview is the exact text that will be executed (parameters substituted); only values of parameters
  declared `secret` are masked in the preview and in logs. All parameters are also passed to the process as
  environment variables (`RUNBOOK_VAR_<name>`), so a script can avoid textual substitution altogether (preferred
  for secrets).
- A step marked `destructive`, or one the built-in risk scanner recognises as potentially destructive, shows a
  warning and needs an explicit extra confirmation before Run is enabled. A step with `elevated: true` says it needs
  administrator rights; the player never elevates silently (it runs with its own rights and says so).
- Scripts are written to a temporary file (UTF-8) and run through the chosen shell as a child process with
  redirected output, a timeout and cancellation. PowerShell steps run inside a thin wrapper that sets
  `$ErrorActionPreference = 'Stop'` and propagates the exit code of the last native command, because
  `pwsh -File` otherwise reports success after a failed native command (verified). Other shells run the file as it is.
- Success is decided by the step's `success` rule (exit codes, output regular expressions), default exit code 0, and
  the player says why it considers a step succeeded or failed.
- A failed step can be retried or skipped; it never advances on its own. `when` conditions only decide whether a
  step is offered or skipped; they never run anything.

## Alternatives considered
- **Run all steps automatically ("play"):** rejected, contradicts the trust model.
- **Sandboxing/containers:** the typical guide installs software on this very machine; a sandbox would defeat it.
- **Signature verification of guides:** deferred; the preview-before-run rule applies to every guide regardless.

## Consequences
The safe path is also the only path. The wrapper behaviour must be documented to guide authors (in `docs/FORMAT.md`).
The risk scanner is a heuristic and can only add warnings, never remove one.
