using System.Collections.Immutable;
using System.Diagnostics;

using ConsoleControl.Contracts;
using ConsoleControl.Core;

using Grpc.Core;
using Grpc.Net.Client;

using ContractDigital = ConsoleControl.Contracts.CanonicalDigitalControl;
using ContractPriority = ConsoleControl.Contracts.ControlPriority;
using DomainDigital = ConsoleControl.Core.CanonicalDigitalControl;
using DomainPriority = ConsoleControl.Core.ControlPriority;

namespace ConsoleControl.Client;

public sealed class GrpcConsoleSession : IConsoleSession
{
    internal static readonly TimeSpan ControlStateRefreshInterval = TimeSpan.FromMilliseconds(50);

    internal static bool RequiresCadenceDelay(
        bool hasPending,
        bool nextIsNeutral,
        bool heartbeatDue) =>
        hasPending && !nextIsNeutral && !heartbeatDue;

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
        cancellationToken.ThrowIfCancellationRequested();
        if (_control is not null)
        {
            throw new InvalidOperationException("This client already has a control session.");
        }

        GrpcControlSession control = new(
            _client,
            _clientId,
            priority,
            OnControlReleased);
        _control = control;
        try
        {
            await control.StartAsync(cancellationToken).ConfigureAwait(false);
            return control;
        }
        catch
        {
            await control.AbortStartupAsync().ConfigureAwait(false);
            if (ReferenceEquals(_control, control))
            {
                _control = null;
            }
            throw;
        }
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
        private readonly TaskCompletionSource<AutomationSessionEnd> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AsyncDuplexStreamingCall<ControlStreamRequest, ControlStreamEvent>? _call;
        private Task? _lifetime;
        private LeaseGeneration _generation;
        private bool _disposeRequested;
        private bool _disposed;

        public Task<AutomationSessionEnd> Completion => _completion.Task;

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
            _lifetime = RunLifetimeAsync(_stop.Token);
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
            _disposeRequested = true;
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
            if (_lifetime is null)
            {
                onReleased(this);
                _completion.TrySetResult(new(AutomationSessionEndReason.Released, null));
            }
        }

        private async Task RunLifetimeAsync(CancellationToken cancellationToken)
        {
            AutomationSessionEnd end;
            try
            {
                end = await MaintainLeaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeRequested)
            {
                end = new(AutomationSessionEndReason.Released, null);
            }
            catch (Exception exception)
            {
                end = new(AutomationSessionEndReason.ConnectionLost, exception.Message);
            }
            onReleased(this);
            _completion.TrySetResult(end);
        }

        private async Task<AutomationSessionEnd> MaintainLeaseAsync(CancellationToken cancellationToken)
        {
            Task send = SendHeartbeatsAsync(cancellationToken);
            Task<AutomationSessionEnd> receive = WatchResponsesAsync(cancellationToken);
            Task completed = await Task.WhenAny(send, receive).ConfigureAwait(false);
            AutomationSessionEnd end = completed == receive
                ? await receive.ConfigureAwait(false)
                : new(AutomationSessionEndReason.ConnectionLost,
                    "The automation heartbeat stream ended unexpectedly.");
            _stop.Cancel();
            try
            {
                await Task.WhenAll(send, receive).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            return _disposeRequested
                ? new(AutomationSessionEndReason.Released, null)
                : end;
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

        private async Task<AutomationSessionEnd> WatchResponsesAsync(CancellationToken cancellationToken)
        {
            while (await _call!.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                if (_call.ResponseStream.Current.BodyCase ==
                    ControlStreamEvent.BodyOneofCase.Condition)
                {
                    return _call.ResponseStream.Current.Condition switch
                    {
                        ControlCondition.LeaseRevoked => new(
                            AutomationSessionEndReason.Preempted,
                            "The automation control lease was revoked."),
                        ControlCondition.BridgeUnavailable => new(
                            AutomationSessionEndReason.BridgeUnavailable,
                            "The controller bridge became unavailable."),
                        _ => new(
                            AutomationSessionEndReason.ConnectionLost,
                            "The daemon ended automation control for an unspecified reason."),
                    };
                }
            }
            return new(
                AutomationSessionEndReason.ConnectionLost,
                "The daemon closed the automation control stream.");
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
        private static readonly TimeSpan HeartbeatInterval = ControlStateRefreshInterval;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ControllerStateMailbox _mailbox = new();
        private TaskCompletionSource _changeSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                if (state == ControllerState.Neutral)
                {
                    _barrier?.TrySetResult();
                    _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _barrier.Task;
                    _mailbox.PublishRequired(state);
                }
                else
                {
                    _mailbox.Publish(state);
                }
                signal = _changeSignal;
                _changeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal.TrySetResult();
            return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await DisposeCoreAsync(confirmLeaseRelease: false).ConfigureAwait(false);
        }

        public async Task AbortStartupAsync()
        {
            await DisposeCoreAsync(confirmLeaseRelease: true).ConfigureAwait(false);
        }

        private async Task DisposeCoreAsync(bool confirmLeaseRelease)
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
                    await _runningSupervisor.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
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
                if (confirmLeaseRelease)
                {
                    await ConfirmLeaseReleasedAsync().ConfigureAwait(false);
                }
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

        private async Task ConfirmLeaseReleasedAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    GetStatusReply status = await client.GetStatusAsync(
                        new GetStatusRequest { ClientId = clientId.Value.ToString("D") },
                        cancellationToken: timeout.Token).ConfigureAwait(false);
                    if (status.ControlOwner != ControlOwnerMessage.ThisClient)
                    {
                        return;
                    }
                    await Task.Delay(20, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is RpcException or OperationCanceledException)
            {
            }
        }

        private async Task SetNeutralForShutdownAsync(CancellationToken cancellationToken)
        {
            Task wait;
            TaskCompletionSource signal;
            lock (_gate)
            {
                _barrier?.TrySetResult();
                _barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _barrier.Task;
                _mailbox.PublishRequired(ControllerState.Neutral);
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
                    if (!_started.Task.IsCompleted)
                    {
                        _started.TrySetException(new ControlConflictException(
                            string.IsNullOrWhiteSpace(exception.Status.Detail)
                                ? "Control is held by another interactive client."
                                : exception.Status.Detail));
                        break;
                    }
                    SetConnectionState(ControlConnectionState.WaitingForControl);
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
                _mailbox.Reset(ControllerState.Neutral);
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
            long lastWrite = Stopwatch.GetTimestamp();

            while (!cancellationToken.IsCancellationRequested)
            {
                bool hasPending;
                bool nextIsNeutral;
                Task changed;
                lock (_gate)
                {
                    hasPending = _mailbox.HasPending;
                    nextIsNeutral = _mailbox.NextIsNeutral;
                    changed = _changeSignal.Task;
                }

                bool heartbeatDue = Stopwatch.GetElapsedTime(lastWrite) >= HeartbeatInterval;
                if (!hasPending && !heartbeatDue)
                {
                    TimeSpan heartbeatWait = HeartbeatInterval - Stopwatch.GetElapsedTime(lastWrite);
                    await Task.WhenAny(changed, Task.Delay(heartbeatWait, cancellationToken)).ConfigureAwait(false);
                    continue;
                }
                if (RequiresCadenceDelay(hasPending, nextIsNeutral, heartbeatDue))
                {
                    TimeSpan cadenceWait = SendInterval - Stopwatch.GetElapsedTime(lastWrite);
                    if (cadenceWait > TimeSpan.Zero)
                    {
                        await Task.Delay(cadenceWait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                ControllerState state;
                lock (_gate)
                {
                    state = _mailbox.TryTake(out ControllerState pending)
                        ? pending
                        : _mailbox.Current;
                }
                await WriteStateAsync(writer, state, cancellationToken).ConfigureAwait(false);
                lastWrite = Stopwatch.GetTimestamp();
                if (state == ControllerState.Neutral)
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