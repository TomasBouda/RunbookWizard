# Architecture

## Purpose
RunbookWizard plays step-by-step guides (setup, install, configuration). Each step has a title, a "why", and a type:
info, manual or script. Script steps run with one click and the app shows what happened and why. The guide format is
one canonical model that people and AIs can both write: [FORMAT.md](FORMAT.md), [schema](../schema/runbook.schema.json).

## Components
- `src/RunbookWizard.Core` (no UI dependency, fully unit tested)
  - `Parsing`: Markdown+YAML or JSON to JsonNode, JSON Schema 2020-12 validation (JsonSchema.Net), typed model, semantic validation. Output: a runbook plus diagnostics.
  - `Execution`: `VariableStore` (values, secrets, masking), `Template` (`{{Name}}` substitution), `Condition` (`when`), `RiskScanner`, `ShellCatalog`, `FlowPlanner` (the path through the guide from `next`/`onFailure`/`choice`, ADR 0008), `StepPlanner` (exact command, working directory, environment, problems, risks), `ScriptRunner` (child process, streamed output, timeout, cancellation), `StepEvaluation` (verdict and captures).
- `src/RunbookWizard.App` (Avalonia 11, MVVM with CommunityToolkit.Mvvm, compiled bindings)
  - `MainWindowViewModel` (pages Welcome, Overview, Parameters, Step, Logs; ordering and blocking), `StepViewModel` (status, commands, output), `VariableViewModel`, `CommandPaletteViewModel`, `LogsViewModel`.
  - `Services`: `SettingsStore` (JSON settings), `AppLog` (Serilog, daily files plus in-memory ring buffer), `ThemeModes`.
  - `Styles`: ops-console tokens and controls copied from IISBlitz/AppShelf, plus `Wizard.axaml`.
  - `Controls`: `MarkdownView` (Markdown.Avalonia engine, themed by `Styles/Markdown.axaml`) and `CodeView` (read-only AvaloniaEdit with `Services/CodeHighlighting`; bash and batch grammars in `Assets/Highlighting`). See ADR 0007. `ParameterElementGenerator` turns `{{Name}}` in the command into inline fields (ADR 0009).
- `tests/RunbookWizard.Core.Tests` unit tests; `tests/ui-fixture` synthetic data for UI automation.

## Data flow
Open a guide, parse, validate, show diagnostics; unrunnable guides (schema or semantic errors) stay on the Overview page.
Parameters are entered once into the `VariableStore`. For a step, `StepPlanner` builds the plan from the current values, the
UI shows the masked command, and on click `ScriptRunner` runs it from a temp script file. Output lines stream to the UI
(secrets masked), `StepEvaluation` judges the result, captures feed later steps, and `when` conditions are re-evaluated.

## Storage
Local only (ADR 0002): `settings.json` and logs under `%LOCALAPPDATA%\RunbookWizard` (override with `RUNBOOKWIZARD_DATA_DIR`
or `--data-dir`). No run-state persistence yet.

## Threading
Parsing is synchronous on open. Scripts run as child processes; output is read asynchronously and posted to the UI thread.
A step can be stopped (cancellation kills the process tree) and a timeout applies.

## Platform-specific parts
Windows first: PowerShell (pwsh or Windows PowerShell), cmd; bash where installed (Git Bash/WSL). Shell discovery is in `ShellCatalog`.
The app uses `UsePlatformDetectOrHeadless` so the same build runs headless for UI automation.

## Known risks
- Guides are untrusted input and scripts run with the user's rights: mitigated by click-to-run, the exact command shown, destructive confirmation and the risk scanner, not by a sandbox (ADR 0004). The risk scanner is heuristic.
- Elevated steps are only flagged, not auto-elevated.
- Secret masking is string replacement of known values; a script that transforms a secret defeats it.
- No resume after closing the app.

## Decisions
See [decisions](decisions/README.md): stack (0001), storage (0002), format (0003), execution and trust (0004), distribution (0005), player behaviour and UI (0006), Markdown and syntax highlighting (0007).
