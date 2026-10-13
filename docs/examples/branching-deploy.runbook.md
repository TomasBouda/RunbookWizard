---
runbook: 1
title: Deploy a site with a choice and a way back
description: A demo of branching and of parameters typed straight into the command. It only prints text, so it is safe to run.
version: "1.0"
platforms: [windows]
shell: powershell
variables:
  - { name: Site, label: Site name, default: demo, required: true, pattern: '[a-z0-9-]+', description: 'Lowercase letters, digits and dashes.' }
  - { name: Port, label: Port, type: number, default: 8080, required: true }
  - { name: Environment, label: Environment, type: choice, choices: [staging, production], default: staging }
  - { name: ApiToken, label: API token, type: secret, required: true, description: 'Masked everywhere; it is never saved.' }
  - { name: FailDeploy, label: Make the deploy fail, type: bool, default: false, description: 'Switch to true to see the failure route.' }
---

This guide shows two things.

- **Branching.** You choose how careful to be. The steps of the path you did not take are skipped, and if the deploy fails the guide
  continues at the rollback instead of stopping.
- **Parameters in the command.** The highlighted fields in a command are the parameters. Type into them right there; the Parameters page
  shows the same values.

## How careful should the deploy be?

```step
id: pick
type: choice
options:
  - label: Quick deploy
    goto: quick-check
    description: One sanity check, then deploy.
  - label: Careful deploy
    goto: backup
    description: Back up the current site first, then deploy.
```

Pick one. You can come back and pick the other path before you deploy.

### Why

A backup costs time but makes a bad deploy harmless. The quick path is for sites where that does not matter.

## Quick sanity check

```step
id: quick-check
next: deploy
```

Looks at the site before the deploy.

### Why

Deploying over a broken site hides which of the two broke it.

```powershell
Write-Output "Checking {{Site}} on port {{Port}}"
```

## Back up the current site

```step
id: backup
```

Copies the current site aside, so the rollback has something to restore.

### Why

Without a copy, a failed deploy leaves you with neither the old nor the new site.

```powershell
Write-Output "Backed up {{Site}} to backup-{{Site}}"
```

## Deploy

```step
id: deploy
onFailure: rollback
success: { outputMatches: 'Deployed' }
```

Deploys the site to the chosen environment. The token is part of the command here only to show that it stays masked: in your own
guides pass secrets through `$env:RUNBOOK_VAR_ApiToken` instead.

### Why

This is the step that can fail. When it does, the guide offers the rollback so the site is not left half deployed.

```powershell
Write-Output "Deploying {{Site}} to {{Environment}} on port {{Port}} with token {{ApiToken}}"
if ('{{FailDeploy}}' -eq 'true') { Write-Output 'The deploy failed.'; exit 2 }
Write-Output "Deployed {{Site}}"
```

## Verify the site

```step
id: verify
next: warm-cache
success: { outputMatches: 'is up' }
```

Checks that the deployed site answers.

```powershell
Write-Output "{{Site}} is up on port {{Port}}"
```

## Roll back

```step
id: rollback
next: finish
```

Restores the site from the backup. This step is offered only when the deploy failed.

### Why

It puts the previous version back, so a failed deploy does not mean an outage.

```powershell
Write-Output "Rolled back {{Site}}"
```

## Warm the cache

```step
id: warm-cache
when: "{{Environment}} == production"
```

Requests the main pages once so the first visitor does not wait. Only for production, so with `staging` this step is skipped by its
condition.

```powershell
Write-Output "Warmed the cache of {{Site}}"
```

## Finished

```step
id: finish
```

The site is deployed, or it was rolled back. Either way the guide has reached its end.
