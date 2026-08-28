using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;

namespace ConsoleControl.Daemon;

internal sealed class ConsoleRuntime(IControllerOutput controllerOutput) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControlLease? _activeLease;
    private CancellationTokenSource? _leaseRevoked;
    private volatile PendingAutomationRequest? _pendingAutomation;
    private ulong _nextGeneration = 1;
    private bool _disposed;

    public bool BridgeConnected => controllerOutput.IsConnected;

    public bool ControlAvailable => _activeLease is null;

    public ControlRequestInfo? PendingControlRequest => _pendingAutomation?.Info;

    public async Task<ControlOwner> GetControlOwnerAsync(
        ClientId client,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeLease is null)
            {
                return ControlOwner.None;
            }
            if (_activeLease.Owner == client)
            {
                return ControlOwner.ThisClient;
            }
            return _activeLease.Priority == ControlPriority.InteractiveUser
                ? ControlOwner.InteractiveClient
                : ControlOwner.AutomationClient;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<ControllerBridgeInventory> GetControllerBridgeInventoryAsync(
        CancellationToken cancellationToken) => ControllerBridges.GetInventoryAsync(cancellationToken);

    public Task<ControllerBridgeStatus> GetControllerBridgeStatusAsync(
        CancellationToken cancellationToken) => controllerOutput is ControllerBridgeRuntime bridges
            ? bridges.GetStatusAsync(cancellationToken)
            : Task.FromResult(new ControllerBridgeStatus(
                null,
                HardwareAvailability.Unknown,
                controllerOutput.IsConnected
                    ? ControllerOutputConnection.Connected
                    : ControllerOutputConnection.NotConfigured,
                "This controller output adapter does not expose bridge selection status.",
                null));

    public async Task<ControllerBridgeSelection> SelectControllerBridgeAsync(
        ControllerBridgeId bridgeId,
        ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeLease is not null)
            {
                throw new ControllerBridgeControlInUseException(
                    "A controller bridge cannot be changed while a client has control. Release control, refresh the bridge inventory, and retry.");
            }
            return await ControllerBridges.SelectAsync(
                bridgeId,
                expectedRevision,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ControlLease> AcquireControlAsync(
        ClientId client,
        ControlPriority priority,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeLease is { } active)
            {
                if (priority != ControlPriority.InteractiveUser ||
                    active.Priority != ControlPriority.Automation)
                {
                    throw new ControlConflictException("Another client already controls the console.");
                }

                await NeutralizeLockedAsync(cancellationToken).ConfigureAwait(false);
                _activeLease = null;
                CancellationTokenSource revoked = _leaseRevoked!;
                _leaseRevoked = null;
                revoked.Cancel();
                revoked.Dispose();
            }

            return GrantLocked(client, priority);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ControlLease> RequestAutomationControlAsync(
        ClientId client,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || System.Text.Encoding.UTF8.GetByteCount(reason) > 256)
        {
            throw new ArgumentException(
                "A control request reason must contain 1 to 256 UTF-8 bytes.", nameof(reason));
        }

        PendingAutomationRequest pending;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeLease is null)
            {
                return GrantLocked(client, ControlPriority.Automation);
            }
            if (_activeLease.Priority != ControlPriority.InteractiveUser)
            {
                throw new ControlConflictException("Another automation client already controls the console.");
            }
            if (_pendingAutomation is not null)
            {
                throw new ControlConflictException("Another automation client is already requesting control.");
            }

            pending = new(
                new(Guid.NewGuid(), reason),
                client,
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pendingAutomation = pending;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            bool removed = false;
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_pendingAutomation, pending))
                {
                    _pendingAutomation = null;
                    removed = true;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (!removed && pending.Completion.Task.IsCompletedSuccessfully)
            {
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            if (exception is TimeoutException)
            {
                throw new ControlRequestTimedOutException(
                    "The interactive user did not answer the control request within 30 seconds.");
            }
            throw;
        }
    }

    public async Task<bool> DeclineControlRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingAutomation is not { } pending || pending.Info.Id != requestId)
            {
                return false;
            }
            _pendingAutomation = null;
            pending.Completion.TrySetException(new ControlRequestDeclinedException(
                "The interactive user declined the control request."));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CancellationToken> GetRevocationTokenAsync(
        ClientId client,
        LeaseGeneration generation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireCurrentLease(client, generation);
            return _leaseRevoked!.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SetControllerStateAsync(
        ClientId client,
        LeaseGeneration generation,
        ControllerState state,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireCurrentLease(client, generation);
            if (!controllerOutput.IsConnected)
            {
                await controllerOutput.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            await controllerOutput.WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> TryReleaseControlAsync(
        ClientId client,
        LeaseGeneration generation,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeLease is not { } lease || lease.Owner != client || lease.Generation != generation)
            {
                return false;
            }

            try
            {
                await NeutralizeLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _activeLease = null;
                _leaseRevoked?.Cancel();
                _leaseRevoked?.Dispose();
                _leaseRevoked = null;
                if (lease.Priority == ControlPriority.InteractiveUser &&
                    _pendingAutomation is { } pending)
                {
                    _pendingAutomation = null;
                    pending.Completion.TrySetResult(
                        GrantLocked(pending.Client, ControlPriority.Automation));
                }
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await NeutralizeLockedAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Firmware independently returns to neutral after its 250 ms BLE timeout.
            }

            _activeLease = null;
            _leaseRevoked?.Cancel();
            _leaseRevoked?.Dispose();
            _leaseRevoked = null;
            _pendingAutomation?.Completion.TrySetException(
                new ObjectDisposedException(nameof(ConsoleRuntime)));
            _pendingAutomation = null;
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            await controllerOutput.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RequireCurrentLease(ClientId client, LeaseGeneration generation)
    {
        if (_activeLease is not { } lease ||
            lease.Owner != client ||
            lease.Generation != generation)
        {
            throw new StaleControlLeaseException("The control lease is missing or no longer current.");
        }
    }

    private ControllerBridgeRuntime ControllerBridges =>
        controllerOutput as ControllerBridgeRuntime
        ?? throw new InvalidOperationException("Controller bridge selection is unavailable for this output adapter.");

    private async ValueTask NeutralizeLockedAsync(CancellationToken cancellationToken)
    {
        if (!controllerOutput.IsConnected)
        {
            return;
        }

        await controllerOutput.WriteStateAsync(ControllerState.Neutral, cancellationToken)
            .ConfigureAwait(false);
    }

    private ControlLease GrantLocked(ClientId client, ControlPriority priority)
    {
        ControlLease lease = new(client, new LeaseGeneration(_nextGeneration++), priority);
        _activeLease = lease;
        _leaseRevoked = new();
        return lease;
    }
}

internal sealed class ControlConflictException(string message) : Exception(message);

internal sealed class StaleControlLeaseException(string message) : Exception(message);

internal sealed class ControlRequestDeclinedException(string message) : Exception(message);

internal sealed class ControlRequestTimedOutException(string message) : Exception(message);

internal sealed record ControlRequestInfo(Guid Id, string Reason);

internal sealed record PendingAutomationRequest(
    ControlRequestInfo Info,
    ClientId Client,
    TaskCompletionSource<ControlLease> Completion);