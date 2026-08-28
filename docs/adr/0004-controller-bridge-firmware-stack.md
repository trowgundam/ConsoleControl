# ADR 0004: Use the Adafruit nRF52 C++ stack for the controller bridge

Status: accepted

## Context

The initial Rust proof used Embassy USB and TrouBLE with Nordic MPSL. Its binaries booted from the recorded `0x26000` application address, as proved by a GPIO-only image, but neither the combined image nor a USB-only image enumerated on the purchased Teyleten nRF52840 clone.

The board already contains S140 6.1.1. OpenPuck exercises the same clone family through Adafruit's nRF52 Arduino core and TinyUSB, and documents clone-specific clock behavior. Continuing with Rust would require resolving USB initialization and POWER/CLOCK ownership while also introducing MPSL beside the installed S140 image.

## Decision

Implement the bridge firmware independently in C++ using Adafruit's nRF52 Arduino core, TinyUSB, and Bluefruit. Preserve the factory UF2 bootloader and installed S140 image.

Use OpenPuck as a hardware and protocol reference. Do not copy its AGPL implementation into ConsoleControl. Controller personalities remain bounded data with compiled handlers for protocols that descriptors alone cannot express.

## Consequences

The firmware language differs from the C# desktop applications. The selected stack has a proven USB path on the target board and uses its installed S140 radio firmware. Firmware builds require Arduino CLI and the Adafruit nRF52 core.

The failed Rust sources and Cargo build files are removed rather than maintained as a second implementation.
