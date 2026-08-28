# CI and release design

## Problem

ConsoleControl needs repeatable validation for ordinary changes and portable release artifacts for tagged versions. The desktop applications depend on native Avalonia, SDL, and SkiaSharp files, while the controller firmware uses a separate Arduino toolchain. A release must not bypass the checks used on pull requests.

## Usage

Every push and pull request restores locked dependencies, checks formatting, builds, runs both integration modes, and assembles a test Linux archive. Firmware changes also run the controller-bridge build. A tag such as `v0.1.0` runs the same validation before creating a GitHub Release from the desktop archive.

After extraction, users run `bin/consolecontrol-daemon`, `bin/consolecontrol-gui`, or configure an MCP client with the absolute path to `bin/consolecontrol-mcp`.

## Shape

The main workflow owns desktop validation, packaging, and release jobs. Packaging depends on the same validation job used by branch and pull-request runs. Only tagged runs upload the archive, and only the final release job receives `contents: write` permission.

`tools/package-linux-release.sh` owns the portable archive layout. Three launchers under `bin/` select separate self-contained application directories under `libexec/consolecontrol/`. This avoids collisions between publish outputs and keeps native dependencies beside the application that owns them.

The reusable firmware workflow pins Arduino CLI 1.4.1 and `adafruit:nrf52` 1.7.0. Firmware changes run it directly, while tagged main-workflow runs call it before publishing the desktop release. It builds twice from clean compiler output, compares both UF2 files, and verifies the recorded application address range. Hardware behavior remains outside CI. CI always discards the generated UF2.

`global.json` pins .NET SDK 10.0.111 without roll-forward. `Directory.Build.props` records `linux-x64` as the current release runtime. Committed NuGet lock files fix the resolved dependency graph. CI restores in locked mode, so SDK or package drift fails validation instead of changing a release. A future Windows target must add its runtime identifier and regenerate the lock files.

## Synthesis decision

The selected design uses one workflow with distinct jobs. This prevents drift between ordinary validation and tag validation. A separate-workflow candidate contributed the launcher layout, explicit host-dependency limits, and clear failure for missing release inputs. Repeating validation in two workflow files was rejected because the commands could diverge.

## Tradeoffs accepted

- We accept a larger directory archive in exchange for predictable native-library loading.
- We accept longer tag builds in exchange for rebuilding and comparing the firmware.
- We validate but do not publish compiled firmware. Users build it locally from the repository source.

## Open questions and risks

- The GUI has no headless startup test. Hardware acceptance remains necessary for video, Bluetooth, and Switch behavior.
- Signed firmware updates are not implemented, so the UF2 remains a manual recovery-path artifact.
