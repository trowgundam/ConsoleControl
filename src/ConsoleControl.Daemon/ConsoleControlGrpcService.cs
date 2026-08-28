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

internal sealed class ConsoleControlGrpcService(
    ConsoleRuntime runtime,
    InputProfileStore inputProfiles,
    VideoRuntime video)
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

    public override async Task Control(
        IAsyncStreamReader<ControlStreamRequest> requestStream,
        IServerStreamWriter<ControlStreamEvent> responseStream,
        ServerCallContext context)
    {
        ClientId client = default;
        ControlLease? lease = null;
        try
        {
            if (!await MoveNextWithInactivityTimeoutAsync(requestStream, context.CancellationToken)
                    .ConfigureAwait(false) ||
                requestStream.Current.BodyCase != ControlStreamRequest.BodyOneofCase.Open)
            {
                throw InvalidArgument("The first control stream message must open the stream.");
            }

            OpenControlStream open = requestStream.Current.Open;
            client = ParseClient(open.ClientId);
            DomainPriority priority = open.Priority switch
            {
                ContractPriority.Automation => DomainPriority.Automation,
                ContractPriority.InteractiveUser => DomainPriority.InteractiveUser,
                _ => throw InvalidArgument("A control priority is required."),
            };
            lease = await runtime.AcquireControlAsync(
                client,
                priority,
                context.CancellationToken).ConfigureAwait(false);
            await responseStream.WriteAsync(new ControlStreamEvent
            {
                Granted = new ControlGranted { LeaseGeneration = lease.Generation.Value },
            }, context.CancellationToken).ConfigureAwait(false);

            while (await MoveNextWithInactivityTimeoutAsync(requestStream, context.CancellationToken)
                       .ConfigureAwait(false))
            {
                if (requestStream.Current.BodyCase != ControlStreamRequest.BodyOneofCase.State)
                {
                    throw InvalidArgument("A control stream may contain only one open message.");
                }
                await runtime.SetControllerStateAsync(
                    client,
                    lease.Generation,
                    ParseState(requestStream.Current.State),
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (ControlConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
        catch (StaleControlLeaseException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
        catch (TimeoutException)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                "The control stream stopped sending state heartbeats."));
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            lease is not null &&
            !runtime.BridgeConnected)
        {
            try
            {
                await responseStream.WriteAsync(new ControlStreamEvent
                {
                    Condition = ControlCondition.BridgeUnavailable,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Stream cleanup below remains authoritative when the client has also gone away.
            }
        }
        finally
        {
            if (lease is not null)
            {
                using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(1));
                try
                {
                    await runtime.TryReleaseControlAsync(client, lease.Generation, cleanup.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Firmware independently returns to neutral after its BLE timeout.
                }
            }
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

    public override async Task<VideoInventoryReply> GetVideoInventory(
        GetVideoInventoryRequest request,
        ServerCallContext context) =>
        ToReply(await video.GetInventoryAsync(context.CancellationToken).ConfigureAwait(false));

    public override async Task<VideoSelectionReply> SelectVideoSource(
        SelectVideoSourceRequest request,
        ServerCallContext context)
    {
        try
        {
            VideoSelection selection = await video.SelectAsync(
                new VideoSourceId(request.SourceId),
                request.ExpectedRevision,
                context.CancellationToken).ConfigureAwait(false);
            return new VideoSelectionReply
            {
                SourceId = selection.SourceId.Value,
                Revision = selection.Revision,
                Status = selection.Status,
            };
        }
        catch (VideoSelectionConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
        catch (ArgumentException exception)
        {
            throw InvalidArgument(exception.Message);
        }
    }

    private static ClientId ParseClient(string value) =>
        Guid.TryParse(value, out Guid parsed)
            ? new ClientId(parsed)
            : throw InvalidArgument("client_id must be a GUID.");

    private static async Task<bool> MoveNextWithInactivityTimeoutAsync(
        IAsyncStreamReader<ControlStreamRequest> stream,
        CancellationToken cancellationToken) =>
        await stream.MoveNext(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
            .ConfigureAwait(false);

    private static ControllerState ParseState(ControllerStateMessage request)
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

    private static VideoInventoryReply ToReply(VideoInventory inventory)
    {
        VideoInventoryReply reply = new()
        {
            Revision = inventory.Revision,
            LiveStreamUri = inventory.LiveStreamUri.ToString(),
            Status = inventory.Status,
        };
        if (inventory.SelectedSourceId is { } selected)
        {
            reply.SelectedSourceId = selected.Value;
        }
        reply.Sources.AddRange(inventory.Sources.Select(source => new VideoSourceMessage
        {
            Id = source.Id.Value,
            DisplayName = source.DisplayName,
            PreferredMode = new VideoModeMessage
            {
                Width = source.PreferredMode.Width,
                Height = source.PreferredMode.Height,
                FramesPerSecond = source.PreferredMode.FramesPerSecond,
            },
        }));
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
