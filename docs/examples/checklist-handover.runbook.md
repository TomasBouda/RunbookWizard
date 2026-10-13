---
runbook: 1
title: Hand over a workstation with checklists
description: A demo of checklists in manual steps, together with Markdown text and a choice. It only prints text, so it is safe to run.
version: "1.0"
platforms: [windows]
shell: powershell
variables:
  - { name: Person, label: New owner, default: Ada, required: true, pattern: '[A-Za-z ]+' }
---

This guide shows **checklists**. A manual step can contain a list of `- [ ]` items; the player turns them into checkboxes and the
confirm button stays disabled until every required item is ticked. An item that starts with `(optional)` may stay unticked.

## How thorough should the handover be?

```step
id: pick
type: choice
options:
  - label: Full handover
    goto: prepare
    description: Go through the whole checklist, then print the summary.
  - label: Quick handover
    goto: quick-signoff
    description: Only the two things that must never be forgotten.
```

Pick one. You can come back and pick the other option before you confirm a step.

## Prepare the workstation

```step
id: prepare
type: manual
confirm: The workstation is ready
```

Go through the machine with the list below. The text around the list is ordinary **Markdown**, so it can use `code`, *emphasis* and
[links](https://example.com).

- [ ] Sign out of every personal account
- [ ] Remove the `C:\Users\Public\Share` folder
- [ ] Update Windows and **restart** once
- [ ] (optional) Wipe the browser profile

The list may sit anywhere in the text; it is always shown as a block under it.

### Why

A forgotten signed-in account or shared folder is the usual way private data leaks to the next owner.

## Print the summary

```step
id: summary
next: finish
```

Prints what will be written on the handover note.

```powershell
Write-Output "Workstation handed over to {{Person}}"
```

## Quick sign-off

```step
id: quick-signoff
type: manual
confirm: I have done both
next: finish
```

The minimum for a quick handover:

- [ ] Sign out of every personal account
- [ ] Hand over the charger

## Finished

```step
id: finish
```

The workstation has a new owner. Pick the other option on the first step to see the other path.
