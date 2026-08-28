using ConsoleControl.Contracts;
using ConsoleControl.Core;
using Grpc.Core;
using ContractPriority = ConsoleControl.Contracts.ControlPriority;
using DomainPriority = ConsoleControl.Core.ControlPriority;

namespace ConsoleControl.Daemon;

internal sealed class ConsoleControlGrpcService(ConsoleRuntime runtime)
    : ConsoleControlService.ConsoleControlServiceBase
{
    public override Task<GetStatusReply> GetStatus(
        GetStatusRequest request,
        ServerCallContext context) =>
        Task.FromResult(new GetStatusReply
        {
            BridgeConnected = runtime.BridgeConnected,
            ControlAvailable = runtime.ControlAvailable,
            Detail = runtime.BridgeConnected ? "Controller bridge connected" : "Controller bridge disconnected",
        });

    public override async Task<AcquireControlReply> AcquireControl(
        AcquireControlRequest request,
        ServerCallContext context)
    {
        ClientId client = ParseClient(request.ClientId);
        DomainPriority priority = request.Priority switch
        {
            ContractPriority.Automation => DomainPriority.Automation,
            ContractPriority.InteractiveUser => DomainPriority.InteractiveUser,
            _ => throw InvalidArgument("A control priority is required."),
        };

        try
        {
            ControlLease lease = await runtime.AcquireControlAsync(
                client,
                priority,
                context.CancellationToken).ConfigureAwait(false);
            return new AcquireControlReply { LeaseGeneration = lease.Generation.Value };
        }
        catch (ControlConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
    }

    public override async Task<SetControllerStateReply> SetControllerState(
        SetControllerStateRequest request,
        ServerCallContext context)
    {
        ControllerState state = ParseState(request);
        try
        {
            await runtime.SetControllerStateAsync(
                ParseClient(request.ClientId),
                new LeaseGeneration(request.LeaseGeneration),
                state,
                context.CancellationToken).ConfigureAwait(false);
            return new SetControllerStateReply();
        }
        catch (StaleControlLeaseException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }

    public override async Task<ReleaseControlReply> ReleaseControl(
        ReleaseControlRequest request,
        ServerCallContext context)
    {
        try
        {
            await runtime.ReleaseControlAsync(
                ParseClient(request.ClientId),
                new LeaseGeneration(request.LeaseGeneration),
                context.CancellationToken).ConfigureAwait(false);
            return new ReleaseControlReply();
        }
        catch (StaleControlLeaseException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }

    private static ClientId ParseClient(string value) =>
        Guid.TryParse(value, out Guid parsed)
            ? new ClientId(parsed)
            : throw InvalidArgument("client_id must be a GUID.");

    private static ControllerState ParseState(SetControllerStateRequest request)
    {
        if (request.Buttons > ushort.MaxValue ||
            request.Dpad > (uint)HatPosition.Neutral ||
            request.LeftStickX > byte.MaxValue ||
            request.LeftStickY > byte.MaxValue ||
            request.RightStickX > byte.MaxValue ||
            request.RightStickY > byte.MaxValue ||
            request.LeftTrigger > byte.MaxValue ||
            request.RightTrigger > byte.MaxValue)
        {
            throw InvalidArgument("Controller state contains an out-of-range value.");
        }

        return new ControllerState(
            (GameButtons)request.Buttons,
            (HatPosition)request.Dpad,
            new StickPosition((byte)request.LeftStickX, (byte)request.LeftStickY),
            new StickPosition((byte)request.RightStickX, (byte)request.RightStickY),
            new TriggerPosition((byte)request.LeftTrigger),
            new TriggerPosition((byte)request.RightTrigger));
    }

    private static RpcException InvalidArgument(string detail) =>
        new(new Status(StatusCode.InvalidArgument, detail));
}
