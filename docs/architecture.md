# ConsoleControl architecture

ConsoleControl concentrates console lifecycle in one daemon session. Capture, input arbitration, controller encoding, and hardware failures meet there. The public client API hides gRPC, FFmpeg, BLE, USB descriptors, and firmware update packets.

## Dominant flows

### Live video

```text
NanoKVM UVC -> FFmpeg capture -> latest decoded frame -> capacity-one subscriber -> GUI renderer
                                      |
                                      +-> retained latest frame -> screenshot request
```

Capture never waits for a client. Each subscriber owns one pending-frame slot. Publishing a new frame replaces a stale pending frame. The retained latest frame uses reference-counted immutable storage so screenshot encoding cannot block capture.

### Interactive input

```text
keyboard or SDL gamepad
        -> GUI mapping
        -> canonical controller snapshot plus lease generation
        -> gRPC boundary
        -> session runtime
        -> active personality encoder
        -> BLE bridge
        -> console-facing USB report
```

The GUI sends complete snapshots instead of button edges. The daemon accepts a snapshot only from the current lease generation. Every lease-loss transition passes through one neutralization operation before another client can write.

### Controller personality activation

```text
personality package
        -> daemon validation
        -> chunked BLE upload
        -> firmware validation into bounded RAM
        -> USB detach
        -> personality activation with neutral state
        -> USB attach and enumeration
```

Personality uploads are idempotent within one bridge boot. A digest identifies the package, chunks have explicit offsets, and repeated commits produce the same active personality. Power loss clears the package. The daemon uploads it again after reconnecting.

## Core types

The domain model starts with complete input state and explicit lease generations.

```csharp
public readonly record struct ControllerState(
    GameButtons Buttons,
    HatPosition DPad,
    StickPosition LeftStick,
    StickPosition RightStick,
    TriggerPosition LeftTrigger,
    TriggerPosition RightTrigger)
{
    public static ControllerState Neutral { get; }
}

public readonly record struct ClientId(Guid Value);
public readonly record struct LeaseGeneration(ulong Value);

public sealed record ControlLease(
    ClientId Owner,
    LeaseGeneration Generation,
    ControlPriority Priority,
    DateTimeOffset ExpiresAt);

public enum ControlPriority
{
    Automation,
    InteractiveUser
}
```

An interactive client may preempt an automation client. Automation cannot preempt an interactive client. A second interactive client receives a conflict until the product defines an explicit takeover experience.

The first client interface contains only operations required by the GUI release.

```csharp
public interface IConsoleSession : IAsyncDisposable
{
    Task<DeviceInventory> GetDevicesAsync(CancellationToken cancellationToken);

    Task ConfigureAsync(
        ConsoleConfiguration configuration,
        CancellationToken cancellationToken);

    IAsyncEnumerable<VideoFrame> WatchVideoAsync(
        CancellationToken cancellationToken);

    Task<IControlSession> TakeControlAsync(
        ControlPriority priority,
        CancellationToken cancellationToken);
}

public interface IControlSession : IAsyncDisposable
{
    Task SetStateAsync(
        ControllerState state,
        CancellationToken cancellationToken);
}
```

Screenshot capture and bounded digital-input sequences use separate client operations. The daemon compiles each sequence into an absolute timeline and remains the only controller writer. Generated protobuf types do not cross the client interface.

## Daemon ownership

`ConsoleRuntime` owns control arbitration and controller writes. `ControllerBridgeRuntime` owns compatible-bridge inventory, persisted explicit selection, revision checks, and the active BlueZ adapter binding. `VideoRuntime` owns video inventory, selection, capture, and latest-frame publication.

At startup, the daemon takes an exclusive per-user file lock before it initializes either hardware runtime. A second daemon exits with an error even when configured with different loopback ports. Releasing the process lock does not delete its file, which avoids a replacement race between competing starters.

Together these daemon runtimes currently own:

- capture and controller-bridge lifetimes;
- persisted hardware selection and controller mappings;
- controller-personality activation;
- lease acquisition, renewal, revocation, and expiry;
- neutralization after every loss of control;
- latest-frame publication;
- shutdown ordering.

The gRPC service parses and validates transport messages, then calls the runtime. It contains no session rules. Hardware adapters implement narrow ports and contain no lease or product policy.

```csharp
internal interface IVideoCapture : IAsyncDisposable
{
    IAsyncEnumerable<DecodedFrame> CaptureAsync(
        VideoSourceId source,
        CancellationToken cancellationToken);
}

internal interface IControllerOutput : IAsyncDisposable
{
    Task<BridgeCapabilities> ConnectAsync(
        ControllerBridgeId bridge,
        CancellationToken cancellationToken);

    Task ApplyPersonalityAsync(
        ControllerPersonality personality,
        CancellationToken cancellationToken);

    ValueTask WriteStateAsync(
        ControllerState state,
        CancellationToken cancellationToken);
}
```

The BlueZ bridge adapter performs a bounded active scan on the configured adapter and includes only devices advertising the firmware service UUID. A selected bridge is persisted even while unavailable; the daemon never silently substitutes another device. The adapter resolves the state characteristic by UUID after connection, because BlueZ object paths are not stable. Selection is rejected while any control lease is active.

The controller output adapter hides BLE discovery, connection, GATT lookup, state writes, and report encoding. The video adapter hides UVC enumeration, FFmpeg invocation, decoding, and frame-buffer ownership.

## Local process boundary

The daemon binds gRPC and MJPEG only to explicit loopback TCP addresses. Clients receive the daemon URI as a command-line option. The daemon does not yet publish an endpoint file, authenticate clients, or manage its own process lifecycle. This explicit transport boundary can later gain a local Windows transport without changing domain interfaces.

## Controller personality format

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

## Bridge protocol

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

Controller-state messages carry monotonically increasing sequence numbers. Firmware ignores old sequence numbers. The USB task reads the latest accepted state without waiting for BLE and emits neutral state after a bounded communication timeout.

## Firmware structure

The firmware uses C++ with Adafruit's nRF52 Arduino core, TinyUSB, and Bluefruit if the hardware proof succeeds. One coordinator owns USB personality, controller state, and update-mode transitions. BLE and USB processing submit events to that coordinator instead of mutating shared state.

The firmware keeps validated personality bytes in bounded RAM. TinyUSB handlers consume only validated descriptor and report-layout structures. Bluefruit callbacks enqueue bridge events; they do not mutate USB state directly.

Board-specific linker layouts live under `firmware/ConsoleControl.ControllerBridge/boards/`. No generic memory map may be used for a physical flash until `ConsoleControl.DeviceProbe` records that board's bootloader and boundaries.

## Update invariants

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

The daemon stores capture selection, controller-bridge selection, the active personality, and input mappings as versioned JSON in the platform's per-user configuration directory. The GUI stores window, layout, and display preferences separately. Neither component uses a database.

## Project boundaries

```text
src/
  ConsoleControl.Core/                 domain state and pure policy
  ConsoleControl.Contracts/            protobuf transport contracts only
  ConsoleControl.Client/               client API, discovery, launch, gRPC adapter
  ConsoleControl.Daemon/               process host and ConsoleRuntime
  ConsoleControl.Video.FFmpeg/          capture and frame ownership
  ConsoleControl.Controller.Bluetooth/ BLE bridge and personality transport
  ConsoleControl.Input.Sdl/             SDL input and canonical mappings
  ConsoleControl.Gui/                   Avalonia UI and presentation settings
tests/
  ConsoleControl.Core.Tests/
  ConsoleControl.Protocol.Tests/
  ConsoleControl.IntegrationTests/
firmware/ConsoleControl.ControllerBridge/
  src/
  bootloader/
  boards/
  tests/
controller-personalities/switch-hori-usb/
tools/
  ConsoleControl.DeviceProbe/
  ConsoleControl.FirmwarePack/
```

`ConsoleControl.IntegrationTests` runs a real daemon and client against fake capture and bridge adapters. It proves lease expiry, neutralization, stale-generation rejection, endpoint authentication, and frame backpressure without hardware. Hardware acceptance tests cover the boundaries that fakes cannot prove.

## Implementation gates

1. Record the purchased board's UF2 metadata and flash layout. Prove recovery after an invalid application. Prove concurrent BLE and static Hori USB operation. Prove neutralization after BLE loss.
2. Upload a Hori personality into RAM, activate it, and control Switch 2. Prove that personality and state traffic cause no flash writes.
3. Implement the daemon and client through fake adapters. Prove control arbitration and bounded video queues end to end.
4. Add the FFmpeg adapter and measure capture-to-display latency before choosing child-process or native bindings.
5. Add the Avalonia GUI and SDL input. Prove the real NanoKVM-to-Switch path.
6. Implement and fault-test signed OTA after the boot layout is known.
7. Add MCP screenshots, hardware inventory and selection, control arbitration, and bounded sequences after the GUI release is stable.
