# ConsoleControl

ConsoleControl lets a local desktop application control a game console through a small controller bridge. The first target is a docked Nintendo Switch 2 connected through a NanoKVM-USB.

The controller hardware proof has passed. The current desktop slice has daemon-backed Avalonia controls, live video, focused keyboard and SDL gamepad forwarding, and a local MCP server for digital input automation.

## First release

The project contains three applications:

- `ConsoleControl.Daemon` owns video capture, the controller bridge, configuration, and control arbitration.
- `ConsoleControl.Gui` connects to the daemon, displays live video, and forwards keyboard or physical-controller input.
- `ConsoleControl.Mcp` exposes screenshots and bounded digital-input sequences to an MCP client over stdio.

The current release supports one local console session, selectable video capture, and one Switch Pro-compatible controller personality. It does not include remote access, audio, recording, motion, rumble, or analog automation.

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

The daemon and GUI are started separately. Both bind or connect only to explicit loopback addresses. Process discovery, automatic launch, endpoint authentication, Unix sockets, and Windows named pipes are not implemented.

The daemon runs FFmpeg as a child process behind `IVideoCaptureAdapter`. Development requires `ffmpeg` and `v4l2-ctl` on `PATH`. FFmpeg reads MJPEG from the selected V4L2 device and copies the compressed frames without transcoding.

The daemon publishes live video as multipart MJPEG over a separate loopback HTTP port. gRPC manages source discovery and selection. The daemon retains only the latest compressed frame, so a slow viewer misses stale frames without blocking capture. The Avalonia client also keeps only one encoded frame waiting for decode.

See [the architecture](docs/architecture.md), [the domain language](CONTEXT.md), and [the architecture decisions](docs/adr/) for the detailed boundaries and rationale.

## Run the controller-button slice

Connect the flashed controller bridge to the Switch dock. Start the daemon:

```sh
dotnet run --project src/ConsoleControl.Daemon -- \
  --listen http://127.0.0.1:5041 \
  --video-listen http://127.0.0.1:5042 \
  --adapter hci0
```

In another terminal, start the GUI:

```sh
dotnet run --project src/ConsoleControl.Gui -- \
  --daemon http://127.0.0.1:5041
```

The GUI scans the configured Bluetooth adapter for firmware advertising the ConsoleControl service UUID. Choose a device from **Controller bridge**. It also lists MJPEG V4L2 capture devices by their stable `/dev/v4l/by-id` names. Choose a device from **Video source**. The daemon remembers both selections and opens the capture device's best MJPEG mode up to 1920x1080 at 60 fps. A controller bridge cannot be changed while any client has control.

The GUI reports `Ready` after the daemon connects to the bridge and grants control. Choose Keyboard or one connected gamepad from the input-source list. Only the selected source sends input. Switching sources, losing keyboard focus, or disconnecting the selected gamepad clears its input before another source can take over.

Controller state travels over one lease-owned gRPC stream. The client retains only the newest state, sends ordinary changes at no more than 60 Hz, and sends a heartbeat every 500 ms. Neutral state bypasses the rate limit. If the stream, daemon, or Bluetooth bridge drops, the daemon releases the lease and neutralizes the controller; the GUI reports the connection state and retries from neutral without replaying held input.

The default keyboard mapping uses the arrow keys for the D-pad, `X/Z/S/A` for `A/B/X/Y`, `Q/E` for `L/R`, `1/3` for `ZL/ZR`, Tab and Enter for Minus and Plus, and `H/C` for Home and Capture. SDL gamepads use their standard positional layout, sticks, shoulders, and triggers.

Choose **Configure input mapping…** to open the modal mapping window. Click a Switch button, then press a key, gamepad button, or trigger on the selected host input source. Use **L STICK** or **R STICK**, then move a host stick, to bind its paired axes. Repeat button capture to assign several host controls to the same Switch control. One host control can also be captured for several Switch controls. Select a displayed button or trigger binding to remove it, then save the profile. Profiles are stored by keyboard identity or SDL device GUID, so equivalent controllers reuse the same profile. The daemon persists profiles in its per-user application-data directory. Stick dead zones, inversion, and scaling are represented in each profile; detailed transform editing is not yet exposed in the GUI.

On-screen controls remain available with either forwarded source. Each click adds an 80 ms overlay without releasing buttons or axes held by the selected source. This slice still uses explicit process startup and an unauthenticated loopback endpoint.

The GUI starts as an observer. Choose **Take Control** to send input. If automation owns the control lease, this action stops its running sequence, sends a neutral controller state, and grants control to the GUI. Choose **Release Control** before automation can take control again.

## Run the MCP server

Start the daemon, then configure your MCP client to run:

```sh
dotnet run --project src/ConsoleControl.Mcp -- \
  --daemon http://127.0.0.1:5041
```

An MCP client configuration that accepts command-and-argument entries can use:

```json
{
  "mcpServers": {
    "console-control": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/ConsoleControl/src/ConsoleControl.Mcp",
        "--",
        "--daemon",
        "http://127.0.0.1:5041"
      ]
    }
  }
}
```

The server exposes status, controller-bridge and video-source inventory and selection, control acquisition and release, screenshots, individual digital input, and bounded sequences. Call `console_get_status` first. Status reads cached state and does not scan hardware. If status reports an unknown, unavailable, or unselected device, enumerate that device type and make an explicit selection before requesting control. Call `console_request_control` with a nonblank reason before sending input. If the GUI has control, it displays that reason and lets the user release control or decline the request. An accepted automation lease remains active between input calls. A GUI takeover revokes it immediately. Input tools refuse commands until the agent requests control again. `console_release_control` is idempotent.

Every MCP tool returns a JSON envelope in its first text block. The envelope always contains `outcome`, `detail`, `recovery`, `retryable`, and `data`; device and result fields use descriptive snake_case names. The MCP initialization response also supplies the essential discovery, screenshot, control, and sequence rules so a client can operate without repository context.

See [the MCP operation skill](.agents/skills/operate-console-control/SKILL.md) for the complete tool workflow and recovery rules. See [the Switch navigation skill](.agents/skills/navigate-nintendo-switch/SKILL.md) for menu navigation conventions.

`console_get_screenshot` and `console_run_sequence` return `low` fidelity by default. `low` is at most 640×360. `medium` is at most 1280×720. `high` returns the exact original JPEG at its captured dimensions. Every screenshot result includes an opaque `screenshot_id`. Call `console_render_screenshot` with that ID to inspect the same frame at another fidelity. The MCP server retains at most 16 original screenshots and 64 MiB for five minutes. Expired or evicted IDs return an error that tells the caller to capture a new screenshot.

Sequences accept at most 256 commands, run for at most 30 seconds, capture at most eight screenshots, and capture at most 32 MiB of original JPEG data. `press` advances the sequence time by its duration. `hold` schedules a release without advancing sequence time. `pause` advances sequence time while scheduled holds remain active. A failed screenshot stops the sequence, releases all buttons, and returns both earlier captures and the failed capture message. The `screenshotFidelity` argument selects the initial rendering for every successful capture. Each capture has its own ID and can be rendered again independently.

Run the repeatable desktop checks with:

```sh
tools/verify-desktop-slice.sh
```

With the daemon and GUI running, sample video delivery and process memory for ten minutes:

```sh
tools/verify-video-stability.sh 10
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
7. Add the MCP server, latest-frame screenshots, bounded digital-input sequences, and interactive takeover. Analog automation remains later work.

Each phase has a hardware or end-to-end result. A successful build alone does not complete a phase.

## References

- [NanoKVM-USB](https://github.com/sipeed/NanoKVM-USB) documents the capture device and its stock CH9329 command path.
- [OpenPuck](https://github.com/safijari/openpuck) demonstrates Switch-compatible Hori and Pro Controller behavior on an nRF52840. ConsoleControl implements its firmware independently rather than copying OpenPuck's AGPL source.
- [Adafruit nRF52 Arduino](https://github.com/adafruit/Adafruit_nRF52_Arduino) provides the board support, TinyUSB integration, and Bluefruit interface to S140.

## License

ConsoleControl will be licensed under GPL-3.0-or-later. Dependency and firmware notices will be recorded when implementation begins.
