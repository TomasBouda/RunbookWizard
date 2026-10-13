# 0003 One guide format: Markdown + YAML frontmatter or JSON, one canonical model, JSON Schema

- **Status:** accepted
- **Date:** 2026-10-13

## Context
The point of the project is a single format for step-by-step procedures that a human can hand-write and an AI can
generate reliably, and that a program can validate before anything runs. Tomáš chose "Markdown with YAML
frontmatter (preferred) or JSON".

## Decision
Two serializations of **one canonical document model** (`schema/runbook.schema.json`, JSON Schema 2020-12):

1. **`*.runbook.md`** - YAML frontmatter holds guide metadata and parameters; each `## ` section of the body is one
   step: the heading is the title, prose is the description, a `### Why` sub-section is the explanation, one fenced
   code block tagged with a shell (`powershell`, `bash`, `cmd`, ...) is the script, an optional fenced block tagged
   `step` carries the step attributes as YAML.
2. **`*.runbook.json`** - the canonical model written out directly.

The parser turns either into the same JSON tree, validates the tree against the schema, then applies semantic checks
(duplicate ids, undefined variables, invalid regular expressions, malformed conditions) and produces typed
diagnostics with a path. The full normative text is `docs/FORMAT.md`; guides under `docs/examples/` are validated
against the schema by the test suite, so spec, schema and examples cannot drift apart unnoticed.

## Survey of existing tools (2026-10-13)
Asked before building: does something already cover "a wizard for non-developers that runs guide steps"?

| Tool | What it is | Adopted | Deliberately not adopted / why it does not cover the need |
|---|---|---|---|
| **Runme** (runme.dev) | Runnable Markdown: fenced blocks executed from a CLI, VS Code extension or notebook UI; attributes after the language tag (`sh { name=build interactive=false }`, HTML-attribute or JSON notation), document and cell options, env prompts | Plain Markdown with fenced code blocks as the authoring form; attributes after the shell tag (`name`, any step field, both notations); named steps; environment variables as the channel for values | Developer-facing (CLI/VS Code), no ordered wizard with step status, no mandatory "why", no success checks, no declared and validated parameters, no "show exact command, run only on click" trust model for untrusted authors |
| **Jupyter `.ipynb`** | JSON notebook of cells with outputs, executed by a kernel server | Nothing of the file format | JSON with embedded outputs is unfriendly to hand-writing and diffs, needs a kernel/server and Python tooling, cell model has no step semantics (status, why, checks, conditions) |
| **Polyglot Notebooks / .NET Interactive** (`.dib`, `.ipynb`) | Multi-language notebooks in VS Code | Idea of per-block language tags | Developer environment inside VS Code; the project is archived and its deprecation announced for 2026 |

No existing tool fully covers the need, so we build on, not instead of: the format stays Runme-shaped (Markdown +
fences + a small attribute layer) and adds the differentiators: a standalone wizard window for non-developer
end users, ordered steps with status, a mandatory-by-convention "why", success checks, parameters entered once,
and nothing runs without a click. Runme-style attributes on the script fence are accepted as shorthand for the `step` block.

## Alternatives considered
- **Everything in YAML (steps in the frontmatter):** perfectly regular but miserable for prose and multi-line
  scripts (indentation, `|` blocks) - the exact things guides consist of.
- **Pure Markdown conventions without attributes:** the nicest to read but cannot express checks, conditions or
  variables without inventing more conventions; the `step` block keeps the Markdown clean and the rest explicit.
- **JSON only:** easy to generate, hard to read and write for people; kept as the interchange form.
- **Existing runbook formats (Rundeck, Ansible playbooks, Jupyter, Runme.dev):** tied to a runtime, much larger, or
  not designed for "explain why" and untrusted authorship.

## Consequences
Any producer can target the JSON Schema and get validation for free; an AI only needs the one-page quick reference in
`docs/FORMAT.md`. The format is versioned (`runbook: 1`); an incompatible change means a new number and a new ADR.
The Markdown form understands only the documented structure; other Markdown in a step description stays description
text and is not interpreted.
