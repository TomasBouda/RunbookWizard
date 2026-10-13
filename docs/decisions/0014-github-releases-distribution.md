# 0014 Distribution through GitHub Releases and TomLabs.AutoUpdate

- **Status:** accepted
- **Date:** 2026-10-13
- **Supersedes:** [0005](0005-distribution.md)

## Context
The repository is public (`TomasBouda/RunbookWizard`), so releases are built by GitHub Actions and published to
GitHub Releases; an Azure DevOps pipeline and pushing to the app store are not used for public repositories.
ADR 0005 left the updater off until a manifest URL was configured; that no longer fits.

## Decision
- The app embeds `TomLabs.AutoUpdate.Avalonia` and starts `Updater` with `GitHubReleasesSource("TomasBouda", "RunbookWizard")`,
  `AppName` `RunbookWizard` (matches the `RunbookWizard-<rid>.zip` assets). Stable follows the latest release, nightly the
  rolling `nightly` pre-release. The version comes from the build (`Directory.Build.props`, plus `-nightly.<sha>` for nightly builds).
- Manifests are signed: the ECDSA P-256 key pair lives in the `~/.tomlabs` vault (DPAPI) and the CI secret
  `UPDATE_SIGNING_KEY`; the public key is embedded as `UpdateOptions.PublicKeyPem`. KeePass is the key's backup.
- `.github/workflows/build.yml` (nightly on push to master) and `release.yml` (tag `v*`) call the shared
  `TomasBouda/TomLabs.AutoUpdate/.github/workflows/publish-app.yml`.
- The app store lists RunbookWizard as a pointer (`Register-GitHubApp.ps1`); it mirrors `README.md`, `CHANGELOG.md`,
  `icon.png` and `appstore.json` from the repository root. Nothing is pushed to the store from this repository.
- Development: `RUNBOOKWIZARD_UPDATE_MANIFEST` (or settings `updateManifestUrl`) points the updater at a test manifest,
  `RUNBOOKWIZARD_NO_UPDATE=1` switches it off. From `dotnet run` or any build that cannot replace its own exe the library
  declines to apply updates; offline checks stay silent.

## Consequences
Going live needs Tomas's approval for: pushing, the `UPDATE_SIGNING_KEY` secret, the `v0.2.0` tag and the store registration.
Losing the private key stops updates for every installed copy.
