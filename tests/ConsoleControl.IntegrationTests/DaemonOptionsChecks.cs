using ConsoleControl.Daemon;

internal static class DaemonOptionsChecks
{
    public static void Run()
    {
        DaemonOptions defaults = DaemonOptions.Parse([]);
        Require(defaults.Port == 5041 && defaults.VideoPort == 5042,
            "default daemon listeners changed unexpectedly");

        RequireRejected(
            ["--listen", "http://0.0.0.0:5041"],
            "the daemon accepted a non-loopback gRPC listener");
        RequireRejected(
            ["--video-listen", "http://localhost:5042"],
            "the daemon accepted a hostname instead of a numeric loopback address");
        RequireRejected(
            ["--listen", "http://127.0.0.2:5041"],
            "the daemon accepted an address it does not bind");
        RequireRejected(
            ["--video-listen", "http://[::1]:5042"],
            "the daemon accepted IPv6 while binding IPv4");
        RequireRejected(
            ["--video-listen", "http://127.0.0.1:5041"],
            "the daemon accepted the gRPC port for video");
    }

    private static void RequireRejected(string[] arguments, string message)
    {
        try
        {
            DaemonOptions.Parse(arguments);
            throw new InvalidOperationException(message);
        }
        catch (ArgumentException)
        {
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}