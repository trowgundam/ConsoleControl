# Dependency notices

The desktop controller-button slice uses these direct runtime dependencies:

| Dependency | Version | Purpose | License |
| --- | --- | --- | --- |
| Avalonia | 12.1.1 | Cross-platform desktop GUI | MIT |
| gRPC for .NET | 2.83.0 | Loopback daemon transport | Apache-2.0 |
| Google.Protobuf | 3.31.1 | Generated transport messages | BSD-3-Clause |
| Tmds.DBus.Protocol | 0.95.0 | Linux BlueZ D-Bus calls | MIT |
| SDL3-CS | 3.4.14.1 | Managed SDL 3 gamepad bindings | zlib |
| SDL3-CS.Linux | 3.4.14.1 | SDL 3 native runtime for Linux | zlib |
| ModelContextProtocol | 2.2.0 | MCP stdio server | Apache-2.0 with retained MIT contributions |
| Microsoft.Extensions.Hosting | 10.0.0 | MCP process host | MIT |
| SkiaSharp | 3.119.4 | MCP screenshot scaling and encoding | MIT |
| SkiaSharp.NativeAssets.Linux | 3.119.4 | Linux SkiaSharp native runtime | MIT and bundled Skia BSD-3-Clause code |

Build-only protobuf generation uses `Grpc.Tools` 2.83.0 under Apache-2.0. See [the dependency license audit](research/dependency-license-audit.md) for the resolved transitive packages, native components, firmware dependencies, and notice obligations. The release archive includes [the third-party notice index](../THIRD-PARTY-NOTICES.md), the referenced license texts, and the notice files supplied by the exact .NET runtime packs.
