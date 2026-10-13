# 0002 Local-only application, small JSON settings, no run-state database

- **Status:** accepted
- **Date:** 2026-10-13

## Context
A guide runs on the machine in front of the person, with that person's rights. There is nothing to sync and no
account. Guides are files the person (or an agent) puts on disk.

## Decision
No server, no database, no telemetry. The app keeps `settings.json` (theme, recent guide files) in
`%APPDATA%\RunbookWizard` (override: `RUNBOOKWIZARD_DATA_DIR`), logs in `%LOCALAPPDATA%\RunbookWizard\logs`
(daily rolling, 14 days). The state of a run (step statuses, entered values, outputs) lives in memory only in
version 0.1; secret values are never written anywhere. A guide is read from wherever it lies; the app never
modifies it.

## Alternatives considered
- **SQLite/LiteDB run history:** useful for resuming a half-finished runbook, but it is state that can leak
  secrets and needs migrations. Deferred to the backlog as "persist run state and export a run log"; when done it
  will be a plain Markdown/JSON export the person chooses to save, not an implicit database.
- **Storing guides in the app's data folder:** would hide them from git and from agents; guides stay ordinary files.

## Consequences
Closing the app loses run progress (acceptable for v0.1, recorded in the backlog). Nothing to back up or migrate.
