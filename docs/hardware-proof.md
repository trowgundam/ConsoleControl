# nRF52840 hardware proof

This procedure applies only to the observed `nice!nano` board with UF2 bootloader 0.6.0 and S140 6.1.1. The recorded application region starts at `0x00026000` and ends before the factory bootloader at `0x000F4000`.

The current candidate image presents a Nintendo Switch Pro Controller and accepts complete eight-byte controller states over Bluetooth LE. It implements the wired USB handshake and begins streaming `0x30` input reports after the host selects that mode. If Bluetooth disconnects or no valid state arrives for 250 ms, it sends neutral state.

The USB-only proof passed on 2026-08-28. Linux enumerated `0f0d:0092` at 12 Mbit/s and bound `hid-generic` as a USB HID 1.11 gamepad. The observed artifact and device details are recorded with the board metadata.

The concurrent BLE and USB proof also passed on 2026-08-28. BlueZ discovered and connected to `ConsoleControl Proof`. An eight-byte report written to characteristic `cc7c0002-8f5d-4b8a-9f6e-4f5f4343544c` produced a Linux `BTN_SOUTH` press through the USB HID device. With no further write, the 250 ms firmware timeout produced the matching release.

Switch 2 did not accept that Hori identity. The dock path and wired-controller setting were then verified with another controller. The Switch Pro replacement passed its laptop proof on 2026-08-28: USB commands `0x02`, `0x03`, and the repeated `0x02` handshake received matching replies; device-info and factory-calibration subcommands passed; selecting mode `0x30` started the input stream. A Bluetooth A state produced a Linux `BTN_EAST` press, and the 250 ms timeout produced its release.

The Switch 2 proof passed on 2026-08-28. The console completed USB setup, selected report mode `0x30`, finalized wired pairing with request type `0x04`, read the calibration ranges, enabled the IMU, and assigned player lights. An A snapshot opened Change Grip/Order, and a simultaneous L + R snapshot registered the controller. The firmware returned to neutral after each snapshot timed out.

It does not implement firmware updates or arbitrary personalities. Those belong after this proof passes.

## Build and validate

Install Arduino CLI, the Adafruit nRF52 core, the .NET 10 SDK, `arm-none-eabi-objcopy`, and `shellcheck`. Then run:

```bash
tools/hardware-proof.sh build
```

The command builds the firmware, converts it to UF2, and rejects an output whose blocks leave the recorded application region. The output is `artifacts/hardware-proof/controller-bridge-proof.uf2`.

With the board in double-reset bootloader mode, inspect it before copying:

```bash
tools/hardware-proof.sh inspect /run/media/jeff/NICENANO
```

The output must name the expected model, board ID, bootloader, and SoftDevice. Stop if any value differs from `firmware/ConsoleControl.ControllerBridge/boards/nicenano-v1-s140-6.1.1/observed.toml`.

## Flash the proof

1. Disconnect the board from the Switch or any other USB host.
2. Connect it to the laptop with a data-capable USB cable.
3. Double-tap reset. Confirm that `NICENANO` mounts.
4. Confirm that the mount is writable with `findmnt -no OPTIONS /run/media/jeff/NICENANO`. Do not copy while it contains `ro`.
5. Copy `artifacts/hardware-proof/controller-bridge-proof.uf2` to the root of `NICENANO`.
6. Wait for the board to accept the image and leave the UF2 volume. Do not unplug it during the copy.

The bootloader writes only the UF2 blocks in the application region. The build command prints and validates the exact generated span. The image does not contain blocks for the factory bootloader or its settings.

## Check the result

On the laptop, run `lsusb -d 057e:2009`. It should show the Nintendo vendor and Pro Controller product IDs. Leave it connected for at least one minute and confirm that it remains present. A Bluetooth scan should also show `ConsoleControl Proof` while USB remains connected.

Resolve its `/dev/hidrawN` node, then run the active handshake check:

```bash
dotnet run --project tools/ConsoleControl.DeviceProbe -- verify-switch-pro /dev/hidrawN
```

The check sends the USB handshake, requests device information and stick calibration, selects report mode `0x30`, and requires the input stream to start.

The diagnostic candidate also exposes notify characteristic `cc7c0003-8f5d-4b8a-9f6e-4f5f4343544c`. It retains up to 127 handshake, subcommand, and reply events in RAM until a BLE client subscribes. Inert rumble traffic is excluded so it cannot displace initialization evidence. Each 20-byte event contains a version, event kind, sequence, report ID, command or subcommand, payload length, selected report mode, up to eight argument bytes, and the low bytes of the drop and unsupported counters. The loop attempts at most one diagnostic notification every 10 ms, after servicing USB. Diagnostics do not write flash and do not transmit from the USB callback.

The first Switch 2 trace reached report mode `0x30`, read controller identity and calibration, then retried manual-pairing request type `0x04`. The candidate maps that request to the Pro Controller's type-`0x03` pairing-completion reply. The laptop verifier checks this response explicitly.

Do not connect a new build to the Switch until the laptop checks pass. The recorded candidate has passed Switch 2 enumeration, initialization, and button-input checks.

## Recover

If the application does not run, double-tap reset again. The `NICENANO` volume should return because this image does not overwrite the factory bootloader. Recovery has not passed until that works after flashing this proof.

Copying the saved `CURRENT.UF2` back is not yet documented as a factory restore. That file includes observed flash contents, but a successful restore has not been tested.
