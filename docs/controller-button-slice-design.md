# Controller button slice design

> Historical proof document. It records the first one-button implementation and is not current operating guidance. Use the root `README.md` for supported commands and `docs/architecture.md` for the current duplex control stream.

## Problem

The first desktop slice must prove the real path from an Avalonia button through a daemon to the Bluetooth controller bridge. It must preserve complete controller snapshots, daemon-owned hardware, and generation-checked control without adding video, daemon discovery, MCP, mappings, or the finished lease-expiry policy.

## Usage

Start the daemon with an explicit loopback address and the proven bridge address:

```sh
dotnet run --project src/ConsoleControl.Daemon -- \
  --listen http://127.0.0.1:5041 \
  --bridge F6:D5:24:56:6F:E2
```

Start the GUI against that daemon:

```sh
dotnet run --project src/ConsoleControl.Gui -- \
  --daemon http://127.0.0.1:5041
```

The GUI acquires one interactive control lease. Pressing its A button sends a complete A state, waits 80 ms, then sends a complete neutral state in a `finally` block.

## Shape

`ConsoleControl.Core` owns canonical controller and lease value types. `ConsoleControl.Contracts` owns protobuf only. `ConsoleControl.Client` hides gRPC, client identity, and lease generation behind `IConsoleSession` and `IControlSession`. `ConsoleControl.Daemon` validates transport input and owns the exclusive lease plus the single neutralization path. `ConsoleControl.Controller.Bluetooth` hides BlueZ D-Bus, the fixed proof-firmware UUIDs, and the eight-byte bridge encoding. `ConsoleControl.Gui` knows only the client and canonical state.

The RPC API accepts complete states. It does not expose a pulse command or input stream. Every state write and release carries the acquired generation, which the daemon checks immediately before a bridge write. Release, client disposal, and daemon shutdown use the same idempotent neutralization operation.

The initial lease is exclusive and generation-checked but has no renewal, expiry, or preemption. That is a temporary single-client limitation. Expiry and renewal must be added together in the control-arbitration phase.

## Synthesis decision

Two candidates agreed on complete unary state writes and the six project boundaries. The selected candidate uses explicit daemon and GUI startup. The other candidate's endpoint publication, daemon auto-start, bearer authentication, renewal loop, and expiry scheduler were rejected because none is needed to prove one button through the real bridge. Its insistence on generation validation immediately before output and one neutralization path was retained.

## Tradeoffs accepted

- We accept manual daemon startup in exchange for leaving endpoint discovery as one coherent later increment.
- We accept a Linux-only BlueZ adapter in exchange for proving the current hardware path; the daemon depends on an output interface rather than D-Bus.
- We accept two state calls per click in exchange for one controller model that extends to held buttons and sticks.
- We accept an 80 ms UI interaction in exchange for keeping timed automation out of the daemon API.

## Alternatives considered

A `PulseButton` RPC lost because it is a partial-state and timing API that cannot compose held controls. A bidirectional input stream lost because reconnect and stream lifetime add complexity before high-rate input exists. An unversioned state RPC lost because it would create a competing-writer path that later MCP control must replace.

## Open questions and risks

- Does BlueZ preserve the proof firmware's GATT object paths across reconnection on this host?
- Does an 80 ms click feel reliable across the complete GUI and gRPC path?
- Daemon discovery remains an installation concern. ADR 0006 settles the first release's unauthenticated loopback trust boundary.

## Next implementation step

Build the canonical state and bridge encoder, then prove A and neutral through a real daemon/client call before expanding the UI.
