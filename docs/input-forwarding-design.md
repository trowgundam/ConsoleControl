# Input forwarding design

## Streaming hardening checkpoint

The control transport uses one bidirectional gRPC call whose lifetime owns one daemon control lease. The first client message requests control, the daemon replies only after granting it, and every later client message is a complete canonical controller state. Any stream exit releases and neutralizes that exact lease generation. Cleanup uses a daemon-owned timeout instead of the already-cancelled RPC token.

`IControlSession.SetStateAsync` remains the transport-neutral client API. Its gRPC implementation retains one revisioned latest state, sends ordinary changes at no more than 60 Hz, sends a heartbeat every 500 ms, and lets neutral revisions bypass the cadence. A reconnect always starts with neutral and does not replay state retained by the previous stream generation.

This design was selected over a separate acquire, bind, client-stream, and release protocol. The duplex call gives lease lifetime one owner and one cleanup path. It also returns connection conditions without polling. A separate immediate-state queue was rejected because it could send neutral and then replay an older retained non-neutral state on the next timer tick.

## Problem

ConsoleControl must forward one selected host input source, either the focused keyboard or one SDL gamepad, without weakening complete controller snapshots or daemon-owned configuration. Mappings are many-to-many. Releasing one host control must not release a canonical control still supplied by another binding. Source changes, focus loss, disconnects, and shutdown must clear stale input.

## Usage

The window works with one `InputForwarder`. It selects a source, reports keyboard focus and key state, requests on-screen holds, and saves a complete profile. SDL handles, protobuf messages, JSON, mapping indexes, and write serialization stay behind their owning boundaries.

```csharp
await forwarder.SelectSourceAsync(source.Id, cancellationToken);
forwarder.Keyboard.SetKey(key, pressed);
await forwarder.SetKeyboardFocusAsync(focused, cancellationToken);
await using InputHold hold = await forwarder.HoldOnScreenAsync(control, cancellationToken);
await forwarder.SaveAndActivateProfileAsync(profile, cancellationToken);
```

## Shape

`ConsoleControl.Core` owns framework-neutral profile types, validation, and a pure full-snapshot mapper. Digital bindings map one host control to a set of canonical controls. Stick bindings pair X and Y with dead-zone, inversion, and scaling transforms. Trigger bindings preserve the normalized analog value and assert the Switch personality's digital ZL or ZR bit above a threshold. Validation rejects multiple analog sources for one canonical axis.

`ConsoleControl.Input.Sdl` owns SDL initialization, event pumping, discovery, live handles, and conversion into stable host-control symbols and normalized snapshots. Live instance IDs select devices. SDL GUIDs select profiles, so equivalent controllers share mappings.

`ConsoleControl.Gui` owns a private single-reader forwarding state machine. It combines the selected source with on-screen overlays, suppresses duplicate states, and is the only writer to `IControlSession`. A one-slot latest-snapshot mailbox may coalesce axis motion, but lifecycle changes and digital transitions are never dropped. A selection generation rejects callbacks from a previously selected device.

`ConsoleControl.Daemon` owns revisioned profile persistence. It atomically replaces one versioned JSON document. `ConsoleControl.Client` exposes domain-facing load and save operations; protobuf and storage DTOs do not cross their adapters.

## Synthesis decision

Candidate A is the base because its `InputForwarder` hides more runtime choreography behind a smaller interface. Candidate B contributed the private single-reader state machine, paired-stick binding shape, explicit failure ordering, and reducer test cases. SDL and Avalonia enums in persisted or Core types were rejected. Daemon-side live mapping was rejected because it would send raw desktop input concepts over gRPC.

## Tradeoffs accepted

- We accept one profile per keyboard or SDL GUID in this milestone in exchange for a smaller editor and predictable identical-controller behavior.
- We accept focused-window keyboard input in exchange for avoiding platform-specific global hooks.
- We accept digital Switch trigger output in exchange for compatibility with the current controller personality; host analog values remain available in canonical state.
- We accept Linux native SDL packaging now. Windows adds its matching native package when Windows becomes an active target.

## Next implementation step

Build and test the profile types and snapshot mapper before adding SDL or Avalonia event handling.
