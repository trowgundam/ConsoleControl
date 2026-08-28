using System.Collections.Immutable;

using ConsoleControl.Core;

namespace ConsoleControl.Daemon;

internal sealed class AutomationRuntime(
    ConsoleRuntime controls,
    IScreenshotSource video,
    TimeProvider timeProvider)
{
    private int _running;

    public async Task<AutomationResult> ExecuteAsync(
        ClientId client,
        LeaseGeneration generation,
        AutomationSequence sequence,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new AutomationBusyException("An automation sequence is already running.");
        }

        try
        {
            return await ExecuteCoreAsync(
                client, generation, sequence, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<AutomationResult> ExecuteCoreAsync(
        ClientId client,
        LeaseGeneration generation,
        AutomationSequence sequence,
        CancellationToken cancellationToken)
    {
        CompiledAutomation plan = sequence.Compile();
        CancellationToken revoked = await controls.GetRevocationTokenAsync(
            client, generation, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource execution =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, revoked);
        CancellationToken token = execution.Token;
        long startedAt = timeProvider.GetTimestamp();
        List<AutomationCapture> captures = [];
        Dictionary<CanonicalDigitalControl, int> active = [];

        try
        {
            foreach (IGrouping<TimeSpan, AutomationEvent> group in
                     plan.Events.Where(item => item.Kind != AutomationEventKind.EndCapture)
                         .GroupBy(item => item.Offset))
            {
                await DelayUntilAsync(startedAt, group.Key, token).ConfigureAwait(false);

                foreach (AutomationEvent item in group.Where(item =>
                             item.Kind == AutomationEventKind.StartCapture))
                {
                    if (!TryCapture(item, startedAt, captures, out string? failure))
                    {
                        await TryNeutralizeAsync(client, generation).ConfigureAwait(false);
                        return Result(AutomationOutcome.ScreenshotFailed, startedAt, captures, failure);
                    }
                }

                bool stateChanged = false;
                foreach (AutomationEvent item in group)
                {
                    if (item.Kind == AutomationEventKind.Release)
                    {
                        Release(active, item.Control!.Value);
                        stateChanged = true;
                    }
                    else if (item.Kind == AutomationEventKind.Press)
                    {
                        active[item.Control!.Value] = active.GetValueOrDefault(item.Control.Value) + 1;
                        stateChanged = true;
                    }
                }

                if (stateChanged)
                {
                    await controls.SetControllerStateAsync(
                        client, generation, Compose(active), token).ConfigureAwait(false);
                }

                foreach (AutomationEvent item in group.Where(item =>
                             item.Kind == AutomationEventKind.Capture))
                {
                    if (!TryCapture(item, startedAt, captures, out string? failure))
                    {
                        await TryNeutralizeAsync(client, generation).ConfigureAwait(false);
                        return Result(AutomationOutcome.ScreenshotFailed, startedAt, captures, failure);
                    }
                }
            }

            await DelayUntilAsync(startedAt, plan.CompletionAt, token).ConfigureAwait(false);
            await controls.SetControllerStateAsync(
                client, generation, ControllerState.Neutral, token).ConfigureAwait(false);

            AutomationEvent? end = plan.Events.FirstOrDefault(item =>
                item.Kind == AutomationEventKind.EndCapture);
            if (end is not null && !TryCapture(end, startedAt, captures, out string? endFailure))
            {
                return Result(AutomationOutcome.ScreenshotFailed, startedAt, captures, endFailure);
            }

            return Result(AutomationOutcome.Completed, startedAt, captures, null);
        }
        catch (OperationCanceledException) when (revoked.IsCancellationRequested)
        {
            await TryNeutralizeAsync(client, generation).ConfigureAwait(false);
            return Result(AutomationOutcome.Preempted, startedAt, captures,
                "The interactive client took control.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryNeutralizeAsync(client, generation).ConfigureAwait(false);
            return Result(AutomationOutcome.Cancelled, startedAt, captures,
                "Sequence execution was cancelled.");
        }
        catch (StaleControlLeaseException)
        {
            return Result(AutomationOutcome.Preempted, startedAt, captures,
                "The automation control lease is no longer current.");
        }
        catch (Exception exception) when (!controls.BridgeConnected)
        {
            await TryNeutralizeAsync(client, generation).ConfigureAwait(false);
            return Result(AutomationOutcome.BridgeUnavailable, startedAt, captures, exception.Message);
        }
    }

    private bool TryCapture(
        AutomationEvent item,
        long startedAt,
        List<AutomationCapture> captures,
        out string? failure)
    {
        TimeSpan actual = timeProvider.GetElapsedTime(startedAt);
        try
        {
            Screenshot screenshot = video.CaptureLatest();
            if (captures.Sum(capture => capture.Screenshot?.Jpeg.Length ?? 0) + screenshot.Jpeg.Length >
                AutomationLimits.MaxAggregateCaptureBytes)
            {
                throw new ScreenshotUnavailableException(
                    $"Sequence screenshots exceed {AutomationLimits.MaxAggregateCaptureBytes / 1024 / 1024} MiB.");
            }
            captures.Add(new(item.CaptureName!, item.Offset, actual, screenshot, null));
            failure = null;
            return true;
        }
        catch (ScreenshotUnavailableException exception)
        {
            failure = exception.Message;
            captures.Add(new(item.CaptureName!, item.Offset, actual, null, failure));
            return false;
        }
    }

    private async Task DelayUntilAsync(
        long startedAt,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        TimeSpan remaining = offset - timeProvider.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryNeutralizeAsync(ClientId client, LeaseGeneration generation)
    {
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(1));
        try
        {
            await controls.SetControllerStateAsync(
                client, generation, ControllerState.Neutral, cleanup.Token).ConfigureAwait(false);
        }
        catch
        {
            // A takeover already neutralized the old generation, or the firmware timeout will.
        }
    }

    private AutomationResult Result(
        AutomationOutcome outcome,
        long startedAt,
        List<AutomationCapture> captures,
        string? detail) =>
        new(outcome, timeProvider.GetElapsedTime(startedAt), captures.ToImmutableArray(), detail);

    private static void Release(
        Dictionary<CanonicalDigitalControl, int> active,
        CanonicalDigitalControl control)
    {
        int remaining = active[control] - 1;
        if (remaining == 0)
        {
            active.Remove(control);
        }
        else
        {
            active[control] = remaining;
        }
    }

    private static ControllerState Compose(Dictionary<CanonicalDigitalControl, int> active)
    {
        GameButtons buttons = GameButtons.None;
        foreach (CanonicalDigitalControl control in active.Keys)
        {
            buttons |= control switch
            {
                CanonicalDigitalControl.A => GameButtons.A,
                CanonicalDigitalControl.B => GameButtons.B,
                CanonicalDigitalControl.X => GameButtons.X,
                CanonicalDigitalControl.Y => GameButtons.Y,
                CanonicalDigitalControl.LeftShoulder => GameButtons.LeftShoulder,
                CanonicalDigitalControl.RightShoulder => GameButtons.RightShoulder,
                CanonicalDigitalControl.LeftTrigger => GameButtons.LeftTrigger,
                CanonicalDigitalControl.RightTrigger => GameButtons.RightTrigger,
                CanonicalDigitalControl.Minus => GameButtons.Minus,
                CanonicalDigitalControl.Plus => GameButtons.Plus,
                CanonicalDigitalControl.Home => GameButtons.Home,
                CanonicalDigitalControl.Capture => GameButtons.Capture,
                _ => GameButtons.None,
            };
        }

        int vertical = (active.ContainsKey(CanonicalDigitalControl.DPadDown) ? 1 : 0) -
                       (active.ContainsKey(CanonicalDigitalControl.DPadUp) ? 1 : 0);
        int horizontal = (active.ContainsKey(CanonicalDigitalControl.DPadRight) ? 1 : 0) -
                         (active.ContainsKey(CanonicalDigitalControl.DPadLeft) ? 1 : 0);
        HatPosition dpad = (horizontal, vertical) switch
        {
            (0, -1) => HatPosition.Up,
            (1, -1) => HatPosition.UpRight,
            (1, 0) => HatPosition.Right,
            (1, 1) => HatPosition.DownRight,
            (0, 1) => HatPosition.Down,
            (-1, 1) => HatPosition.DownLeft,
            (-1, 0) => HatPosition.Left,
            (-1, -1) => HatPosition.UpLeft,
            _ => HatPosition.Neutral,
        };
        return ControllerState.Neutral with { Buttons = buttons, DPad = dpad };
    }
}

internal sealed class AutomationBusyException(string message) : Exception(message);

internal interface IScreenshotSource
{
    Screenshot CaptureLatest();
}