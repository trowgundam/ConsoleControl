using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;
using ConsoleControl.Daemon;

AssertEncoding(
    ControllerState.Neutral,
    [0x00, 0x00, 0x08, 0x80, 0x80, 0x80, 0x80, 0x00],
    "neutral");
AssertEncoding(
    ControllerState.Neutral with { Buttons = GameButtons.A },
    [0x04, 0x00, 0x08, 0x80, 0x80, 0x80, 0x80, 0x00],
    "A");
AssertEncoding(
    ControllerState.Neutral with
    {
        Buttons = GameButtons.A
            | GameButtons.B
            | GameButtons.X
            | GameButtons.Y
            | GameButtons.LeftShoulder
            | GameButtons.RightShoulder
            | GameButtons.LeftTrigger
            | GameButtons.RightTrigger
            | GameButtons.Minus
            | GameButtons.Plus
            | GameButtons.Home
            | GameButtons.Capture,
    },
    [0xFF, 0x33, 0x08, 0x80, 0x80, 0x80, 0x80, 0x00],
    "all exposed buttons");

foreach (HatPosition direction in Enum.GetValues<HatPosition>())
{
    AssertEncoding(
        ControllerState.Neutral with { DPad = direction },
        [0x00, 0x00, (byte)direction, 0x80, 0x80, 0x80, 0x80, 0x00],
        $"D-pad {direction}");
}

FakeControllerOutput output = new();
await using ConsoleRuntime runtime = new(output);
ClientId owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
ClientId staleOwner = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
ControlLease lease = await runtime.AcquireControlAsync(
    owner,
    ControlPriority.InteractiveUser,
    CancellationToken.None);

ControllerState pressed = ControllerState.Neutral with { Buttons = GameButtons.A };
await runtime.SetControllerStateAsync(owner, lease.Generation, pressed, CancellationToken.None);
Require(output.States.SequenceEqual([pressed]), "current lease did not reach controller output");

try
{
    await runtime.SetControllerStateAsync(staleOwner, lease.Generation, pressed, CancellationToken.None);
    throw new InvalidOperationException("stale owner was accepted");
}
catch (StaleControlLeaseException)
{
}

Require(output.States.Count == 1, "stale lease reached controller output");
await runtime.ReleaseControlAsync(owner, lease.Generation, CancellationToken.None);
Require(output.States.SequenceEqual([pressed, ControllerState.Neutral]),
    "release did not neutralize through controller output");

Console.WriteLine("controller encoding: passed");
Console.WriteLine("lease generation: passed");
Console.WriteLine("neutral on release: passed");

static void AssertEncoding(ControllerState state, byte[] expected, string name)
{
    byte[] actual = new byte[ProofBridgeStateEncoder.EncodedLength];
    ProofBridgeStateEncoder.Encode(state, actual);
    Require(actual.SequenceEqual(expected),
        $"{name} encoding mismatch: {Convert.ToHexString(actual)}");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class FakeControllerOutput : IControllerOutput
{
    public List<ControllerState> States { get; } = [];

    public bool IsConnected => true;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask WriteStateAsync(
        ControllerState state,
        CancellationToken cancellationToken)
    {
        States.Add(state);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
