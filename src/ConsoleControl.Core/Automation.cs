using System.Collections.Immutable;

namespace ConsoleControl.Core;

public static class AutomationLimits
{
    public const int MaxCommands = 256;
    public const int MaxCaptures = 8;
    public const int MaxAggregateCaptureBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultPressDuration = TimeSpan.FromMilliseconds(80);
}

public abstract record AutomationCommand
{
    private AutomationCommand() { }

    public sealed record Press(CanonicalDigitalControl Control, TimeSpan Duration) : AutomationCommand;
    public sealed record Hold(CanonicalDigitalControl Control, TimeSpan Duration) : AutomationCommand;
    public sealed record Pause(TimeSpan Duration) : AutomationCommand;
    public sealed record Capture(string Name) : AutomationCommand;
}

public sealed record AutomationSequence(
    ImmutableArray<AutomationCommand> Commands,
    bool CaptureStart = false,
    bool CaptureEnd = false)
{
    public CompiledAutomation Compile()
    {
        if (Commands.IsDefault)
        {
            throw new ArgumentException("Commands must be initialized.", nameof(Commands));
        }
        if (Commands.Length > AutomationLimits.MaxCommands)
        {
            throw new ArgumentException($"A sequence may contain at most {AutomationLimits.MaxCommands} commands.");
        }

        List<AutomationEvent> events = [];
        HashSet<string> captureNames = new(StringComparer.Ordinal);
        int captureCount = (CaptureStart ? 1 : 0) + (CaptureEnd ? 1 : 0);
        TimeSpan cursor = TimeSpan.Zero;
        TimeSpan completion = TimeSpan.Zero;
        int order = 0;

        if (CaptureStart)
        {
            captureNames.Add("start");
            events.Add(new(TimeSpan.Zero, AutomationEventKind.StartCapture, null, "start", order++));
        }

        foreach (AutomationCommand command in Commands)
        {
            switch (command)
            {
                case AutomationCommand.Press press:
                    ValidateControl(press.Control);
                    ValidateDuration(press.Duration, nameof(press.Duration));
                    events.Add(new(cursor, AutomationEventKind.Press, press.Control, null, order++));
                    cursor = AddChecked(cursor, press.Duration);
                    events.Add(new(cursor, AutomationEventKind.Release, press.Control, null, order++));
                    completion = Max(completion, cursor);
                    break;

                case AutomationCommand.Hold hold:
                    ValidateControl(hold.Control);
                    ValidateDuration(hold.Duration, nameof(hold.Duration));
                    events.Add(new(cursor, AutomationEventKind.Press, hold.Control, null, order++));
                    TimeSpan releaseAt = AddChecked(cursor, hold.Duration);
                    events.Add(new(releaseAt, AutomationEventKind.Release, hold.Control, null, order++));
                    completion = Max(completion, releaseAt);
                    break;

                case AutomationCommand.Pause pause:
                    ValidateDuration(pause.Duration, nameof(pause.Duration));
                    cursor = AddChecked(cursor, pause.Duration);
                    completion = Max(completion, cursor);
                    break;

                case AutomationCommand.Capture capture:
                    ValidateCaptureName(capture.Name, captureNames);
                    captureCount++;
                    events.Add(new(cursor, AutomationEventKind.Capture, null, capture.Name, order++));
                    break;

                default:
                    throw new ArgumentException("The sequence contains an unsupported command.");
            }
        }

        if (captureCount > AutomationLimits.MaxCaptures)
        {
            throw new ArgumentException($"A sequence may request at most {AutomationLimits.MaxCaptures} screenshots.");
        }
        if (completion > AutomationLimits.MaxDuration)
        {
            throw new ArgumentException($"A sequence may run for at most {AutomationLimits.MaxDuration.TotalSeconds:0} seconds.");
        }
        if (events.Count == 0 && !CaptureEnd)
        {
            throw new ArgumentException("A sequence must contain an action or request a screenshot.");
        }
        if (CaptureEnd)
        {
            captureNames.Add("end");
            events.Add(new(completion, AutomationEventKind.EndCapture, null, "end", order++));
        }

        return new(events
            .OrderBy(item => item.Offset)
            .ThenBy(item => EventPhase(item.Kind))
            .ThenBy(item => item.RequestOrder)
            .ToImmutableArray(), completion);
    }

    private static void ValidateControl(CanonicalDigitalControl control)
    {
        if (control == CanonicalDigitalControl.Unspecified || !Enum.IsDefined(control))
        {
            throw new ArgumentOutOfRangeException(nameof(control));
        }
    }

    private static void ValidateDuration(TimeSpan duration, string parameterName)
    {
        if (duration <= TimeSpan.Zero || duration > AutomationLimits.MaxDuration)
        {
            throw new ArgumentOutOfRangeException(parameterName,
                $"Duration must be between 1 ms and {AutomationLimits.MaxDuration.TotalSeconds:0} seconds.");
        }
    }

    private static void ValidateCaptureName(string name, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(name) || System.Text.Encoding.UTF8.GetByteCount(name) > 64)
        {
            throw new ArgumentException("Screenshot names must contain 1 to 64 UTF-8 bytes.", nameof(name));
        }
        if (name is "start" or "end" || !names.Add(name))
        {
            throw new ArgumentException($"Screenshot name '{name}' is reserved or duplicated.", nameof(name));
        }
    }

    private static TimeSpan AddChecked(TimeSpan left, TimeSpan right) =>
        TimeSpan.FromTicks(checked(left.Ticks + right.Ticks));

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static int EventPhase(AutomationEventKind kind) => kind switch
    {
        AutomationEventKind.StartCapture => 0,
        AutomationEventKind.Release => 1,
        AutomationEventKind.Press => 2,
        AutomationEventKind.Capture => 3,
        AutomationEventKind.EndCapture => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public enum AutomationOutcome
{
    Completed,
    Preempted,
    Cancelled,
    BridgeUnavailable,
    ScreenshotFailed,
}

public sealed record Screenshot(
    ulong Generation,
    ulong Sequence,
    VideoMode Mode,
    DateTimeOffset ReceivedAt,
    byte[] Jpeg);

public sealed record AutomationCapture(
    string Name,
    TimeSpan ScheduledAt,
    TimeSpan ActualAt,
    Screenshot? Screenshot,
    string? Failure);

public sealed record AutomationResult(
    AutomationOutcome Outcome,
    TimeSpan Elapsed,
    ImmutableArray<AutomationCapture> Captures,
    string? Detail);

public enum AutomationEventKind
{
    StartCapture,
    Release,
    Press,
    Capture,
    EndCapture,
}

public sealed record AutomationEvent(
    TimeSpan Offset,
    AutomationEventKind Kind,
    CanonicalDigitalControl? Control,
    string? CaptureName,
    int RequestOrder);

public sealed record CompiledAutomation(
    ImmutableArray<AutomationEvent> Events,
    TimeSpan CompletionAt);