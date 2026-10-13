# 0005 Distribution through TomLabs.AutoUpdate and the app store

- **Status:** superseded by [0014](0014-github-releases-distribution.md)
- **Date:** 2026-10-13

## Context
Personal desktop apps use TomLabs.AutoUpdate only. Where the app is published depends on repository visibility
(GitHub Releases for public, the app store apps.tomlabs.cz for private Azure DevOps repositories), and creating
the remote repository, pipeline and signing key are outward-facing actions that need Tomas's approval.

## Decision
The app embeds `TomLabs.AutoUpdate.Avalonia`; the updater stays off until an update manifest URL is configured
(`RUNBOOKWIZARD_UPDATE_MANIFEST` or the release pipeline's default), exactly like AppShelf. The remote, pipeline and
signing key are prepared as commands in the TaskGraph backlog and executed once Tomas approves.

## Alternatives considered
Squirrel/Velopack/NetSparkle are excluded by the personal-app standards.

## Consequences
Until the remote and pipeline exist the app works locally and is started from a build. Status moves to accepted
when the first release is published.
