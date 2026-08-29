using System.Collections.Immutable;

using ConsoleControl.Contracts;
using ConsoleControl.Core;

using Grpc.Core;

using ContractDigital = ConsoleControl.Contracts.CanonicalDigitalControl;
using ContractPriority = ConsoleControl.Contracts.ControlPriority;
using DomainDigital = ConsoleControl.Core.CanonicalDigitalControl;
using DomainPriority = ConsoleControl.Core.ControlPriority;

namespace ConsoleControl.Daemon;

internal sealed class ConsoleControlGrpcService(
    ConsoleRuntime runtime,
    VideoRuntime video,
    AutomationRuntime automation)
    : ConsoleControlService.ConsoleControlServiceBase
{
    public override async Task<GetStatusReply> GetStatus(
        GetStatusRequest request,
        ServerCallContext context)
    {
        ClientId client = ParseClient(request.ClientId);
        ControllerBridgeStatus bridge = await runtime.GetControllerBridgeStatusAsync(
            context.CancellationToken).ConfigureAwait(false);
        VideoCaptureStatus videoStatus = video.GetStatus();
        GetStatusReply reply = new()
        {
            DaemonVersion = typeof(ConsoleControlGrpcService).Assembly.GetName().Version?.ToString() ?? "unknown",
            ProtocolVersion = 1,
            ControlOwner = ToMessage(await runtime.GetControlOwnerAsync(
                client, context.CancellationToken).ConfigureAwait(false)),
            ControllerBridge = ToMessage(bridge),
            Video = ToMessage(videoStatus),
        };
        if (runtime.PendingControlRequest is { } pending)
        {
            reply.PendingControlRequestId = pending.Id.ToString("D");
            reply.PendingControlRequestReason = pending.Reason;
        }
        return reply;
    }

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
            lease = priority == DomainPriority.Automation
                ? await runtime.RequestAutomationControlAsync(
                    client,
                    open.HasRequestReason ? open.RequestReason : string.Empty,
                    context.CancellationToken).ConfigureAwait(false)
                : await runtime.AcquireControlAsync(
                    client,
                    priority,
                    context.CancellationToken).ConfigureAwait(false);
            await responseStream.WriteAsync(new ControlStreamEvent
            {
                Granted = new ControlGranted { LeaseGeneration = lease.Generation.Value },
            }, context.CancellationToken).ConfigureAwait(false);

            CancellationToken revoked = await runtime.GetRevocationTokenAsync(
                client, lease.Generation, context.CancellationToken).ConfigureAwait(false);
            using CancellationTokenSource streamLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, revoked);

            while (await MoveNextWithInactivityTimeoutAsync(requestStream, streamLifetime.Token)
                       .ConfigureAwait(false))
            {
                if (requestStream.Current.BodyCase == ControlStreamRequest.BodyOneofCase.Heartbeat &&
                    lease.Priority == DomainPriority.Automation)
                {
                    continue;
                }
                if (requestStream.Current.BodyCase != ControlStreamRequest.BodyOneofCase.State)
                {
                    throw InvalidArgument("A control stream may contain only one open message.");
                }
                await runtime.SetControllerStateAsync(
                    client,
                    lease.Generation,
                    ParseState(requestStream.Current.State),
                    streamLifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            lease is not null && !context.CancellationToken.IsCancellationRequested)
        {
            try
            {
                await responseStream.WriteAsync(new ControlStreamEvent
                {
                    Condition = ControlCondition.LeaseRevoked,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The generation is already revoked; cleanup remains authoritative.
            }
        }
        catch (ControlConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
        catch (ControlRequestDeclinedException exception)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, exception.Message));
        }
        catch (ControlRequestTimedOutException exception)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, exception.Message));
        }
        catch (ArgumentException exception)
        {
            throw InvalidArgument(exception.Message);
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

    public override async Task<VideoInventoryReply> GetVideoInventory(
        GetVideoInventoryRequest request,
        ServerCallContext context)
    {
        try
        {
            return ToReply(await video.GetInventoryAsync(context.CancellationToken).ConfigureAwait(false));
        }
        catch (ConsoleOperationException exception)
        {
            throw ToRpcException(exception);
        }
    }

    public override async Task<ControllerBridgeInventoryReply> GetControllerBridgeInventory(
        GetControllerBridgeInventoryRequest request,
        ServerCallContext context)
    {
        try
        {
            return ToReply(await runtime.GetControllerBridgeInventoryAsync(context.CancellationToken).ConfigureAwait(false));
        }
        catch (ConsoleOperationException exception)
        {
            throw ToRpcException(exception);
        }
    }

    public override async Task<ControllerBridgeSelectionReply> SelectControllerBridge(
        SelectControllerBridgeRequest request,
        ServerCallContext context)
    {
        try
        {
            ControllerBridgeSelection selection = await runtime.SelectControllerBridgeAsync(
                new ControllerBridgeId(request.BridgeId),
                request.ExpectedRevision,
                context.CancellationToken).ConfigureAwait(false);
            return new()
            {
                BridgeId = selection.BridgeId.Value,
                Revision = selection.Revision,
                State = ToMessage(selection.State),
                Status = selection.Status,
            };
        }
        catch (ControllerBridgeSelectionConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.Aborted, exception.Message));
        }
        catch (ControllerBridgeControlInUseException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
        catch (ConsoleOperationException exception)
        {
            throw ToRpcException(exception);
        }
        catch (ArgumentException exception)
        {
            throw InvalidArgument(exception.Message);
        }
    }

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
        catch (ConsoleOperationException exception)
        {
            throw ToRpcException(exception);
        }
        catch (ArgumentException exception)
        {
            throw InvalidArgument(exception.Message);
        }
    }

    public override Task<ScreenshotReply> GetScreenshot(
        GetScreenshotRequest request,
        ServerCallContext context)
    {
        try
        {
            return Task.FromResult(ToReply(video.CaptureLatest()));
        }
        catch (ScreenshotUnavailableException exception)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, exception.Message));
        }
    }

    public override async Task<AutomationResultReply> RunAutomation(
        RunAutomationRequest request,
        ServerCallContext context)
    {
        try
        {
            AutomationResult result = await automation.ExecuteAsync(
                ParseClient(request.ClientId),
                new LeaseGeneration(request.LeaseGeneration),
                ParseAutomation(request),
                context.CancellationToken).ConfigureAwait(false);
            return ToReply(result);
        }
        catch (ArgumentException exception)
        {
            throw InvalidArgument(exception.Message);
        }
        catch (OverflowException exception)
        {
            throw InvalidArgument(exception.Message);
        }
        catch (AutomationBusyException exception)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, exception.Message));
        }
        catch (StaleControlLeaseException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }

    public override async Task<DeclineControlRequestReply> DeclineControlRequest(
        DeclineControlRequestMessage request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.RequestId, out Guid requestId))
        {
            throw InvalidArgument("request_id must be a GUID.");
        }
        return new()
        {
            Declined = await runtime.DeclineControlRequestAsync(
                requestId, context.CancellationToken).ConfigureAwait(false),
        };
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

    internal static ControllerState ParseState(ControllerStateMessage request)
    {
        const uint knownButtons = (uint)(GameButtons.Y | GameButtons.B | GameButtons.A | GameButtons.X |
            GameButtons.LeftShoulder | GameButtons.RightShoulder | GameButtons.LeftTrigger |
            GameButtons.RightTrigger | GameButtons.Minus | GameButtons.Plus | GameButtons.LeftStick |
            GameButtons.RightStick | GameButtons.Home | GameButtons.Capture);
        if ((request.Buttons & ~knownButtons) != 0 ||
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

    private static AutomationSequence ParseAutomation(RunAutomationRequest request) => new(
        request.Commands.Select(ParseAutomationCommand).ToImmutableArray(),
        request.CaptureStart,
        request.CaptureEnd);

    private static AutomationCommand ParseAutomationCommand(AutomationCommandMessage message) =>
        message.BodyCase switch
        {
            AutomationCommandMessage.BodyOneofCase.Press => new AutomationCommand.Press(
                ParseEnum<DomainDigital, ContractDigital>(message.Press.Control),
                Milliseconds(message.Press.DurationMs)),
            AutomationCommandMessage.BodyOneofCase.Hold => new AutomationCommand.Hold(
                ParseEnum<DomainDigital, ContractDigital>(message.Hold.Control),
                Milliseconds(message.Hold.DurationMs)),
            AutomationCommandMessage.BodyOneofCase.PauseMs =>
                new AutomationCommand.Pause(Milliseconds(message.PauseMs)),
            AutomationCommandMessage.BodyOneofCase.Capture =>
                new AutomationCommand.Capture(message.Capture.Name),
            _ => throw InvalidArgument("Every automation command needs a body."),
        };

    private static TimeSpan Milliseconds(uint value) => TimeSpan.FromMilliseconds(value);

    private static ScreenshotReply ToReply(Screenshot screenshot) => new()
    {
        Generation = screenshot.Generation,
        Sequence = screenshot.Sequence,
        Mode = new VideoModeMessage
        {
            Width = screenshot.Mode.Width,
            Height = screenshot.Mode.Height,
            FramesPerSecond = screenshot.Mode.FramesPerSecond,
        },
        ReceivedAtUnixMs = screenshot.ReceivedAt.ToUnixTimeMilliseconds(),
        Jpeg = Google.Protobuf.ByteString.CopyFrom(screenshot.Jpeg),
    };

    private static AutomationResultReply ToReply(AutomationResult result)
    {
        AutomationResultReply reply = new()
        {
            Outcome = (AutomationOutcomeMessage)((int)result.Outcome + 1),
            ElapsedMs = checked((uint)Math.Ceiling(result.Elapsed.TotalMilliseconds)),
        };
        if (result.Detail is not null)
        {
            reply.Detail = result.Detail;
        }
        reply.Captures.AddRange(result.Captures.Select(capture =>
        {
            AutomationCaptureReply item = new()
            {
                Name = capture.Name,
                ScheduledAtMs = checked((uint)Math.Ceiling(capture.ScheduledAt.TotalMilliseconds)),
                ActualAtMs = checked((uint)Math.Ceiling(capture.ActualAt.TotalMilliseconds)),
            };
            if (capture.Screenshot is not null)
            {
                item.Screenshot = ToReply(capture.Screenshot);
            }
            if (capture.Failure is not null)
            {
                item.Failure = capture.Failure;
            }
            return item;
        }));
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

    private static ControllerBridgeInventoryReply ToReply(ControllerBridgeInventory inventory)
    {
        ControllerBridgeInventoryReply reply = new()
        {
            Revision = inventory.Revision,
            State = ToMessage(inventory.State),
            Status = inventory.Status,
        };
        if (inventory.SelectedBridgeId is { } selected)
        {
            reply.SelectedBridgeId = selected.Value;
        }
        reply.Bridges.AddRange(inventory.Bridges.Select(bridge => new ControllerBridgeMessage
        {
            Id = bridge.Id.Value,
            DisplayName = bridge.DisplayName,
            Connected = bridge.BluetoothConnected,
        }));
        return reply;
    }

    private static ControllerBridgeStateMessage ToMessage(ControllerBridgeState state) => state switch
    {
        ControllerBridgeState.SelectionRequired => ControllerBridgeStateMessage.SelectionRequired,
        ControllerBridgeState.Unavailable => ControllerBridgeStateMessage.Unavailable,
        ControllerBridgeState.Disconnected => ControllerBridgeStateMessage.Disconnected,
        ControllerBridgeState.Ready => ControllerBridgeStateMessage.Ready,
        ControllerBridgeState.Faulted => ControllerBridgeStateMessage.Faulted,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static ControlOwnerMessage ToMessage(ControlOwner owner) => owner switch
    {
        ControlOwner.None => ControlOwnerMessage.None,
        ControlOwner.ThisClient => ControlOwnerMessage.ThisClient,
        ControlOwner.InteractiveClient => ControlOwnerMessage.InteractiveClient,
        ControlOwner.AutomationClient => ControlOwnerMessage.AutomationClient,
        _ => throw new ArgumentOutOfRangeException(nameof(owner)),
    };

    private static HardwareAvailabilityMessage ToMessage(HardwareAvailability availability) => availability switch
    {
        HardwareAvailability.Unknown => HardwareAvailabilityMessage.Unknown,
        HardwareAvailability.Available => HardwareAvailabilityMessage.Available,
        HardwareAvailability.Unavailable => HardwareAvailabilityMessage.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(availability)),
    };

    private static ControllerBridgeStatusMessage ToMessage(ControllerBridgeStatus status)
    {
        ControllerBridgeStatusMessage message = new()
        {
            Availability = ToMessage(status.Availability),
            OutputConnection = status.OutputConnection switch
            {
                ControllerOutputConnection.NotConfigured => ControllerOutputConnectionMessage.NotConfigured,
                ControllerOutputConnection.DisconnectedUntilInput => ControllerOutputConnectionMessage.DisconnectedUntilInput,
                ControllerOutputConnection.Connected => ControllerOutputConnectionMessage.Connected,
                ControllerOutputConnection.Faulted => ControllerOutputConnectionMessage.Faulted,
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            },
            Detail = status.Detail,
        };
        if (status.SelectedBridgeId is { } selected)
        {
            message.SelectedBridgeId = selected.Value;
        }
        if (status.LastInventoryAt is { } lastInventory)
        {
            message.LastInventoryAtUnixMs = lastInventory.ToUnixTimeMilliseconds();
        }
        return message;
    }

    private static VideoCaptureStatusMessage ToMessage(VideoCaptureStatus status)
    {
        VideoCaptureStatusMessage message = new()
        {
            Availability = ToMessage(status.Availability),
            CaptureState = status.CaptureState switch
            {
                VideoCaptureState.SelectionRequired => VideoCaptureStateMessage.SelectionRequired,
                VideoCaptureState.Starting => VideoCaptureStateMessage.Starting,
                VideoCaptureState.Streaming => VideoCaptureStateMessage.Streaming,
                VideoCaptureState.Reconnecting => VideoCaptureStateMessage.Reconnecting,
                VideoCaptureState.Faulted => VideoCaptureStateMessage.Faulted,
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            },
            Detail = status.Detail,
        };
        if (status.SelectedSourceId is { } selected)
        {
            message.SelectedSourceId = selected.Value;
        }
        if (status.ActiveMode is { } mode)
        {
            message.ActiveMode = new()
            {
                Width = mode.Width,
                Height = mode.Height,
                FramesPerSecond = mode.FramesPerSecond,
            };
        }
        if (status.LatestFrameAt is { } latest)
        {
            message.LatestFrameAtUnixMs = latest.ToUnixTimeMilliseconds();
        }
        return message;
    }

    private static RpcException ToRpcException(ConsoleOperationException exception)
    {
        Metadata trailers = new()
        {
            { "console-failure-code", ToFailureCode(exception.Code) },
            { "console-retryable", exception.Retryable ? "true" : "false" },
        };
        return new RpcException(new Status(StatusCode.Unavailable, exception.Message), trailers);
    }

    private static string ToFailureCode(ConsoleFailureCode code) => code switch
    {
        ConsoleFailureCode.ControllerBridgeInventoryFailed => "controller_bridge_inventory_failed",
        ConsoleFailureCode.VideoSourceInventoryFailed => "video_source_inventory_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
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