using Avalonia;

namespace ConsoleControl.Gui;

internal static class Program
{
    public static Uri DaemonUri { get; private set; } = new("http://127.0.0.1:5041");

    [STAThread]
    public static void Main(string[] args)
    {
        DaemonUri = ParseDaemonUri(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static Uri ParseDaemonUri(string[] args)
    {
        Uri daemon = DaemonUri;
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] != "--daemon")
            {
                throw new ArgumentException("Usage: ConsoleControl.Gui [--daemon http://127.0.0.1:5041]");
            }

            daemon = new Uri(args[index + 1], UriKind.Absolute);
        }

        if (daemon.Scheme != Uri.UriSchemeHttp || !daemon.IsLoopback)
        {
            throw new ArgumentException("--daemon must be an HTTP loopback URI.");
        }

        return daemon;
    }
}
