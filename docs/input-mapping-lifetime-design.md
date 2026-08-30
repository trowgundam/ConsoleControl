# Lease-independent input mapping

## Problem

The GUI used to create `InputForwarder` only after acquiring an `IControlSession`. That tied SDL discovery, input selection, mapping capture, and profile storage to a daemon lease even though those are local GUI responsibilities. Releasing control also erased the visible input source and made mapping unavailable.

The lease must still remain the only authority that permits controller-state writes. Delayed input work created under one lease must never write through a later lease.

## Usage

The main window creates one input coordinator for its whole lifetime. It discovers input sources and supports mapping before control is acquired.

```csharp
_input = new InputForwarder(new SdlGamepadManager(), configuration);
await _input.SelectSourceAsync(source, cancellationToken);

await _input.AttachControlAsync(control, cancellationToken);
await _input.DetachControlAsync(cancellationToken);
```

Mapping uses the active local profile in both observer and control modes.

```csharp
MappingWindowViewModel editor = new(_input);
CapturedHostControl captured = await _input.CaptureNextInputAsync(cancellationToken);
await _input.SaveAndActivateProfileAsync(profile, cancellationToken);
```

## Shape

`InputForwarder` now has a GUI lifetime. It owns one `SdlGamepadManager`, source selection, host snapshots, mapping capture, and profiles. Its optional `ControlAttachment` has a lease lifetime and owns the `IControlSession`, controller-state mailbox, primary state, overlays, and last-sent state.

Every queued drain and delayed pulse captures the attachment that authorized it. The write path checks object identity before calling `IControlSession.SetStateAsync`. Detach removes the attachment before awaiting, runs the shared neutralization operation with a forced final write, unsubscribes its connection callback, and disposes it. A later lease always gets a new attachment and fresh output state.

Attaching clears input collected while observing and sends a complete neutral snapshot. Source changes, focus loss, gamepad disconnect, capture start, detach, and shutdown use the same neutralization behavior. While detached, input events may update capture state but cannot enter a controller-state mailbox.

The mapping dialog exposes one shared capture state across digital buttons and sticks. It disables other targets while waiting and gives the user a visible cancel action.

## Synthesis decision

Two designs were compared. The selected design keeps one coordinator and gives each lease a private `ControlAttachment`. A split `InputWorkspace` and `LeaseOutputWriter` made write authority obvious by type, but it introduced a shared event boundary and reverse reset calls. A late callback from an old writer could clear shared input state and cause a neutral update on a newer lease unless both modules gained attachment generations. That extra machinery converged on the selected design with a longer call chain.

The selected design also keeps the current SDL selection, capture, mailbox, and composition code in one ordered command loop. No daemon, protocol, public API, or dependency changes are required.

## Tradeoffs accepted

- The SDL manager lives for the window lifetime so device discovery and mappings remain stable across lease changes.
- `InputForwarder` owns the attached control session so callers cannot get detach and disposal order wrong.
- Observer-mode keyboard events are accepted only during an active mapping capture. Normal keys are not swallowed and never become controller input later.
- Input held before lease acquisition is cleared instead of replayed when control begins.

## Alternatives considered

- A separate workspace and output writer split ownership cleanly, but required state-event ordering, reverse reset coordination, and attachment generations across two modules.
- A null control-session adapter preserved the constructor but represented missing authority with an object that still looked writable.
- A second mapping-only SDL manager duplicated device ownership and could disagree with the live input selection.

## Open risks

- SDL initialization failure still makes local input unavailable. A keyboard-only fallback would be a separate product decision.
