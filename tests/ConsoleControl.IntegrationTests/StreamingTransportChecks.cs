using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;

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
        VerifyCadencePolicy();
        string profilePath = Path.Combine(Path.GetTempPath(), $"consolecontrol-stream-{Guid.NewGuid():N}.json");
        RecordingOutput output = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<IControllerOutput>(output);
        builder.Services.AddSingleton<ConsoleRuntime>();
        builder.Services.AddSingleton<IVideoCaptureAdapter, EmptyVideoAdapter>();
        builder.Services.AddSingleton<IVideoSelectionStore>(new VideoSelectionStore(profilePath + ".video"));
        builder.Services.AddSingleton(new VideoStreamAddress(new Uri("http://127.0.0.1/video.mjpeg")));
        builder.Services.AddSingleton<VideoRuntime>();
        builder.Services.AddSingleton<IScreenshotSource>(services =>
            services.GetRequiredService<VideoRuntime>());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AutomationRuntime>();

        await using WebApplication app = builder.Build();
        app.MapGrpcService<ConsoleControlGrpcService>();
        await app.StartAsync();
        Uri address = new(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        try
        {
            await VerifyCoalescingAndNeutralBarrierAsync(address, output);
            await VerifyCancelledStartupCleanupAsync(address);
            await VerifyInteractiveConflictAsync(address);
            await VerifyAbruptCancellationAsync(address, output, app.Services.GetRequiredService<ConsoleRuntime>());
            await VerifyInactivityCleanupAsync(address, output, app.Services.GetRequiredService<ConsoleRuntime>());
            await VerifyAutomationLeaseLifetimeAsync(
                address, app.Services.GetRequiredService<ConsoleRuntime>());
            await VerifyAutomationRevocationVisibilityAsync(address);
            await VerifyControlRequestAsync(address);
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

    private static async Task VerifyAutomationRevocationVisibilityAsync(Uri address)
    {
        await using GrpcConsoleSession agent = GrpcConsoleSession.Connect(address);
        await using GrpcConsoleSession gui = GrpcConsoleSession.Connect(address);
        IAutomationSession automation = await agent.RequestAutomationControlAsync(
            "Verify idle takeover visibility.", CancellationToken.None);
        await using IControlSession interactive = await gui.TakeControlAsync(
            ConsoleControl.Core.ControlPriority.InteractiveUser, CancellationToken.None);

        AutomationSessionEnd ended = await automation.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        TestAssert.Require(ended.Reason == AutomationSessionEndReason.Preempted,
            "automation completion did not report interactive takeover");
        await interactive.DisposeAsync();

        IAutomationSession reacquired = await agent.RequestAutomationControlAsync(
            "Verify reacquisition after takeover.", CancellationToken.None);
        await reacquired.DisposeAsync();
    }

    private static async Task VerifyAutomationLeaseLifetimeAsync(
        Uri address,
        ConsoleRuntime runtime)
    {
        await using GrpcConsoleSession session = GrpcConsoleSession.Connect(address);
        IAutomationSession control = await session.RequestAutomationControlAsync(
            "Verify lease heartbeats.", CancellationToken.None);
        TestAssert.Require(!runtime.ControlAvailable, "automation did not acquire the control lease");
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        TestAssert.Require(!runtime.ControlAvailable, "automation heartbeats did not retain the control lease");
        await control.DisposeAsync();
        await WaitUntilAsync(() => runtime.ControlAvailable, TimeSpan.FromSeconds(1));
    }

    private static async Task VerifyControlRequestAsync(Uri address)
    {
        await using GrpcConsoleSession gui = GrpcConsoleSession.Connect(address);
        await using GrpcConsoleSession agent = GrpcConsoleSession.Connect(address);
        IControlSession guiControl = await gui.TakeControlAsync(
            ConsoleControl.Core.ControlPriority.InteractiveUser,
            CancellationToken.None);

        Task<IAutomationSession> declinedRequest = agent.RequestAutomationControlAsync(
            "Test the visible request reason.", CancellationToken.None);
        PendingControlRequest pending = await WaitForPendingRequestAsync(gui);
        TestAssert.Require(pending.Reason == "Test the visible request reason.",
            "the daemon did not preserve the agent's control request reason");
        TestAssert.Require(await gui.DeclineControlRequestAsync(pending.Id, CancellationToken.None),
            "the GUI client could not decline the pending request");
        try
        {
            await declinedRequest;
            throw new InvalidOperationException("a declined automation request acquired control");
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.PermissionDenied)
        {
        }

        Task<IAutomationSession> approvedRequest = agent.RequestAutomationControlAsync(
            "Continue the integration check.", CancellationToken.None);
        await WaitForPendingRequestAsync(gui);
        await guiControl.DisposeAsync();
        IAutomationSession automation = await approvedRequest.WaitAsync(TimeSpan.FromSeconds(1));
        await automation.DisposeAsync();
    }

    private static async Task<PendingControlRequest> WaitForPendingRequestAsync(
        GrpcConsoleSession session)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1))
        {
            ConsoleStatus status = await session.GetStatusAsync(CancellationToken.None);
            if (status.PendingControlRequest is { } pending)
            {
                return pending;
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("the control request did not appear in daemon status");
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
        TestAssert.Require(output.Count - beforeBurst <= 10,
            "a burst of 200 states was not coalesced before reaching the bridge");

        int beforeHold = output.Count;
        ControllerState held = ControllerState.Neutral with { Buttons = GameButtons.B };
        await control.SetStateAsync(held, CancellationToken.None);
        await WaitUntilAsync(() => output.Count >= beforeHold + 4, TimeSpan.FromSeconds(1));
        TestAssert.Require(output.Since(beforeHold).Take(4).All(state => state == held),
            "a held state was not refreshed continuously without neutral gaps");
        await control.SetStateAsync(ControllerState.Neutral, CancellationToken.None);
        await WaitUntilAsync(() => output.Last == ControllerState.Neutral, TimeSpan.FromSeconds(1));

        int beforeTap = output.Count;
        ControllerState pressed = ControllerState.Neutral with { Buttons = GameButtons.A };
        await control.SetStateAsync(pressed, CancellationToken.None);
        await control.SetStateAsync(ControllerState.Neutral, CancellationToken.None);
        await WaitUntilAsync(
            () => output.ContainsOrderedTransition(beforeTap, pressed, ControllerState.Neutral),
            TimeSpan.FromSeconds(1));
        IReadOnlyList<ControllerState> tapStates = output.Since(beforeTap);
        int pressedIndex = tapStates.ToList().FindIndex(state => state == pressed);
        int releasedIndex = tapStates.ToList().FindIndex(
            pressedIndex + 1,
            state => state == ControllerState.Neutral);
        TestAssert.Require(pressedIndex >= 0 && releasedIndex > pressedIndex,
            "a press and release submitted before the state pump ran were coalesced away");

        await control.SetStateAsync(ControllerState.Neutral, CancellationToken.None);
        await WaitUntilAsync(() => output.Last == ControllerState.Neutral, TimeSpan.FromMilliseconds(150));
    }

    private static void VerifyCadencePolicy()
    {
        TestAssert.Require(GrpcConsoleSession.ControlStateRefreshInterval < TimeSpan.FromMilliseconds(250),
            "the control-state refresh does not arrive before the firmware neutral timeout");
        TestAssert.Require(GrpcConsoleSession.RequiresCadenceDelay(
            hasPending: true,
            nextIsNeutral: false,
            heartbeatDue: false),
            "an ordinary pending state bypassed the 60 Hz cadence");
        TestAssert.Require(!GrpcConsoleSession.RequiresCadenceDelay(
            hasPending: true,
            nextIsNeutral: true,
            heartbeatDue: false),
            "a neutral state at the queue head did not bypass the cadence");
    }

    private static async Task VerifyCancelledStartupCleanupAsync(Uri address)
    {
        await using GrpcConsoleSession session = GrpcConsoleSession.Connect(address);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        try
        {
            await session.TakeControlAsync(
                ConsoleControl.Core.ControlPriority.InteractiveUser,
                cancelled.Token);
            throw new InvalidOperationException("cancelled control startup unexpectedly succeeded");
        }
        catch (OperationCanceledException)
        {
        }

        await using IControlSession recovered = await session.TakeControlAsync(
            ConsoleControl.Core.ControlPriority.InteractiveUser,
            CancellationToken.None);
        TestAssert.Require(recovered.ConnectionState == ControlConnectionState.Ready,
            "cancelled startup left a hidden control session in the client");
    }

    private static async Task VerifyInteractiveConflictAsync(Uri address)
    {
        await using GrpcConsoleSession first = GrpcConsoleSession.Connect(address);
        await using GrpcConsoleSession second = GrpcConsoleSession.Connect(address);
        await using IControlSession owner = await first.TakeControlAsync(
            ConsoleControl.Core.ControlPriority.InteractiveUser,
            CancellationToken.None);
        try
        {
            await second.TakeControlAsync(
                ConsoleControl.Core.ControlPriority.InteractiveUser,
                CancellationToken.None);
            throw new InvalidOperationException("a second interactive client appeared to acquire control");
        }
        catch (ConsoleControl.Client.ControlConflictException)
        {
        }
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
        TestAssert.Require(await call.ResponseStream.MoveNext(CancellationToken.None) &&
                call.ResponseStream.Current.BodyCase == ControlStreamEvent.BodyOneofCase.Granted,
            "the daemon did not grant the abrupt-cancellation test stream");

        ControllerState pressed = ControllerState.Neutral with { Buttons = GameButtons.A };
        await call.RequestStream.WriteAsync(StateRequest(pressed), CancellationToken.None);
        await WaitUntilAsync(() => output.Last == pressed, TimeSpan.FromSeconds(1));
        streamStop.Cancel();
        await WaitUntilAsync(() => runtime.ControlAvailable, TimeSpan.FromSeconds(1));
        TestAssert.Require(output.Last == ControllerState.Neutral,
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
        TestAssert.Require(await call.ResponseStream.MoveNext(CancellationToken.None),
            "the daemon did not grant the inactivity test stream");
        ControllerState pressed = ControllerState.Neutral with { Buttons = GameButtons.B };
        await call.RequestStream.WriteAsync(StateRequest(pressed), CancellationToken.None);
        await WaitUntilAsync(() => output.Last == pressed, TimeSpan.FromSeconds(1));

        await WaitUntilAsync(() => runtime.ControlAvailable, TimeSpan.FromSeconds(2));
        TestAssert.Require(output.Last == ControllerState.Neutral,
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

        public IReadOnlyList<ControllerState> Since(int index)
        {
            lock (_gate)
            {
                return _states.Skip(index).ToArray();
            }
        }

        public bool ContainsOrderedTransition(
            int index,
            ControllerState first,
            ControllerState second)
        {
            lock (_gate)
            {
                int firstIndex = _states.FindIndex(index, state => state == first);
                return firstIndex >= 0 &&
                    _states.FindIndex(firstIndex + 1, state => state == second) > firstIndex;
            }
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