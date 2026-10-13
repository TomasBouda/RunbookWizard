# 0001 Stack and solution layout

- **Status:** accepted
- **Date:** 2026-10-13

## Context
RunbookWizard is a personal Windows desktop tool that walks a person through a procedure and runs its script
steps. The format and its parser must be reusable (a future MCP server, a CLI validator) without dragging a UI
along, and the personal-app standards (ops-console style, TomLabs.UiAutomation, TomLabs.AutoUpdate) are Avalonia-based.

## Decision
.NET 10 (`net10.0`), Avalonia 11.3 with CommunityToolkit.Mvvm and the IISBlitz "ops console" styles. Three projects:
`RunbookWizard.Core` (model, parser, validator, variable substitution, condition and success evaluation, script
runner; no UI dependency), `RunbookWizard.App` (Avalonia player) and `RunbookWizard.Core.Tests` (xUnit).
Libraries: YamlDotNet (frontmatter), JsonSchema.Net (schema validation), Serilog (log). The version lives only in
`Directory.Build.props`.

## Alternatives considered
- **One project:** quicker, but the parser could not be reused by a CLI or MCP server without referencing Avalonia.
- **Markdig for the Markdown body:** the body is a small, strictly defined subset (headings, fences); a purpose-built
  line scanner is shorter, has no surprises and gives exact error positions. Markdown rendering for display is a
  separate, later concern.
- **Blazor/Electron/WPF:** rejected by the personal-app standards (Avalonia everywhere, one look).

## Consequences
The Core library can be packaged or hosted by other front ends. Tests run without a display. The App stays thin.
