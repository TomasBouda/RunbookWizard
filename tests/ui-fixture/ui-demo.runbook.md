---
runbook: 1
title: UI demo guide
description: A synthetic guide that exercises every state of the player. It touches nothing outside its own temp folder.
shell: powershell
platforms: [windows]
variables:
  - { name: Name, label: Your name, default: Ada, required: true, pattern: '[A-Za-z ]+' }
  - { name: Mode, label: Mode, type: choice, choices: [quick, full], default: quick }
  - { name: Token, label: Demo token, type: secret, default: demo-secret-value }
---

## Read this first

A step of type **info** is only text. It has no command and needs *one click* to move on.

- Descriptions are rendered as Markdown: **bold**, *italic*, `inline code` and [links](https://example.com).
- Lists, headings and fenced code blocks work too.

### Details

```json
{ "format": "runbook", "version": 1, "enabled": true }
```

### Why

It shows the description and the *Why* block as an end user sees them, with **formatting** and `code`.

## Greet

```step
success: { outputMatches: 'Hello, ' }
captures:
  - { variable: Greeting, pattern: '(Hello, .+)' }
```

Prints a greeting built from the parameter above.

### Why

The success rule looks for the greeting in the output, and the capture stores it for the next step.

```powershell
Write-Output "Hello, {{Name}}"
Write-Output "token length: $($env:RUNBOOK_VAR_Token.Length)"
```

## Use the captured text

Uses the value captured by the previous step.

```powershell
Write-Output 'Captured: {{Greeting}}'
```

## Full mode only

```step
when: "{{Mode}} == full"
```

This step is offered only when the mode parameter is set to full.

```powershell
Write-Output 'full mode'
```

## Fails on purpose

```step
continueOnFailure: true
```

Exits with a non-zero code so the failed state, the verdict and Retry can be seen.

### Why

An error here usually means the check did not pass; the verdict names the rule that decided.

```powershell
Write-Output 'about to fail'
exit 3
```

## Clean the demo folder

```step
destructive: true
warning: Deletes the demo folder in the temp directory.
```

Removes a folder this guide created. It asks for a second confirmation.

```powershell
Remove-Item -Recurse -Force (Join-Path $env:TEMP 'runbookwizard-ui-demo') -ErrorAction SilentlyContinue
Write-Output 'demo folder removed'
```

## Restart the machine

```step
type: manual
confirm: I have restarted the machine
```

A manual step: the person does it, then confirms.

## Show the folder with cmd

Runs a **cmd** command so the batch highlighting can be seen.

```cmd
REM list the temp folder
echo Hello %USERNAME%
if exist "%TEMP%" (dir /b "%TEMP%" | find /c /v "") else (echo none)
```
