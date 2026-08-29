# Desktop hardware acceptance

This record covers the current daemon and Avalonia GUI on the target Linux workstation with the tested Teyleten Robot Pro Micro nRF52840 controller bridge, docked Switch 2, and NanoKVM USB capture device.

## Test identity

- Physical board: Teyleten Robot Pro Micro nRF52840 clone, Amazon ASIN `B0CYLNZ6V4`
- Reported UF2 identity: model `nice!nano`, board ID `nRF52840-nicenano`
- Bootloader and radio stack: UF2 0.6.0 with S140 6.1.1
- Firmware artifact: SHA-256 `46d822221e9128b880d8d10db138a1cc4bf59bfeeed2bb666a463d234ef686bc`, application span `0x00026000..0x00045B00`
- USB controller identity: `057e:2009`, Switch Pro Controller candidate
- Capture source: `USB3 Video: USB3 Video`
- Capture format: MJPEG pass-through at 1920×1080, 60 fps
- Console: docked Nintendo Switch 2
- Host: Linux desktop using BlueZ adapter `hci0`

The board's factory UF2 recovery remained available through double reset after application flashing. The desktop recovery test also covered daemon restart and NanoKVM USB disconnect: the GUI resumed the persisted capture source and controller input without selecting replacement hardware. The current firmware has no semantic version, so the artifact digest identifies the exact tested image.

## 2026-08-29 runtime-hardening checkpoint

The watched GUI session passed these checks against real hardware:

- Video displayed with the expected aspect ratio.
- Unplugging and reconnecting the NanoKVM restored the persisted capture source without selecting another device.
- Rapid controller tapping produced visible press and release transitions on Switch 2.
- Rapid mapped input was repeated after the ordered neutral-cadence bypass; presses released cleanly without sticking or disappearing.
- Keyboard input worked immediately after taking control and after switching from a Steam Controller back to Keyboard.
- A focused dropdown did not consume mapped arrow keys or change the selected input source.
- The mapping dialog captured keyboard arrow keys without requiring a click on empty space.
- Steam Controller forwarding still worked after the input-mailbox and routed-key changes.
- With a mapped input held, switching Steam Controller → Keyboard and Keyboard → the same Steam Controller did not replay a stale pressed state. Fresh input from both sources continued to work after each switch. This verifies the SDL selection-generation guard in the publication candidate based on commit `dafc82b`.
- The GUI imported the existing keyboard and Steam Controller mappings into its local configuration file. It restored the saved Steam Controller after restart and acquired an unowned control lease at startup.
- Window size and maximized state persisted across GUI restarts.
- Closing the GUI released its lease. The MCP status endpoint then reported `control_owner` as `none` and `this_mcp_has_control` as `false`.
- The Switch 2 controller tester recognized A, B, X, Y, L, R, ZL, ZR, Minus, Plus, and all four D-pad directions from MCP automation. HOME and Capture are excluded by the console's tester.
- A four-second MCP B hold exited the controller tester after the daemon began refreshing active automation state every 50 ms. The pre-fix hardware run stayed in the tester because the firmware's 250 ms timeout neutralized the unrefreshed state.
- The rebuilt MCP contract advertised `left_stick_click` and `right_stick_click`. The Switch 2 tester recognized both as L3 and R3 on hardware.

The controller-state packet still uses the eight-byte proof protocol. Sequence numbers and stale-packet rejection were not tested because they are deferred firmware work.
