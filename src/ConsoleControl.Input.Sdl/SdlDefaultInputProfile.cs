using System.Collections.Immutable;

using ConsoleControl.Core;

using SDL3;

namespace ConsoleControl.Input.Sdl;

public static class SdlDefaultInputProfile
{
    public static InputProfile Create(string guid) => new InputProfile(
        new(InputSourceKind.Gamepad, guid),
        "Standard gamepad",
        [
            Bind(SDL.GamepadButton.East, CanonicalDigitalControl.A),
            Bind(SDL.GamepadButton.South, CanonicalDigitalControl.B),
            Bind(SDL.GamepadButton.North, CanonicalDigitalControl.X),
            Bind(SDL.GamepadButton.West, CanonicalDigitalControl.Y),
            Bind(SDL.GamepadButton.LeftShoulder, CanonicalDigitalControl.LeftShoulder),
            Bind(SDL.GamepadButton.RightShoulder, CanonicalDigitalControl.RightShoulder),
            Bind(SDL.GamepadButton.Back, CanonicalDigitalControl.Minus),
            Bind(SDL.GamepadButton.Start, CanonicalDigitalControl.Plus),
            Bind(SDL.GamepadButton.Guide, CanonicalDigitalControl.Home),
            Bind(SDL.GamepadButton.Misc1, CanonicalDigitalControl.Capture),
            Bind(SDL.GamepadButton.LeftStick, CanonicalDigitalControl.LeftStickClick),
            Bind(SDL.GamepadButton.RightStick, CanonicalDigitalControl.RightStickClick),
            Bind(SDL.GamepadButton.DPadUp, CanonicalDigitalControl.DPadUp),
            Bind(SDL.GamepadButton.DPadRight, CanonicalDigitalControl.DPadRight),
            Bind(SDL.GamepadButton.DPadDown, CanonicalDigitalControl.DPadDown),
            Bind(SDL.GamepadButton.DPadLeft, CanonicalDigitalControl.DPadLeft),
        ],
        [
            new(SdlGamepadManager.AxisId(SDL.GamepadAxis.LeftX),
                SdlGamepadManager.AxisId(SDL.GamepadAxis.LeftY), CanonicalStick.Left,
                AxisTransform.StickDefault, AxisTransform.StickDefault with { Inverted = true }),
            new(SdlGamepadManager.AxisId(SDL.GamepadAxis.RightX),
                SdlGamepadManager.AxisId(SDL.GamepadAxis.RightY), CanonicalStick.Right,
                AxisTransform.StickDefault, AxisTransform.StickDefault with { Inverted = true }),
        ],
        [
            new(SdlGamepadManager.AxisId(SDL.GamepadAxis.LeftTrigger), CanonicalTrigger.Left,
                AxisTransform.TriggerDefault, 0.5f),
            new(SdlGamepadManager.AxisId(SDL.GamepadAxis.RightTrigger), CanonicalTrigger.Right,
                AxisTransform.TriggerDefault, 0.5f),
        ]).Validate();

    private static DigitalBinding Bind(
        SDL.GamepadButton source,
        params CanonicalDigitalControl[] targets) =>
        new(SdlGamepadManager.ButtonId(source), targets.ToImmutableArray());
}