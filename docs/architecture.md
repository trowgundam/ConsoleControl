# ConsoleControl architecture

ConsoleControl concentrates console lifecycle in one daemon session. Capture, input arbitration, controller encoding, and hardware failures meet there. The public client API hides gRPC, FFmpeg, BLE, USB descriptors, and firmware update packets.

## Dominant flows

### Live video

```text
NanoKVM UVC MJPEG -> FFmpeg pass-through -> retained encoded JPEG -> MJPEG subscriber -> GUI decoder
                                                   |
                                                   +-> copied JPEG -> screenshot request
```

Capture never waits for a client. Each subscriber observes only the newest complete encoded JPEG. Publishing replaces the retained frame, and screenshot callers receive a copied byte array so they cannot mutate capture-owned data.

### Interactive input

```text
keyboard or SDL gamepad
        -> GUI mapping
        -> canonical controller snapshot plus lease generation
        -> gRPC boundary
        -> session runtime
        -> fixed Switch proof encoder
        -> BLE bridge
        -> console-facing USB report
```

The GUI sends complete snapshots instead of button edges. The daemon accepts a snapshot only from the current lease generation. Every lease-loss transition passes through one neutralization operation before another client can write.

### Future controller personality activation

```text
personality package
        -> daemon validation
        -> chunked BLE upload
        -> firmware validation into bounded RAM
        -> USB detach
        -> personality activation with neutral state
        -> USB attach and enumeration
```

This flow is a design target, not current behavior. The proof firmware exposes one fixed Switch-compatible USB personality. A future personality upload must be idempotent within one bridge boot: a digest identifies the package, chunks have explicit offsets, and repeated commits produce the same active personality. Power loss clears the package, after which the daemon uploads it again.

## Core and client contracts

`ConsoleControl.Core` defines complete `ControllerState` snapshots, control priority and ownership, hardware inventory and selection types, input profiles, video status, encoded frames, screenshots, and bounded automation sequences. `ClientId`, `LeaseGeneration`, and `ControlLease` remain daemon-side arbitration details.

`IConsoleSession` exposes status, explicit controller-bridge and video selection, screenshots, interactive control, automation control requests, and request rejection. `IControlSession` sends complete controller states over one duplex stream. `IAutomationSession` reports asynchronous lease completion and runs daemon-owned bounded sequences. The current signatures live in `src/ConsoleControl.Client/IConsoleSession.cs`; generated protobuf types never cross that boundary.

An interactive client may preempt an automation client. Automation cannot preempt an interactive client. A second interactive client receives a conflict until the product defines an explicit takeover experience. The daemon compiles automation sequences into an absolute timeline and remains the only controller writer.

## Daemon ownership

`ConsoleRuntime` owns control arbitration and controller writes. `ControllerBridgeRuntime` owns compatible-bridge inventory, persisted explicit selection, revision checks, and the active BlueZ adapter binding. `VideoRuntime` owns video inventory, selection, capture, and latest-frame publication.

At startup, the daemon takes an exclusive per-user file lock before it initializes either hardware runtime. A second daemon exits with an error even when configured with different loopback ports. Releasing the process lock does not delete its file, which avoids a replacement race between competing starters.

Together these daemon runtimes currently own:

- capture and controller-bridge lifetimes;
- persisted hardware selection;
- lease acquisition, renewal, revocation, and expiry;
- neutralization after every loss of control;
- latest-frame publication;
- shutdown ordering.

The gRPC service parses and validates transport messages, then calls the runtime. It contains no session rules. Hardware adapters implement narrow ports and contain no lease or product policy.

```csharp
internal interface IVideoCaptureAdapter
{
    Task<ImmutableArray<VideoSource>> GetSourcesAsync(
        CancellationToken cancellationToken);

    Task<IVideoCaptureSession> OpenAsync(
        VideoSource source,
        CancellationToken cancellationToken);
}

internal interface IControllerOutput : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    ValueTask WriteStateAsync(
        ControllerState state,
        CancellationToken cancellationToken);
}
```

The BlueZ bridge adapter performs a bounded active scan on the configured adapter and includes only devices advertising the firmware service UUID. A selected bridge is persisted even while unavailable; the daemon never silently substitutes another device. The adapter resolves the state characteristic by UUID after connection, because BlueZ object paths are not stable. Selection is rejected while any control lease is active.

The controller output adapter hides BLE discovery, connection, GATT lookup, state writes, and report encoding. The video adapter hides UVC enumeration, FFmpeg invocation, MJPEG pass-through, and encoded-frame ownership.

## Local process boundary

The daemon binds gRPC and MJPEG only to numeric loopback TCP addresses. Clients receive the daemon URI as a command-line option. The current single-user release deliberately does not authenticate local clients: any process on the host can observe video or request control. Remote access and untrusted multi-user hosts are outside this trust boundary and require a new ADR. The transport seam can later gain a local Windows transport without changing domain interfaces.

## Future firmware architecture

The remaining controller-personality, bridge-protocol, firmware-layout, and OTA sections define later checkpoints. They are not implemented by the current fixed-personality proof firmware or desktop release.

### Controller personality format

A personality package is versioned data with strict size and capability limits. Validation constructs trusted descriptor and layout types from package bytes. Internal code does not pass unrelated byte arrays as a personality.

```csharp
public sealed record ControllerPersonality(
    PersonalityId Id,
    PersonalityFormatVersion FormatVersion,
    UsbDeviceDescriptor Device,
    UsbConfigurationDescriptor Configuration,
    HidReportDescriptor ReportDescriptor,
    InputReportLayout Input,
    OutputBehavior Output,
    UsbTiming Timing);

public abstract record OutputBehavior
{
    public sealed record Ignore : OutputBehavior;
    public sealed record FixedReplies(
        ImmutableArray<OutputReplyRule> Rules) : OutputBehavior;
    public sealed record CompiledHandler(HandlerId Id) : OutputBehavior;
}
```

The firmware rejects unsupported descriptor classes, excessive lengths, invalid endpoints, excessive packet sizes, unsafe polling intervals, inconsistent report layouts, and unknown compiled handlers. The first Hori personality uses `Ignore` unless hardware testing proves that a fixed reply is necessary.

### Bridge protocol

The BLE protocol is versioned independently from the personality format. Its operations are:

```text
GetCapabilities
BeginPersonality
WritePersonalityChunk
CommitPersonality
ActivatePersonality
SetControllerState
Neutralize
BeginFirmwareUpdate
WriteFirmwareChunk
FinishFirmwareUpdate
ActivateFirmware
```

Personality transfers use RAM and can resume only during the same boot. Firmware transfers use inactive flash and may resume across reconnects when update metadata proves that existing chunks belong to the same signed package. Repeated chunks must match existing bytes. Repeated commit operations are safe.

The target bridge protocol gives controller-state messages monotonically increasing sequence numbers and makes firmware ignore old sequence numbers. The current eight-byte proof protocol does neither; adding both is deferred until the next firmware checkpoint. The current USB task still reads the latest received state without waiting for BLE and emits neutral state after a bounded communication timeout.

### Firmware structure

The firmware uses C++ with Adafruit's nRF52 Arduino core, TinyUSB, and Bluefruit if the hardware proof succeeds. One coordinator owns USB personality, controller state, and update-mode transitions. BLE and USB processing submit events to that coordinator instead of mutating shared state.

The firmware keeps validated personality bytes in bounded RAM. TinyUSB handlers consume only validated descriptor and report-layout structures. Bluefruit callbacks enqueue bridge events; they do not mutate USB state directly.

Board-specific linker layouts live under `firmware/ConsoleControl.ControllerBridge/boards/`. No generic memory map may be used for a physical flash until `ConsoleControl.DeviceProbe` records that board's bootloader and boundaries.

### Update invariants

The exact update mechanism remains conditional on the hardware proof, but every acceptable implementation preserves these rules:

- The bootloader verifies a signed manifest before activation. Desktop verification is an early error check, not the trust boundary.
- The manifest binds the board, image length, firmware version, digest, and rollback policy.
- Application firmware cannot address the recovery bootloader, public key, radio stack, active application, or protected configuration regions through its flash writer.
- Upload writes only inactive image storage and update metadata.
- Power loss before commit leaves the confirmed image selected.
- A new image starts as pending and confirms itself only after BLE, USB, and watchdog health checks pass.
- An unconfirmed image rolls back after a bounded number of boots.
- Recovery claims require a destructive recovery test on the exact board.
- Normal updates use BLE. USB maintenance mode may use the same package after moving the board to the laptop.

The proof must determine whether the factory boot chain can support these rules. Replacing the factory bootloader requires a new design decision because it changes the bricking risk and recovery procedure.

## Configuration ownership

The daemon stores capture selection and controller-bridge selection as schema-versioned JSON in the platform's per-user configuration directory. It rejects unknown selection schemas. The current fixed-personality release stores no active personality.

The GUI stores input mappings, the last selected input source, and window size and maximized state in one schema-versioned JSON document. An SDL profile key contains the device GUID, so identical controllers share a mapping. If the saved controller is absent, the GUI uses Keyboard without replacing the saved preference. The GUI restores the controller when that GUID becomes available again. Writes take a cross-process lock, reload the current document, merge one preference, and replace the file atomically. On first use, the GUI imports the former `input-profiles.json` and `gui-preferences.json` files without deleting them.

## Project boundaries

```text
src/
  ConsoleControl.Core/                 domain state and pure policy
  ConsoleControl.Contracts/            protobuf transport contracts only
  ConsoleControl.Client/               client API, discovery, launch, gRPC adapter
  ConsoleControl.Daemon/               process host and ConsoleRuntime
  ConsoleControl.Video.FFmpeg/          capture and frame ownership
  ConsoleControl.Controller.Bluetooth/ BLE bridge discovery and state transport
  ConsoleControl.Input.Sdl/             SDL input and canonical mappings
  ConsoleControl.Gui/                   Avalonia UI and presentation settings
tests/
  ConsoleControl.IntegrationTests/
firmware/ConsoleControl.ControllerBridge/
  boards/
  ConsoleControl.ControllerBridge.ino
controller-personalities/
tools/
  ConsoleControl.DeviceProbe/
```

`ConsoleControl.IntegrationTests` runs a real daemon and client against fake capture and bridge adapters. It proves lease expiry, neutralization, stale-generation rejection, loopback-only listener validation, and frame backpressure without hardware. Hardware acceptance tests cover the boundaries that fakes cannot prove.

## Checkpoint history and future gates

Completed checkpoints recorded elsewhere in the repository cover the board metadata and recovery proof, fixed Switch USB personality, daemon and client, FFmpeg capture, Avalonia and SDL input, and digital MCP automation.

Future firmware gates are:

1. Upload a controller personality into RAM, activate it, and prove that personality and state traffic cause no flash writes.
2. Implement and fault-test signed OTA after the boot layout is known.
3. Version the controller-state packet, add a monotonic sequence number, reject stale packets in firmware, and prove the behavior on hardware.
