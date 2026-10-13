# 0011 License

- **Status:** accepted
- **Date:** 2026-10-13

## Context
The repository lives on GitHub (`TomasBouda/RunbookWizard`) and was created with an Apache-2.0 `LICENSE`. The
dependencies we use (Avalonia, Markdown.Avalonia, AvaloniaEdit) are MIT-licensed, so they are compatible.

## Decision
The project is licensed under **Apache-2.0**. The `LICENSE` file from the initial GitHub commit is kept, the README
states it, and `Directory.Build.props` declares `PackageLicenseExpression` and `Copyright` for every project.

## Consequences
Apache-2.0 includes an explicit patent grant and requires keeping the license and notices in redistributions.
Changing the license later would need the consent of all contributors, so it is decided once here.
