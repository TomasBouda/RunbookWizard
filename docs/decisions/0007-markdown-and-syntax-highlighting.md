# 0007 Markdown rendering and syntax highlighting

- **Status:** accepted
- **Date:** 2026-10-13

## Context
Step descriptions, "why" and info text are Markdown, and the command preview is code. Both were shown as raw text.
Guides are untrusted (ADR 0004), so rendering must not execute anything. Mature Avalonia libraries were preferred over
hand-written parsing.

## Decision
- **Markdown:** `Markdown.Avalonia` 11.0.3 (engine only, `Markdown.Transform`). 11.0.2 fails on Avalonia 11.3
  (`StaticBinding` in a `MultiBinding`); the 12.x previews target Avalonia 12. `Controls/MarkdownView` wraps the engine,
  adds `Styles/Markdown.axaml` (theme tokens, so light and dark follow the app) and uses a plain-text fallback if
  rendering throws. Links open only for `http`/`https`.
- **Highlighting:** `Avalonia.AvaloniaEdit` 11.3.0 as a read-only `Controls/CodeView`. Stock grammars cover PowerShell,
  JSON, XML, C#, JS, Python, SQL and more; there is no bash or batch grammar, so `Assets/Highlighting/Bash.xshd` and
  `Batch.xshd` are our own. TextMate grammars were considered and dropped (heavier, extra theme plumbing).
- **Fenced blocks:** the engine's `SyntaxHigh` plugin (shipped in the Full package) is not used. `MarkdownView` replaces
  each rendered code block with a `CodeView` after load. This relies on the engine's visual structure (`Border.CodeBlock`).
- **Dark theme:** the grammars are drawn for a light background, so `CodeHighlighting.Apply` re-tones each colour for the
  active theme (same hue, lifted lightness; near-black/white becomes the theme text colour).
- **Command preview** uses the shell of the step (`CodeLanguage.ForShell`). The preview is the already masked script, so
  secret masking is unchanged.
- **Live output stays plain.** Output lines are not code, are streamed, and are coloured by stream (stderr).

## Consequences
- Two new packages; the Full Markdown package also pulls in unused plugin assemblies.
- Highlighting quality of the bash and batch grammars is basic (keywords, strings, variables, comments, numbers).
- A Markdown.Avalonia upgrade needs a visual re-check of code blocks.
