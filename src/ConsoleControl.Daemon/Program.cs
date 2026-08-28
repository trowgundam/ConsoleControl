using System.Net;
using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Daemon;
using Microsoft.AspNetCore.Server.Kestrel.Core;

DaemonOptions options = DaemonOptions.Parse(args);
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Listen(IPAddress.Loopback, options.Port, listen =>
        listen.Protocols = HttpProtocols.Http2));
builder.Services.AddGrpc();

BluezControllerOutput controllerOutput = new(options.Adapter, options.BridgeAddress);
await controllerOutput.ConnectAsync(CancellationToken.None);
builder.Services.AddSingleton<IControllerOutput>(controllerOutput);
builder.Services.AddSingleton<ConsoleRuntime>();
builder.Services.AddSingleton<InputProfileStore>();

WebApplication app = builder.Build();
app.MapGrpcService<ConsoleControlGrpcService>();
app.MapGet("/", () => "ConsoleControl daemon requires a gRPC client.");
await app.RunAsync();
