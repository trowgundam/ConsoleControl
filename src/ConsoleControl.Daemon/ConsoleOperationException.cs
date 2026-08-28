namespace ConsoleControl.Daemon;

internal enum ConsoleFailureCode
{
    ControllerBridgeInventoryFailed,
    VideoSourceInventoryFailed,
}

internal sealed class ConsoleOperationException(
    ConsoleFailureCode code,
    string message,
    bool retryable,
    Exception? innerException = null) : Exception(message, innerException)
{
    public ConsoleFailureCode Code { get; } = code;

    public bool Retryable { get; } = retryable;
}