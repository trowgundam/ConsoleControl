using System.Collections.Immutable;
using System.Diagnostics;

using ConsoleControl.Contracts;
using ConsoleControl.Core;

using Grpc.Core;
using Grpc.Net.Client;

using ContractDigital = ConsoleControl.Contracts.CanonicalDigitalControl;
using ContractPriority = ConsoleControl.Contracts.ControlPriority;
using ContractSourceKind = ConsoleControl.Contracts.InputSourceKind;
using ContractStick = ConsoleControl.Contracts.CanonicalStick;
using ContractTrigger = ConsoleControl.Contracts.CanonicalTrigger;
using DomainDigital = ConsoleControl.Core.CanonicalDigitalControl;
using DomainPriority = ConsoleControl.Core.ControlPriority;
using DomainSourceKind = ConsoleControl.Core.InputSourceKind;
using DomainStick = ConsoleControl.Core.CanonicalStick;
using DomainTrigger = ConsoleControl.Core.CanonicalTrigger;

namespace ConsoleControl.Client;

public sealed class GrpcConsoleSession : IConsoleSession
{
    private readonly GrpcChannel _channel;
    private readonly ConsoleControlService.ConsoleControlServiceClient _client;
    private readonly ClientId _clientId = new(Guid.NewGuid());
    private bool _disposed;
    private GrpcControlSession? _control;
    private GrpcAutomationSession? _automation;

    private GrpcConsoleSession(Uri daemonUri)
    {
        if (daemonUri.Scheme != Uri.UriSchemeHttp || !daemonUri.IsLoopback)
        {
            throw new ArgumentException("The daemon URI must use HTTP on loopback.", nameof(daemonUri));
        }

        _channel = GrpcChannel.ForAddress(daemonUri);
        _client = new ConsoleControlService.ConsoleControlServiceClient(_channel);
    }

    public static GrpcConsoleSession Connect(Uri daemonUri) => new(daemonUri);

    public async Task<ConsoleStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GetStatusReply reply = await _client.GetStatusAsync(
            new GetStatusRequest { ClientId = _clientId.Value.ToString("D") },
            cancellationToken: cancellationToken);
        PendingControlRequest? pending = reply.HasPendingControlRequestId &&
                                         reply.HasPendingControlRequestReason &&
                                         Guid.TryParse(reply.PendingControlRequestId, out Guid requestId)
            ? new(requestId, reply.PendingControlRequestReason)
            : null;
        return new ConsoleStatus(
            reply.DaemonVersion,
            reply.ProtocolVersion,
            reply.ControlOwner switch
            {
                ControlOwnerMessage.None => ControlOwner.None,
                ControlOwnerMessage.ThisClient => ControlOwner.ThisClient,
                ControlOwnerMessage.InteractiveClient => ControlOwner.InteractiveClient,
                ControlOwnerMessage.AutomationClient => ControlOwner.AutomationClient,
                _ => throw new InvalidOperationException("The daemon returned an unknown control owner."),
            },
            ParseBridgeStatus(reply.ControllerBridge),
            ParseVideoStatus(reply.Video),
            pending);
    }

    public async Task<InputConfiguration> GetInputConfigurationAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InputConfigurationReply reply = await _client.GetInputConfigurationAsync(
            new GetInputConfigurationRequest(),
            cancellationToken: cancellationToken);
        return ParseConfiguration(reply);
    }

    public async Task<InputConfiguration> SaveInputProfileAsync(
        InputProfile profile,
        ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        profile.Validate();
        InputConfigurationReply reply = await _client.SaveInputProfileAsync(
            new SaveInputProfileRequest
            {
                ExpectedRevision = expectedRevision,
                Profile = ToMessage(profile),
            },
            cancellationToken: cancellationToken);
        return ParseConfiguration(reply);
    }

    public async Task<VideoInventory> GetVideoInventoryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoInventoryReply reply = await _client.GetVideoInventoryAsync(
            new GetVideoInventoryRequest(),
            cancellationToken: cancellationToken);
        return ParseVideoInventory(reply);
    }

    public async Task<ControllerBridgeInventory> GetControllerBridgeInventoryAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ControllerBridgeInventoryReply reply = await _client.GetControllerBridgeInventoryAsync(
            new GetControllerBridgeInventoryRequest(),
            cancellationToken: cancellationToken);
        return new(
            reply.Bridges.Select(bridge => new ControllerBridge(
                new(bridge.Id),
                bridge.DisplayName,
                bridge.Connected)).ToImmutableArray(),
            reply.HasSelectedBridgeId ? new ControllerBridgeId(reply.SelectedBridgeId) : null,
            reply.Revision,
            ParseBridgeState(reply.State),
            reply.Status);
    }

    public async Task<ControllerBridgeSelection> SelectControllerBridgeAsync(
        ControllerBridgeId bridgeId,
        ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ControllerBridgeSelectionReply reply = await _client.SelectControllerBridgeAsync(
            new SelectControllerBridgeRequest
            {
                BridgeId = bridgeId.Value,
                ExpectedRevision = expectedRevision,
            },
            cancellationToken: cancellationToken);
        return new(new(reply.BridgeId), reply.Revision, ParseBridgeState(reply.State), reply.Status);
    }

    public async Task<VideoSelection> SelectVideoSourceAsync(
        VideoSourceId sourceId,
        ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoSelectionReply reply = await _client.SelectVideoSourceAsync(
            new SelectVideoSourceRequest
            {
                SourceId = sourceId.Value,
                ExpectedRevision = expectedRevision,
            },
            cancellationToken: cancellationToken);
        return new(new(reply.SourceId), reply.Revision, reply.Status);
    }

    public async Task<IControlSession> TakeControlAsync(
        DomainPriority priority,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_control is not null)
        {
            throw new InvalidOperationException("This client already has a control session.");
        }

        _control = new GrpcControlSession(
            _client,
            _clientId,
            priority,
            OnControlReleased);
        await _control.StartAsync(cancellationToken).ConfigureAwait(false);
        return _control;
    }

    public async Task<Screenshot> GetScreenshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ScreenshotReply reply = await _client.GetScreenshotAsync(
            new GetScreenshotRequest(), cancellationToken: cancellationToken);
        return ParseScreenshot(reply);
    }

    public async Task<IAutomationSession> RequestAutomationControlAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_control is not null || _automation is not null)
        {
            throw new InvalidOperationException("This client already has a control session.");
        }

        _automation = new(_client, _clientId, reason, OnAutomationReleased);
        try
        {
            await _automation.StartAsync(cancellationToken).ConfigureAwait(false);
            return _automation;
        }
        catch
        {
            await _automation.DisposeAsync().ConfigureAwait(false);
            _automation = null;
            throw;
        }
    }

    public async Task<bool> DeclineControlRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        DeclineControlRequestReply reply = await _client.DeclineControlRequestAsync(
            new DeclineControlRequestMessage { RequestId = requestId.ToString("D") },
            cancellationToken: cancellationToken);
        return reply.Declined;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
            _control = null;
        }
        if (_automation is not null)
        {
            await _automation.DisposeAsync().ConfigureAwait(false);
            _automation = null;
        }

        await _channel.ShutdownAsync().ConfigureAwait(false);
    }

    private void OnAutomationReleased(GrpcAutomationSession released)
    {
        if (ReferenceEquals(_automation, released))
        {
            _automation = null;
        }
    }

    private void OnControlReleased(GrpcControlSession released)
    {
        if (ReferenceEquals(_control, released))
        {
            _control = null;
        }
    }

    private static InputConfiguration ParseConfiguration(InputConfigurationReply reply) =>
        new(reply.Revision, reply.Profiles.Select(ParseProfile).ToImmutableArray());

    private static VideoInventory ParseVideoInventory(VideoInventoryReply reply) => new(
        reply.Sources.Select(source => new VideoSource(
            new(source.Id),
            source.DisplayName,
            new(
                checked((ushort)source.PreferredMode.Width),
                checked((ushort)source.PreferredMode.Height),
                checked((ushort)source.PreferredMode.FramesPerSecond))))
            .ToImmutableArray(),
        reply.HasSelectedSourceId ? new VideoSourceId(reply.SelectedSourceId) : null,
        reply.Revision,
        new Uri(reply.LiveStreamUri, UriKind.Absolute),
        reply.Status);

    private static ControllerBridgeState ParseBridgeState(ControllerBridgeStateMessage state) => state switch
    {
        ControllerBridgeStateMessage.SelectionRequired => ControllerBridgeState.SelectionRequired,
        ControllerBridgeStateMessage.Unavailable => ControllerBridgeState.Unavailable,
        ControllerBridgeStateMessage.Disconnected => ControllerBridgeState.Disconnected,
        ControllerBridgeStateMessage.Ready => ControllerBridgeState.Ready,
        ControllerBridgeStateMessage.Faulted => ControllerBridgeState.Faulted,
        _ => throw new InvalidOperationException("The daemon returned an unknown controller bridge state."),
    };

    private static ControllerBridgeStatus ParseBridgeStatus(ControllerBridgeStatusMessage status) => new(
        status.HasSelectedBridgeId ? new ControllerBridgeId(status.SelectedBridgeId) : null,
        ParseAvailability(status.Availability),
        status.OutputConnection switch
        {
            ControllerOutputConnectionMessage.NotConfigured => ControllerOutputConnection.NotConfigured,
            ControllerOutputConnectionMessage.DisconnectedUntilInput => ControllerOutputConnection.DisconnectedUntilInput,
            ControllerOutputConnectionMessage.Connected => ControllerOutputConnection.Connected,
            ControllerOutputConnectionMessage.Faulted => ControllerOutputConnection.Faulted,
            _ => throw new InvalidOperationException("The daemon returned an unknown controller output state."),
        },
        status.Detail,
        status.HasLastInventoryAtUnixMs
            ? DateTimeOffset.FromUnixTimeMilliseconds(status.LastInventoryAtUnixMs)
            : null);

    private static VideoCaptureStatus ParseVideoStatus(VideoCaptureStatusMessage status) => new(
        status.HasSelectedSourceId ? new VideoSourceId(status.SelectedSourceId) : null,
        ParseAvailability(status.Availability),
        status.CaptureState switch
        {
            VideoCaptureStateMessage.SelectionRequired => VideoCaptureState.SelectionRequired,
            VideoCaptureStateMessage.Starting => VideoCaptureState.Starting,
            VideoCaptureStateMessage.Streaming => VideoCaptureState.Streaming,
            VideoCaptureStateMessage.Reconnecting => VideoCaptureState.Reconnecting,
            VideoCaptureStateMessage.Faulted => VideoCaptureState.Faulted,
            _ => throw new InvalidOperationException("The daemon returned an unknown video capture state."),
        },
        status.ActiveMode is not null
            ? new VideoMode(
                checked((ushort)status.ActiveMode.Width),
                checked((ushort)status.ActiveMode.Height),
                checked((ushort)status.ActiveMode.FramesPerSecond))
            : null,
        status.HasLatestFrameAtUnixMs
            ? DateTimeOffset.FromUnixTimeMilliseconds(status.LatestFrameAtUnixMs)
            : null,
        status.Detail);

    private static HardwareAvailability ParseAvailability(HardwareAvailabilityMessage availability) => availability switch
    {
        HardwareAvailabilityMessage.Unknown => HardwareAvailability.Unknown,
        HardwareAvailabilityMessage.Available => HardwareAvailability.Available,
        HardwareAvailabilityMessage.Unavailable => HardwareAvailability.Unavailable,
        _ => throw new InvalidOperationException("The daemon returned an unknown hardware availability."),
    };

    private static InputProfile ParseProfile(InputProfileMessage message) => new InputProfile(
        new((DomainSourceKind)message.SourceKind, message.HardwareId),
        message.Name,
        message.DigitalBindings.Select(binding => new DigitalBinding(
            new(binding.Source),
            binding.Targets.Select(target => (DomainDigital)target).ToImmutableArray())).ToImmutableArray(),
        message.StickBindings.Select(binding => new StickBinding(
            new(binding.XSource),
            new(binding.YSource),
            (DomainStick)binding.Target,
            ParseTransform(binding.XTransform),
            ParseTransform(binding.YTransform))).ToImmutableArray(),
        message.TriggerBindings.Select(binding => new TriggerBinding(
            new(binding.Source),
            (DomainTrigger)binding.Target,
            ParseTransform(binding.Transform),
            binding.DigitalThreshold)).ToImmutableArray()).Validate();

    private static AxisTransform ParseTransform(AxisTransformMessage message) =>
        new(message.DeadZone, message.Inverted, message.Scale);

    private static InputProfileMessage ToMessage(InputProfile profile)
    {
        InputProfileMessage message = new()
        {
            SourceKind = (ContractSourceKind)profile.Key.Kind,
            HardwareId = profile.Key.HardwareId,
            Name = profile.Name,
        };
        message.DigitalBindings.AddRange(profile.DigitalBindings.Select(binding =>
        {
            DigitalBindingMessage result = new() { Source = binding.Source.Value };
            result.Targets.AddRange(binding.Targets.Select(target => (ContractDigital)target));
            return result;
        }));
        message.StickBindings.AddRange(profile.StickBindings.Select(binding => new StickBindingMessage
        {
            XSource = binding.XSource.Value,
            YSource = binding.YSource.Value,
            Target = (ContractStick)binding.Target,
            XTransform = ToMessage(binding.XTransform),
            YTransform = ToMessage(binding.YTransform),
        }));
        message.TriggerBindings.AddRange(profile.TriggerBindings.Select(binding => new TriggerBindingMessage
        {
            Source = binding.Source.Value,
            Target = (ContractTrigger)binding.Target,
            Transform = ToMessage(binding.Transform),
            DigitalThreshold = binding.DigitalThreshold,
        }));
        return message;
    }

    private static AxisTransformMessage ToMessage(AxisTransform transform) => new()
    {
        DeadZone = transform.DeadZone,
        Inverted = transform.Inverted,
        Scale = transform.Scale,
    };

    private static Screenshot ParseScreenshot(ScreenshotReply reply) => new(
        reply.Generation,
        reply.Sequence,
        new(
            checked((ushort)reply.Mode.Width),
            checked((ushort)reply.Mode.Height),
            checked((ushort)reply.Mode.FramesPerSecond)),
        DateTimeOffset.FromUnixTimeMilliseconds(reply.ReceivedAtUnixMs),
        reply.Jpeg.ToByteArray());

    private sealed class GrpcAutomationSession(
        ConsoleControlService.ConsoleControlServiceClient client,
        ClientId clientId,
        string reason,
        Action<GrpcAutomationSession> onReleased) : IAutomationSession
    {
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);
        private readonly CancellationTokenSource _stop = new();
        private AsyncDuplexStreamingCall<ControlStreamRequest, ControlStreamEvent>? _call;
        private Task? _lifetime;
        private LeaseGeneration _generation;
        private bool _disposed;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _call = client.Control(cancellationToken: _stop.Token);
            await _call.RequestStream.WriteAsync(new ControlStreamRequest
            {
                Open = new OpenControlStream
                {
                    ClientId = clientId.Value.ToString("D"),
                    Priority = ContractPriority.Automation,
                    RequestReason = reason,
                },
            }, cancellationToken).ConfigureAwait(false);
            if (!await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false) ||
                _call.ResponseStream.Current.BodyCase != ControlStreamEvent.BodyOneofCase.Granted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable,
                    "The daemon did not grant automation control."));
            }
            _generation = new(_call.ResponseStream.Current.Granted.LeaseGeneration);
            _lifetime = MaintainLeaseAsync(_stop.Token);
        }

        public async Task<AutomationResult> RunAsync(
            AutomationSequence sequence,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            sequence.Compile();
            RunAutomationRequest request = new()
            {
                ClientId = clientId.Value.ToString("D"),
                LeaseGeneration = _generation.Value,
                CaptureStart = sequence.CaptureStart,
                CaptureEnd = sequence.CaptureEnd,
            };
            request.Commands.AddRange(sequence.Commands.Select(ToMessage));
            AutomationResultReply reply = await client.RunAutomationAsync(
                request, cancellationToken: cancellationToken);
            return new(
                (AutomationOutcome)((int)reply.Outcome - 1),
                TimeSpan.FromMilliseconds(reply.ElapsedMs),
                reply.Captures.Select(ParseCapture).ToImmutableArray(),
                reply.HasDetail ? reply.Detail : null);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stop.Cancel();
            try
            {
                if (_lifetime is not null)
                {
                    await _lifetime.ConfigureAwait(false);
                }
            }
            catch
            {
                // Closing the stream still makes daemon cleanup authoritative.
            }
            _call?.Dispose();
            _stop.Dispose();
            onReleased(this);
        }

        private async Task MaintainLeaseAsync(CancellationToken cancellationToken)
        {
            Task send = SendHeartbeatsAsync(cancellationToken);
            Task receive = WatchResponsesAsync(cancellationToken);
            await Task.WhenAny(send, receive).ConfigureAwait(false);
            _stop.Cancel();
            try
            {
                await Task.WhenAll(send, receive).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
        {
            using PeriodicTimer timer = new(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await _call!.RequestStream.WriteAsync(new ControlStreamRequest
                {
                    Heartbeat = new ControlHeartbeat(),
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WatchResponsesAsync(CancellationToken cancellationToken)
        {
            while (await _call!.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                if (_call.ResponseStream.Current.BodyCase ==
                    ControlStreamEvent.BodyOneofCase.Condition)
                {
                    return;
                }
            }
        }

        private static AutomationCommandMessage ToMessage(AutomationCommand command) => command switch
        {
            AutomationCommand.Press press => new()
            {
                Press = new()
                {
                    Control = (ContractDigital)press.Control,
                    DurationMs = checked((uint)press.Duration.TotalMilliseconds),
                },
            },
            AutomationCommand.Hold hold => new()
            {
                Hold = new()
                {
                    Control = (ContractDigital)hold.Control,
                    DurationMs = checked((uint)hold.Duration.TotalMilliseconds),
                },
            },
            AutomationCommand.Pause pause => new()
            {
                PauseMs = checked((uint)pause.Duration.TotalMilliseconds),
            },
            AutomationCommand.Capture capture => new()
            {
                Capture = new() { Name = capture.Name },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

        private static AutomationCapture ParseCapture(AutomationCaptureReply reply) => new(
            reply.Name,
            TimeSpan.FromMilliseconds(reply.ScheduledAtMs),
            TimeSpan.FromMilliseconds(reply.ActualAtMs),
            reply.Screenshot is not null ? ParseScreenshot(reply.Screenshot) : null,
            reply.HasFailure ? reply.Failure : null);
    }

    private sealed class GrpcControlSession(
        ConsoleControlService.ConsoleControlServiceClient client,
        ClientId clientId,
        DomainPriority priority,
        Action<GrpcControlSession> onReleased) : IControlSession
    {
        private static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(1000d / 60d);
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _changeSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ControllerState _desired = ControllerState.Neutral;
        private ulong _revision;
        private TaskCompletionSource? _barrier;
        private ControlConnectionState _connectionState = ControlConnectionState.Connecting;
        private Task? _runningSupervisor;
        private bool _disposed;

        public ControlConnectionState ConnectionState
        {
            get
            {
                lock (_gate)
                {
                    return _connectionState;
                }
            }
        }

        public event EventHandler<ControlConnectionState>? ConnectionStateChanged;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _runningSupervisor = SuperviseAsync(_stop.Token);
            await _started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task SetStateAsync(
            ControllerState state,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            Task? wait = null;
            TaskCompletionSource signal;
            lock (_gate)
            {
                if (state != ControllerState.Neutral && _barrier is not null)
                {
                    return Task.CompletedTask;
                }
                _desired = state;
                _revision++;
                if (state == ControllerState.Neutral)
                {
                    _barrier?.TrySetResult();
                    _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _barrier.Task;
                }
                signal = _changeSignal;
                _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal.TrySetResult();
            return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            using CancellationTokenSource neutralTimeout = new(TimeSpan.FromMilliseconds(500));
            try
            {
                await SetNeutralForShutdownAsync(neutralTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _stop.Cancel();
            lock (_gate)
            {
                _changeSignal.TrySetResult();
            }
            try
            {
                if (_runningSupervisor is not null)
                {
                    await _runningSupervisor.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetConnectionState(ControlConnectionState.Stopped);
                lock (_gate)
                {
                    _barrier?.TrySetCanceled();
                    _barrier = null;
                }
                onReleased(this);
                _stop.Dispose();
            }
        }

        private async Task SetNeutralForShutdownAsync(CancellationToken cancellationToken)
        {
            Task wait;
            TaskCompletionSource signal;
            lock (_gate)
            {
                _desired = ControllerState.Neutral;
                _revision++;
                _barrier?.TrySetResult();
                _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _barrier.Task;
                signal = _changeSignal;
                _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            signal.TrySetResult();
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task SuperviseAsync(CancellationToken cancellationToken)
        {
            int failureCount = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SetConnectionState(failureCount == 0
                        ? ControlConnectionState.Connecting
                        : ControlConnectionState.Reconnecting);
                    await RunStreamAsync(cancellationToken).ConfigureAwait(false);
                    failureCount = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (RpcException exception) when (exception.StatusCode == StatusCode.Aborted)
                {
                    SetConnectionState(ControlConnectionState.WaitingForControl);
                    _started.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    failureCount++;
                }
                catch (BridgeUnavailableException)
                {
                    SetConnectionState(ControlConnectionState.WaitingForBridge);
                    _started.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    failureCount++;
                }
                catch (Exception)
                {
                    SetConnectionState(ControlConnectionState.Reconnecting);
                    _started.TrySetResult();
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 200 * ++failureCount)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private async Task RunStreamAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource streamStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using AsyncDuplexStreamingCall<ControlStreamRequest, ControlStreamEvent> call =
                client.Control(cancellationToken: streamStop.Token);
            await call.RequestStream.WriteAsync(new ControlStreamRequest
            {
                Open = new OpenControlStream
                {
                    ClientId = clientId.Value.ToString("D"),
                    Priority = priority switch
                    {
                        DomainPriority.Automation => ContractPriority.Automation,
                        DomainPriority.InteractiveUser => ContractPriority.InteractiveUser,
                        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
                    },
                },
            }, cancellationToken).ConfigureAwait(false);

            if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.BodyCase != ControlStreamEvent.BodyOneofCase.Granted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "The daemon did not grant control."));
            }

            lock (_gate)
            {
                _desired = ControllerState.Neutral;
                _revision++;
            }
            await WriteStateAsync(call.RequestStream, ControllerState.Neutral, cancellationToken).ConfigureAwait(false);
            CompleteBarrier();
            SetConnectionState(ControlConnectionState.Ready);
            _started.TrySetResult();

            Task writer = PumpStatesAsync(call.RequestStream, streamStop.Token);
            Task reader = WatchEventsAsync(call.ResponseStream, streamStop.Token);
            Task completed = await Task.WhenAny(writer, reader).ConfigureAwait(false);
            streamStop.Cancel();
            try
            {
                await Task.WhenAll(writer, reader).ConfigureAwait(false);
            }
            catch when (completed.IsFaulted)
            {
                await completed.ConfigureAwait(false);
                throw;
            }
        }

        private static async Task WatchEventsAsync(
            IAsyncStreamReader<ControlStreamEvent> reader,
            CancellationToken cancellationToken)
        {
            while (await reader.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                if (reader.Current.BodyCase == ControlStreamEvent.BodyOneofCase.Condition &&
                    reader.Current.Condition == ControlCondition.BridgeUnavailable)
                {
                    throw new BridgeUnavailableException();
                }
            }
            throw new RpcException(new Status(StatusCode.Unavailable, "The control stream ended."));
        }

        private async Task PumpStatesAsync(
            IClientStreamWriter<ControlStreamRequest> writer,
            CancellationToken cancellationToken)
        {
            ulong sentRevision;
            lock (_gate)
            {
                sentRevision = _revision;
            }
            long lastWrite = Stopwatch.GetTimestamp();

            while (!cancellationToken.IsCancellationRequested)
            {
                ControllerState desired;
                ulong revision;
                bool barrier;
                Task changed;
                lock (_gate)
                {
                    desired = _desired;
                    revision = _revision;
                    barrier = _barrier is not null;
                    changed = _changeSignal.Task;
                }

                bool heartbeatDue = Stopwatch.GetElapsedTime(lastWrite) >= HeartbeatInterval;
                if (revision == sentRevision && !heartbeatDue)
                {
                    TimeSpan heartbeatWait = HeartbeatInterval - Stopwatch.GetElapsedTime(lastWrite);
                    await Task.WhenAny(changed, Task.Delay(heartbeatWait, cancellationToken)).ConfigureAwait(false);
                    continue;
                }
                if (!barrier && !heartbeatDue)
                {
                    TimeSpan cadenceWait = SendInterval - Stopwatch.GetElapsedTime(lastWrite);
                    if (cadenceWait > TimeSpan.Zero)
                    {
                        await Task.WhenAny(changed, Task.Delay(cadenceWait, cancellationToken)).ConfigureAwait(false);
                        continue;
                    }
                }

                await WriteStateAsync(writer, desired, cancellationToken).ConfigureAwait(false);
                sentRevision = revision;
                lastWrite = Stopwatch.GetTimestamp();
                if (barrier)
                {
                    CompleteBarrier();
                }
            }
        }

        private static Task WriteStateAsync(
            IClientStreamWriter<ControlStreamRequest> writer,
            ControllerState state,
            CancellationToken cancellationToken) =>
            writer.WriteAsync(new ControlStreamRequest
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
            }, cancellationToken);

        private void CompleteBarrier()
        {
            TaskCompletionSource? barrier;
            lock (_gate)
            {
                barrier = _barrier;
                _barrier = null;
            }
            barrier?.TrySetResult();
        }

        private void SetConnectionState(ControlConnectionState state)
        {
            lock (_gate)
            {
                if (_connectionState == state)
                {
                    return;
                }
                _connectionState = state;
            }
            ConnectionStateChanged?.Invoke(this, state);
        }

        private sealed class BridgeUnavailableException : Exception
        {
        }
    }
}