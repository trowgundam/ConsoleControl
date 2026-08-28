using System.Collections.Immutable;
using System.Text.Json;

using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;

namespace ConsoleControl.Daemon;

internal sealed class ControllerBridgeRuntime(
    IControllerBridgeAdapter adapter,
    IControllerBridgeSelectionStore store) : IControllerOutput
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ControllerBridgeId? _selectedBridgeId;
    private ulong _revision;
    private ImmutableHashSet<ControllerBridgeId> _lastInventory = [];
    private DateTimeOffset? _lastInventoryAt;
    private string? _lastFailure;
    private bool _initialized;
    private bool _disposed;

    public bool IsConnected => adapter.IsConnected;

    public async Task<ControllerBridgeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HardwareAvailability availability = _selectedBridgeId is null || _lastInventoryAt is null
                ? HardwareAvailability.Unknown
                : _lastInventory.Contains(_selectedBridgeId.Value)
                    ? HardwareAvailability.Available
                    : HardwareAvailability.Unavailable;
            ControllerOutputConnection output = _selectedBridgeId is null
                ? ControllerOutputConnection.NotConfigured
                : adapter.IsConnected
                    ? ControllerOutputConnection.Connected
                    : _lastFailure is null
                        ? ControllerOutputConnection.DisconnectedUntilInput
                        : ControllerOutputConnection.Faulted;
            string detail = _selectedBridgeId is null
                ? "No controller bridge is selected."
                : _lastFailure ?? (adapter.IsConnected
                    ? "The daemon controller-output session is connected."
                    : "The selected bridge will connect when controller input begins.");
            return new(_selectedBridgeId, availability, output, detail, _lastInventoryAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }
            StoredControllerBridgeSelection stored = await store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            _selectedBridgeId = stored.BridgeId;
            _revision = stored.Revision;
            if (_selectedBridgeId is { } selected)
            {
                await adapter.ConfigureAsync(selected, cancellationToken).ConfigureAwait(false);
            }
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ControllerBridgeInventory> GetInventoryAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<ControllerBridge> available = await EnumerateAsync(cancellationToken)
            .ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastInventory = available.Select(bridge => bridge.Id).ToImmutableHashSet();
            _lastInventoryAt = DateTimeOffset.UtcNow;
            _lastFailure = null;
            return Inventory(available);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ControllerBridgeSelection> SelectAsync(
        ControllerBridgeId bridgeId,
        ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<ControllerBridge> available = await EnumerateAsync(cancellationToken)
            .ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_revision != expectedRevision)
            {
                throw new ControllerBridgeSelectionConflictException(
                    $"Controller bridge revision {expectedRevision} is stale; current revision is {_revision}. Refresh the bridge inventory and retry.");
            }
            if (!available.Any(bridge => bridge.Id == bridgeId))
            {
                throw new ArgumentException(
                    $"Controller bridge '{bridgeId}' is not in the current compatible bridge inventory.",
                    nameof(bridgeId));
            }
            if (_selectedBridgeId == bridgeId)
            {
                return Selection(available);
            }

            ControllerBridgeId? previous = _selectedBridgeId;
            await adapter.ConfigureAsync(bridgeId, cancellationToken).ConfigureAwait(false);
            ulong revision = checked(_revision + 1);
            try
            {
                await store.WriteAsync(new(bridgeId, revision), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await adapter.ConfigureAsync(previous, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            _selectedBridgeId = bridgeId;
            _revision = revision;
            return Selection(available);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (_selectedBridgeId is null)
        {
            throw new InvalidOperationException(
                "No controller bridge is selected. Enumerate and select a controller bridge before requesting input.");
        }
        try
        {
            await adapter.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _lastFailure = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _lastFailure = exception.Message;
            throw;
        }
    }

    public ValueTask WriteStateAsync(
        ControllerState state,
        CancellationToken cancellationToken) => adapter.WriteStateAsync(state, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await adapter.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RecordInventoryFailureAsync(string detail)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _lastFailure = detail;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImmutableArray<ControllerBridge>> EnumerateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await adapter.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordInventoryFailureAsync(exception.Message).ConfigureAwait(false);
            throw new ConsoleOperationException(
                ConsoleFailureCode.ControllerBridgeInventoryFailed,
                $"Controller bridge inventory failed: {exception.Message}",
                retryable: true,
                exception);
        }
    }

    private ControllerBridgeInventory Inventory(ImmutableArray<ControllerBridge> available)
    {
        ControllerBridgeState state = CurrentState(available);
        return new(available, _selectedBridgeId, _revision, state, Status(state));
    }

    private ControllerBridgeSelection Selection(ImmutableArray<ControllerBridge> available)
    {
        ControllerBridgeState state = CurrentState(available);
        return new(_selectedBridgeId!.Value, _revision, state, Status(state));
    }

    private ControllerBridgeState CurrentState(ImmutableArray<ControllerBridge> available)
    {
        if (_selectedBridgeId is null)
        {
            return ControllerBridgeState.SelectionRequired;
        }
        if (!available.Any(bridge => bridge.Id == _selectedBridgeId))
        {
            return ControllerBridgeState.Unavailable;
        }
        return adapter.IsConnected ? ControllerBridgeState.Ready : ControllerBridgeState.Disconnected;
    }

    private static string Status(ControllerBridgeState state) => state switch
    {
        ControllerBridgeState.SelectionRequired => "No controller bridge selected",
        ControllerBridgeState.Unavailable => "The selected controller bridge is unavailable",
        ControllerBridgeState.Disconnected => "Controller bridge selected; connects when input begins",
        ControllerBridgeState.Ready => "Controller bridge connected",
        _ => "Controller bridge faulted",
    };
}

internal interface IControllerBridgeSelectionStore
{
    Task<StoredControllerBridgeSelection> ReadAsync(CancellationToken cancellationToken);

    Task WriteAsync(
        StoredControllerBridgeSelection selection,
        CancellationToken cancellationToken);
}

internal sealed record StoredControllerBridgeSelection(
    ControllerBridgeId? BridgeId,
    ulong Revision);

internal sealed class ControllerBridgeSelectionStore(string? path = null)
    : IControllerBridgeSelectionStore
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ConsoleControl",
        "controller-bridge-selection.json");

    public async Task<StoredControllerBridgeSelection> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new(null, 0);
        }
        await using FileStream stream = File.OpenRead(_path);
        StoredSelection? stored = await JsonSerializer.DeserializeAsync<StoredSelection>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return stored is null
            ? new(null, 0)
            : new(
                string.IsNullOrWhiteSpace(stored.BridgeId) ? null : new(stored.BridgeId),
                stored.Revision);
    }

    public async Task WriteAsync(
        StoredControllerBridgeSelection selection,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        Directory.CreateDirectory(directory!);
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(
                stream,
                new StoredSelection(1, selection.BridgeId?.Value, selection.Revision),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record StoredSelection(int SchemaVersion, string? BridgeId, ulong Revision);
}

internal sealed class ControllerBridgeSelectionConflictException(string message) : Exception(message);

internal sealed class ControllerBridgeControlInUseException(string message) : Exception(message);