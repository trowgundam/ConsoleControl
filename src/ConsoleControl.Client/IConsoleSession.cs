using ConsoleControl.Core;

namespace ConsoleControl.Client;

public sealed record ConsoleStatus(
    bool BridgeConnected,
    bool ControlAvailable,
    string Detail,
    PendingControlRequest? PendingControlRequest);

public sealed record PendingControlRequest(Guid Id, string Reason);

public interface IConsoleSession : IAsyncDisposable
{
    Task<ConsoleStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<InputConfiguration> GetInputConfigurationAsync(CancellationToken cancellationToken);

    Task<InputConfiguration> SaveInputProfileAsync(
        InputProfile profile,
        ulong expectedRevision,
        CancellationToken cancellationToken);

    Task<VideoInventory> GetVideoInventoryAsync(CancellationToken cancellationToken);

    Task<VideoSelection> SelectVideoSourceAsync(
        VideoSourceId sourceId,
        ulong expectedRevision,
        CancellationToken cancellationToken);

    Task<Screenshot> GetScreenshotAsync(CancellationToken cancellationToken);

    Task<IAutomationSession> RequestAutomationControlAsync(
        string reason,
        CancellationToken cancellationToken);

    Task<bool> DeclineControlRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken);

    Task<IControlSession> TakeControlAsync(
        ControlPriority priority,
        CancellationToken cancellationToken);
}

public interface IAutomationSession : IAsyncDisposable
{
    Task<AutomationResult> RunAsync(
        AutomationSequence sequence,
        CancellationToken cancellationToken);
}

public interface IControlSession : IAsyncDisposable
{
    ControlConnectionState ConnectionState { get; }

    event EventHandler<ControlConnectionState>? ConnectionStateChanged;

    Task SetStateAsync(
        ControllerState state,
        CancellationToken cancellationToken);
}

public enum ControlConnectionState
{
    Connecting,
    Ready,
    WaitingForBridge,
    WaitingForControl,
    Reconnecting,
    Stopped,
}