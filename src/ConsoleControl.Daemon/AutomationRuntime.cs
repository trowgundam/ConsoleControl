using System.Collections.Immutable;

using ConsoleControl.Core;

namespace ConsoleControl.Daemon;

internal sealed class AutomationRuntime(
    ConsoleRuntime controls,
    IScreenshotSource video,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan StateRefreshInterval = TimeSpan.FromMilliseconds(50);
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
                await DelayUntilAsync(
                    startedAt, group.Key, client, generation, active, token).ConfigureAwait(false);

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

            await DelayUntilAsync(
                startedAt, plan.CompletionAt, client, generation, active, token).ConfigureAwait(false);
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
        ClientId client,
        LeaseGeneration generation,
        Dictionary<CanonicalDigitalControl, int> active,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining = offset - timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(
                remaining < StateRefreshInterval ? remaining : StateRefreshInterval,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
            if (active.Count != 0 && offset - timeProvider.GetElapsedTime(startedAt) > TimeSpan.Zero)
            {
                await controls.SetControllerStateAsync(
                    client, generation, Compose(active), cancellationToken).ConfigureAwait(false);
            }
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
        => ControllerStateComposer.FromDigitalControls(active.Keys);
}

internal sealed class AutomationBusyException(string message) : Exception(message);

internal interface IScreenshotSource
{
    Screenshot CaptureLatest();
}