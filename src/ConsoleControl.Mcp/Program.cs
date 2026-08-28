using ConsoleControl.Client;
using ConsoleControl.Mcp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args is ["-h"] or ["--help"])
{
    Console.WriteLine("""
        ConsoleControl MCP server

        Usage:
          ConsoleControl.Mcp [--daemon <uri>]

        Options:
          --daemon <uri>  ConsoleControl daemon loopback address (default: http://127.0.0.1:5041)
          -h, --help      Show this help

        The daemon is a separate process and must be running before tools are used.
        """);
    return;
}

Uri daemonUri = ParseDaemonUri(args);
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IConsoleSession>(_ => GrpcConsoleSession.Connect(daemonUri));
builder.Services.AddSingleton<AutomationControl>();
builder.Services.AddSingleton<ScreenshotLibrary>();
builder.Services.AddMcpServer(options => options.ServerInstructions = McpInstructions.Text)
    .WithStdioServerTransport()
    .WithTools<ConsoleTools>();

await builder.Build().RunAsync();

static Uri ParseDaemonUri(string[] arguments)
{
    if (arguments.Length is not 0 and not 2 ||
        arguments.Length == 2 && arguments[0] != "--daemon")
    {
        throw new ArgumentException("Usage: ConsoleControl.Mcp [--daemon <HTTP loopback URI>]");
    }
    int index = Array.IndexOf(arguments, "--daemon");
    string value = index >= 0 && index + 1 < arguments.Length
        ? arguments[index + 1]
        : "http://127.0.0.1:5041";
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
        uri.Scheme != Uri.UriSchemeHttp ||
        !uri.IsLoopback)
    {
        throw new ArgumentException("--daemon must be an HTTP loopback URI.");
    }
    return uri;
}