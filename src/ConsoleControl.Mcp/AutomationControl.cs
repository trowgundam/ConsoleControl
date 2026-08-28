using ConsoleControl.Client;
using ConsoleControl.Core;

using Grpc.Core;

namespace ConsoleControl.Mcp;

internal sealed class AutomationControl(IConsoleSession console) : IAsyncDisposable
{
    private readonly SemaphoreSlim _acquireGate = new(1, 1);
    private IAutomationSession? _control;

    public bool HasControl => Volatile.Read(ref _control) is not null;

    public Task<Screenshot> GetScreenshotAsync(CancellationToken cancellationToken) =>
        console.GetScreenshotAsync(cancellationToken);

    public Task<ConsoleStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        console.GetStatusAsync(cancellationToken);

    public Task<ControllerBridgeInventory> GetControllerBridgesAsync(CancellationToken cancellationToken) =>
        console.GetControllerBridgeInventoryAsync(cancellationToken);

    public Task<ControllerBridgeSelection> SelectControllerBridgeAsync(
        ControllerBridgeId bridgeId,
        ulong revision,
        CancellationToken cancellationToken) =>
        console.SelectControllerBridgeAsync(bridgeId, revision, cancellationToken);

    public Task<VideoInventory> GetVideoSourcesAsync(CancellationToken cancellationToken) =>
        console.GetVideoInventoryAsync(cancellationToken);

    public Task<VideoSelection> SelectVideoSourceAsync(
        VideoSourceId sourceId,
        ulong revision,
        CancellationToken cancellationToken) =>
        console.SelectVideoSourceAsync(sourceId, revision, cancellationToken);

    public async Task<bool> ReleaseControlAsync()
    {
        IAutomationSession? control = Interlocked.Exchange(ref _control, null);
        if (control is null)
        {
            return false;
        }
        await control.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RequestControlAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _control) is not null)
        {
            return false;
        }

        await _acquireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_control is not null)
            {
                return false;
            }
            _control = await console.RequestAutomationControlAsync(reason, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            _acquireGate.Release();
        }
    }

    public async Task<AutomationResult> RunAsync(
        AutomationSequence sequence,
        CancellationToken cancellationToken)
    {
        IAutomationSession control = Volatile.Read(ref _control)
            ?? throw new InvalidOperationException(
                "Automation does not have control. Call console_request_control with a reason first.");
        try
        {
            AutomationResult result = await control.RunAsync(sequence, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == AutomationOutcome.Preempted)
            {
                await ForgetControlAsync(control).ConfigureAwait(false);
            }
            return result;
        }
        catch (RpcException exception) when (
            exception.StatusCode is StatusCode.FailedPrecondition
                or StatusCode.Aborted
                or StatusCode.Unavailable)
        {
            await ForgetControlAsync(control).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IAutomationSession? control = Interlocked.Exchange(ref _control, null);
        if (control is not null)
        {
            await control.DisposeAsync().ConfigureAwait(false);
        }
        await console.DisposeAsync().ConfigureAwait(false);
        _acquireGate.Dispose();
    }

    private async Task ForgetControlAsync(IAutomationSession expected)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _control, null, expected), expected))
        {
            await expected.DisposeAsync().ConfigureAwait(false);
        }
    }
}