# ConsoleControl

ConsoleControl lets a local desktop application control a game console through a small controller bridge. The first target is a docked Nintendo Switch 2 connected through a NanoKVM-USB.

The controller hardware proof has passed. The current desktop slice has daemon-backed Avalonia controls plus focused keyboard and SDL gamepad forwarding. Video capture remains later work.

## First release

The first usable release contains two applications:

- `ConsoleControl.Daemon` owns video capture, the controller bridge, configuration, and control arbitration.
- `ConsoleControl.Gui` starts or connects to the daemon, displays live video, and forwards keyboard or physical-controller input.

The first release supports one local console session, one NanoKVM-USB capture source, and one Hori-compatible Switch controller personality. It does not include remote access, audio, recording, motion, rumble, macros, or an MCP server.

An MCP server is a later application. It will use the same daemon API to request screenshots and submit bounded input sequences. It will not consume the live video stream.

## Hardware

The planned setup is:

```text
                         HDMI
Nintendo Switch 2 -----------------> NanoKVM-USB
        ^                                  |
        | USB controller                   | USB 3 capture
        |                                  v
nRF52840 bridge <---- Bluetooth LE ---- ConsoleControl.Daemon
                                               ^
                                               | gRPC on loopback TCP
                                               v
                                       ConsoleControl.Gui
```

The controller bridge target is the Teyleten Robot Pro Micro nRF52840 board sold under Amazon ASIN `B0CYLNZ6V4`.

The NanoKVM-USB control port cannot emulate a Switch controller with its stock firmware. Its client sends fixed CH9329 keyboard and mouse commands. The CH9329 custom HID mode has a fixed vendor-defined interface and cannot supply the descriptors or protocol behavior expected by a Switch. ConsoleControl therefore treats the NanoKVM-USB as a UVC capture device and uses the nRF52840 for controller output.

## How control works

The GUI converts keyboard, on-screen, and SDL gamepad input into complete canonical controller snapshots. Complete snapshots avoid stuck buttons caused by a lost button-release event.

The daemon grants one control lease at a time. Observers can always watch video. A future agent may hold an automation lease, but an interactive GUI user can revoke it and take control. Revocation, disconnect, timeout, bridge loss, personality change, and daemon shutdown all send a neutral controller state. Delayed writes from an old lease are rejected by lease generation.

The daemon translates canonical state through the active controller personality. The first personality is a wired Hori-compatible controller with buttons, D-pad, and two sticks. Motion and rumble require a different personality and are outside the first release.

## Controller bridge

The bridge firmware uses C++ with Adafruit's nRF52 Arduino core, TinyUSB, and Bluefruit. This stack uses the board's installed S140 radio firmware and follows the same hardware path that OpenPuck has exercised on these nRF52840 clones. A hardware proof must validate it on the purchased board before the desktop projects depend on it.

The Hori proof established concurrent USB and Bluetooth operation, end-to-end button input, and automatic neutral release, but Switch 2 rejected that older controller identity. The wired Switch Pro replacement now passes on Switch 2. The console completes initialization and accepts button snapshots forwarded over Bluetooth. Build and inspect the firmware with `tools/hardware-proof.sh`. See [the hardware proof procedure](docs/hardware-proof.md) before flashing it.

During startup, the daemon uploads a versioned controller personality over BLE. The package contains the USB identity, descriptors, input-report layout, polling limits, and output behavior. The bridge validates the package in bounded RAM, disconnects its USB device, installs the personality, and reconnects. Personalities and controller state are not written to flash.

Descriptors can define straightforward HID controllers, including the initial Hori target. Descriptors cannot implement timing-sensitive handshakes. A future protocol such as a full Switch Pro Controller may select a handler already compiled into the firmware. The bridge never accepts uploaded executable code.

The bridge repeats the latest accepted report at the USB polling rate. USB polling never waits for BLE. A BLE timeout or disconnect immediately replaces the current report with neutral state.

## Firmware updates and recovery

Normal firmware updates travel over BLE while the board remains connected to the Switch. The updater stages a signed image outside the running application, verifies it, and marks it pending. The boot process rolls back an image that fails its health check.

The same signed package may later travel through a USB maintenance interface after moving the board's USB cable to the computer. Application code can request maintenance mode, so routine USB updates do not require a double reset. The board has only one USB connection, so it cannot remain connected to the Switch during a laptop-driven USB update.

The factory UF2 bootloader remains the recovery path. Before firmware development, the hardware proof must read `INFO_UF2.TXT`, record the exact bootloader and memory map, back up recoverable metadata, and prove double-reset recovery. Firmware tooling must reject writes to the bootloader and other protected regions. The project will not claim recovery guarantees until that proof passes on the purchased board.

The nRF52840 rates a flash page for 10,000 erase cycles. Ordinary control and personality changes cause no flash writes. Firmware updates, boot metadata, and BLE bonding are the expected flash writers.

## Desktop architecture

The daemon presents a narrow client API over gRPC on loopback TCP. Generated protobuf messages remain transport details. GUI code uses canonical domain types through `ConsoleControl.Client`.

The GUI starts the daemon when no compatible instance is available. The daemon publishes its URI, process ID, protocol version, instance nonce, and per-launch bearer secret in a user-only runtime file. The endpoint component owns publication and channel creation. It does not prebuild Unix socket or Windows named-pipe implementations.

FFmpeg handles UVC format negotiation and decoding behind a capture interface. Development initially requires a system FFmpeg installation. The choice between an FFmpeg child process and native bindings remains open until the video latency proof measures the simpler route.

Every live-video subscriber has room for one pending frame. Slow clients lose stale frames instead of adding latency. The daemon retains the latest full-resolution decoded frame separately so a later MCP server can request a screenshot without joining the live stream.

See [the architecture](docs/architecture.md), [the domain language](CONTEXT.md), and [the architecture decisions](docs/adr/) for the detailed boundaries and rationale.

## Run the controller-button slice

Connect the flashed controller bridge to the Switch dock. Start the daemon:

```sh
dotnet run --project src/ConsoleControl.Daemon -- \
  --listen http://127.0.0.1:5041 \
  --bridge F6:D5:24:56:6F:E2
```

In another terminal, start the GUI:

```sh
dotnet run --project src/ConsoleControl.Gui -- \
  --daemon http://127.0.0.1:5041
```

The GUI reports `Ready` after the daemon connects to the bridge and grants control. Choose Keyboard or one connected gamepad from the input-source list. Only the selected source sends input. Switching sources, losing keyboard focus, or disconnecting the selected gamepad clears its input before another source can take over.

The default keyboard mapping uses the arrow keys for the D-pad, `X/Z/S/A` for `A/B/X/Y`, `Q/E` for `L/R`, `1/3` for `ZL/ZR`, Tab and Enter for Minus and Plus, and `H/C` for Home and Capture. SDL gamepads use their standard positional layout, sticks, shoulders, and triggers.

Choose **Configure input mapping…** to open the modal mapping window. Click a Switch button, then press a key, gamepad button, or trigger on the selected host input source. Use **L STICK** or **R STICK**, then move a host stick, to bind its paired axes. Repeat button capture to assign several host controls to the same Switch control. One host control can also be captured for several Switch controls. Select a displayed button or trigger binding to remove it, then save the profile. Profiles are stored by keyboard identity or SDL device GUID, so equivalent controllers reuse the same profile. The daemon persists profiles in its per-user application-data directory. Stick dead zones, inversion, and scaling are represented in each profile; detailed transform editing is not yet exposed in the GUI.

On-screen controls remain available with either forwarded source. Each click adds an 80 ms overlay without releasing buttons or axes held by the selected source. This temporary slice still uses explicit process startup, a fixed bridge address, and an unauthenticated loopback endpoint.

Run the repeatable desktop checks with:

```sh
tools/verify-desktop-slice.sh
```

## Planned repository layout

```text
src/
  ConsoleControl.Core/
  ConsoleControl.Contracts/
  ConsoleControl.Client/
  ConsoleControl.Daemon/
  ConsoleControl.Video.FFmpeg/
  ConsoleControl.Controller.Bluetooth/
  ConsoleControl.Input.Sdl/
  ConsoleControl.Gui/
tests/
  ConsoleControl.Core.Tests/
  ConsoleControl.Protocol.Tests/
  ConsoleControl.IntegrationTests/
firmware/
  ConsoleControl.ControllerBridge/
controller-personalities/
  switch-hori-usb/
tools/
  ConsoleControl.DeviceProbe/
  ConsoleControl.FirmwarePack/
docs/
  adr/
  hardware/
  protocols/
```

Each .NET project has its own directory. Firmware, controller-personality data, documentation, and tools stay in their own top-level directories.

## Delivery plan

1. Prove the exact board, factory bootloader, memory map, double-reset recovery, concurrent BLE and USB operation, static Hori enumeration on Switch 2, and neutralization after BLE loss.
2. Upload the Hori personality into RAM, re-enumerate USB, forward controller snapshots, and prove that normal operation writes no flash.
3. Build the daemon's session runtime, control lease, FFmpeg capture path, gRPC boundary, and fake-hardware integration tests.
4. Build the Avalonia GUI with low-latency video, keyboard input, SDL controller mapping, device selection, daemon startup, and visible control ownership.
5. Prove the full path on the NanoKVM-USB, nRF52840 bridge, and Switch 2. This completes the first release.
6. Harden signed BLE updates, interrupted-update rollback, USB maintenance mode, packaging, and recovery documentation.
7. Add the MCP server, latest-frame screenshots, bounded input sequences, and interactive takeover.

Each phase has a hardware or end-to-end result. A successful build alone does not complete a phase.

## References

- [NanoKVM-USB](https://github.com/sipeed/NanoKVM-USB) documents the capture device and its stock CH9329 command path.
- [OpenPuck](https://github.com/safijari/openpuck) demonstrates Switch-compatible Hori and Pro Controller behavior on an nRF52840. ConsoleControl implements its firmware independently rather than copying OpenPuck's AGPL source.
- [Adafruit nRF52 Arduino](https://github.com/adafruit/Adafruit_nRF52_Arduino) provides the board support, TinyUSB integration, and Bluefruit interface to S140.

## License

ConsoleControl will be licensed under GPL-3.0-or-later. Dependency and firmware notices will be recorded when implementation begins.
