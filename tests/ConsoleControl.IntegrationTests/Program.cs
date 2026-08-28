using System.Collections.Immutable;

using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;
using ConsoleControl.Daemon;

if (args is ["verify-mjpeg", string multipartPath])
{
    await VideoRuntimeChecks.VerifyMultipartFileAsync(multipartPath, 2);
    Console.WriteLine("multipart JPEG reader: passed");
    return;
}

if (args is ["video-latency", string daemonUri, string streamUri])
{
    await VideoLatencyProbe.RunAsync(new Uri(daemonUri), new Uri(streamUri));
    return;
}

if (args is ["automation"])
{
    await AutomationChecks.RunAsync();
    Console.WriteLine("automation checks: passed");
    return;
}

if (args is ["screenshots"])
{
    ScreenshotLibraryChecks.Run();
    Console.WriteLine("screenshot library checks: passed");
    return;
}

if (args is ["streaming"])
{
    await StreamingTransportChecks.RunAsync();
    Console.WriteLine("streaming transport: passed");
    return;
}

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

InputProfile mappingProfile = new(
    InputProfileKey.Keyboard,
    "test",
    [
        new(new("key.one"), [CanonicalDigitalControl.A, CanonicalDigitalControl.RightShoulder]),
        new(new("key.two"), [CanonicalDigitalControl.A]),
        new(new("key.up"), [CanonicalDigitalControl.DPadUp]),
        new(new("key.right"), [CanonicalDigitalControl.DPadRight]),
    ],
    [new(new("axis.x"), new("axis.y"), CanonicalStick.Left,
        AxisTransform.StickDefault, AxisTransform.StickDefault)],
    [new(new("axis.trigger"), CanonicalTrigger.Right,
        AxisTransform.TriggerDefault, 0.5f)]);

ControllerState mapped = InputMapper.Map(mappingProfile, new(
    [new("key.one"), new("key.two"), new("key.up"), new("key.right")],
    ImmutableDictionary<HostControlId, float>.Empty
        .Add(new("axis.x"), 1f)
        .Add(new("axis.y"), -1f)
        .Add(new("axis.trigger"), 0.75f)));
Require(mapped.Buttons.HasFlag(GameButtons.A), "many-to-one mapping omitted A");
Require(mapped.Buttons.HasFlag(GameButtons.RightShoulder), "one-to-many mapping omitted R");
Require(mapped.Buttons.HasFlag(GameButtons.RightTrigger), "analog trigger omitted digital ZR");
Require(mapped.DPad == HatPosition.UpRight, "D-pad diagonal was not composed");
Require(mapped.LeftStick == new StickPosition(255, 0), "stick transform was not applied");
Require(mapped.RightTrigger.Value > 128, "analog trigger value was not retained");

ControllerState overlappingRelease = InputMapper.Map(mappingProfile, new(
    [new("key.two")],
    ImmutableDictionary<HostControlId, float>.Empty));
Require(overlappingRelease.Buttons == GameButtons.A,
    "releasing one of two bindings incorrectly released their shared target");

InputProfile invertedStickProfile = mappingProfile with
{
    StickBindings =
    [
        new(new("left.x"), new("left.y"), CanonicalStick.Left,
            AxisTransform.StickDefault, AxisTransform.StickDefault with { Inverted = true }),
        new(new("right.x"), new("right.y"), CanonicalStick.Right,
            AxisTransform.StickDefault, AxisTransform.StickDefault with { Inverted = true }),
    ],
};
ControllerState sticksUp = InputMapper.Map(invertedStickProfile, new(
    [],
    ImmutableDictionary<HostControlId, float>.Empty
        .Add(new("left.y"), -1f)
        .Add(new("right.y"), -1f)));
Require(sticksUp.LeftStick.Y == byte.MaxValue && sticksUp.RightStick.Y == byte.MaxValue,
    "inverted Y transforms did not translate upward movement to upward Switch stick values");

string profilePath = Path.Combine(Path.GetTempPath(), $"consolecontrol-profile-{Guid.NewGuid():N}.json");
try
{
    InputProfileStore profileStore = new(profilePath);
    InputConfiguration initialConfiguration = await profileStore.ReadAsync(CancellationToken.None);
    Require(initialConfiguration.Revision == 0 && initialConfiguration.Profiles.IsEmpty,
        "a missing profile file did not produce an empty configuration");
    InputConfiguration savedConfiguration = await profileStore.SaveAsync(
        mappingProfile,
        initialConfiguration.Revision,
        CancellationToken.None);
    Require(savedConfiguration.Revision == 1 && savedConfiguration.Profiles.SequenceEqual([mappingProfile]),
        "profile save did not return the updated configuration");
    InputConfiguration reloadedConfiguration = await profileStore.ReadAsync(CancellationToken.None);
    Require(reloadedConfiguration.Revision == 1
        && reloadedConfiguration.Profiles.Length == 1
        && reloadedConfiguration.Profiles[0].Key == mappingProfile.Key
        && reloadedConfiguration.Profiles[0].DigitalBindings.Length == mappingProfile.DigitalBindings.Length
        && reloadedConfiguration.Profiles[0].StickBindings.Length == mappingProfile.StickBindings.Length
        && reloadedConfiguration.Profiles[0].TriggerBindings.Length == mappingProfile.TriggerBindings.Length,
        "profile persistence did not round-trip through JSON");
    try
    {
        await profileStore.SaveAsync(mappingProfile, 0, CancellationToken.None);
        throw new InvalidOperationException("a stale profile revision was accepted");
    }
    catch (InputConfigurationConflictException)
    {
    }
}
finally
{
    if (File.Exists(profilePath))
    {
        File.Delete(profilePath);
    }
}

await StreamingTransportChecks.RunAsync();
VideoRuntimeChecks.VerifyJpegDimensions();
await VideoRuntimeChecks.RunAsync();
await AutomationChecks.RunAsync();
ScreenshotLibraryChecks.Run();

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
output.Disconnect();
await runtime.SetControllerStateAsync(owner, lease.Generation, ControllerState.Neutral, CancellationToken.None);
Require(output.ConnectCount == 1 && output.IsConnected,
    "a state write did not reconnect the controller bridge");
bool released = await runtime.TryReleaseControlAsync(owner, lease.Generation, CancellationToken.None);
Require(released && output.States.SequenceEqual([pressed, ControllerState.Neutral, ControllerState.Neutral]),
    "stream cleanup did not neutralize and release current control");
bool staleRelease = await runtime.TryReleaseControlAsync(owner, lease.Generation, CancellationToken.None);
Require(!staleRelease, "repeated stream cleanup affected a released generation");

Console.WriteLine("controller encoding: passed");
Console.WriteLine("lease generation: passed");
Console.WriteLine("neutral on release: passed");
Console.WriteLine("input mapping: passed");
Console.WriteLine("input profile persistence: passed");
Console.WriteLine("streaming transport: passed");
Console.WriteLine("video runtime: passed");
Console.WriteLine("JPEG dimensions: passed");
Console.WriteLine("automation timeline: passed");
Console.WriteLine("interactive takeover: passed");

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

    public bool IsConnected { get; private set; } = true;

    public int ConnectCount { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectCount++;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public void Disconnect() => IsConnected = false;

    public ValueTask WriteStateAsync(
        ControllerState state,
        CancellationToken cancellationToken)
    {
        States.Add(state);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}