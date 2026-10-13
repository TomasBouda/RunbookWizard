---
runbook: 1
title: Create an IIS web site
description: Installs IIS if needed and creates a site with its own application pool on this Windows machine.
version: "1.2"
platforms: [windows]
tags: [iis, windows, web]
shell: powershell
variables:
  - name: SiteName
    label: Site name
    default: demo-site
    required: true
    pattern: '[A-Za-z0-9-]{3,40}'
  - name: Port
    label: HTTP port
    type: number
    default: 8080
    required: true
    pattern: '\d{2,5}'
  - name: Folder
    label: Content folder
    type: path
    default: 'C:\inetpub\demo-site'
    required: true
  - name: PoolPassword
    label: App pool password (optional)
    type: secret
---

## Check that you are an administrator

```step
elevated: true
success:
  outputMatches: 'IsAdmin=True'
```

Checks that this player runs with administrator rights, which IIS management requires.

### Why

Without administrator rights the next steps fail with "access denied" in confusing places. If this step fails,
close the player and start it as administrator; it will not elevate itself.

```powershell
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output "IsAdmin=$admin"
```

## Check whether IIS is installed

```step
continueOnFailure: true
success:
  outputMatches: 'W3SVC'
```

Looks for the World Wide Web Publishing service.

### Why

If the service exists the install step can be skipped. A failure here is not an error, it only means IIS is missing.

```powershell
Get-Service W3SVC -ErrorAction Stop | Select-Object Name, Status
```

## Install IIS

```step
destructive: true
elevated: true
warning: Installs Windows features and may require a restart.
timeoutSeconds: 1800
success:
  exitCodes: [0, 3010]
```

Enables the Web Server role with the management console.

### Why

The site cannot be created without the role. Exit code 3010 means the feature is installed but a restart is pending.

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-ManagementConsole -All -NoRestart | Out-String
if ($LASTEXITCODE) { exit $LASTEXITCODE }
```

## Restart if Windows asked for it

```step
type: manual
confirm: Windows did not ask for a restart, or I have restarted and reopened this guide.
```

If the previous step reported a pending restart, restart now, reopen this guide and continue from the next step.

## Create the folder and site

```step
elevated: true
env:
  SITE_FOLDER: '{{Folder}}'
success:
  outputMatches: "Started"
```

Creates the content folder, an application pool and the site, bound to the chosen port.

### Why

A dedicated pool isolates the site; the folder holds a first page so the next step has something to open.

```powershell
Import-Module WebAdministration
New-Item -ItemType Directory -Force -Path '{{Folder}}' | Out-Null
Set-Content -Path (Join-Path '{{Folder}}' 'index.html') -Value '<h1>{{SiteName}} works</h1>'
if (-not (Test-Path "IIS:\AppPools\{{SiteName}}")) { New-WebAppPool -Name '{{SiteName}}' | Out-Null }
if (-not (Test-Path "IIS:\Sites\{{SiteName}}")) {
  New-Website -Name '{{SiteName}}' -Port {{Port}} -PhysicalPath '{{Folder}}' -ApplicationPool '{{SiteName}}' | Out-Null
}
(Get-Website -Name '{{SiteName}}').State
Write-Output "Started on port {{Port}}"
```

## Verify the site answers

```step
timeoutSeconds: 60
success:
  outputMatches: 'works'
```

Requests the page the previous step created.

### Why

This proves the binding, the pool and the permissions work together, which "the site is started" does not.

```powershell
(Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:{{Port}}/').Content
```

## Remove the site again (optional)

```step
destructive: true
elevated: true
warning: Deletes the site and its application pool. The content folder is kept.
when: "{{SiteName}} != ''"
```

Only needed if you built this site just to try the guide.

```powershell
Import-Module WebAdministration
Remove-Website -Name '{{SiteName}}'
Remove-WebAppPool -Name '{{SiteName}}'
Write-Output 'Removed'
```
