# Centralize console lifecycle in a local daemon

ConsoleControl uses one local daemon to own capture, controller output, configuration, control leases, and neutralization. GUI and future MCP clients use a narrow gRPC API over loopback TCP. This keeps hardware ownership and competing-input policy in one process while allowing every client to observe the same session.

The authentication sentence in the original decision is superseded by [ADR 0006](0006-use-loopback-as-the-local-trust-boundary.md).
