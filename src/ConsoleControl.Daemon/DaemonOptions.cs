using System.Net;

namespace ConsoleControl.Daemon;

internal sealed record DaemonOptions(int Port, int VideoPort, string Adapter, bool ShowHelp)
{
    public const string Usage = """
        ConsoleControl daemon

        Usage:
          ConsoleControl.Daemon [options]

        Options:
          --listen <uri>        gRPC loopback address (default: http://127.0.0.1:5041)
          --video-listen <uri>  MJPEG loopback address (default: http://127.0.0.1:5042)
          --adapter <name>      BlueZ adapter used to find controller bridges (default: hci0)
          -h, --help            Show this help
        """;

    public static DaemonOptions Parse(string[] args)
    {
        Uri listen = new("http://127.0.0.1:5041");
        Uri videoListen = new("http://127.0.0.1:5042");
        string adapter = "hci0";
        if (args is ["-h"] or ["--help"])
        {
            return new(listen.Port, videoListen.Port, adapter, true);
        }

        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for '{args[index]}'.");
            }

            switch (args[index])
            {
                case "--listen":
                    listen = new Uri(args[index + 1], UriKind.Absolute);
                    break;
                case "--adapter":
                    adapter = args[index + 1];
                    break;
                case "--video-listen":
                    videoListen = new Uri(args[index + 1], UriKind.Absolute);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }

        if (listen.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(listen.Host, out IPAddress? address) ||
            !IPAddress.IsLoopback(address) ||
            listen.Port is <= 0 or > 65535 ||
            listen.AbsolutePath != "/")
        {
            throw new ArgumentException("--listen must be an HTTP loopback URI with a port and no path.");
        }

        if (videoListen.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(videoListen.Host, out IPAddress? videoAddress) ||
            !IPAddress.IsLoopback(videoAddress) ||
            videoListen.Port is <= 0 or > 65535 ||
            videoListen.AbsolutePath != "/" ||
            videoListen.Port == listen.Port)
        {
            throw new ArgumentException("--video-listen must be a distinct HTTP loopback URI with a port and no path.");
        }

        return new DaemonOptions(listen.Port, videoListen.Port, adapter, false);
    }
}