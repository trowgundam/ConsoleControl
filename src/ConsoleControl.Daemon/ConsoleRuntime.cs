using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;

namespace ConsoleControl.Daemon;

internal sealed class ConsoleRuntime(IControllerOutput controllerOutput) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControlLease? _activeLease;
    private ulong _nextGeneration = 1;
    private bool _disposed;

    public bool BridgeConnected => controllerOutput.IsConnected;

    public bool ControlAvailable => _activeLease is null;

    public async Task<ControlLease> AcquireControlAsync(
        ClientId client,
        ControlPriority priority,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeLease is not null)
            {
                throw new ControlConflictException("Another client already controls the console.");
            }

            ControlLease lease = new(client, new LeaseGeneration(_nextGeneration++), priority);
            _activeLease = lease;
            return lease;
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
            await controllerOutput.WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseControlAsync(
        ClientId client,
        LeaseGeneration generation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireCurrentLease(client, generation);
            await NeutralizeLockedAsync(cancellationToken).ConfigureAwait(false);
            _activeLease = null;
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

    private async ValueTask NeutralizeLockedAsync(CancellationToken cancellationToken)
    {
        if (!controllerOutput.IsConnected)
        {
            return;
        }

        await controllerOutput.WriteStateAsync(ControllerState.Neutral, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed class ControlConflictException(string message) : Exception(message);

internal sealed class StaleControlLeaseException(string message) : Exception(message);
