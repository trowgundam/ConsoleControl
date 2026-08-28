using ConsoleControl.Core;

namespace ConsoleControl.Controller.Bluetooth;

public interface IControllerOutput : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    ValueTask WriteStateAsync(
        ControllerState state,
        CancellationToken cancellationToken);
}
