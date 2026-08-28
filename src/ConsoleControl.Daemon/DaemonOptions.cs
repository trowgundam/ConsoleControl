using System.Net;

namespace ConsoleControl.Daemon;

internal sealed record DaemonOptions(int Port, string Adapter, string BridgeAddress)
{
    public static DaemonOptions Parse(string[] args)
    {
        Uri listen = new("http://127.0.0.1:5041");
        string adapter = "hci0";
        string bridge = "F6:D5:24:56:6F:E2";

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
                case "--bridge":
                    bridge = args[index + 1];
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

        return new DaemonOptions(listen.Port, adapter, bridge);
    }
}
