# RunbookWizard

Avalonia (net10.0) player for step-by-step guides. Read `docs/ARCHITECTURE.md` and `docs/decisions/` first.

## Build and test
```
dotnet build RunbookWizard.slnx
dotnet test RunbookWizard.slnx
```

## Verifying the UI (headless, never the live desktop)
Uses the `tomlabs-ui` tool (`dotnet tool install -g TomLabs.UiAutomation.Tool`), run from the repository root.
```
tomlabs-ui start                    # builds Release into .ui-build, copies tests/ui-fixture to %TEMP%\RunbookWizard-ui-<port>
tomlabs-ui call "do/open?arg=<FULL path of the guide>"
tomlabs-ui call "click?name=StartButton"
tomlabs-ui call "get?path=SelectedStep.Status"
tomlabs-ui shot out.png
tomlabs-ui stop
```
- Env var `RUNBOOKWIZARD_AUTOMATION=<port>` enables the channel; data folder from `RUNBOOKWIZARD_DATA_DIR` or `--data-dir`.
- App actions: `do/page?arg=overview|parameters|logs`, `do/open?arg=<absolute path>` (a relative path resolves against `.ui-build`), `do/select?arg=<step id or number>`, `do/choose?arg=<option number or label>` (answers the selected choice step), `do/elevation?arg=on|off|real` (pretends the player is or is not elevated, to show both states), `do/tick?arg=<item number|all|none>` (toggles checklist items of the selected manual step; confirm with the `FinishButton` click).
- Button names: StartButton, RunButton, ConfirmRunButton, CancelConfirmButton, StopButton, FinishButton, NextButton, SkipButton, ResetButton.
- The fixture guide `tests/ui-fixture/ui-demo.runbook.md` exercises every state (success and capture, condition skip, failure, destructive, manual). It only touches its own temp folder.
