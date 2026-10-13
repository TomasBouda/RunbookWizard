# 0009 Parameters inside the command

- **Status:** accepted
- **Date:** 2026-10-13

## Context
Parameters were entered only on the Parameters page, away from the command that uses them. The brief: the `{{Name}}`
placeholders of the command preview become editable fields inside the highlighted command, the rest stays read only, the
value is shared with the Parameters page, and secrets stay masked. The command is still shown in full before running and
nothing runs without a click (ADR 0004). No format change.

## Decision
- **Approach:** the command stays an AvaloniaEdit `CodeView` (ADR 0007). A `VisualLineElementGenerator`
  (`Controls/ParameterElements.cs`) replaces each placeholder with an `InlineObjectElement` holding a `TextBox` (masked
  for secrets, red border when the value is invalid), or a `ComboBox` for `choice` and `bool` variables. The field is
  bound two-way to the shared `VariableViewModel.Value`, so the Parameters page and the command always agree.
- **What the editor holds:** `StepPlan.InlineScript`. It keeps `{{Declared}}` placeholders and `\{{X}}` escapes;
  everything else (captures, built-ins) is already substituted, with secrets masked. The text that runs is unchanged
  (`StepPlan`), so the exact command and masking rules of ADR 0004 hold.
- **Focus workaround:** the read-only `TextArea` takes the focus back when a `GotFocus` bubbles up to it. A field marks
  its own `GotFocus` handled and focuses itself after the editor's pointer handling, so typing works.
- **Fixed field width:** AvaloniaEdit cannot re-measure an inline object, so the width is chosen when the line is built
  (value length, clamped). A redraw would recreate the controls and drop the focus, so nothing redraws while typing; a longer
  value scrolls inside its field.

## Consequences
- Keyboard navigation between fields (Tab) is not handled specially and was not verified; click into the next field. A follow-up task is filed.
- Selecting text across a field is not possible; the fields are islands in the text.
- Relies on AvaloniaEdit's inline-object support and on the focus behaviour above; re-check on an AvaloniaEdit upgrade.
- The placeholder regex of the generator mirrors `Template`; a change to the substitution syntax must change both.
