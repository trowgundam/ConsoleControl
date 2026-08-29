# ConsoleControl agent instructions

## Start with current context

Read `README.md` for scope and delivery phase. Read `CONTEXT.md` before naming domain concepts. Read `docs/architecture.md` for ownership, data flow, and implementation gates. Read the relevant file under `docs/adr/` before changing an accepted boundary.

The repository has passed its controller, GUI, video, and digital MCP proofs on the target hardware. Preserve those working paths and verify real hardware when changing their boundaries.

## Preserve the product boundary

- Keep one daemon as the owner of a console session. The daemon owns capture and controller lifetimes, hardware selection, leases, neutralization, and shutdown order. The GUI owns host-input mappings and presentation preferences.
- Keep gRPC and protobuf at the transport boundary. GUI, input, and domain code use canonical types from `ConsoleControl.Core`.
- Keep FFmpeg, BLE, USB, and platform APIs behind hardware adapters. Hardware adapters contain protocol behavior, not lease or product policy.
- Send complete `ControllerState` snapshots. Do not model daemon input as independent button-down and button-up events.
- Authorize every controller write with the active lease generation. Route revocation, disconnect, expiry, bridge loss, personality change, and shutdown through the same neutralization operation.
- Give each live-video subscriber a bounded latest-frame slot. Capture must not wait for a slow client.
- Keep the endpoint seam limited to publishing daemon connection data and creating a client channel. Add another local transport only when a supported platform requires it.
- Keep gRPC and MJPEG bound to numeric loopback addresses. The current single-user release deliberately has no client authentication. Propose a new ADR before adding remote access or supporting an untrusted multi-user host.

## Protect the controller bridge

- Treat purchased boards as unknown until `INFO_UF2.TXT`, the MCU, bootloader version, UF2 family, and flash boundaries have been recorded.
- Preserve the factory bootloader during the initial firmware proof. Propose a new ADR before replacing it.
- Put verified memory maps under `firmware/ConsoleControl.ControllerBridge/boards/`. Tie each map to observed board metadata.
- Constrain application flash writes to explicit inactive-image and update-metadata regions. Keep bootloader, active-image, signing-key, radio-stack, and protected configuration regions outside the writable type or address range.
- Keep controller personalities and controller state in RAM. Ordinary control and personality activation must not write flash.
- Validate personality size, descriptors, endpoints, report layout, polling limits, and requested handler before USB activation.
- Keep USB polling independent of BLE progress. A BLE timeout or disconnect emits neutral state.
- Accept executable firmware only through a signed update package. The bootloader verifies the signature before activation.
- Test power loss and invalid images against the actual recovery path before describing OTA as safe.

## Keep the current release narrow

The current release includes the daemon, Avalonia GUI, and local stdio MCP server. It includes live video, explicit controller and video selection, keyboard input, SDL controller forwarding and mapping, one control lease, screenshots, and bounded digital automation.

Analog automation, motion, rumble, audio, recording, remote access, multiple consoles, service installation, bundled FFmpeg, and additional local transports remain later work. Add one only when the user changes the release scope.

## Structure new work

Place every .NET project in its own root-level project directory under `src/`, `tests/`, or `tools/` as shown in `docs/architecture.md`. Keep C++ firmware under `firmware/`, runtime personality assets under `controller-personalities/`, and supplemental material under `docs/`.

Use GPL-3.0-or-later-compatible dependencies. OpenPuck is an AGPL research reference. Implement protocol behavior independently unless the user explicitly accepts AGPL for the affected artifact. Record protocol provenance and dependency licenses.

## Prove each boundary

Run the narrowest repeatable checks first, then exercise the real path affected by the change. A build is necessary but does not prove hardware or user behavior.

- Domain tests prove mapping, lease transitions, stale-generation rejection, automation timeline validation, and personality validation.
- Daemon and client integration tests use fake capture and bridge adapters to prove loopback-only binding, neutralization, and frame backpressure.
- Protocol tests use recorded packets and descriptors without hiding byte-level mismatches behind mocks.
- Controller-state sequence validation becomes a required protocol and hardware test when the deferred firmware checkpoint adds sequence numbers and stale-packet rejection.
- Hardware acceptance tests record board identity, firmware version, capture format, console result, and recovery result.
- GUI work is complete only after driving the real application and checking the visible result.
- MCP work is complete only after exercising the published tool through a running daemon. Check both successful results and structured errors.

Run `dotnet format ConsoleControl.slnx --no-restore` after each coherent C# change. Then build with disabled build servers and one MSBuild node before running the narrowest relevant checks.

When hardware is unavailable, report the unverified boundary. Do not replace a missing hardware result with an inference from a mock or successful compilation.
