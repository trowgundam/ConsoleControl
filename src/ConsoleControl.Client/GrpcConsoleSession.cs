using System.Collections.Immutable;
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

        AcquireControlReply reply = await _client.AcquireControlAsync(
            new AcquireControlRequest
            {
                ClientId = _clientId.Value.ToString("D"),
                Priority = priority switch
                {
                    DomainPriority.Automation => ContractPriority.Automation,
                    DomainPriority.InteractiveUser => ContractPriority.InteractiveUser,
                    _ => throw new ArgumentOutOfRangeException(nameof(priority)),
                },
            },
            cancellationToken: cancellationToken);

        _control = new GrpcControlSession(
            _client,
            _clientId,
            new LeaseGeneration(reply.LeaseGeneration),
            OnControlReleased);
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
        LeaseGeneration generation,
        Action<GrpcControlSession> onReleased) : IControlSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public async Task SetStateAsync(
            ControllerState state,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SetStateCoreAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
                try
                {
                    await SetStateCoreAsync(ControllerState.Neutral, timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    await client.ReleaseControlAsync(
                        new ReleaseControlRequest
                        {
                            ClientId = clientId.Value.ToString("D"),
                            LeaseGeneration = generation.Value,
                        },
                        cancellationToken: timeout.Token);
                }

                _disposed = true;
                onReleased(this);
            }
            catch (RpcException exception) when (
                exception.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled or StatusCode.DeadlineExceeded)
            {
                _disposed = true;
                onReleased(this);
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }

        private async Task SetStateCoreAsync(
            ControllerState state,
            CancellationToken cancellationToken)
        {
            await client.SetControllerStateAsync(
                new SetControllerStateRequest
                {
                    ClientId = clientId.Value.ToString("D"),
                    LeaseGeneration = generation.Value,
                    Buttons = (uint)state.Buttons,
                    Dpad = (uint)state.DPad,
                    LeftStickX = state.LeftStick.X,
                    LeftStickY = state.LeftStick.Y,
                    RightStickX = state.RightStick.X,
                    RightStickY = state.RightStick.Y,
                    LeftTrigger = state.LeftTrigger.Value,
                    RightTrigger = state.RightTrigger.Value,
                },
                cancellationToken: cancellationToken);
        }
    }
}
