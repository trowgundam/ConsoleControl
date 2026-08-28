using ConsoleControl.Client;
using ConsoleControl.Mcp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Uri daemonUri = ParseDaemonUri(args);
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IConsoleSession>(_ => GrpcConsoleSession.Connect(daemonUri));
builder.Services.AddSingleton<AutomationControl>();
builder.Services.AddSingleton<ScreenshotLibrary>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ConsoleTools>();

await builder.Build().RunAsync();

static Uri ParseDaemonUri(string[] arguments)
{
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