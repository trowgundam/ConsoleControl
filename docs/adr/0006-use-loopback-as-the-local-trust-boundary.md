# Use loopback as the local trust boundary

Status: accepted

## Decision

The current single-user release exposes gRPC and MJPEG without client authentication. The daemon accepts only numeric HTTP loopback listener addresses and requires distinct ports for control and video.

Any local process can observe the console and compete for its control lease. This is acceptable for the intended trusted personal workstation. ConsoleControl must state that limitation in user documentation.

This decision covers only desktop transports. The proof firmware exposes an unpaired, writable BLE characteristic, so nearby BLE centrals can bypass the daemon's lease. The initial release accepts a trusted nearby-radio environment and documents that limitation. Pairing, bonding, and authenticated GATT access require a later firmware security design.

Remote access, binding to a non-loopback interface, or supporting an untrusted multi-user host requires a new ADR and an authenticated transport. Do not add a token that is stored beside a world-readable endpoint and describe it as a meaningful security boundary.

## Consequences

- Installation and MCP startup need no credential provisioning.
- Loopback validation is a release invariant and has an integration regression check.
- OS-level process or user isolation remains outside ConsoleControl.
- BLE peers are not authenticated in the proof firmware; daemon arbitration applies only to cooperating ConsoleControl clients.
- This decision supersedes ADR 0001's proposed endpoint file and per-launch bearer secret.
