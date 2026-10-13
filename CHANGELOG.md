# Changelog

## Unreleased

- Added: branching in guides. A `choice` step lets the reader pick a way; `next` and `onFailure` jump to another step; steps on a path not taken show as skipped. Unknown targets and loops are rejected when the guide opens.
- Added: parameters inside the command. The `{{Name}}` placeholders in the command preview are fields you edit right there (masked for secrets); they share the value with the Parameters page.
- Added: the example guide `docs/examples/branching-deploy.runbook.md`.
- Added: Markdown in step descriptions, "why" and info text is rendered (bold, italic, code, lists, links, headings, fenced code).
- Added: syntax highlighting for the command preview (PowerShell, bash, cmd) and for fenced code blocks, in light and dark.

## 0.1.0 — 2026-10-13

- Added: one guide format, Markdown with YAML frontmatter or JSON, validated against a JSON Schema, with a specification and examples.
- Added: the player. Step list with status, description, "why", the exact command, a Run button and live output.
- Added: script steps in PowerShell, bash or cmd, success checks (exit code, output patterns), captured values and parameters entered once.
- Added: conditions, retry after a failure, skip, and a second confirmation for destructive steps.
- Added: secret parameters are masked in the command, the output and the log.
- Added: Logs page, command palette (Ctrl+K), System/Light/Dark theme, version in the header.
