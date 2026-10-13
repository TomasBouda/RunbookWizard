# 0008 Branching in guides

- **Status:** accepted
- **Date:** 2026-10-13

## Context
Guides were a straight line (ADR 0006): ordered, blocking steps. Real procedures have a decision ("quick or careful?")
and a way back when something fails (a rollback). A full flow graph was judged too heavy for guides that are meant to be
read and written as Markdown.

## Decision
Simple branching, additive to format version 1 (existing guides are unchanged and still valid):
- New step type `choice` with `options: [{ label, goto, description? }]` (at least two).
- Per-step `next` (jump after success or skip) and `onFailure` (jump after a failure, next to Retry and Skip).
- Targets are step ids. The schema checks the shape; the semantic validator rejects unknown targets, duplicate option
  labels and **loops** (a path that returns to a step it already visited). Repeating a step is what Retry is for.
- **The path is derived, not stored.** `Core/Execution/FlowPlanner` computes the path from the statuses of the steps:
  done or skipped goes to `next` else the following step; a done `choice` to the chosen target; a failed step to
  `onFailure`, else on with `continueOnFailure`, else it blocks. Steps that are off the possible path get the reversible
  status "skipped by branch", so picking another option or Reset brings them back. No separate run state exists (ADR 0002).
- `when` is unchanged and combines with branching: a step skipped by its condition continues at its `next`.
- Capture ordering ("a variable used before a step captured it") is judged in file order, not along the path.

## Consequences
- A guide author can express choices and rollbacks without a graph editor.
- A path that depends on loops (poll until ready) cannot be written; a script does the waiting instead.
- Jumps backwards are allowed as long as they do not form a loop on any path.
- Older players do not know `choice`/`next`/`onFailure`; the format version stays 1 because they are only additions,
  and the schema rejects them for readers of the old schema.
