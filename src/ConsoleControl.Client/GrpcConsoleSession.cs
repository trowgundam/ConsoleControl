using System.Collections.Immutable;
using System.Diagnostics;
using ConsoleControl.Contracts;
using ConsoleControl.Core;
using Grpc.Core;
using Grpc.Net.Client;
using ContractPriority = ConsoleControl.Contracts.ControlPriority;
using DomainPriority = ConsoleControl.Core.ControlPriority;
using ContractDigital = ConsoleControl.Contracts.CanonicalDigitalControl;
using DomainDigital = ConsoleControl.Core.CanonicalDigitalControl;
using ContractSourceKind = ConsoleControl.Contracts.InputSourceKind;
using DomainSourceKind = ConsoleControl.Core.InputSourceKind;
using ContractStick = ConsoleControl.Contracts.CanonicalStick;
using DomainStick = ConsoleControl.Core.CanonicalStick;
using ContractTrigger = ConsoleControl.Contracts.CanonicalTrigger;
using DomainTrigger = ConsoleControl.Core.CanonicalTrigger;

namespace ConsoleControl.Client;

public sealed class GrpcConsoleSession : IConsoleSession
{
    private readonly GrpcChannel _channel;
    private readonly ConsoleControlService.ConsoleControlServiceClient _client;
    private readonly ClientId _clientId = new(Guid.NewGuid());
    private bool _disposed;
    private GrpcControlSession? _control;

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
            new GetStatusRequest(),
            cancellationToken: cancellationToken);
        return new ConsoleStatus(reply.BridgeConnected, reply.ControlAvailable, reply.Detail);
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

        await _channel.ShutdownAsync().ConfigureAwait(false);
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
