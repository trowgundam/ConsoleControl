namespace ConsoleControl.Core;

[Flags]
public enum GameButtons : ushort
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    X = 1 << 2,
    Y = 1 << 3,
    LeftShoulder = 1 << 4,
    RightShoulder = 1 << 5,
    LeftTrigger = 1 << 6,
    RightTrigger = 1 << 7,
    Minus = 1 << 8,
    Plus = 1 << 9,
    LeftStick = 1 << 10,
    RightStick = 1 << 11,
    Home = 1 << 12,
    Capture = 1 << 13,
}

public enum HatPosition : byte
{
    Up,
    UpRight,
    Right,
    DownRight,
    Down,
    DownLeft,
    Left,
    UpLeft,
    Neutral,
}

public readonly record struct StickPosition(byte X, byte Y)
{
    public static StickPosition Centered { get; } = new(128, 128);
}

public readonly record struct TriggerPosition(byte Value)
{
    public static TriggerPosition Released { get; } = new(0);
}

public readonly record struct ControllerState(
    GameButtons Buttons,
    HatPosition DPad,
    StickPosition LeftStick,
    StickPosition RightStick,
    TriggerPosition LeftTrigger,
    TriggerPosition RightTrigger)
{
    public static ControllerState Neutral { get; } = new(
        GameButtons.None,
        HatPosition.Neutral,
        StickPosition.Centered,
        StickPosition.Centered,
        TriggerPosition.Released,
        TriggerPosition.Released);
}