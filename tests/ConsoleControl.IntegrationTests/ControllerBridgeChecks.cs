using System.Collections.Immutable;

using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;
using ConsoleControl.Daemon;

internal static class ControllerBridgeChecks
{
    public static async Task RunAsync()
    {
        ControllerBridgeId firstId = new("F6:D5:24:56:6F:E2");
        ControllerBridgeId secondId = new("D2:11:22:33:44:55");
        FakeBridgeAdapter adapter = new([
            new(firstId, "ConsoleControl One", false),
            new(secondId, "ConsoleControl Two", false),
        ]);
        MemoryBridgeSelectionStore store = new();
        await using ControllerBridgeRuntime bridges = new(adapter, store);
        await bridges.InitializeAsync(CancellationToken.None);

        ControllerBridgeStatus statusBeforeInventory = await bridges.GetStatusAsync(CancellationToken.None);
        TestAssert.Require(adapter.EnumerationCount == 0
            && statusBeforeInventory.Availability == HardwareAvailability.Unknown,
            "cached controller bridge status unexpectedly scanned Bluetooth hardware");

        ControllerBridgeInventory initial = await bridges.GetInventoryAsync(CancellationToken.None);
        TestAssert.Require(initial.Revision == 0 && initial.SelectedBridgeId is null
            && initial.Bridges.Length == 2
            && initial.State == ControllerBridgeState.SelectionRequired,
            "initial bridge inventory did not require an explicit selection");

        ControllerBridgeSelection selected = await bridges.SelectAsync(
            firstId, initial.Revision, CancellationToken.None);
        TestAssert.Require(selected.Revision == 1 && selected.BridgeId == firstId
            && store.Selection == firstId,
            "bridge selection was not published and persisted");

        ControllerBridgeSelection repeated = await bridges.SelectAsync(
            firstId, selected.Revision, CancellationToken.None);
        TestAssert.Require(repeated.Revision == selected.Revision && adapter.ConfigureCount == 1,
            "reselecting the current bridge was not idempotent");

        try
        {
            await bridges.SelectAsync(secondId, 0, CancellationToken.None);
            throw new InvalidOperationException("a stale bridge revision was accepted");
        }
        catch (ControllerBridgeSelectionConflictException)
        {
        }

        await using ConsoleRuntime console = new(bridges);
        ClientId owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ControlLease lease = await console.AcquireControlAsync(
            owner, ControlPriority.Automation, CancellationToken.None);
        try
        {
            await console.SelectControllerBridgeAsync(
                secondId, selected.Revision, CancellationToken.None);
            throw new InvalidOperationException("bridge selection changed during an active lease");
        }
        catch (ControllerBridgeControlInUseException)
        {
        }
        await console.TryReleaseControlAsync(owner, lease.Generation, CancellationToken.None);

        ControllerBridgeSelection changed = await console.SelectControllerBridgeAsync(
            secondId, selected.Revision, CancellationToken.None);
        TestAssert.Require(changed.BridgeId == secondId && changed.Revision == 2
            && adapter.SelectedBridgeId == secondId,
            "bridge selection did not change after control was released");
    }

    private sealed class FakeBridgeAdapter(ImmutableArray<ControllerBridge> bridges)
        : IControllerBridgeAdapter
    {
        public ControllerBridgeId? SelectedBridgeId { get; private set; }
        public bool IsConnected { get; private set; }
        public int ConfigureCount { get; private set; }
        public int EnumerationCount { get; private set; }

        public Task<ImmutableArray<ControllerBridge>> EnumerateAsync(
            CancellationToken cancellationToken)
        {
            EnumerationCount++;
            return Task.FromResult(bridges);
        }

        public Task ConfigureAsync(
            ControllerBridgeId? bridgeId,
            CancellationToken cancellationToken)
        {
            SelectedBridgeId = bridgeId;
            IsConnected = false;
            ConfigureCount++;
            return Task.CompletedTask;
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask WriteStateAsync(
            ControllerState state,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryBridgeSelectionStore : IControllerBridgeSelectionStore
    {
        public ControllerBridgeId? Selection { get; private set; }
        public ulong Revision { get; private set; }

        public Task<StoredControllerBridgeSelection> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StoredControllerBridgeSelection(Selection, Revision));

        public Task WriteAsync(
            StoredControllerBridgeSelection selection,
            CancellationToken cancellationToken)
        {
            Selection = selection.BridgeId;
            Revision = selection.Revision;
            return Task.CompletedTask;
        }
    }
}