# Third-party notices

The ConsoleControl desktop archive distributes software from the projects listed below. The corresponding license texts are under [`third-party-licenses/`](third-party-licenses/). A self-contained desktop archive also includes the unmodified third-party notice files supplied with its exact .NET and ASP.NET Core runtime packs.

Versions in this file match the `linux-x64` publish audited on 2026-08-28. Run a new audit when a dependency version or release target changes.

## Desktop archive

| Component | Version | License | License file |
| --- | --- | --- | --- |
| .NET and ASP.NET Core runtimes | 10.0.11 | MIT, with upstream third-party notices | `DotNetRuntime-LICENSE.txt` and generated runtime notice files |
| Avalonia packages | 12.1.1 | MIT | `Avalonia-LICENSE.txt` |
| Google.Protobuf | 3.31.1 | BSD-3-Clause | `ProtocolBuffers-LICENSE.txt` |
| gRPC for .NET packages | 2.83.0 | Apache-2.0 | `GrpcDotNet-LICENSE.txt` |
| Tmds.DBus.Protocol | 0.94.1 and 0.95.0 | MIT | `Tmds.DBus-LICENSE.txt` |
| MicroCom.Runtime | 0.11.6 | MIT | `MicroCom-LICENSE.txt` |
| SDL3-CS and SDL3-CS.Linux | 3.4.14.1 | zlib | `SDL3-CS-LICENSE.txt` and `SDL-LICENSE.txt` |
| SkiaSharp and SkiaSharp.NativeAssets.Linux | 3.119.4 | MIT; bundled Skia uses BSD-3-Clause | `SkiaSharp-LICENSE.txt` and `Skia-LICENSE.txt` |
| HarfBuzzSharp and HarfBuzzSharp.NativeAssets.Linux | 8.3.1.3 | MIT; bundled HarfBuzz uses its MIT-style license | `SkiaSharp-LICENSE.txt` and `HarfBuzz-LICENSE.txt` |
| ModelContextProtocol and ModelContextProtocol.Core | 2.2.0 | Apache-2.0 and retained MIT contributions | `McpCSharpSdk-LICENSE.txt` |
| Microsoft.Extensions packages and System.Diagnostics.EventLog | 8.0.0, 10.0.0, 10.0.10, and 10.8.3 | MIT | `DotNetRuntime-LICENSE.txt` and `DotNetExtensions-LICENSE.txt` |

The gRPC family includes `Grpc.AspNetCore`, `Grpc.AspNetCore.Server`, `Grpc.AspNetCore.Server.ClientFactory`, `Grpc.Core.Api`, `Grpc.Net.Client`, `Grpc.Net.ClientFactory`, and `Grpc.Net.Common`.

The Avalonia family includes `Avalonia`, `Avalonia.Desktop`, `Avalonia.FreeDesktop`, `Avalonia.FreeDesktop.AtSpi`, `Avalonia.HarfBuzz`, `Avalonia.Native`, `Avalonia.Remote.Protocol`, `Avalonia.Skia`, `Avalonia.Themes.Fluent`, `Avalonia.Win32`, and `Avalonia.X11`.

The complete Microsoft.Extensions inventory is recorded in [the dependency license audit](docs/research/dependency-license-audit.md).

## Research references

NanoKVM-USB and OpenPuck are not distributed dependencies. ConsoleControl includes no code, binaries, assets, generated files, packages, or submodules from either project. They are therefore absent from these notices.
