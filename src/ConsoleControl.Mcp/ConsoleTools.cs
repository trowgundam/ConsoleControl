using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;

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
                "granted",
                acquired ? "Control granted." : "Automation already has control.",
                isError: false);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.PermissionDenied)
        {
            return ControlRequestResult("declined", exception.Status.Detail, isError: true);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.DeadlineExceeded)
        {
            return ControlRequestResult("timed_out", exception.Status.Detail, isError: true);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Aborted)
        {
            return ControlRequestResult("unavailable", exception.Status.Detail, isError: true);
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
            return ToolError(
                "screenshot_unavailable",
                $"The daemon could not provide a current screenshot: {exception.Status.Detail}");
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
        CancellationToken cancellationToken = default) =>
        Run(new AutomationSequence([
            new AutomationCommand.Press(ParseControl(control), Milliseconds(durationMs)),
        ]), ScreenshotFidelity.Low, cancellationToken);

    [McpServerTool(Name = "console_hold"),
     Description("Holds one digital console control for a bounded duration, then releases it.")]
    public Task<CallToolResult> Hold(
        [Description("Digital control such as a, dpad_right, right_shoulder, or home")] string control,
        [Description("Hold duration in milliseconds")] int durationMs,
        CancellationToken cancellationToken = default) =>
        Run(new AutomationSequence([
            new AutomationCommand.Hold(ParseControl(control), Milliseconds(durationMs)),
        ]), ScreenshotFidelity.Low, cancellationToken);

    [McpServerTool(Name = "console_run_sequence"),
     Description("Runs up to 256 ordered digital input, pause, and screenshot commands. Press advances the timeline. Hold schedules its release but does not advance the timeline, so later commands may overlap it. Pause advances the timeline while holds remain active.")]
    public Task<CallToolResult> RunSequence(
        [Description("Ordered commands with action press, hold, pause, or screenshot")]
        SequenceCommandInput[] commands,
        [Description("Capture the console before the first input")]
        bool startScreenshot = false,
        [Description("Capture the console after all scheduled releases")]
        bool endScreenshot = false,
        [Description("Initial fidelity for every returned screenshot: low, medium, or high. Each returned screenshot ID can be rendered again independently.")]
        string screenshotFidelity = "low",
        CancellationToken cancellationToken = default)
    {
        try
        {
            ImmutableArray<AutomationCommand> parsed = commands.Select(ParseCommand).ToImmutableArray();
            return Run(
                new(parsed, startScreenshot, endScreenshot),
                ParseFidelity(screenshotFidelity),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ToolError("invalid_request", exception.Message));
        }
    }

    private async Task<CallToolResult> Run(
        AutomationSequence sequence,
        ScreenshotFidelity screenshotFidelity,
        CancellationToken cancellationToken)
    {
        AutomationResult result;
        try
        {
            result = await automation.RunAsync(sequence, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return ControlRequestResult("control_required", exception.Message, isError: true);
        }
        ImmutableArray<RetainedRendering> renderings;
        try
        {
            renderings = screenshots.AddBatchAndRender(
                result.Captures
                    .Where(capture => capture.Screenshot is not null)
                    .Select(capture => capture.Screenshot!)
                    .ToArray(),
                screenshotFidelity);
        }
        catch (ScreenshotRetentionException exception)
        {
            return ToolError("screenshot_retention_failed", exception.Message);
        }
        catch (ScreenshotRenderException exception)
        {
            return ToolError("screenshot_render_failed", exception.Message, exception.Id);
        }

        int renderingIndex = 0;
        List<object> captureMetadata = [];
        foreach (AutomationCapture capture in result.Captures)
        {
            RetainedRendering? rendering = capture.Screenshot is null
                ? null
                : renderings[renderingIndex++];
            captureMetadata.Add(new
            {
                capture.Name,
                scheduled_at_ms = Math.Ceiling(capture.ScheduledAt.TotalMilliseconds),
                actual_at_ms = Math.Ceiling(capture.ActualAt.TotalMilliseconds),
                capture.Failure,
                sequence = capture.Screenshot?.Sequence,
                screenshot_id = rendering?.Id.Value,
                fidelity = rendering?.Fidelity.ToString().ToLowerInvariant(),
                width = rendering?.Width,
                height = rendering?.Height,
                source_width = rendering?.SourceWidth,
                source_height = rendering?.SourceHeight,
                expires_at = rendering?.ExpiresAt,
            });
        }

        List<ContentBlock> content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new
                {
                    outcome = result.Outcome.ToString(),
                    elapsed_ms = Math.Ceiling(result.Elapsed.TotalMilliseconds),
                    detail = result.Detail,
                    captures = captureMetadata,
                }),
            },
        ];
        renderingIndex = 0;
        foreach (AutomationCapture capture in result.Captures)
        {
            if (capture.Screenshot is not null)
            {
                RetainedRendering rendering = renderings[renderingIndex++];
                content.Add(new TextContentBlock { Text = $"Screenshot: {capture.Name}" });
                content.Add(ImageContentBlock.FromBytes(rendering.Jpeg, "image/jpeg"));
            }
        }
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
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new
                {
                    screenshot_id = screenshot.Id.Value,
                    screenshot.Generation,
                    screenshot.Sequence,
                    fidelity = screenshot.Fidelity.ToString().ToLowerInvariant(),
                    screenshot.Width,
                    screenshot.Height,
                    source_width = screenshot.SourceWidth,
                    source_height = screenshot.SourceHeight,
                    received_at = screenshot.ReceivedAt,
                    expires_at = screenshot.ExpiresAt,
                }),
            },
            ImageContentBlock.FromBytes(screenshot.Jpeg, "image/jpeg"),
        ],
    };

    private static CallToolResult ToolError(
        string outcome,
        string detail,
        ScreenshotId? screenshotId = null) => new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        outcome,
                        detail,
                        screenshot_id = screenshotId?.Value,
                    }),
                },
            ],
            IsError = true,
        };

    private static CallToolResult ControlRequestResult(
        string outcome,
        string detail,
        bool isError) => new()
        {
            Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new { outcome, detail }),
            },
        ],
            IsError = isError,
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
}