using Avalonia.Input;

using ConsoleControl.Client;
using ConsoleControl.Core;
using ConsoleControl.Gui;
using ConsoleControl.Input.Sdl;

internal static class InputForwardingChecks
{
    public static async Task RunAsync()
    {
        ControllerStateMailbox mailbox = new();
        ControllerState pressed = ControllerState.Neutral with { Buttons = GameButtons.A };
        ControllerState released = ControllerState.Neutral;

        TestAssert.Require(mailbox.Publish(pressed), "the first digital transition did not schedule a drain");
        mailbox.Publish(released);
        TestAssert.Require(mailbox.TryTake(out ControllerState first) && first == pressed,
            "a press was lost when release arrived before the drain");
        TestAssert.Require(mailbox.TryTake(out ControllerState second) && second == released,
            "a release was lost when it arrived before the drain");

        ControllerState analogOne = released with { LeftStick = new(140, 128) };
        ControllerState analogTwo = released with { LeftStick = new(200, 128) };
        mailbox.Publish(analogOne);
        mailbox.Publish(analogTwo);
        TestAssert.Require(mailbox.TryTake(out ControllerState analog) && analog == analogTwo,
            "analog-only snapshots were not coalesced to the latest state");
        TestAssert.Require(!mailbox.TryTake(out _), "the mailbox retained a stale analog snapshot");

        ControllerState triggerPressed = analogTwo with
        {
            Buttons = GameButtons.RightTrigger,
            RightTrigger = new(180),
        };
        ControllerState triggerReleased = analogTwo with
        {
            RightTrigger = TriggerPosition.Released,
        };
        mailbox.Publish(triggerPressed);
        mailbox.Publish(triggerReleased);
        TestAssert.Require(mailbox.TryTake(out ControllerState triggerDown)
            && triggerDown.Buttons.HasFlag(GameButtons.RightTrigger),
            "a trigger threshold press was coalesced as analog motion");
        TestAssert.Require(mailbox.TryTake(out ControllerState triggerUp)
            && !triggerUp.Buttons.HasFlag(GameButtons.RightTrigger),
            "a trigger threshold release was coalesced as analog motion");

        GamepadDeviceId device = new(42);
        InputSourceOption selected = new(
            "gamepad:42",
            "Test gamepad",
            new(InputSourceKind.Gamepad, "test-guid"),
            device);
        GamepadSnapshot stale = new(device, 7, HostInputSnapshot.Empty);
        TestAssert.Require(!InputForwarder.IsCurrentGamepadSnapshot(selected, 8, stale),
            "a snapshot from an earlier selection generation was accepted");
        TestAssert.Require(InputForwarder.IsCurrentGamepadSnapshot(selected, 7, stale),
            "a snapshot from the active selection generation was rejected");

        ControllerState stickClicks = ControllerStateComposer.FromDigitalControls([
            CanonicalDigitalControl.LeftStickClick,
            CanonicalDigitalControl.RightStickClick,
        ]);
        TestAssert.Require(stickClicks.Buttons.HasFlag(GameButtons.LeftStick)
            && stickClicks.Buttons.HasFlag(GameButtons.RightStick),
            "canonical stick clicks did not compose to L3 and R3 buttons");
        TestAssert.Require((int)CanonicalDigitalControl.DPadLeft ==
                (int)ConsoleControl.Contracts.CanonicalDigitalControl.DpadLeft
            && (int)CanonicalDigitalControl.LeftStickClick ==
                (int)ConsoleControl.Contracts.CanonicalDigitalControl.LeftStickClick
            && (int)CanonicalDigitalControl.RightStickClick ==
                (int)ConsoleControl.Contracts.CanonicalDigitalControl.RightStickClick,
            "domain and protobuf digital-control values diverged");

        InputSourceOption keyboard = InputSourceOption.Keyboard;
        InputProfileKey steamKey = new(InputSourceKind.Gamepad, "steam-guid");
        InputSourceOption steam = new("gamepad:9", "Steam Controller", steamKey, new(9));
        TestAssert.Require(MainWindowViewModel.ChooseInputSource([keyboard, steam], keyboard, steamKey) == steam,
            "an available preferred controller was not restored");
        TestAssert.Require(MainWindowViewModel.ChooseInputSource([keyboard], null, steamKey) == keyboard,
            "a missing preferred controller did not fall back to Keyboard");
        TestAssert.Require(
            MainWindowViewModel.NextOnScreenControllerLayout(OnScreenControllerLayout.Compact) ==
                OnScreenControllerLayout.Full,
            "the compact controller layout did not advance to full");
        TestAssert.Require(
            MainWindowViewModel.NextOnScreenControllerLayout(OnScreenControllerLayout.Full) ==
                OnScreenControllerLayout.Hidden,
            "the full controller layout did not advance to hidden");
        TestAssert.Require(
            MainWindowViewModel.NextOnScreenControllerLayout(OnScreenControllerLayout.Hidden) ==
                OnScreenControllerLayout.Compact,
            "the hidden controller layout did not advance to compact");
        TestAssert.Require(
            MainWindowViewModel.FormatSessionDetailText(
                DaemonAvailability.Unavailable,
                "Unavailable: connection refused") ==
            "Unavailable: connection refused Start the ConsoleControl daemon, or click the status to retry.",
            "the unavailable-daemon tooltip discarded the connection failure detail");
        TestAssert.Require(
            MainWindowViewModel.IsActiveControlState(ControlConnectionState.Ready),
            "a ready control session was not treated as active");
        foreach (ControlConnectionState inactiveState in Enum.GetValues<ControlConnectionState>()
                     .Where(state => state != ControlConnectionState.Ready))
        {
            TestAssert.Require(
                !MainWindowViewModel.IsActiveControlState(inactiveState),
                $"a {inactiveState} control session was incorrectly treated as active");
        }
        TestAssert.Require(
            MainWindowViewModel.ControlSessionHeadline(ControlConnectionState.Reconnecting) ==
                "Reconnecting control",
            "the session pill did not expose the reconnecting control state");
        InputProfile gamepadProfile = DefaultInputProfiles.For(steamKey);
        HostControlId heldGamepadButton = gamepadProfile.DigitalBindings[0].Source;
        ControllerState heldGamepadState = InputMapper.Map(
            gamepadProfile,
            new([heldGamepadButton], []));
        (ControllerState suppressedGamepadState, bool stillSuppressed) =
            InputForwarder.ApplyGamepadLeaseBoundary(heldGamepadState, suppressUntilNeutral: true);
        TestAssert.Require(suppressedGamepadState == ControllerState.Neutral && stillSuppressed,
            "a gamepad button held before lease acquisition was replayed under the new lease");
        (ControllerState releasedGamepadState, stillSuppressed) =
            InputForwarder.ApplyGamepadLeaseBoundary(ControllerState.Neutral, stillSuppressed);
        TestAssert.Require(releasedGamepadState == ControllerState.Neutral && !stillSuppressed,
            "a neutral gamepad snapshot did not clear lease-boundary suppression");
        (ControllerState freshGamepadState, _) =
            InputForwarder.ApplyGamepadLeaseBoundary(heldGamepadState, stillSuppressed);
        TestAssert.Require(freshGamepadState == heldGamepadState,
            "fresh gamepad input stayed suppressed after returning to neutral");
        TaskCompletionSource ignoredCancellation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TestAssert.Require(
            !await InputForwarder.CompletesWithinAsync(
                ignoredCancellation.Task,
                TimeSpan.FromMilliseconds(20)),
            "a session operation that ignored cancellation was not bounded independently");

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"consolecontrol-detached-input-{Guid.NewGuid():N}");
        try
        {
            GuiConfigurationStore configuration = new(Path.Combine(directory, "gui-configuration.json"));
            configuration.Load();
            await using InputForwarder input = new(new SdlGamepadManager(), configuration);

            TestAssert.Require(input.Sources.Contains(InputSourceOption.Keyboard),
                "Keyboard was unavailable before acquiring a control lease");
            await input.SelectSourceAsync(InputSourceOption.Keyboard, CancellationToken.None);

            Task<CapturedHostControl> capture = input.CaptureNextInputAsync(CancellationToken.None);
            TestAssert.Require(input.SetKey(PhysicalKey.F1, true),
                "detached keyboard input did not reach mapping capture");
            CapturedHostControl captured = await capture;
            TestAssert.Require(captured.Control == DefaultInputProfiles.KeyboardId(PhysicalKey.F1),
                "detached mapping capture returned the wrong physical key");

            await input.SaveAndActivateProfileAsync(
                DefaultInputProfiles.For(InputProfileKey.Keyboard),
                CancellationToken.None);
            TestAssert.Require(configuration.Current.Profiles.Any(
                    profile => profile.Key == InputProfileKey.Keyboard),
                "a detached mapping profile was not saved");
            int observerActivities = 0;
            input.InputActivityDetected += (_, _) => observerActivities++;
            await input.SetKeyboardFocusAsync(true, CancellationToken.None);
            TestAssert.Require(!input.SetKey(PhysicalKey.X, true),
                "observer-mode keyboard input was swallowed outside mapping capture");
            TestAssert.Require(observerActivities == 1,
                "mapped observer input did not report activity");
            input.SetKey(PhysicalKey.X, true);
            TestAssert.Require(observerActivities == 1,
                "a held observer input repeatedly reported activity");
            input.SetKey(PhysicalKey.X, false);
            input.SetKey(PhysicalKey.X, true);
            TestAssert.Require(observerActivities == 2,
                "a second observer input edge did not report activity");

            RecordingControlSession firstControl = new();
            await input.AttachControlAsync(firstControl, CancellationToken.None);
            TestAssert.Require(firstControl.States.SequenceEqual([ControllerState.Neutral]),
                "attaching a lease did not begin with a complete neutral snapshot");
            input.SetKey(PhysicalKey.X, true);
            TestAssert.Require(firstControl.States.SequenceEqual([ControllerState.Neutral]),
                "a keyboard key held before lease acquisition was replayed under the new lease");
            input.SetKey(PhysicalKey.X, false);
            input.SetKey(PhysicalKey.X, true);
            await WaitUntilAsync(() => firstControl.States.Count >= 2);
            TestAssert.Require(firstControl.States[^1] != ControllerState.Neutral,
                "fresh keyboard input stayed suppressed after the held key was released");
            input.SetKey(PhysicalKey.X, false);
            await WaitUntilAsync(() => firstControl.States[^1] == ControllerState.Neutral);

            Task oldLeasePulse = input.PulseAsync(
                CanonicalDigitalControl.A,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);
            await WaitUntilAsync(() => firstControl.States.Count >= 2);
            await input.DetachControlAsync(CancellationToken.None);
            TestAssert.Require(firstControl.States[^1] == ControllerState.Neutral,
                "detaching a control lease did not finish through neutralization");

            RecordingControlSession secondControl = new();
            await input.AttachControlAsync(secondControl, CancellationToken.None);
            await oldLeasePulse;
            TestAssert.Require(secondControl.States.SequenceEqual([ControllerState.Neutral]),
                "delayed work from an old lease wrote through the replacement lease");
            await input.DetachControlAsync(CancellationToken.None);

            RecordingControlSession blockedControl = new();
            await input.AttachControlAsync(blockedControl, CancellationToken.None);
            TestAssert.Require(input.SetKey(PhysicalKey.X, true),
                "the cancellation regression setup did not publish a pressed state");
            await WaitUntilAsync(() => blockedControl.States.Count >= 2);
            Task blockedWriteEntered = blockedControl.BlockNextWrite();
            Task blockedNeutral = input.SetKeyboardFocusAsync(false, CancellationToken.None);
            await blockedWriteEntered;

            using CancellationTokenSource captureStop = new();
            Task<CapturedHostControl> cancelledCapture =
                input.CaptureNextInputAsync(captureStop.Token);
            captureStop.Cancel();
            try
            {
                await cancelledCapture;
                throw new InvalidOperationException("a cancelled mapping capture completed successfully");
            }
            catch (OperationCanceledException)
            {
            }

            blockedControl.ReleaseBlockedWrite();
            await blockedNeutral;
            Task<CapturedHostControl> captureAfterCancellation =
                input.CaptureNextInputAsync(CancellationToken.None);
            TestAssert.Require(input.SetKey(PhysicalKey.F2, true),
                "mapping capture could not restart after cancellation while the input queue was busy");
            CapturedHostControl recaptured = await captureAfterCancellation;
            TestAssert.Require(recaptured.Control == DefaultInputProfiles.KeyboardId(PhysicalKey.F2),
                "the restarted mapping capture returned the wrong physical key");
            await input.DetachControlAsync(CancellationToken.None);

            RecordingControlSession waitingControl = new(ControlConnectionState.WaitingForBridge);
            await input.AttachControlAsync(waitingControl, CancellationToken.None);
            TestAssert.Require(waitingControl.States.Count == 0,
                "attaching while the controller bridge was unavailable waited on a neutral write");
            await input.DetachControlAsync(CancellationToken.None);
            TestAssert.Require(waitingControl.Disposed,
                "detaching a reconnecting control session did not dispose it");

            RecordingControlSession reconnectingControl = new();
            await input.AttachControlAsync(reconnectingControl, CancellationToken.None);
            int statesBeforeReconnect = reconnectingControl.States.Count;
            reconnectingControl.SetConnectionState(ControlConnectionState.Reconnecting);
            await input.DetachControlAsync(CancellationToken.None);
            TestAssert.Require(reconnectingControl.States.Count == statesBeforeReconnect,
                "detaching during reconnect attempted an uncompletable neutral write");
            TestAssert.Require(reconnectingControl.Disposed,
                "detaching during reconnect did not dispose the control session");

            RecordingControlSession cancellationIgnoringControl = new();
            Task ignoredWriteEntered = cancellationIgnoringControl.BlockNextWrite();
            Task boundedAttach = input.AttachControlAsync(
                cancellationIgnoringControl,
                CancellationToken.None);
            await ignoredWriteEntered;
            await boundedAttach.WaitAsync(TimeSpan.FromSeconds(2));
            cancellationIgnoringControl.SetConnectionState(ControlConnectionState.Reconnecting);
            await input.DetachControlAsync(CancellationToken.None);
            cancellationIgnoringControl.ReleaseBlockedWrite();
            TestAssert.Require(cancellationIgnoringControl.Disposed,
                "a cancellation-ignoring control session prevented bounded detach");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class RecordingControlSession(
        ControlConnectionState connectionState = ControlConnectionState.Ready) : IControlSession
    {
        private readonly object _gate = new();
        private readonly List<ControllerState> _states = [];
        private TaskCompletionSource? _blockedWrite;
        private TaskCompletionSource? _blockedWriteEntered;

        public ControlConnectionState ConnectionState { get; private set; } = connectionState;
        public event EventHandler<ControlConnectionState>? ConnectionStateChanged;
        public bool Disposed { get; private set; }

        public IReadOnlyList<ControllerState> States
        {
            get
            {
                lock (_gate)
                {
                    return _states.ToArray();
                }
            }
        }

        public Task SetStateAsync(ControllerState state, CancellationToken cancellationToken)
        {
            Task? blockedWrite;
            lock (_gate)
            {
                _states.Add(state);
                blockedWrite = _blockedWrite?.Task;
                _blockedWriteEntered?.TrySetResult();
            }
            return blockedWrite ?? Task.CompletedTask;
        }

        public Task BlockNextWrite()
        {
            lock (_gate)
            {
                _blockedWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _blockedWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return _blockedWriteEntered.Task;
            }
        }

        public void ReleaseBlockedWrite()
        {
            lock (_gate)
            {
                _blockedWrite?.TrySetResult();
                _blockedWrite = null;
                _blockedWriteEntered = null;
            }
        }

        public void SetConnectionState(ControlConnectionState state)
        {
            ConnectionState = state;
            ConnectionStateChanged?.Invoke(this, state);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}