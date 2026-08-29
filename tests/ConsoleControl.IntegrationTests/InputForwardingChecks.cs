using ConsoleControl.Core;
using ConsoleControl.Gui;
using ConsoleControl.Input.Sdl;

internal static class InputForwardingChecks
{
    public static void Run()
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
    }
}