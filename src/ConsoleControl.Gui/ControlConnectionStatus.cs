using ConsoleControl.Client;

namespace ConsoleControl.Gui;

internal static class ControlConnectionStatus
{
    internal static string For(ControlConnectionState state) => state switch
    {
        ControlConnectionState.Ready => "Ready",
        ControlConnectionState.WaitingForBridge => "Controller bridge disconnected; reconnecting...",
        ControlConnectionState.WaitingForControl => "Control is held by another client; waiting...",
        ControlConnectionState.Reconnecting => "Daemon disconnected; reconnecting...",
        ControlConnectionState.Stopped => "Controller forwarding stopped",
        _ => "Connecting to daemon...",
    };
}