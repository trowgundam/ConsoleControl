# Use loopback as the local trust boundary

Status: accepted

## Decision

The current single-user release exposes gRPC and MJPEG without client authentication. The daemon accepts only numeric HTTP loopback listener addresses and requires distinct ports for control and video.

Any local process can observe the console and compete for its control lease. This is acceptable for the intended trusted personal workstation. ConsoleControl must state that limitation in user documentation.

Remote access, binding to a non-loopback interface, or supporting an untrusted multi-user host requires a new ADR and an authenticated transport. Do not add a token that is stored beside a world-readable endpoint and describe it as a meaningful security boundary.

## Consequences

- Installation and MCP startup need no credential provisioning.
- Loopback validation is a release invariant and has an integration regression check.
- OS-level process or user isolation remains outside ConsoleControl.
- This decision supersedes ADR 0001's proposed endpoint file and per-launch bearer secret.
