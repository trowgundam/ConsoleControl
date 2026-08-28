namespace ConsoleControl.Mcp;

internal static class McpInstructions
{
    public const string Text = """
        ConsoleControl observes and controls one locally attached game console through a separate daemon.
        Begin with console_get_status. It is a cheap cached observation and does not scan hardware.
        Use the inventory tools only when status reports an unknown, unavailable, or unselected device; selection requires the revision returned by that inventory call.
        Screenshots and all inventory/status tools are read-only and do not require control. Start screenshots at low fidelity, then render the same screenshot_id at higher fidelity when needed.
        Before sending input, call console_request_control with a concrete user-facing reason. An interactive user may decline the request or preempt an active sequence. Release control when the task is complete.
        Every tool's first text content block is a JSON envelope with outcome, detail, recovery, retryable, and data. Treat isError as failure and follow recovery before retrying.
        Sequence holds overlap subsequent commands: hold schedules an asynchronous release, while press and pause advance the sequence timeline.
        """;
}