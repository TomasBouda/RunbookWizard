# 0015 In-app MCP server: read tools, and visible runs that the person confirms

- **Status:** accepted
- **Date:** 2026-10-17
- **Extends:** ADR 0004 (nothing runs without the person) and ADR 0013 (elevation gate). Nothing in them changes.

## Context
Agents write and maintain runbooks, and they want to check them the way the player sees them: parsed, validated,
with their placeholders. Tomáš approved (2026-10-15) letting an agent also drive a run, on two conditions: the run
is visible in the app, and every step an agent wants to run is confirmed by the person in the app. Markdone 0.6.0
(its ADR 0003) and WSPlayer (its ADR 0001–0003) already host an in-app MCP server; this follows the same pattern.

## Decision
- **Transport.**
  - Streamable HTTP, stateless, served by `ModelContextProtocol.AspNetCore` (the version Markdone uses) on
    `http://127.0.0.1:5233/mcp`. Only loopback is bound.
  - Ports already in use: TaskGraph 5230/5231, Markdone 5232, WSPlayer 5240.
  - The port can be set in settings and through `RUNBOOKWIZARD_MCP_PORT`.
  - Requests that carry a browser `Origin` header are rejected, so a web page cannot post to the server.
  - There is no auth token. The trust boundary is loopback on a single-user desktop, as in TaskGraph and Markdone.
- **On by default, with a switch.**
  - A small Settings panel (new; reachable from the header and the command palette) has a toggle "MCP server for agents". It turns the server on or off immediately, without a restart. The
    setting is `McpEnabled` (default true), next to `McpPort`.
  - The settings show the state (listening, off, or port in use) and a copyable
    `claude mcp add --transport http runbookwizard http://127.0.0.1:<port>/mcp`.
  - A port that is already in use is logged once and shown there. It never stops the app from starting.
  - `GET /health` on the same port answers the standard `{ status, version, name: "RunbookWizard" }`.
- **Read tools** run through the Core parser and validators, never the UI. They change nothing.
  - **`list_runbooks`**: the recent guides, the bundled examples and the guide open in the player. Each entry
    gives path, title, source (`recent`, `example` or `open`) and whether it parses.
  - **`get_runbook {path}`**: the title, and the steps with id, title, kind, shell, `elevated`, `destructive`
    (declared, or flagged by the risk scanner), `when`, and the script text as written, with placeholders not
    substituted. It also gives the placeholders with name, type, default, `secret` and description.
    - It never returns a value the person entered.
  - **`validate_runbook {path}` or `{content}`**: the schema and semantic diagnostics as
    `{ severity, line, message }`. This lets an agent validate a draft before writing it to disk.
- **Action tools** act on the player in the app, and each is shown there as an agent action.
  - **`open_runbook {path}`**: opens the guide in the player, like File → Open, and brings the window forward. A
    run already in progress is not replaced: the tool returns an error and asks the person to close the run first.
  - **`fill_placeholders {values}`**: sets values for placeholders of the open guide. The person sees them filled
    in the form and can change them.
    - A placeholder of type `secret` is refused. The person types secrets.
    - An unknown name is refused, and the error names it.
  - **`run_step {stepId}`**: asks to run one step. Only the step the player currently offers can be run, so the
    flow, the `when` conditions and the checklists stay where ADR 0006/0008/0010 put them.
    - **Confirmation in the app.** The app shows the request: which agent (from the MCP client info), which step,
      the exact command preview with secrets masked, and the destructive or elevated warning. The person chooses
      **Run** or **Decline**.
    - The tool call waits for that answer for at most 120 s. No answer counts as declined.
    - Nothing runs without the person's Run. The app never treats a step as confirmed in advance, and never
      treats one confirmation as covering later steps.
    - **Elevated steps.** A step with `elevated: true` in a player that is not elevated is refused before any
      prompt, with the ADR 0013 reason. Restarting as administrator is something only the person does in the UI.
      In an elevated player the step still needs the confirmation.
    - **Other step types.** Only `script` steps run through the tool. An `info`, `manual` (with or without a checklist) or `choice` step is not run by an agent. The tool answers that the
      person has to complete it in the app.
    - **What the tool returns:** `declined`, or the step result: success or failure with the reason the player
      gives, the exit code, and the last 16 KB of output with secret values masked, exactly as in the player.
      The player advances (or not) exactly as after a click.
- **Limits and hygiene.**
  - `path` must exist and be a guide file (`.md` or `.json`, as File → Open accepts) no larger than 1 MB.
  - `content` is limited to 1 MB.
  - `values` is limited to 100 entries, each no longer than 4 KB.
  - Unknown tool arguments are rejected.
  - The log records the tool name, the step id and the outcome, never the arguments' values.
  - At most one `run_step` waits at a time. A second one is refused.
- **Details settled during the implementation.**
  - `path` must be absolute; a relative path is refused.
  - `validate_runbook` diagnostics also carry the JSON `path` of the problem.
  - `get_runbook` gives no default for a secret placeholder.
  - "A run in progress" for `open_runbook` means one of these: a request waits, a step is running, or the guide is
    unfinished and the person has done, failed or skipped a step. Opening a guide and typing values does not count.
  - For a destructive step, the banner shows the warning, and its **Run** is the explicit extra confirmation that
    ADR 0004 asks for. The step's own two-click confirmation is not asked again.
  - `fill_placeholders` is refused while a request waits or a step runs, so the command the person confirms cannot
    change under them.
  - A confirmed run behaves exactly like a click: the player does not move on by itself. The result names the
    `currentStep`.
  - The agent is named by MCP ClientInfo: the title, else the name, at most 60 characters, else "an agent".

## Alternatives considered
- **`run_runbook` (run all steps unattended):** rejected. It is out of scope by the approval, and it contradicts
  ADR 0004.
- **Confirmation once per run instead of per step:** rejected. A single yes would cover destructive steps that
  appear later, after branching.
- **Agent supplies secrets:** rejected. A secret would then pass through the agent's transcript. Secrets remain
  typed in the app and are passed as `RUNBOOK_VAR_*` environment variables (ADR 0004).
- **stdio server (a separate process):** rejected. It could not show anything in the running app or get the
  person's confirmation.
- **Auth token:** postponed. It is not needed for loopback on a personal desktop, the same as Markdone and
  TaskGraph.

## Consequences
- The App project references ASP.NET Core (Kestrel) via `ModelContextProtocol.AspNetCore`. It has a small
  hosted server and a dispatcher that marshals tool calls onto the UI thread.
- The player gets an "agent request" banner (confirmation) and marks the actions an agent took (opened, filled,
  ran), so the person can always see what an agent did.
- Tests cover:
  - every tool's happy path and limit errors;
  - refusal of a secret placeholder;
  - declined, timed-out and confirmed runs;
  - an elevated step refused in a non-elevated player;
  - refusal of a step that is not current;
  - the Origin rejection.
- They also include one HTTP round trip of initialize, then tools/list, then list_runbooks.
