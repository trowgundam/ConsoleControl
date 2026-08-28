# Dependency notices

The desktop controller-button slice uses these direct runtime dependencies:

| Dependency | Version | Purpose | License |
| --- | --- | --- | --- |
| Avalonia | 12.1.1 | Cross-platform desktop GUI | MIT |
| gRPC for .NET | 2.83.0 | Loopback daemon transport | Apache-2.0 |
| Google.Protobuf | 3.31.1 | Generated transport messages | BSD-3-Clause |
| Tmds.DBus.Protocol | 0.95.0 | Linux BlueZ D-Bus calls | MIT |

Build-only protobuf generation uses `Grpc.Tools` 2.83.0 under Apache-2.0. Transitive dependency notices remain part of packaging work.
