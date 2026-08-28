using System.Collections.Immutable;

namespace ConsoleControl.Core;

public enum InputSourceKind
{
    Unspecified,
    Keyboard,
    Gamepad,
}

public readonly record struct InputProfileKey(InputSourceKind Kind, string HardwareId)
{
    public static InputProfileKey Keyboard { get; } = new(InputSourceKind.Keyboard, "keyboard");
}

public readonly record struct HostControlId(string Value)
{
    public override string ToString() => Value;
}

public enum CanonicalDigitalControl
{
    Unspecified,
    A,
    B,
    X,
    Y,
    LeftShoulder,
    RightShoulder,
    LeftTrigger,
    RightTrigger,
    Minus,
    Plus,
    Home,
    Capture,
    DPadUp,
    DPadRight,
    DPadDown,
    DPadLeft,
}

public enum CanonicalStick
{
    Unspecified,
    Left,
    Right,
}

public enum CanonicalTrigger
{
    Unspecified,
    Left,
    Right,
}

public readonly record struct AxisTransform(float DeadZone, bool Inverted, float Scale)
{
    public static AxisTransform StickDefault { get; } = new(0.15f, false, 1f);
    public static AxisTransform TriggerDefault { get; } = new(0.05f, false, 1f);
}

public sealed record DigitalBinding(
    HostControlId Source,
    ImmutableArray<CanonicalDigitalControl> Targets);

public sealed record StickBinding(
    HostControlId XSource,
    HostControlId YSource,
    CanonicalStick Target,
    AxisTransform XTransform,
    AxisTransform YTransform);

public sealed record TriggerBinding(
    HostControlId Source,
    CanonicalTrigger Target,
    AxisTransform Transform,
    float DigitalThreshold);

public sealed record InputProfile(
    InputProfileKey Key,
    string Name,
    ImmutableArray<DigitalBinding> DigitalBindings,
    ImmutableArray<StickBinding> StickBindings,
    ImmutableArray<TriggerBinding> TriggerBindings)
{
    public InputProfile Validate()
    {
        if (string.IsNullOrWhiteSpace(Key.HardwareId))
        {
            throw new ArgumentException("An input profile must have a hardware ID.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("An input profile must have a name.");
        }

        foreach (DigitalBinding binding in DigitalBindings)
        {
            ValidateControl(binding.Source);
            if (binding.Targets.IsDefaultOrEmpty)
            {
                throw new ArgumentException($"Digital binding '{binding.Source}' has no targets.");
            }
        }

        if (DigitalBindings.SelectMany(binding => binding.Targets.Select(target => (binding.Source, target)))
            .Distinct().Count() != DigitalBindings.Sum(binding => binding.Targets.Length))
        {
            throw new ArgumentException("An input profile contains a duplicate digital binding.");
        }

        foreach (StickBinding binding in StickBindings)
        {
            ValidateControl(binding.XSource);
            ValidateControl(binding.YSource);
            ValidateTransform(binding.XTransform);
            ValidateTransform(binding.YTransform);
        }

        if (StickBindings.Select(binding => binding.Target).Distinct().Count() != StickBindings.Length)
        {
            throw new ArgumentException("Only one binding may target each canonical stick.");
        }

        foreach (TriggerBinding binding in TriggerBindings)
        {
            ValidateControl(binding.Source);
            ValidateTransform(binding.Transform);
            if (binding.DigitalThreshold is < 0f or > 1f)
            {
                throw new ArgumentException("A trigger threshold must be between zero and one.");
            }
        }

        if (TriggerBindings.Select(binding => binding.Target).Distinct().Count() != TriggerBindings.Length)
        {
            throw new ArgumentException("Only one binding may target each canonical trigger.");
        }

        return this;
    }

    private static void ValidateControl(HostControlId control)
    {
        if (string.IsNullOrWhiteSpace(control.Value))
        {
            throw new ArgumentException("A host control ID cannot be empty.");
        }
    }

    private static void ValidateTransform(AxisTransform transform)
    {
        if (transform.DeadZone is < 0f or >= 1f)
        {
            throw new ArgumentException("An axis dead zone must be at least zero and less than one.");
        }

        if (transform.Scale is <= 0f or > 4f)
        {
            throw new ArgumentException("An axis scale must be greater than zero and no more than four.");
        }
    }
}

public sealed record InputConfiguration(
    ulong Revision,
    ImmutableArray<InputProfile> Profiles);

public sealed record HostInputSnapshot(
    ImmutableHashSet<HostControlId> Pressed,
    ImmutableDictionary<HostControlId, float> Axes)
{
    public static HostInputSnapshot Empty { get; } = new([], []);
}

public static class InputMapper
{
    public static ControllerState Map(InputProfile profile, HostInputSnapshot input)
    {
        profile.Validate();
        HashSet<CanonicalDigitalControl> digital = [];
        foreach (DigitalBinding binding in profile.DigitalBindings)
        {
            if (input.Pressed.Contains(binding.Source))
            {
                digital.UnionWith(binding.Targets);
            }
        }

        StickPosition leftStick = StickPosition.Centered;
        StickPosition rightStick = StickPosition.Centered;
        foreach (StickBinding binding in profile.StickBindings)
        {
            StickPosition position = new(
                ToStickByte(TransformStick(ReadAxis(input, binding.XSource), binding.XTransform)),
                ToStickByte(TransformStick(ReadAxis(input, binding.YSource), binding.YTransform)));
            if (binding.Target == CanonicalStick.Left)
            {
                leftStick = position;
            }
            else
            {
                rightStick = position;
            }
        }

        TriggerPosition leftTrigger = TriggerPosition.Released;
        TriggerPosition rightTrigger = TriggerPosition.Released;
        foreach (TriggerBinding binding in profile.TriggerBindings)
        {
            float value = TransformTrigger(ReadAxis(input, binding.Source), binding.Transform);
            if (value >= binding.DigitalThreshold)
            {
                digital.Add(binding.Target == CanonicalTrigger.Left
                    ? CanonicalDigitalControl.LeftTrigger
                    : CanonicalDigitalControl.RightTrigger);
            }

            TriggerPosition position = new(ToTriggerByte(value));
            if (binding.Target == CanonicalTrigger.Left)
            {
                leftTrigger = position;
            }
            else
            {
                rightTrigger = position;
            }
        }

        return new ControllerState(
            ToButtons(digital),
            ToHat(digital),
            leftStick,
            rightStick,
            leftTrigger,
            rightTrigger);
    }

    private static float ReadAxis(HostInputSnapshot input, HostControlId source) =>
        input.Axes.TryGetValue(source, out float value) ? value : 0f;

    private static float TransformStick(float value, AxisTransform transform)
    {
        value = Math.Clamp(value, -1f, 1f);
        float magnitude = Math.Abs(value);
        value = magnitude <= transform.DeadZone
            ? 0f
            : MathF.CopySign((magnitude - transform.DeadZone) / (1f - transform.DeadZone), value);
        if (transform.Inverted)
        {
            value = -value;
        }

        return Math.Clamp(value * transform.Scale, -1f, 1f);
    }

    private static float TransformTrigger(float value, AxisTransform transform)
    {
        value = Math.Clamp(value, 0f, 1f);
        value = value <= transform.DeadZone
            ? 0f
            : (value - transform.DeadZone) / (1f - transform.DeadZone);
        if (transform.Inverted)
        {
            value = 1f - value;
        }

        return Math.Clamp(value * transform.Scale, 0f, 1f);
    }

    private static byte ToStickByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round((value + 1f) * 127.5f), 0, 255);

    private static byte ToTriggerByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

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
}
