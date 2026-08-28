using System.Collections.Immutable;

using ConsoleControl.Core;

namespace ConsoleControl.Controller.Bluetooth;

public interface IControllerBridgeAdapter : IControllerOutput
{
    ControllerBridgeId? SelectedBridgeId { get; }

    Task<ImmutableArray<ControllerBridge>> EnumerateAsync(CancellationToken cancellationToken);

    Task ConfigureAsync(
        ControllerBridgeId? bridgeId,
        CancellationToken cancellationToken);
}