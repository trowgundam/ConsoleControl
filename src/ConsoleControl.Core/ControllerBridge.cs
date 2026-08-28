using System.Collections.Immutable;

namespace ConsoleControl.Core;

public readonly record struct ControllerBridgeId
{
    public ControllerBridgeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (System.Text.Encoding.UTF8.GetByteCount(value) > 256)
        {
            throw new ArgumentException("A controller bridge ID cannot exceed 256 UTF-8 bytes.", nameof(value));
        }
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ControllerBridge(
    ControllerBridgeId Id,
    string DisplayName,
    bool BluetoothConnected);

public enum HardwareAvailability
{
    Unknown,
    Available,
    Unavailable,
}

public enum ControllerOutputConnection
{
    NotConfigured,
    DisconnectedUntilInput,
    Connected,
    Faulted,
}

public sealed record ControllerBridgeStatus(
    ControllerBridgeId? SelectedBridgeId,
    HardwareAvailability Availability,
    ControllerOutputConnection OutputConnection,
    string Detail,
    DateTimeOffset? LastInventoryAt);

public enum ControllerBridgeState
{
    SelectionRequired,
    Unavailable,
    Disconnected,
    Ready,
    Faulted,
}

public sealed record ControllerBridgeInventory(
    ImmutableArray<ControllerBridge> Bridges,
    ControllerBridgeId? SelectedBridgeId,
    ulong Revision,
    ControllerBridgeState State,
    string Status);

public sealed record ControllerBridgeSelection(
    ControllerBridgeId BridgeId,
    ulong Revision,
    ControllerBridgeState State,
    string Status);