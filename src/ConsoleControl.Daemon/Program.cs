using System.Net;
using System.Text;

using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;
using ConsoleControl.Daemon;
using ConsoleControl.Video.FFmpeg;

using Microsoft.AspNetCore.Server.Kestrel.Core;

DaemonOptions options = DaemonOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(DaemonOptions.Usage);
    return;
}
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Grpc.AspNetCore.Server.ServerCallHandler", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, options.Port, listen =>
        listen.Protocols = HttpProtocols.Http2);
    kestrel.Listen(IPAddress.Loopback, options.VideoPort, listen =>
        listen.Protocols = HttpProtocols.Http1);
});
builder.Services.AddGrpc();

BluezControllerOutput controllerAdapter = new(options.Adapter);
ControllerBridgeRuntime controllerOutput = new(controllerAdapter, new ControllerBridgeSelectionStore());
builder.Services.AddSingleton(controllerOutput);
builder.Services.AddSingleton<IControllerOutput>(controllerOutput);
builder.Services.AddSingleton<ConsoleRuntime>();
builder.Services.AddSingleton<InputProfileStore>();
builder.Services.AddSingleton<IVideoCaptureAdapter, FfmpegVideoCaptureAdapter>();
builder.Services.AddSingleton<VideoSelectionStore>();
builder.Services.AddSingleton(new VideoStreamAddress(
    new Uri($"http://127.0.0.1:{options.VideoPort}/video/live.mjpeg")));
builder.Services.AddSingleton<VideoRuntime>();
builder.Services.AddSingleton<IScreenshotSource>(services =>
    services.GetRequiredService<VideoRuntime>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AutomationRuntime>();

WebApplication app = builder.Build();
app.MapGrpcService<ConsoleControlGrpcService>();
app.MapGet("/video/live.mjpeg", async (HttpContext context, VideoRuntime video) =>
{
    const string boundary = "consolecontrol-frame";
    context.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
    context.Response.Headers.CacheControl = "no-store, no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        VideoFrameSubscription subscription = video.Subscribe();
        while (!context.RequestAborted.IsCancellationRequested)
        {
            EncodedVideoFrame frame = await subscription.WaitForNextAsync(context.RequestAborted);
            byte[] header = Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Jpeg.Length}\r\n\r\n");
            await context.Response.Body.WriteAsync(header, context.RequestAborted);
            await context.Response.Body.WriteAsync(frame.Jpeg, context.RequestAborted);
            await context.Response.Body.WriteAsync("\r\n"u8.ToArray(), context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (VideoStreamInterruptedException)
    {
        context.Abort();
    }
});
app.MapGet("/", () => "ConsoleControl daemon requires a gRPC client.");
await app.Services.GetRequiredService<VideoRuntime>().InitializeAsync(CancellationToken.None);
await controllerOutput.InitializeAsync(CancellationToken.None);
await app.RunAsync();