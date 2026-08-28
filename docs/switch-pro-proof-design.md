# Switch Pro hardware-proof design

## Problem

Switch 2 did not accept the descriptor-only Hori Pokkén personality, although Linux accepted it and the same dock path accepted another wired controller. The next proof must answer the active Switch Pro USB handshake while preserving the proven Bluetooth state transport, 250 ms fail-neutral behavior, factory bootloader, and no-flash control path.

## Shape

The proof stays in one Arduino sketch. A short critical section protects the Bluetooth mailbox. A separate fixed command queue transfers USB output reports from the TinyUSB callback to the main loop. The callback does no protocol work and never transmits. The main loop owns the handshake state, one pending reply, the input timer, state translation, and every USB send.

The USB personality uses the captured 203-byte `057e:2009` Pro Controller report descriptor. It accepts the documented `0x80` USB initialization family and an explicit allowlist of `0x01` subcommands. It supplies a read-only virtual calibration image from RAM. SPI writes and erases return write-protected and never touch flash. Periodic `0x30` reports begin only after the host selects that mode.

## Synthesis decision

Two sketches agreed that the TinyUSB callback must only enqueue bounded data and that the loop must own protocol state. The selected design adds the stronger byte-for-byte host verification plan, then removes its second queue in favor of one pending reply. It also keeps the Bluetooth mailbox independent, handles at most one command per loop, protects queue indices with a critical section, and counts unsupported or dropped commands instead of claiming success.

Immediate replies from the USB callback were rejected because they mix TinyUSB callback and loop ownership. A generic positive ACK was rejected because it hides missing protocol behavior. A multi-file personality framework was rejected until the hardware proof establishes the actual Switch 2 contract.

## Tradeoffs accepted

- The proof uses fixed RAM identity and pairing data so ordinary operation writes no flash.
- Motion and rumble commands are accepted where initialization requires them, but the features remain inert.
- The existing Hori-shaped eight-byte Bluetooth state remains temporary input to avoid changing two boundaries in one proof.
- The virtual calibration image implements only documented and observed address ranges.

## Sources

- dekuNukem's `Nintendo_Switch_Reverse_Engineering` documents the USB command and Switch controller subcommand protocols.
- A public capture of a genuine Nintendo Pro Controller supplies the device and HID descriptors.
- OpenPuck is used only to compare observable behavior. Its AGPL implementation is not copied into ConsoleControl.

## Acceptance result

Linux proved enumeration, handshake framing, report gating, Bluetooth input translation, and fail-neutral behavior. The physical Switch 2 test then proved console compatibility, wired pairing finalization, calibration reads, player assignment, and BLE-to-console button input. Motion behavior remains outside this proof.
