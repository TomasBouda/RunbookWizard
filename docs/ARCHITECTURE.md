# Architecture

## Purpose
RunbookWizard plays step-by-step guides (setup, install, configuration). Each step has a title, a "why", and a type:
info, manual or script. Script steps run with one click and the app shows what happened and why. The guide format is
one canonical model that people and AIs can both write: [FORMAT.md](FORMAT.md), [schema](../schema/runbook.schema.json).

## Components
- `src/RunbookWizard.Core` (no UI dependency, fully unit tested)
  - `Parsing`: Markdown+YAML or JSON to JsonNode, JSON Schema 2020-12 validation (JsonSchema.Net), typed model, semantic validation. Output: a runbook plus diagnostics.
  - `Execution`: `VariableStore` (values, secrets, masking), `Template` (`{{Name}}` substitution), `Condition` (`when`), `RiskScanner`, `ShellCatalog`, `FlowPlanner` (the path through the guide from `next`/`onFailure`/`choice`, ADR 0008), `StepPlanner` (exact command, working directory, environment, problems, risks), `ScriptRunner` (child process, streamed output, timeout, cancellation), `StepEvaluation` (verdict and captures), `Checklist` (checkbox items of manual steps, confirm gating, state fingerprint; ADR 0010).
- `src/RunbookWizard.App` (Avalonia 11, MVVM with CommunityToolkit.Mvvm, compiled bindings)
  - `MainWindowViewModel` (pages Welcome, Overview, Parameters, Step, Logs, Settings; ordering and blocking), `StepViewModel` (status, commands, output), `VariableViewModel`, `CommandPaletteViewModel`, `LogsViewModel`.
  - `Services`: `SettingsStore` (JSON settings), `AppLog` (Serilog, daily files plus in-memory ring buffer), `ThemeModes`.
  - `Styles`: ops-console tokens and controls copied from IISBlitz/AppShelf, plus `Wizard.axaml`.
  - `Controls`: `MarkdownView` (Markdown.Avalonia engine, themed by `Styles/Markdown.axaml`) and `CodeView` (read-only AvaloniaEdit with `Services/CodeHighlighting`; bash and batch grammars in `Assets/Highlighting`). See ADR 0007. `ParameterElementGenerator` turns `{{Name}}` in the command into inline fields (ADR 0009).
  - `Mcp`: the in-app MCP server (ADR 0015). `McpServerHost` (Kestrel on 127.0.0.1, `/mcp` and `/health`, refuses Origin headers and foreign hosts), `McpServerController` (start, restart and stop from the Settings toggle), `RunbookTools` (the six tools; action tools run on the UI thread through `IUiThread`), `AgentRequestViewModel` (the Run/Decline banner of `run_step`).
- `tests/RunbookWizard.Core.Tests` and `tests/RunbookWizard.App.Tests` (MCP tools against the real player view model, HTTP round trip) unit tests; `tests/ui-fixture` synthetic data for UI automation.

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
- The player never elevates itself. Elevated steps are gated when it is not elevated (`ElevationGate`, `IElevationProbe`) and the person can restart it through UAC (`ElevatedRelaunch`, ADR 0013); values typed so far are not carried into the new instance.
- Secret masking is string replacement of known values; a script that transforms a secret defeats it.
- The MCP server has no token (ADR 0015): any local process can call it. It is loopback only, refuses browsers (Origin header) and DNS rebinding (Host), and every run still needs the person to press Run in the app.
- No resume after closing the app.

## Decisions
See [decisions](decisions/README.md): stack (0001), storage (0002), format (0003), execution and trust (0004), distribution (0005), player behaviour and UI (0006), Markdown and syntax highlighting (0007), branching (0008), inline parameters (0009), checklists in manual steps (0010), license (0011), placeholder escape (0012), elevation handling (0013), GitHub Releases distribution and self-update (0014, supersedes 0005), in-app MCP server (0015).
