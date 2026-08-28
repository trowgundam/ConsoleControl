using ConsoleControl.Contracts;
using ConsoleControl.Core;
using Grpc.Core;
using Grpc.Net.Client;
using ContractPriority = ConsoleControl.Contracts.ControlPriority;
using DomainPriority = ConsoleControl.Core.ControlPriority;

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
