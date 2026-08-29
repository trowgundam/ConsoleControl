using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;

using ConsoleControl.Client;
using ConsoleControl.Core;

using Grpc.Core;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ConsoleControl.Mcp;

internal sealed record SequenceCommandInput(
    [property: Description("press, hold, pause, or screenshot")] string Action,
    [property: Description("Digital control name for press or hold")] string? Control = null,
    [property: Description("Duration in milliseconds for press, hold, or pause")] int? DurationMs = null,
    [property: Description("Unique screenshot name")] string? Name = null);

[McpServerToolType]
internal sealed class ConsoleTools(
    AutomationControl automation,
    ScreenshotLibrary screenshots)
{
    [McpServerTool(Name = "console_get_status", ReadOnly = true),
     Description("Reports daemon reachability, control availability, and selected controller/video state.")]
    public async Task<CallToolResult> GetStatus(CancellationToken cancellationToken)
    {
        try
        {
            ConsoleStatus status = await automation.GetStatusAsync(cancellationToken);
            return Success("status_observed", "Current cached daemon status.", new
            {
                daemon_version = status.DaemonVersion,
                protocol_version = status.ProtocolVersion,
                control_owner = SnakeCase(status.ControlOwner),
                this_mcp_has_control = status.ControlOwner == ControlOwner.ThisClient,
                controller_bridge = new
                {
                    selected_bridge_id = status.ControllerBridge.SelectedBridgeId?.Value,
                    availability = SnakeCase(status.ControllerBridge.Availability),
                    output_connection = SnakeCase(status.ControllerBridge.OutputConnection),
                    detail = status.ControllerBridge.Detail,
                    last_inventory_at = status.ControllerBridge.LastInventoryAt,
                },
                video = new
                {
                    selected_source_id = status.Video.SelectedSourceId?.Value,
                    availability = SnakeCase(status.Video.Availability),
                    capture_state = SnakeCase(status.Video.CaptureState),
                    active_mode = status.Video.ActiveMode is { } mode
                        ? new { width = mode.Width, height = mode.Height, frames_per_second = mode.FramesPerSecond }
                        : null,
                    latest_frame_at = status.Video.LatestFrameAt,
                    detail = status.Video.Detail,
                },
                supported_controls = Enum.GetValues<CanonicalDigitalControl>()
                    .Where(control => control != CanonicalDigitalControl.Unspecified)
                    .Select(SnakeCase),
                screenshot_fidelities = new[] { "low", "medium", "high" },
                sequence_limit = 256,
            });
        }
        catch (RpcException exception)
        {
            return RpcError(exception, "status_unavailable", "Start the ConsoleControl daemon, then retry.");
        }
    }

    [McpServerTool(Name = "console_get_controller_bridges", ReadOnly = true),
     Description("Scans the configured Bluetooth adapter for compatible ConsoleControl controller bridges.")]
    public async Task<CallToolResult> GetControllerBridges(CancellationToken cancellationToken)
    {
        try
        {
            ControllerBridgeInventory inventory = await automation.GetControllerBridgesAsync(cancellationToken);
            return Success("controller_bridge_inventory_observed", inventory.Status, new
            {
                revision = inventory.Revision,
                selected_bridge_id = inventory.SelectedBridgeId?.Value,
                state = SnakeCase(inventory.State),
                bridges = inventory.Bridges.Select(bridge => new
                {
                    bridge_id = bridge.Id.Value,
                    display_name = bridge.DisplayName,
                    bluetooth_connected = bridge.BluetoothConnected,
                }),
            });
        }
        catch (RpcException exception)
        {
            return RpcError(exception, "bridge_scan_failed", "Check Bluetooth and the daemon's --adapter setting, then retry.");
        }
    }

    [McpServerTool(Name = "console_select_controller_bridge"),
     Description("Selects one compatible controller bridge. Selection is refused while any client has control.")]
    public async Task<CallToolResult> SelectControllerBridge(
        [Description("Opaque bridge ID returned by console_get_controller_bridges")] string bridgeId,
        [Description("Revision returned by console_get_controller_bridges")] ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            ControllerBridgeSelection selected = await automation.SelectControllerBridgeAsync(
                new(bridgeId), expectedRevision, cancellationToken);
            return Success("controller_bridge_selected", selected.Status, new
            {
                bridge_id = selected.BridgeId.Value,
                revision = selected.Revision,
                state = SnakeCase(selected.State),
            });
        }
        catch (ArgumentException exception)
        {
            return ToolError("invalid_bridge", exception.Message, recovery: "Refresh the bridge inventory and use one returned ID.");
        }
        catch (RpcException exception)
        {
            return RpcError(exception, "bridge_selection_failed", "Release control, refresh the bridge inventory, and retry with its current revision.");
        }
    }

    [McpServerTool(Name = "console_get_video_sources", ReadOnly = true),
     Description("Lists video capture sources and the current selection.")]
    public async Task<CallToolResult> GetVideoSources(CancellationToken cancellationToken)
    {
        try
        {
            VideoInventory inventory = await automation.GetVideoSourcesAsync(cancellationToken);
            return Success("video_source_inventory_observed", inventory.Status, new
            {
                revision = inventory.Revision,
                selected_source_id = inventory.SelectedSourceId?.Value,
                sources = inventory.Sources.Select(source => new
                {
                    source_id = source.Id.Value,
                    display_name = source.DisplayName,
                    width = source.PreferredMode.Width,
                    height = source.PreferredMode.Height,
                    frames_per_second = source.PreferredMode.FramesPerSecond,
                }),
            });
        }
        catch (RpcException exception)
        {
            return RpcError(exception, "video_inventory_failed", "Check that the capture device is connected and the daemon is running.");
        }
    }

    [McpServerTool(Name = "console_select_video_source"),
     Description("Selects a video source using the current inventory revision.")]
    public async Task<CallToolResult> SelectVideoSource(
        [Description("Opaque source ID returned by console_get_video_sources")] string sourceId,
        [Description("Revision returned by console_get_video_sources")] ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            VideoSelection selected = await automation.SelectVideoSourceAsync(
                new(sourceId), expectedRevision, cancellationToken);
            return Success("video_source_selected", selected.Status, new
            {
                source_id = selected.SourceId.Value,
                revision = selected.Revision,
            });
        }
        catch (ArgumentException exception)
        {
            return ToolError("invalid_video_source", exception.Message, recovery: "Refresh the video inventory and use one returned ID.");
        }
        catch (RpcException exception)
        {
            return RpcError(exception, "video_selection_failed", "Refresh the video inventory and retry with its current revision.");
        }
    }

    [McpServerTool(Name = "console_release_control"),
     Description("Releases this MCP process's automation lease and neutralizes controller input. Safe to call repeatedly.")]
    public async Task<CallToolResult> ReleaseControl()
    {
        bool released = await automation.ReleaseControlAsync();
        return Success(
            released ? "control_released" : "control_not_owned",
            released ? "Automation control released." : "This MCP process did not own control.",
            new { released });
    }

    [McpServerTool(Name = "console_request_control"),
     Description("Requests controller control. A nonblank reason is required and is shown to an interactive user who currently has control. The request waits up to 30 seconds for the user to release or decline control.")]
    public async Task<CallToolResult> RequestControl(
        [Description("Why the agent needs to control the console. Shown verbatim in the GUI prompt.")]
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            bool acquired = await automation.RequestControlAsync(reason, cancellationToken);
            return ControlRequestResult(
                "control_granted",
                acquired ? "Control granted." : "Automation already has control.",
                isError: false);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.PermissionDenied)
        {
            return ControlRequestResult("control_declined", exception.Status.Detail, isError: true);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.DeadlineExceeded)
        {
            return ControlRequestResult("control_request_timed_out", exception.Status.Detail, isError: true);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Aborted)
        {
            return ControlRequestResult("control_unavailable", exception.Status.Detail, isError: true);
        }
        catch (RpcException exception)
        {
            return RpcError(exception, "control_request_failed", "Check status and retry after resolving the reported condition.");
        }
    }

    [McpServerTool(Name = "console_get_screenshot", ReadOnly = true),
     Description("Returns the most recent console video frame without taking controller control.")]
    public async Task<CallToolResult> GetScreenshot(
        [Description("low (640x360), medium (1280x720), or high (exact original). Start low and render the returned screenshot ID at higher fidelity only when needed.")]
        string fidelity = "low",
        CancellationToken cancellationToken = default)
    {
        ScreenshotFidelity parsed;
        try
        {
            parsed = ParseFidelity(fidelity);
        }
        catch (ArgumentException exception)
        {
            return ToolError("invalid_fidelity", exception.Message);
        }

        try
        {
            Screenshot screenshot = await automation.GetScreenshotAsync(cancellationToken);
            return ScreenshotResult(screenshots.AddAndRender(screenshot, parsed));
        }
        catch (ScreenshotRetentionException exception)
        {
            return ToolError("screenshot_retention_failed", exception.Message);
        }
        catch (ScreenshotRenderException exception)
        {
            return ToolError("screenshot_render_failed", exception.Message, exception.Id);
        }
        catch (RpcException exception)
        {
            return RpcError(
                exception,
                "screenshot_unavailable",
                "Check console_get_status. Start the daemon or restore video capture as reported, then retry.");
        }
    }

    [McpServerTool(Name = "console_render_screenshot", ReadOnly = true),
     Description("Renders a previously captured screenshot at another fidelity. This uses the exact retained frame and does not capture a newer frame.")]
    public CallToolResult RenderScreenshot(
        [Description("Opaque screenshot_id returned by console_get_screenshot or console_run_sequence")]
        string screenshotId,
        [Description("low (640x360), medium (1280x720), or high (exact original)")]
        string fidelity)
    {
        if (!TryParseScreenshotId(screenshotId, out ScreenshotId id))
        {
            return ToolError(
                "screenshot_not_found",
                $"Screenshot ID '{screenshotId}' is invalid. Use a screenshot_id returned by this MCP process.");
        }

        ScreenshotFidelity parsed;
        try
        {
            parsed = ParseFidelity(fidelity);
        }
        catch (ArgumentException exception)
        {
            return ToolError("invalid_fidelity", exception.Message, id);
        }

        try
        {
            return screenshots.Render(id, parsed) switch
            {
                ScreenshotRenderResult.Found found => ScreenshotResult(found.Rendering),
                ScreenshotRenderResult.Unavailable unavailable => ToolError(
                    unavailable.Reason switch
                    {
                        ScreenshotUnavailableReason.Expired => "screenshot_expired",
                        ScreenshotUnavailableReason.Evicted => "screenshot_evicted",
                        _ => "screenshot_not_found",
                    },
                    unavailable.Detail,
                    id),
                _ => throw new InvalidOperationException("Unknown screenshot render result."),
            };
        }
        catch (ScreenshotRenderException exception)
        {
            return ToolError("screenshot_render_failed", exception.Message, exception.Id);
        }
    }

    [McpServerTool(Name = "console_press"),
     Description("Presses one digital console control. The press completes before this call returns.")]
    public Task<CallToolResult> Press(
        [Description("Digital control such as a, dpad_right, right_shoulder, or home")] string control,
        [Description("Press duration in milliseconds. Defaults to 80.")] int durationMs = 80,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Run(new AutomationSequence([
                new AutomationCommand.Press(ParseControl(control), Milliseconds(durationMs)),
            ]), cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ToolError("invalid_request", exception.Message));
        }
    }

    [McpServerTool(Name = "console_hold"),
     Description("Holds one digital console control for a bounded duration, then releases it.")]
    public Task<CallToolResult> Hold(
        [Description("Digital control such as a, dpad_right, right_shoulder, or home")] string control,
        [Description("Hold duration in milliseconds")] int durationMs,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Run(new AutomationSequence([
                new AutomationCommand.Hold(ParseControl(control), Milliseconds(durationMs)),
            ]), cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ToolError("invalid_request", exception.Message));
        }
    }

    [McpServerTool(Name = "console_run_sequence"),
     Description("Runs up to 256 ordered digital input, pause, and screenshot commands. Press advances the timeline. Hold schedules its release but does not advance the timeline, so later commands may overlap it. Pause advances the timeline while holds remain active.")]
    public Task<CallToolResult> RunSequence(
        [Description("Ordered commands with action press, hold, pause, or screenshot")]
        SequenceCommandInput[] commands,
        [Description("Capture the console before the first input")]
        bool startScreenshot = false,
        [Description("Capture the console after all scheduled releases")]
        bool endScreenshot = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(commands);
            ImmutableArray<AutomationCommand> parsed = commands.Select(ParseCommand).ToImmutableArray();
            return Run(
                new(parsed, startScreenshot, endScreenshot),
                cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return Task.FromResult(ToolError("invalid_request", exception.Message));
        }
    }

    private async Task<CallToolResult> Run(
        AutomationSequence sequence,
        CancellationToken cancellationToken)
    {
        AutomationResult result;
        try
        {
            sequence.Compile();
            result = await automation.RunAsync(sequence, cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return ToolError("invalid_request", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ControlRequestResult("control_required", exception.Message, isError: true);
        }
        catch (RpcException exception)
        {
            return RpcError(exception, "automation_failed", "Check status, request control again if needed, and retry.");
        }
        ImmutableArray<RetainedScreenshot> retained;
        try
        {
            retained = screenshots.AddBatch(
                result.Captures
                    .Where(capture => capture.Screenshot is not null)
                    .Select(capture => capture.Screenshot!)
                    .ToArray());
        }
        catch (ScreenshotRetentionException exception)
        {
            return ToolError("screenshot_retention_failed", exception.Message);
        }
        int retainedIndex = 0;
        List<object> captureMetadata = [];
        foreach (AutomationCapture capture in result.Captures)
        {
            RetainedScreenshot? screenshot = capture.Screenshot is null
                ? null
                : retained[retainedIndex++];
            captureMetadata.Add(new
            {
                name = capture.Name,
                scheduled_at_ms = Math.Ceiling(capture.ScheduledAt.TotalMilliseconds),
                actual_at_ms = Math.Ceiling(capture.ActualAt.TotalMilliseconds),
                failure = capture.Failure,
                generation = screenshot?.Generation,
                sequence = screenshot?.Sequence,
                screenshot_id = screenshot?.Id.Value,
                source_width = screenshot?.SourceWidth,
                source_height = screenshot?.SourceHeight,
                received_at = screenshot?.ReceivedAt,
                expires_at = screenshot?.ExpiresAt,
            });
        }

        string outcome = result.Outcome switch
        {
            AutomationOutcome.Completed => "automation_completed",
            AutomationOutcome.Preempted => "automation_preempted",
            AutomationOutcome.Cancelled => "automation_cancelled",
            AutomationOutcome.BridgeUnavailable => "controller_bridge_unavailable",
            AutomationOutcome.ScreenshotFailed => "screenshot_failed",
            _ => "automation_failed",
        };
        List<ContentBlock> content =
        [
            EnvelopeBlock(
                outcome,
                result.Detail ?? "Automation ended without additional detail.",
                new
                {
                    elapsed_ms = Math.Ceiling(result.Elapsed.TotalMilliseconds),
                    captures = captureMetadata,
                },
                result.Outcome == AutomationOutcome.Completed ? null : AutomationRecovery(result.Outcome),
                result.Outcome is AutomationOutcome.BridgeUnavailable or AutomationOutcome.ScreenshotFailed),
        ];
        return new()
        {
            Content = content,
            IsError = result.Outcome != AutomationOutcome.Completed,
        };
    }

    private static CallToolResult ScreenshotResult(RetainedRendering screenshot) => new()
    {
        Content =
        [
            EnvelopeBlock(
                "screenshot_rendered",
                "Screenshot rendered from a retained console video frame.",
                new
                {
                    screenshot_id = screenshot.Id.Value,
                    generation = screenshot.Generation,
                    sequence = screenshot.Sequence,
                    fidelity = screenshot.Fidelity.ToString().ToLowerInvariant(),
                    width = screenshot.Width,
                    height = screenshot.Height,
                    source_width = screenshot.SourceWidth,
                    source_height = screenshot.SourceHeight,
                    received_at = screenshot.ReceivedAt,
                    expires_at = screenshot.ExpiresAt,
                }),
            ImageContentBlock.FromBytes(screenshot.Jpeg, "image/jpeg"),
        ],
    };

    private static CallToolResult ToolError(
        string outcome,
        string detail,
        ScreenshotId? screenshotId = null,
        string? recovery = null,
        bool retryable = false) => new()
        {
            Content = [EnvelopeBlock(
                outcome,
                detail,
                screenshotId is { } id ? new { screenshot_id = id.Value } : new { },
                recovery ?? "Correct the reported condition and retry.",
                retryable)],
            IsError = true,
        };

    private static CallToolResult RpcError(
        RpcException exception,
        string fallbackOutcome,
        string recovery) => ToolError(
            FailureCode(exception) ?? exception.StatusCode switch
            {
                StatusCode.Unavailable => "daemon_unavailable",
                StatusCode.Aborted => "revision_conflict",
                StatusCode.FailedPrecondition => "control_or_selection_conflict",
                StatusCode.InvalidArgument => "invalid_request",
                StatusCode.PermissionDenied => "permission_denied",
                StatusCode.DeadlineExceeded => "timed_out",
                _ => fallbackOutcome,
            },
            string.IsNullOrWhiteSpace(exception.Status.Detail)
                ? $"The daemon returned {exception.StatusCode}."
                : exception.Status.Detail,
            recovery: recovery,
            retryable: FailureRetryable(exception) || exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded);

    private static CallToolResult Success(string outcome, string detail, object data) => new()
    {
        Content = [EnvelopeBlock(outcome, detail, data)],
    };

    private static CallToolResult ControlRequestResult(
        string outcome,
        string detail,
        bool isError) => isError
            ? ToolError(outcome, detail, recovery: ControlRecovery(outcome), retryable:
                outcome is "control_request_timed_out" or "control_unavailable")
            : Success(outcome, detail, new { has_control = true });

    private static TextContentBlock EnvelopeBlock(
        string outcome,
        string detail,
        object data,
        string? recovery = null,
        bool retryable = false) => new()
        {
            Text = JsonSerializer.Serialize(new { outcome, detail, recovery, retryable, data }),
        };

    private static string? FailureCode(RpcException exception) =>
        exception.Trailers.FirstOrDefault(entry => entry.Key == "console-failure-code")?.Value;

    private static bool FailureRetryable(RpcException exception) =>
        string.Equals(
            exception.Trailers.FirstOrDefault(entry => entry.Key == "console-retryable")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static string ControlRecovery(string outcome) => outcome switch
    {
        "control_declined" => "Wait for the interactive user to release control, or ask again later with a clear reason.",
        "control_request_timed_out" => "Check console_get_status, then request control again if an interactive client still owns it.",
        "control_unavailable" => "Check console_get_status and resolve the reported controller bridge or lease condition.",
        "control_required" => "Call console_request_control with a nonblank reason before sending input.",
        _ => "Check console_get_status and retry after resolving the reported condition.",
    };

    private static string AutomationRecovery(AutomationOutcome outcome) => outcome switch
    {
        AutomationOutcome.Preempted => "An interactive user took control. Request control again only when appropriate.",
        AutomationOutcome.Cancelled => "Submit the sequence again if it is still needed.",
        AutomationOutcome.BridgeUnavailable => "Inspect console_get_status and console_get_controller_bridges, then select or reconnect a bridge.",
        AutomationOutcome.ScreenshotFailed => "Inspect console_get_status and console_get_video_sources, then retry after video is streaming.",
        _ => "Inspect console_get_status before retrying.",
    };

    private static AutomationCommand ParseCommand(SequenceCommandInput command) =>
        command.Action.Trim().ToLowerInvariant() switch
        {
            "press" => new AutomationCommand.Press(
                ParseRequiredControl(command),
                Milliseconds(command.DurationMs ?? 80)),
            "hold" => new AutomationCommand.Hold(
                ParseRequiredControl(command),
                Milliseconds(command.DurationMs ?? throw new ArgumentException(
                    "A hold command requires durationMs."))),
            "pause" => new AutomationCommand.Pause(
                Milliseconds(command.DurationMs ?? throw new ArgumentException(
                    "A pause command requires durationMs."))),
            "screenshot" => new AutomationCommand.Capture(
                command.Name ?? throw new ArgumentException("A screenshot command requires name.")),
            _ => throw new ArgumentException($"Unknown sequence action '{command.Action}'."),
        };

    private static CanonicalDigitalControl ParseRequiredControl(SequenceCommandInput command) =>
        ParseControl(command.Control ?? throw new ArgumentException(
            $"A {command.Action} command requires control."));

    private static CanonicalDigitalControl ParseControl(string value)
    {
        string normalized = value.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        foreach (CanonicalDigitalControl candidate in Enum.GetValues<CanonicalDigitalControl>())
        {
            string name = candidate.ToString().Replace("_", "", StringComparison.Ordinal);
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
        throw new ArgumentException($"Unknown digital control '{value}'.");
    }

    private static ScreenshotFidelity ParseFidelity(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "low" => ScreenshotFidelity.Low,
            "medium" => ScreenshotFidelity.Medium,
            "high" => ScreenshotFidelity.High,
            _ => throw new ArgumentException(
                $"Unknown screenshot fidelity '{value}'. Use low, medium, or high."),
        };

    private static bool TryParseScreenshotId(string value, out ScreenshotId id)
    {
        bool valid = value.StartsWith("ss_", StringComparison.Ordinal) &&
            Guid.TryParseExact(value.AsSpan(3), "N", out _);
        id = valid ? new(value) : default;
        return valid;
    }

    private static TimeSpan Milliseconds(int value) => TimeSpan.FromMilliseconds(value);

    private static string SnakeCase<T>(T value) where T : struct, Enum
    {
        string name = value.ToString();
        return string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));
    }
}