# 0012 Placeholder escape is `{{!Name}}`, not `\{{Name}}`

- **Status:** accepted
- **Date:** 2026-10-13
- **Supersedes:** the escape rule (`\{{Name}}`) of ADR 0003 / the format spec section 4.2, and the escape mention in ADR 0009.

## Context
Running `docs/examples/iis-site.runbook.md` showed the literal `{{SiteName}}` in the command preview instead of an editable field.
Reproduced headless: `Test-Path "IIS:\AppPools\{{SiteName}}"` is rendered as `IIS:\AppPools{{SiteName}}` in the executed command.
Cause: `\{{Name}}` was the escape for a literal `{{Name}}`, so a placeholder right after a path backslash lost the backslash and was
not substituted. The executed command was wrong, not only the preview. On Windows a backslash before a placeholder is
common (`C:\{{Folder}}`, `IIS:\Sites\{{Site}}`, `\\server\{{Share}}`), while a literal `{{Name}}` in a script is rare.

## Decision
The escape is `{{!Name}}` (renders as `{{Name}}`). A backslash has no meaning for the substitution. The same regex shape is used by
`Template` and the inline field generator. No migration: none of the shipped examples used the old escape.

## Consequences
- A guide that relied on `\{{Name}}` to stay literal now gets the value substituted (and the backslash kept). Pre-1.0, so accepted.
- A guide with `{{!Name}}` read by an older player shows `{{!Name}}` unresolved; it does not run wrongly silently because the text stays visible in the preview.
