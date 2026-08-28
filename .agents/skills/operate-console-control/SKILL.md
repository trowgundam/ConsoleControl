---
name: operate-console-control
description: Operate a local game console through ConsoleControl MCP. Use when starting or troubleshooting the daemon or MCP server, selecting controller/video hardware, acquiring or releasing control, taking screenshots, or sending digital controller input and sequences.
---

# Operate ConsoleControl

Use ConsoleControl only on the local machine. The daemon is a separate process; the stdio MCP server does not start it.

## Start the processes

Before starting a session, ask whether the user wants to watch through the GUI or run headless. Skip the question when the user already chose a mode. For a watched session, start both the daemon and GUI so the user can observe the video and take control. For a headless session, start only the daemon. The MCP client starts the stdio MCP server in either mode.

From the repository root, start the daemon in a persistent terminal:

```sh
dotnet run --project src/ConsoleControl.Daemon -- --adapter hci0
```

For a watched session, start the GUI in another persistent terminal:

```sh
dotnet run --project src/ConsoleControl.Gui -- --daemon http://127.0.0.1:5041
```

Configure the agent's MCP client to start:

```sh
dotnet run --project src/ConsoleControl.Mcp -- --daemon http://127.0.0.1:5041
```

Use `--help` on either application to inspect address options. Do not start a second daemon on the same ports.

## Establish a usable session

1. Call `console_get_status`. This is a cached observation and does not scan Bluetooth or video hardware. Read `control_owner`, `this_mcp_has_control`, `controller_bridge`, and `video` from `data`.
2. If `selected_bridge_id` is absent or controller `availability` is `unknown` or `unavailable`, call `console_get_controller_bridges`. If exactly one expected bridge is present, select its `bridge_id` with `console_select_controller_bridge` and the returned revision. If identity is ambiguous, show each `display_name` and `bridge_id` to the user and ask which one to select.
3. If `selected_source_id` is absent or video `availability` is `unknown` or `unavailable`, call `console_get_video_sources`. Select the expected `source_id` with `console_select_video_source` and the returned revision. Ask the user if identity is ambiguous.
4. Call `console_get_screenshot` at low fidelity to confirm the console is visible.
5. Before input, call `console_request_control` with a concrete reason that tells the user what will happen.
6. Release the lease with `console_release_control` when the task is finished or before changing the controller bridge.

Inventory revisions prevent stale selections. On a revision conflict, enumerate again and retry once with the new revision. Never guess an opaque device ID.

## Observe and act

Start screenshots at `low`. Use `console_render_screenshot` with the returned `screenshot_id` to render the exact retained frame at `medium` or `high`; do not capture a new frame when comparing fidelity. Capture a new screenshot after input when state may have changed. Retained frames expire after five minutes and can be evicted by capacity limits. Use the MCP screenshot tools instead of opening the capture device through FFmpeg or another process.

Use `console_press` for one atomic digital action. Use `console_hold` for a bounded hold. Use `console_run_sequence` when timing or simultaneous buttons matter:

- `press` holds a control for its duration and advances the sequence clock.
- `hold` presses now, schedules release, and does not advance the clock.
- `pause` advances the clock while scheduled holds remain pressed.
- `screenshot` captures at that point in the timeline.

Keep sequences short enough to observe and interrupt. Digital automation supports at most 256 commands, 30 seconds, eight screenshots, and 32 MiB of original screenshot data. Analog automation is not available.

Prefer a separate `console_get_screenshot` call after a sequence. Include a `screenshot` command in the sequence only when the exact timeline position matters. Bundled screenshots can make the tool response too large for the MCP client or model context.

## Recover from errors

Every tool's first text block is a JSON envelope with `outcome`, `detail`, `recovery`, `retryable`, and `data`. Read `recovery` before retrying an error. Device and result fields use descriptive snake_case names.

- `daemon_unavailable`: start or restart the daemon, then call status again.
- `control_required`: request control with a reason.
- `revision_conflict`: refresh the relevant inventory before selecting.
- `control_or_selection_conflict`: release this agent's control. If another client owns it, ask the user to release it.
- `screenshot_expired` or `screenshot_evicted`: capture a new screenshot.
- `controller_bridge_inventory_failed`: check Bluetooth and the daemon's adapter setting, then refresh inventory.
- `video_source_inventory_failed`: check the capture device and daemon, then refresh inventory.
- `controller_bridge_unavailable`: check bridge power and Bluetooth, refresh inventory, then reconnect through a new control request.
- `automation_preempted`: the GUI user took control. Stop sending input and request control again only when needed.

Do not loop on a failed selection or control request. Surface the returned detail to the user after one refreshed retry.
