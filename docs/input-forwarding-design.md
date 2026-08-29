# Input forwarding design

## Streaming hardening checkpoint

The control transport uses one bidirectional gRPC call whose lifetime owns one daemon control lease. The first client message requests control, the daemon replies only after granting it, and every later client message is a complete canonical controller state. Any stream exit releases and neutralizes that exact lease generation. Cleanup uses a daemon-owned timeout instead of the already-cancelled RPC token.

`IControlSession.SetStateAsync` remains the transport-neutral client API. Its gRPC implementation retains every complete state that changes buttons or D-pad direction and keeps one latest slot for analog-only changes. Changed non-neutral states send at no more than 60 Hz. A neutral at the head of the ordered queue bypasses the cadence. While interactive control is connected, the client refreshes the latest complete state every 50 ms so the controller bridge never reaches its 250 ms fail-neutral timeout during a sustained hold. The separate automation stream sends a control heartbeat every 500 ms. A reconnect starts with neutral and does not replay state retained by the previous stream generation.

This design was selected over a separate acquire, bind, client-stream, and release protocol. The duplex call gives lease lifetime one owner and one cleanup path. It also returns connection conditions without polling. An unordered immediate-state queue was rejected because it could send neutral and then replay an older retained non-neutral state. The implemented mailbox avoids that failure by preserving digital order and allowing neutral to bypass cadence only after earlier transitions have been sent.

## Problem

ConsoleControl must forward one selected host input source, either the focused keyboard or one SDL gamepad, without weakening complete controller snapshots. Mappings are many-to-many. Releasing one host control must not release a canonical control still supplied by another binding. Source changes, focus loss, disconnects, and shutdown must clear stale input.

## Usage

The window works with one `InputForwarder`. It selects a source, reports keyboard focus and key state, requests on-screen holds, and saves a complete profile. SDL handles, protobuf messages, JSON, mapping indexes, and write serialization stay behind their owning boundaries.

```csharp
await forwarder.SelectSourceAsync(source, cancellationToken);
forwarder.SetKey(key, pressed);
await forwarder.SetKeyboardFocusAsync(focused, cancellationToken);
Guid overlayId = Guid.NewGuid();
await forwarder.SetOverlayAsync(overlayId, control, pressed: true, cancellationToken);
await forwarder.SetOverlayAsync(overlayId, control, pressed: false, cancellationToken);
await forwarder.SaveAndActivateProfileAsync(profile, cancellationToken);
```

## Shape

`ConsoleControl.Core` owns framework-neutral profile types, validation, and a pure full-snapshot mapper. Digital bindings map one host control to a set of canonical controls. Stick bindings pair X and Y with dead-zone, inversion, and scaling transforms. Trigger bindings preserve the normalized analog value and assert the Switch personality's digital ZL or ZR bit above a threshold. Validation rejects multiple analog sources for one canonical axis.

`ConsoleControl.Input.Sdl` owns SDL initialization, event pumping, discovery, live handles, and conversion into stable host-control symbols and normalized snapshots. Live instance IDs select devices. SDL GUIDs select profiles, so equivalent controllers share mappings.

`ConsoleControl.Gui` owns a private single-reader forwarding state machine. It combines the selected source with on-screen overlays, suppresses duplicate states, and is the only writer to `IControlSession`. `ConsoleControl.Core.ControllerStateMailbox` preserves ordered digital transitions and coalesces analog-only motion. The GUI writer and gRPC state pump both use it, so the guarantee continues through the transport boundary. A selection generation rejects callbacks from a previously selected device.

`ConsoleControl.Gui` owns profile and presentation preference persistence because host keyboard and SDL input exist only in that process. It stores mappings, the desired input source, and window state in one schema-versioned JSON document. Each narrow write reloads and merges under a cross-process lock before atomically replacing the file. The daemon and the protobuf contract contain no host-input concepts.

## Synthesis decision

Candidate A is the base because its `InputForwarder` hides more runtime choreography behind a smaller interface. Candidate B contributed the private single-reader state machine, paired-stick binding shape, explicit failure ordering, and reducer test cases. SDL and Avalonia enums in persisted or Core types were rejected. Daemon-side live mapping was rejected because it would send raw desktop input concepts over gRPC.

## Tradeoffs accepted

- We accept one profile per keyboard or SDL GUID in this milestone in exchange for a smaller editor and predictable identical-controller behavior.
- We accept focused-window keyboard input in exchange for avoiding platform-specific global hooks.
- We accept digital Switch trigger output in exchange for compatibility with the current controller personality; host analog values remain available in canonical state.
- We accept Linux native SDL packaging now. Windows adds its matching native package when Windows becomes an active target.

## Next implementation step

Build and test the profile types and snapshot mapper before adding SDL or Avalonia event handling.
