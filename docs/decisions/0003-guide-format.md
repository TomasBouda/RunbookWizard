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
