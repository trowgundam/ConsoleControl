using System.Diagnostics;
using System.Net;
using System.Collections.Immutable;
using ConsoleControl.Client;
using ConsoleControl.Contracts;
using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;
using ConsoleControl.Daemon;
using ConsoleControl.Video.FFmpeg;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class StreamingTransportChecks
{
    public static async Task RunAsync()
    {
        string profilePath = Path.Combine(Path.GetTempPath(), $"consolecontrol-stream-{Guid.NewGuid():N}.json");
        RecordingOutput output = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<IControllerOutput>(output);
        builder.Services.AddSingleton<ConsoleRuntime>();
        builder.Services.AddSingleton(new InputProfileStore(profilePath));
        builder.Services.AddSingleton<IVideoCaptureAdapter, EmptyVideoAdapter>();
        builder.Services.AddSingleton(new VideoSelectionStore(profilePath + ".video"));
        builder.Services.AddSingleton(new VideoStreamAddress(new Uri("http://127.0.0.1/video.mjpeg")));
        builder.Services.AddSingleton<VideoRuntime>();

        await using WebApplication app = builder.Build();
        app.MapGrpcService<ConsoleControlGrpcService>();
        await app.StartAsync();
        Uri address = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        try
        {
            await VerifyCoalescingAndNeutralBarrierAsync(address, output);
            await VerifyAbruptCancellationAsync(address, output, app.Services.GetRequiredService<ConsoleRuntime>());
            await VerifyInactivityCleanupAsync(address, output, app.Services.GetRequiredService<ConsoleRuntime>());
        }
        finally
        {
            await app.StopAsync();
            if (File.Exists(profilePath))
            {
                File.Delete(profilePath);
            }
        }
    }

    private static async Task VerifyCoalescingAndNeutralBarrierAsync(Uri address, RecordingOutput output)
    {
        await using GrpcConsoleSession session = GrpcConsoleSession.Connect(address);
        await using IControlSession control = await session.TakeControlAsync(
            ConsoleControl.Core.ControlPriority.InteractiveUser,
            CancellationToken.None);

        int beforeBurst = output.Count;
        ControllerState latest = ControllerState.Neutral;
        for (int index = 1; index <= 200; index++)
        {
            latest = ControllerState.Neutral with
            {
                LeftStick = new StickPosition((byte)index, ControllerState.Neutral.LeftStick.Y),
            };
            await control.SetStateAsync(latest, CancellationToken.None);
        }

        await WaitUntilAsync(() => output.Last == latest, TimeSpan.FromSeconds(1));
        Require(output.Count - beforeBurst <= 10,
            "a burst of 200 states was not coalesced before reaching the bridge");

        Stopwatch neutralLatency = Stopwatch.StartNew();
        await control.SetStateAsync(ControllerState.Neutral, CancellationToken.None);
        await WaitUntilAsync(() => output.Last == ControllerState.Neutral, TimeSpan.FromMilliseconds(150));
        neutralLatency.Stop();
        Require(neutralLatency.Elapsed < TimeSpan.FromMilliseconds(150),
            "a neutral barrier waited for the ordinary state cadence");
    }

    private static async Task VerifyAbruptCancellationAsync(
        Uri address,
        RecordingOutput output,
        ConsoleRuntime runtime)
    {
        using GrpcChannel channel = GrpcChannel.ForAddress(address);
        ConsoleControlService.ConsoleControlServiceClient client = new(channel);
        using CancellationTokenSource streamStop = new();
        using AsyncDuplexStreamingCall<ControlStreamRequest, ControlStreamEvent> call =
            client.Control(cancellationToken: streamStop.Token);
        await call.RequestStream.WriteAsync(OpenRequest(), CancellationToken.None);
        Require(await call.ResponseStream.MoveNext(CancellationToken.None) &&
                call.ResponseStream.Current.BodyCase == ControlStreamEvent.BodyOneofCase.Granted,
            "the daemon did not grant the abrupt-cancellation test stream");

        ControllerState pressed = ControllerState.Neutral with { Buttons = GameButtons.A };
        await call.RequestStream.WriteAsync(StateRequest(pressed), CancellationToken.None);
        await WaitUntilAsync(() => output.Last == pressed, TimeSpan.FromSeconds(1));
        streamStop.Cancel();
        await WaitUntilAsync(() => runtime.ControlAvailable, TimeSpan.FromSeconds(1));
        Require(output.Last == ControllerState.Neutral,
            "abrupt stream cancellation did not neutralize the bridge");
    }

    private static async Task VerifyInactivityCleanupAsync(
        Uri address,
        RecordingOutput output,
        ConsoleRuntime runtime)
    {
        using GrpcChannel channel = GrpcChannel.ForAddress(address);
        ConsoleControlService.ConsoleControlServiceClient client = new(channel);
        using AsyncDuplexStreamingCall<ControlStreamRequest, ControlStreamEvent> call = client.Control();
        await call.RequestStream.WriteAsync(OpenRequest(), CancellationToken.None);
        Require(await call.ResponseStream.MoveNext(CancellationToken.None),
            "the daemon did not grant the inactivity test stream");
        ControllerState pressed = ControllerState.Neutral with { Buttons = GameButtons.B };
        await call.RequestStream.WriteAsync(StateRequest(pressed), CancellationToken.None);
        await WaitUntilAsync(() => output.Last == pressed, TimeSpan.FromSeconds(1));

        await WaitUntilAsync(() => runtime.ControlAvailable, TimeSpan.FromSeconds(2));
        Require(output.Last == ControllerState.Neutral,
            "an inactive stream did not neutralize the bridge");
    }

    private static ControlStreamRequest OpenRequest() => new()
    {
        Open = new OpenControlStream
        {
            ClientId = Guid.NewGuid().ToString("D"),
            Priority = ConsoleControl.Contracts.ControlPriority.InteractiveUser,
        },
    };

    private static ControlStreamRequest StateRequest(ControllerState state) => new()
    {
        State = new ControllerStateMessage
        {
            Buttons = (uint)state.Buttons,
            Dpad = (uint)state.DPad,
            LeftStickX = state.LeftStick.X,
            LeftStickY = state.LeftStick.Y,
            RightStickX = state.RightStick.X,
            RightStickY = state.RightStick.Y,
            LeftTrigger = state.LeftTrigger.Value,
            RightTrigger = state.RightTrigger.Value,
        },
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= timeout)
            {
                throw new InvalidOperationException("Timed out waiting for the streaming transport condition.");
            }
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingOutput : IControllerOutput
    {
        private readonly object _gate = new();
        private readonly List<ControllerState> _states = [];

        public bool IsConnected { get; private set; } = true;

        public int Count
        {
            get { lock (_gate) { return _states.Count; } }
        }

        public ControllerState Last
        {
            get { lock (_gate) { return _states.LastOrDefault(); } }
        }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public ValueTask WriteStateAsync(ControllerState state, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _states.Add(state);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyVideoAdapter : IVideoCaptureAdapter
    {
        public Task<ImmutableArray<VideoSource>> GetSourcesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<VideoSource>.Empty);

        public Task<IVideoCaptureSession> OpenAsync(
            VideoSource source,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The transport check must not open video capture.");
    }
}
