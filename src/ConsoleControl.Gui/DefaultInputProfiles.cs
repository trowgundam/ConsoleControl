using System.Collections.Immutable;
using Avalonia.Input;
using ConsoleControl.Core;
using ConsoleControl.Input.Sdl;

namespace ConsoleControl.Gui;

internal static class DefaultInputProfiles
{
    public static InputProfile For(InputProfileKey key) => key.Kind switch
    {
        InputSourceKind.Keyboard => Keyboard,
        InputSourceKind.Gamepad => SdlDefaultInputProfile.Create(key.HardwareId),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    public static HostControlId KeyboardId(PhysicalKey key) =>
        new($"keyboard.physical.{key.ToString().ToLowerInvariant()}");

    private static InputProfile Keyboard { get; } = new InputProfile(
        InputProfileKey.Keyboard,
        "Keyboard",
        [
            Bind(PhysicalKey.X, CanonicalDigitalControl.A),
            Bind(PhysicalKey.Z, CanonicalDigitalControl.B),
            Bind(PhysicalKey.S, CanonicalDigitalControl.X),
            Bind(PhysicalKey.A, CanonicalDigitalControl.Y),
            Bind(PhysicalKey.Q, CanonicalDigitalControl.LeftShoulder),
            Bind(PhysicalKey.E, CanonicalDigitalControl.RightShoulder),
            Bind(PhysicalKey.Digit1, CanonicalDigitalControl.LeftTrigger),
            Bind(PhysicalKey.Digit3, CanonicalDigitalControl.RightTrigger),
            Bind(PhysicalKey.Tab, CanonicalDigitalControl.Minus),
            Bind(PhysicalKey.Enter, CanonicalDigitalControl.Plus),
            Bind(PhysicalKey.H, CanonicalDigitalControl.Home),
            Bind(PhysicalKey.C, CanonicalDigitalControl.Capture),
            Bind(PhysicalKey.ArrowUp, CanonicalDigitalControl.DPadUp),
            Bind(PhysicalKey.ArrowRight, CanonicalDigitalControl.DPadRight),
            Bind(PhysicalKey.ArrowDown, CanonicalDigitalControl.DPadDown),
            Bind(PhysicalKey.ArrowLeft, CanonicalDigitalControl.DPadLeft),
        ],
        [],
        []).Validate();

    private static DigitalBinding Bind(PhysicalKey source, params CanonicalDigitalControl[] targets) =>
        new(KeyboardId(source), targets.ToImmutableArray());

}
