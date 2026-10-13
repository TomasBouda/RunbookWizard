---
runbook: 1
title: Hello, RunbookWizard
description: A harmless tour of every step type. Everything it creates lives in the temp folder and the last step removes it again.
version: "1.0"
author: Tomas Bouda
tags: [demo, tutorial]
shell: powershell
variables:
  - name: Name
    label: Your name
    description: Used in the greeting.
    default: World
    required: true
    pattern: '[A-Za-z ]{1,30}'
  - name: Mode
    label: Mode
    type: choice
    choices: [quick, thorough]
    default: quick
---

This guide shows how a runbook looks and behaves. Nothing runs until you press a button, and each script is shown
exactly as it will be executed.

## Read this first

A runbook is a list of steps. A step is either **info** (you read it), **manual** (you do something and confirm) or
**script** (the player runs it). The *Why* section explains what the step is for.

### Why

Knowing the purpose of a step lets you decide whether to run it, change it or skip it.

## Confirm you are ready

```step
type: manual
confirm: I have read the introduction and I am ready to run the demo.
```

Manual steps are for things a script cannot do: signing in somewhere, rebooting, approving a prompt.

## Say hello

```step
success:
  outputMatches: 'Hello, '
```

Prints a greeting that uses the name from the Parameters page. The step counts as done when the output contains the
greeting.

### Why

Shows parameter substitution (`{{Name}}`) and an output check instead of relying on the exit code alone.

```powershell
Write-Output "Hello, {{Name}}!"
```

## Create a scratch file

Writes a small file into the temp folder, so there is something to clean up later.

### Why

The last step is destructive on purpose, to show the extra confirmation.

```powershell
$path = Join-Path $env:TEMP 'runbookwizard-demo.txt'
Set-Content -Path $path -Value "Created for {{Name}} in {{Mode}} mode"
Get-Item $path | Select-Object FullName, Length
```

## Find out the PowerShell version

```step
captures:
  - variable: PsVersion
    pattern: '(\d+\.\d+\.\d+)'
```

Prints the version of PowerShell that runs the steps and remembers it in the variable `PsVersion`.

### Why

Later steps can use values that earlier steps found out.

```powershell
$PSVersionTable.PSVersion.ToString()
```

## Use the captured value

```step
when: "{{os}} == windows"
```

Only offered on Windows. Uses the version captured above.

```powershell
Write-Output "Running on PowerShell {{PsVersion}} ({{os}})"
```

## An exit code that is fine

```step
success:
  exitCodes: [3]
```

The script ends with exit code 3, which this step lists as success. Many installers use codes like 3010 ("reboot
required") for "worked".

### Why

The player judges a step by the rule in the guide, not by "zero is good", and tells you which rule decided.

```powershell
Write-Output 'Finished with a non-zero but expected exit code'
exit 3
```

## Remove the scratch file

```step
destructive: true
warning: Deletes the demo file in the temp folder. Nothing else is touched.
```

Removes `runbookwizard-demo.txt` from the temp folder.

### Why

Leaves the machine as it was before the demo.

```powershell
Remove-Item -Path (Join-Path $env:TEMP 'runbookwizard-demo.txt') -ErrorAction SilentlyContinue
Write-Output 'Cleaned up'
```
