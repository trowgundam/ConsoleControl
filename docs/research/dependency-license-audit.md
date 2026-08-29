# Dependency license audit

This audit covers the proposed `linux-x64` portable archive and the dependencies used to build the controller bridge locally. It records the dependency versions resolved on 2026-08-28. It is an engineering inventory, not legal advice.

## Result

The desktop dependencies use MIT, Apache-2.0, BSD-3-Clause, or zlib licenses. The self-contained applications also ship the MIT-licensed .NET 10.0.11 runtime. The project does not distribute a compiled controller-bridge UF2. Users install the firmware dependencies and build the UF2 locally.

`NanoKVM-USB` and OpenPuck do not belong in the release notices. ConsoleControl contains no source, binary, asset, package, submodule, or generated file from either project. The repository cites both as research references. The firmware comment says that its USB descriptor came from a genuine controller capture, not OpenPuck. A later change that copies either project's code or assets must revisit this conclusion.

The desktop release includes `THIRD-PARTY-NOTICES.md`, the applicable license texts, and the two upstream notice files supplied by the exact .NET runtime packs. The desktop archive excludes firmware binaries and firmware-only dependency licenses.

## Audit method

The runtime list comes from the `libraries` sections of these generated publish manifests:

- `src/ConsoleControl.Daemon/bin/Release/net10.0/linux-x64/ConsoleControl.Daemon.deps.json`
- `src/ConsoleControl.Gui/bin/Release/net10.0/linux-x64/ConsoleControl.Gui.deps.json`
- `src/ConsoleControl.Mcp/bin/Release/net10.0/linux-x64/ConsoleControl.Mcp.deps.json`

The package licenses come from each resolved package's `.nuspec` and packaged license file under the build host's NuGet global-packages directory. The firmware inventory comes from the installed `adafruit:nrf52` 1.7.0 core under the build host's Arduino data directory and the sketch's includes.

The `.deps.json` manifests are a better release inventory than `project.assets.json`. The asset files also contain build-time packages and native assets for platforms that the Linux publish does not ship.

## Desktop runtime components

| Component | Resolved version | Shipped by | License | Primary metadata |
| --- | --- | --- | --- | --- |
| .NET runtime, `Microsoft.NETCore.App.Runtime.linux-x64` | 10.0.11 | Daemon, GUI, MCP | MIT, with bundled third-party notices | [dotnet/dotnet](https://github.com/dotnet/dotnet), local `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` in the runtime package |
| ASP.NET Core runtime, `Microsoft.AspNetCore.App.Runtime.linux-x64` | 10.0.11 | Daemon | MIT, with bundled third-party notices | [dotnet/dotnet](https://github.com/dotnet/dotnet), local `LICENSE.txt` and `THIRD-PARTY-NOTICES.TXT` in the runtime package |
| Google.Protobuf | 3.31.1 | Daemon, GUI, MCP | BSD-3-Clause | [NuGet metadata and source](https://github.com/protocolbuffers/protobuf) |
| gRPC for .NET packages | 2.83.0 | Daemon, GUI, MCP | Apache-2.0 | [grpc/grpc-dotnet](https://github.com/grpc/grpc-dotnet) |
| Tmds.DBus.Protocol | 0.95.0 in Daemon, 0.94.1 in GUI | Daemon, GUI | MIT | [Tmds.DBus](https://github.com/tmds/Tmds.DBus) |
| Avalonia packages | 12.1.1 | GUI | MIT | [Avalonia](https://github.com/AvaloniaUI/Avalonia) |
| MicroCom.Runtime | 0.11.6 | GUI | MIT | [MicroCom](https://github.com/kekekeks/MicroCom) |
| Microsoft.Extensions.DependencyInjection.Abstractions and Microsoft.Extensions.Logging.Abstractions | 8.0.0 | GUI | MIT | [dotnet/runtime](https://github.com/dotnet/runtime) |
| SDL3-CS and SDL3-CS.Linux | 3.4.14.1 | GUI | zlib | [SDL3-CS](https://github.com/edwardgushchin/SDL3-CS), packaged `LICENSE` |
| SkiaSharp and SkiaSharp.NativeAssets.Linux | 3.119.4 | GUI, MCP | MIT | [SkiaSharp](https://github.com/mono/SkiaSharp), packaged `LICENSE.txt` |
| HarfBuzzSharp and HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | GUI | MIT | [SkiaSharp](https://github.com/mono/SkiaSharp), packaged `LICENSE.txt` |
| ModelContextProtocol and ModelContextProtocol.Core | 2.2.0 | MCP | Apache-2.0 | [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MCP | MIT | [dotnet/extensions](https://github.com/dotnet/extensions) |
| Microsoft.Extensions packages listed below | 10.0.0 or 10.0.10 | MCP | MIT | [dotnet/dotnet](https://github.com/dotnet/dotnet) |
| System.Diagnostics.EventLog | 10.0.0 | MCP | MIT | [dotnet/dotnet](https://github.com/dotnet/dotnet) |

The gRPC group contains `Grpc.AspNetCore`, `Grpc.AspNetCore.Server`, `Grpc.AspNetCore.Server.ClientFactory`, `Grpc.Core.Api`, `Grpc.Net.Client`, `Grpc.Net.ClientFactory`, and `Grpc.Net.Common`, all at 2.83.0. `Grpc.Tools` 2.83.0 is build-only because its project reference uses `PrivateAssets="all"`; it does not appear in a release `.deps.json` file.

The Avalonia group contains `Avalonia`, `Avalonia.Desktop`, `Avalonia.FreeDesktop`, `Avalonia.FreeDesktop.AtSpi`, `Avalonia.HarfBuzz`, `Avalonia.Native`, `Avalonia.Remote.Protocol`, `Avalonia.Skia`, `Avalonia.Themes.Fluent`, `Avalonia.Win32`, and `Avalonia.X11`. The Linux publish manifest includes the Win32 managed assembly, but excludes `Avalonia.Angle.Windows.Natives` and all non-Linux SkiaSharp and HarfBuzzSharp native-asset packages. `Avalonia.BuildServices` 11.3.2 is build-only.

The MCP ships these Microsoft.Extensions 10.0.10 packages:

- `Microsoft.Extensions.Caching.Abstractions`
- `Microsoft.Extensions.Configuration.Abstractions`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Diagnostics.Abstractions`
- `Microsoft.Extensions.Hosting.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.Options`
- `Microsoft.Extensions.Primitives`

`Microsoft.Extensions.AI.Abstractions` resolves to 10.8.3. The MCP ships these Microsoft.Extensions 10.0.0 packages:

- `Microsoft.Extensions.Configuration`
- `Microsoft.Extensions.Configuration.Binder`
- `Microsoft.Extensions.Configuration.CommandLine`
- `Microsoft.Extensions.Configuration.EnvironmentVariables`
- `Microsoft.Extensions.Configuration.FileExtensions`
- `Microsoft.Extensions.Configuration.Json`
- `Microsoft.Extensions.Configuration.UserSecrets`
- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Diagnostics`
- `Microsoft.Extensions.FileProviders.Abstractions`
- `Microsoft.Extensions.FileProviders.Physical`
- `Microsoft.Extensions.FileSystemGlobbing`
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Logging`
- `Microsoft.Extensions.Logging.Configuration`
- `Microsoft.Extensions.Logging.Console`
- `Microsoft.Extensions.Logging.Debug`
- `Microsoft.Extensions.Logging.EventLog`
- `Microsoft.Extensions.Logging.EventSource`
- `Microsoft.Extensions.Options.ConfigurationExtensions`

### Desktop notice obligations

- MIT packages require preservation of their copyright and permission notices in copies or substantial portions.
- Apache-2.0 packages require a copy of the license. Preserve any upstream `NOTICE` content when a distributed package supplies it.
- BSD-3-Clause requires its copyright, conditions, and disclaimer in binary-distribution documentation or other materials.
- The zlib notice in both SDL3-CS packages says not to misrepresent origin, to mark altered source versions, and not to remove the notice from source distributions. Product acknowledgment is appreciated but not required.
- The .NET runtime packages each contain `THIRD-PARTY-NOTICES.TXT`. Do not replace those detailed files with a one-line .NET entry.

The assembled `0.1.0-hardening.1` audit archive contained 54 ELF files: 45 .NET runtime files, three ConsoleControl application hosts, two SkiaSharp files, one HarfBuzzSharp file, and three SDL files. The archive included the SkiaSharp and upstream Skia notices, the upstream HarfBuzz notice, the SDL3-CS and upstream SDL notices, and the exact .NET 10.0.11 runtime notices. These agree with the projects' primary license files and release metadata.

`tools/audit-linux-native-assets.sh` now scans the assembled package rather than inferring its contents from NuGet metadata. Every ELF file must match exactly one rule in `tools/linux-x64-native-assets.tsv`, and all notices named by that rule must exist. Packaging fails for an unknown native file, an ambiguous rule, or a missing notice. The generated `NATIVE-ASSET-INVENTORY.tsv` records exact paths and digests for review; digests document the artifact but do not act as a brittle acceptance allowlist.

## Controller firmware components

The workflow installs `adafruit:nrf52` 1.7.0 and compiles for `adafruit:nrf52:feather52840`. The generated UF2 contains only the application region starting at `0x00026000`. It does not package the factory UF2 bootloader or the S140 SoftDevice already installed on the tested board. CI discards the generated firmware when validation finishes.

| Component | Version | How it enters the UF2 | License | Primary metadata |
| --- | --- | --- | --- | --- |
| Adafruit nRF52 Arduino core | 1.7.0 | Arduino core linked into the application | LGPL-2.1-or-later | [Adafruit nRF52 core](https://github.com/adafruit/Adafruit_nRF52_Arduino), local root `LICENSE` |
| Adafruit TinyUSB Library | 3.6.0 | `#include <Adafruit_TinyUSB.h>` | MIT | Local `libraries/Adafruit_TinyUSB_Arduino/library.properties` and `LICENSE`, [upstream source](https://github.com/adafruit/Adafruit_TinyUSB_Arduino) |
| Adafruit Bluefruit nRF52 Libraries | 0.21.0 | `#include <bluefruit.h>` | MIT | Local `libraries/Bluefruit52Lib/library.properties` and `LICENSE`, [upstream source](https://github.com/adafruit/Adafruit_nRF52_Arduino) |
| Adafruit nRFCrypto wrapper | Core-bundled version | Selected by the Bluefruit build | MIT | Local `libraries/Adafruit_nRFCrypto/LICENSE` |
| ARM CryptoCell CC310 archive | 0.9.13 | Precompiled static archive selected by Adafruit nRFCrypto | ARM Object Code and Header Files License 1.0 | Local `libraries/Adafruit_nRFCrypto/src/cortex-m4/license.txt` |
| FreeRTOS kernel | Bundled with core 1.7.0; standalone version not recorded | Core runtime | MIT | Local `cores/nRF5/freertos/License/license.txt` |
| Nordic nrfx | Bundled with core 1.7.0; standalone version not recorded | Core drivers and headers | BSD-3-Clause | Local `cores/nRF5/nordic/nrfx/LICENSE`, [nrfx source](https://github.com/NordicSemiconductor/nrfx) |

The compile may pull more source files from the board core than the direct include list suggests. The installed core does not provide a machine-readable software bill of materials for the linked firmware. Before generating final notices, retain the verbose compiler and linker inputs from a clean tagged build and compare them with the core's per-file license headers. In particular, confirm TinyUSB's bundled upstream code and any Nordic SDK files selected by the linker.

### Why releases exclude the firmware binary

The generated firmware statically links the LGPL-covered Adafruit core and ARM's precompiled CryptoCell archive. The ARM archive license restricts its use and reverse engineering. ConsoleControl therefore publishes the firmware source and local build instructions but no compiled UF2. This avoids distributing the combined binary and keeps firmware dependencies out of the desktop archive.

The firmware sketch grants an additional permission under GPLv3 section 7 for linking or combining it with CryptoCell CC310 archive version 0.9.13 under the ARM Object Code and Header Files License 1.0. This resolves the GPL-side permission for that intended combination without changing the ARM terms. ConsoleControl still does not distribute the combined UF2 because the LGPL relinking obligation and the complete firmware-binary notice set have not been resolved.

Revisit the complete firmware license inventory before any future binary distribution. A future release would need to address LGPL relinking and the GPL compatibility of the ARM archive instead of treating a source archive as sufficient.

## Tools and host dependencies that are not shipped

The release build uses the .NET SDK, Arduino CLI, the Adafruit board package, GNU Arm Embedded binutils, ShellCheck, and GitHub Actions. The portable desktop archive does not contain those tools, so their licenses do not belong in the runtime notice file.

The desktop applications invoke or communicate with host software and libraries such as FFmpeg, BlueZ, D-Bus, and Linux graphics or window-system libraries. The packaging script does not copy those system components into the archive. Document them as installation requirements, not bundled dependencies. If a future package bundles FFmpeg or another system library, run a new audit against the exact binary and its enabled codecs.

## Release follow-up

1. Generating `THIRD-PARTY-NOTICES.md` from the exact resolved packages would reduce manual version maintenance. The assembled-native-file audit is already enforced.
2. Re-run the firmware audit before changing the decision not to distribute compiled firmware.
