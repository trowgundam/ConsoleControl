# ADR 0005: Execute digital automation timelines in the daemon

Status: accepted

## Context

An MCP client needs screenshots, individual button actions, and bounded sequences. A hold must overlap later commands without exposing raw button-down and button-up state to the client. The existing interactive gRPC stream coalesces controller snapshots at 60 Hz, so it cannot preserve every scheduled transition.

## Decision

The daemon compiles each digital-input sequence into an absolute timeline and executes it with a monotonic clock. `Press` advances the sequence cursor by its duration. `Hold` schedules its release without advancing the cursor. `Pause` advances the cursor while scheduled holds remain active. Screenshot commands copy the latest retained JPEG at their timeline position.

The MCP server retains one automation control lease between input calls. The agent must request that lease with a nonblank reason. If the GUI has control, the daemon holds the request for up to 30 seconds and the GUI displays the reason. The user can decline the request or release control. Release grants the waiting agent control atomically.

An interactive GUI client can revoke an active automation lease. Takeover sends a neutral controller state, cancels the active sequence, invalidates its lease generation, and then grants control to the GUI. Cleanup from the old generation cannot change the new owner's state.

The first automation contract accepts only digital buttons and D-pad directions. It limits a sequence to 256 commands, 30 seconds, eight screenshots, and 32 MiB of screenshot data. A failed screenshot stops the sequence, neutralizes the controller, and returns earlier captures plus the failed capture message.

The MCP server retains each original JPEG behind an opaque, process-local screenshot ID. It returns a low, medium, or high rendering without changing the daemon capture contract. A later `console_render_screenshot` call can render the exact retained frame at another fidelity. The cache holds at most 16 originals and 64 MiB for five minutes. Macro captures enter the cache as one batch, so inserting later captures cannot evict earlier captures from the same response.

## Consequences

The daemon owns timing, overlapping state composition, screenshot ordering, cancellation, and neutralization. MCP and other clients describe intent without managing controller edges or background release tasks.

The MCP server owns agent-specific rendering, screenshot IDs, and retention. The daemon and GUI continue to use the original capture path. Fidelity changes do not delay the daemon automation timeline.

Analog automation needs explicit composition and conflict rules before it can join this command format. The current schema omits analog commands rather than assigning implicit precedence.
