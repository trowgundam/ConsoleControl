namespace ConsoleControl.Core;

public static class ControllerStateComposer
{
    public static ControllerState FromDigitalControls(
        IEnumerable<CanonicalDigitalControl> controls) =>
        WithDigitalControls(ControllerState.Neutral, controls);

    public static ControllerState WithDigitalControls(
        ControllerState state,
        IEnumerable<CanonicalDigitalControl> controls)
    {
        HashSet<CanonicalDigitalControl> active = controls.ToHashSet();
        return state with { Buttons = ToButtons(active), DPad = ToHat(active) };
    }

    public static ControllerState AddDigitalControls(
        ControllerState state,
        IEnumerable<CanonicalDigitalControl> controls)
    {
        HashSet<CanonicalDigitalControl> active = controls.ToHashSet();
        AddHatDirections(state.DPad, active);
        return state with
        {
            Buttons = state.Buttons | ToButtons(active),
            DPad = ToHat(active),
        };
    }

    private static GameButtons ToButtons(HashSet<CanonicalDigitalControl> controls)
    {
        GameButtons buttons = GameButtons.None;
        foreach (CanonicalDigitalControl control in controls)
        {
            buttons |= control switch
            {
                CanonicalDigitalControl.A => GameButtons.A,
                CanonicalDigitalControl.B => GameButtons.B,
                CanonicalDigitalControl.X => GameButtons.X,
                CanonicalDigitalControl.Y => GameButtons.Y,
                CanonicalDigitalControl.LeftShoulder => GameButtons.LeftShoulder,
                CanonicalDigitalControl.RightShoulder => GameButtons.RightShoulder,
                CanonicalDigitalControl.LeftTrigger => GameButtons.LeftTrigger,
                CanonicalDigitalControl.RightTrigger => GameButtons.RightTrigger,
                CanonicalDigitalControl.Minus => GameButtons.Minus,
                CanonicalDigitalControl.Plus => GameButtons.Plus,
                CanonicalDigitalControl.Home => GameButtons.Home,
                CanonicalDigitalControl.Capture => GameButtons.Capture,
                CanonicalDigitalControl.LeftStickClick => GameButtons.LeftStick,
                CanonicalDigitalControl.RightStickClick => GameButtons.RightStick,
                _ => GameButtons.None,
            };
        }

        return buttons;
    }

    private static HatPosition ToHat(HashSet<CanonicalDigitalControl> controls)
    {
        int vertical = (controls.Contains(CanonicalDigitalControl.DPadDown) ? 1 : 0)
            - (controls.Contains(CanonicalDigitalControl.DPadUp) ? 1 : 0);
        int horizontal = (controls.Contains(CanonicalDigitalControl.DPadRight) ? 1 : 0)
            - (controls.Contains(CanonicalDigitalControl.DPadLeft) ? 1 : 0);
        return (horizontal, vertical) switch
        {
            (0, -1) => HatPosition.Up,
            (1, -1) => HatPosition.UpRight,
            (1, 0) => HatPosition.Right,
            (1, 1) => HatPosition.DownRight,
            (0, 1) => HatPosition.Down,
            (-1, 1) => HatPosition.DownLeft,
            (-1, 0) => HatPosition.Left,
            (-1, -1) => HatPosition.UpLeft,
            _ => HatPosition.Neutral,
        };
    }

    private static void AddHatDirections(
        HatPosition hat,
        HashSet<CanonicalDigitalControl> controls)
    {
        if (hat is HatPosition.Up or HatPosition.UpRight or HatPosition.UpLeft)
        {
            controls.Add(CanonicalDigitalControl.DPadUp);
        }
        if (hat is HatPosition.Right or HatPosition.UpRight or HatPosition.DownRight)
        {
            controls.Add(CanonicalDigitalControl.DPadRight);
        }
        if (hat is HatPosition.Down or HatPosition.DownRight or HatPosition.DownLeft)
        {
            controls.Add(CanonicalDigitalControl.DPadDown);
        }
        if (hat is HatPosition.Left or HatPosition.UpLeft or HatPosition.DownLeft)
        {
            controls.Add(CanonicalDigitalControl.DPadLeft);
        }
    }
}