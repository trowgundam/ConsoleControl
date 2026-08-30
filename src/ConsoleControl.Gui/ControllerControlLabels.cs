using ConsoleControl.Core;

namespace ConsoleControl.Gui;

internal static class ControllerControlLabels
{
    internal static string For(CanonicalDigitalControl control) => control switch
    {
        CanonicalDigitalControl.LeftShoulder => "L",
        CanonicalDigitalControl.RightShoulder => "R",
        CanonicalDigitalControl.LeftTrigger => "ZL",
        CanonicalDigitalControl.RightTrigger => "ZR",
        CanonicalDigitalControl.LeftStickClick => "L3",
        CanonicalDigitalControl.RightStickClick => "R3",
        CanonicalDigitalControl.DPadUp => "D-pad up",
        CanonicalDigitalControl.DPadRight => "D-pad right",
        CanonicalDigitalControl.DPadDown => "D-pad down",
        CanonicalDigitalControl.DPadLeft => "D-pad left",
        _ => control.ToString(),
    };
}