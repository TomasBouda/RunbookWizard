# 0010 Checklists in manual steps

- **Status:** accepted
- **Date:** 2026-10-13

## Context
A `manual` step says "do this, then confirm". Real manual work is often several small things ("sign out, remove the
share, restart") and one confirm click makes it easy to skip one. Guides are Markdown that people and AIs write, so the
syntax had to be something they already know, with no new field.

## Decision
Additive to format version 1 (no schema change, existing guides are unchanged):
- A checklist is Markdown task-list lines in the description of a `manual` step: `- [ ] item` (`*` and `+` bullets also work).
  Only the unticked form is an item; `- [x]` is ordinary text. Lines inside code fences are examples, not items.
- **Optional items** start with `(optional)` (case-insensitive): `- [ ] (optional) Wipe the browser profile`. Everything else is required.
- The player takes the item lines out of the rendered description (the rest stays Markdown, see ADR 0007) and shows them as
  checkboxes in a block under it. The confirm button is shown but disabled until every required item is ticked, with a
  "2 of 3 required items ticked" counter and a hint. Optional items never block.
- Boxes are editable while the step is open; they are locked once the step is done, skipped by a branch or blocked.
  **Reset** clears them. Item text is plain text (inline Markdown markers are dropped).
- Because `description` carries the list, the JSON form needs nothing new and the JSON Schema is unchanged. The semantic
  validator warns when a checklist is in a non-manual step (it would only be plain text) and when every item is optional.
- Logic is in `Core/Execution/Checklist` (parse, gating, progress, fingerprint) and unit tested; the UI binds to it.
- Branching (ADR 0008) is unaffected: a manual step with unticked required items is simply an open step on the path, so
  the path holds there; confirming it settles the step and the path continues at its `next` or the following step.

## State and the pending "persist run state" task
The tick state lives in `StepViewModel` for the run only (ADR 0002: no run-state store yet). It is designed to be stored later
without changing the format or the parser:
- The whole state of a step is the **set of ticked item indexes** (`StepViewModel.TickedItems`, 0-based, in document order),
  keyed by the step id.
- `Checklist.Fingerprint` (a short hash of the items' text and required flag) is stored with it. When a guide was edited and the
  fingerprint no longer matches, the stored ticks are discarded rather than applied to different items.
- A store restores by setting `ChecklistItemViewModel.IsChecked`; gating recomputes from that, nothing else is derived.

## Consequences
- Authors get checklists with syntax they know; an older player shows the list as plain Markdown and the step can still be confirmed.
- Items cannot be nested, multi-line or carry their own ids; reordering items in a guide invalidates stored ticks (fingerprint).
- Nothing is saved when the app is closed until the run-state task is done.
