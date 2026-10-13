# Architectural decision records

One file per decision, `NNNN-short-title.md`. A decision is never edited into something else: a change of mind is
a new record that supersedes the old one. Current shape of the system: [../ARCHITECTURE.md](../ARCHITECTURE.md).

| # | Decision | Status |
|---|---|---|
| [0001](0001-stack-and-solution-layout.md) | Stack and solution layout: Avalonia, net10.0, Core library + App | accepted |
| [0002](0002-local-only-storage.md) | Local-only application, small JSON settings, no run-state database | accepted |
| [0003](0003-guide-format.md) | One guide format: Markdown + YAML frontmatter or JSON, one canonical model, JSON Schema | accepted |
| [0004](0004-execution-model-and-trust.md) | Execution model: nothing runs without a click, exact command shown, guides are untrusted | accepted |
| [0005](0005-distribution.md) | Distribution through TomLabs.AutoUpdate and the app store | proposed |
| [0006](0006-player-behaviour-and-ui.md) | Player behaviour and UI: ordered blocking steps, double confirmation for destructive, ops-console style | accepted |
| [0007](0007-markdown-and-syntax-highlighting.md) | Markdown rendering (Markdown.Avalonia) and syntax highlighting (AvaloniaEdit + own bash/batch grammars) | accepted |
| [0008](0008-branching-in-guides.md) | Branching in guides: `choice`, `next`, `onFailure`, derived path, no loops | accepted |
| [0009](0009-inline-parameters.md) | Parameters inside the command: fields as AvaloniaEdit inline objects | accepted |
| [0010](0010-checklists-in-manual-steps.md) | Checklists in manual steps: `- [ ] item` in the description, `(optional)` marker, confirm gated, state as ticked indexes | accepted |
| [0011](0011-license.md) | License: Apache-2.0 | accepted |
| [0012](0012-placeholder-escape.md) | Placeholder escape is `{{!Name}}`: a backslash before a placeholder is a path separator | accepted |
| [0013](0013-elevation-handling.md) | Elevation handling: detect, banner, gate elevated steps, restart as administrator through UAC without secrets | accepted |
