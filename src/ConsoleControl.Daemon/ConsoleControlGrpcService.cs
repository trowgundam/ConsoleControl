using System.Collections.Immutable;
using ConsoleControl.Contracts;
using ConsoleControl.Core;
using Grpc.Core;
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

namespace ConsoleControl.Daemon;

internal sealed class ConsoleControlGrpcService(ConsoleRuntime runtime, InputProfileStore inputProfiles)
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

    public override async Task<InputConfigurationReply> GetInputConfiguration(
        GetInputConfigurationRequest request,
        ServerCallContext context) =>
        ToReply(await inputProfiles.ReadAsync(context.CancellationToken).ConfigureAwait(false));

    public override async Task<InputConfigurationReply> SaveInputProfile(
        SaveInputProfileRequest request,
        ServerCallContext context)
    {
        if (request.Profile is null)
        {
            throw InvalidArgument("An input profile is required.");
        }

        try
        {
            InputConfiguration updated = await inputProfiles.SaveAsync(
                ParseProfile(request.Profile),
                request.ExpectedRevision,
                context.CancellationToken).ConfigureAwait(false);
            return ToReply(updated);
        }
        catch (ArgumentException exception)
        {
            throw InvalidArgument(exception.Message);
        }
        catch (InputConfigurationConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
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

    private static InputProfile ParseProfile(InputProfileMessage message)
    {
        DomainSourceKind kind = ParseEnum<DomainSourceKind, ContractSourceKind>(message.SourceKind);
        InputProfile profile = new(
            new(kind, message.HardwareId),
            message.Name,
            message.DigitalBindings.Select(binding => new DigitalBinding(
                new(binding.Source),
                binding.Targets.Select(ParseEnum<DomainDigital, ContractDigital>).ToImmutableArray()))
                .ToImmutableArray(),
            message.StickBindings.Select(binding => new StickBinding(
                new(binding.XSource),
                new(binding.YSource),
                ParseEnum<DomainStick, ContractStick>(binding.Target),
                ParseTransform(binding.XTransform),
                ParseTransform(binding.YTransform))).ToImmutableArray(),
            message.TriggerBindings.Select(binding => new TriggerBinding(
                new(binding.Source),
                ParseEnum<DomainTrigger, ContractTrigger>(binding.Target),
                ParseTransform(binding.Transform),
                binding.DigitalThreshold)).ToImmutableArray());
        return profile.Validate();
    }

    private static AxisTransform ParseTransform(AxisTransformMessage? message) =>
        message is null
            ? throw InvalidArgument("An axis transform is required.")
            : new(message.DeadZone, message.Inverted, message.Scale);

    private static InputConfigurationReply ToReply(InputConfiguration configuration)
    {
        InputConfigurationReply reply = new() { Revision = configuration.Revision };
        reply.Profiles.AddRange(configuration.Profiles.Select(ToMessage));
        return reply;
    }

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

    private static TDomain ParseEnum<TDomain, TContract>(TContract value)
        where TDomain : struct, Enum
        where TContract : struct, Enum
    {
        int numeric = Convert.ToInt32(value);
        TDomain parsed = (TDomain)Enum.ToObject(typeof(TDomain), numeric);
        return numeric != 0 && Enum.IsDefined(parsed)
            ? parsed
            : throw InvalidArgument($"Unsupported {typeof(TDomain).Name} value {numeric}.");
    }
}
