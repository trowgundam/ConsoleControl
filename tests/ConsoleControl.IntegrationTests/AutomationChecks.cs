using ConsoleControl.Controller.Bluetooth;
using ConsoleControl.Core;
using ConsoleControl.Daemon;

internal static class AutomationChecks
{
    public static async Task RunAsync()
    {
        CompiledAutomation compiled = new AutomationSequence(
        [
            new AutomationCommand.Press(
                CanonicalDigitalControl.DPadRight, TimeSpan.FromMilliseconds(80)),
            new AutomationCommand.Hold(
                CanonicalDigitalControl.RightShoulder, TimeSpan.FromSeconds(4)),
            new AutomationCommand.Pause(TimeSpan.FromSeconds(1)),
            new AutomationCommand.Capture("during-hold"),
        ], CaptureStart: true, CaptureEnd: true).Compile();
        TestAssert.Require(compiled.CompletionAt == TimeSpan.FromMilliseconds(4080),
            "a timed hold did not extend macro completion independently of the cursor");
        TestAssert.Require(compiled.Events.Single(item => item.CaptureName == "during-hold").Offset ==
                TimeSpan.FromMilliseconds(1080),
            "press and pause cursor advancement produced the wrong screenshot offset");

        ClientId automationOwner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ClientId interactiveOwner = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        await using (ConsoleRuntime reasonConsole = new(new FakeOutput()))
        {
            try
            {
                await reasonConsole.RequestAutomationControlAsync(
                    automationOwner, " ", CancellationToken.None);
                throw new InvalidOperationException("an empty control request reason was accepted");
            }
            catch (ArgumentException)
            {
            }
        }
        FakeOutput takeoverOutput = new();
        await using (ConsoleRuntime console = new(takeoverOutput))
        {
            ControlLease automation = await console.AcquireControlAsync(
                automationOwner, ControlPriority.Automation, CancellationToken.None);
            CancellationToken revoked = await console.GetRevocationTokenAsync(
                automationOwner, automation.Generation, CancellationToken.None);
            await console.SetControllerStateAsync(
                automationOwner,
                automation.Generation,
                ControllerState.Neutral with { Buttons = GameButtons.RightShoulder },
                CancellationToken.None);
            ControlLease interactive = await console.AcquireControlAsync(
                interactiveOwner, ControlPriority.InteractiveUser, CancellationToken.None);
            TestAssert.Require(revoked.IsCancellationRequested,
                "interactive takeover did not cancel automation execution");
            TestAssert.Require(takeoverOutput.States[^1] == ControllerState.Neutral,
                "interactive takeover did not neutralize automation before granting control");
            TestAssert.Require(!await console.TryReleaseControlAsync(
                    automationOwner, automation.Generation, CancellationToken.None),
                "cleanup from a preempted generation affected interactive control");
            await console.SetControllerStateAsync(
                interactiveOwner,
                interactive.Generation,
                ControllerState.Neutral with { Buttons = GameButtons.A },
                CancellationToken.None);
            TestAssert.Require(takeoverOutput.States[^1].Buttons == GameButtons.A,
                "interactive input did not work after automation preemption");
        }

        FakeOutput macroOutput = new();
        await using (ConsoleRuntime console = new(macroOutput))
        {
            ControlLease lease = await console.AcquireControlAsync(
                automationOwner, ControlPriority.Automation, CancellationToken.None);
            AutomationRuntime runtime = new(console, new UnusedScreenshots(), TimeProvider.System);
            AutomationResult result = await runtime.ExecuteAsync(
                automationOwner,
                lease.Generation,
                new AutomationSequence([
                    new AutomationCommand.Hold(
                        CanonicalDigitalControl.RightShoulder, TimeSpan.FromMilliseconds(80)),
                    new AutomationCommand.Pause(TimeSpan.FromMilliseconds(20)),
                    new AutomationCommand.Press(
                        CanonicalDigitalControl.A, TimeSpan.FromMilliseconds(20)),
                ]),
                CancellationToken.None);
            TestAssert.Require(result.Outcome == AutomationOutcome.Completed,
                "overlapping macro did not complete");
            int overlap = macroOutput.States.FindIndex(state =>
                state.Buttons == (GameButtons.RightShoulder | GameButtons.A));
            TestAssert.Require(overlap > 0
                && macroOutput.States.Take(overlap).Any(state =>
                    state.Buttons == GameButtons.RightShoulder)
                && macroOutput.States.Skip(overlap + 1).Any(state =>
                    state.Buttons == GameButtons.RightShoulder)
                && macroOutput.States[^1] == ControllerState.Neutral,
                "macro executor did not compose overlapping holds and presses");
        }

        FakeOutput sustainedHoldOutput = new();
        await using (ConsoleRuntime console = new(sustainedHoldOutput))
        {
            ControlLease lease = await console.AcquireControlAsync(
                automationOwner, ControlPriority.Automation, CancellationToken.None);
            AutomationRuntime runtime = new(console, new UnusedScreenshots(), TimeProvider.System);
            AutomationResult result = await runtime.ExecuteAsync(
                automationOwner,
                lease.Generation,
                new AutomationSequence([
                    new AutomationCommand.Hold(
                        CanonicalDigitalControl.B, TimeSpan.FromMilliseconds(400)),
                ]),
                CancellationToken.None);
            TestAssert.Require(result.Outcome == AutomationOutcome.Completed,
                "a sustained automation hold did not complete");
            TestAssert.Require(sustainedHoldOutput.States.Count(state => state.Buttons == GameButtons.B) >= 4,
                "automation did not refresh a held state before the firmware safety timeout");
        }

        FakeOutput screenshotFailureOutput = new();
        await using (ConsoleRuntime console = new(screenshotFailureOutput))
        {
            ControlLease lease = await console.AcquireControlAsync(
                automationOwner, ControlPriority.Automation, CancellationToken.None);
            AutomationRuntime runtime = new(
                console,
                new FailingScreenshots(),
                TimeProvider.System);
            AutomationResult result = await runtime.ExecuteAsync(
                automationOwner,
                lease.Generation,
                new AutomationSequence([
                    new AutomationCommand.Press(
                        CanonicalDigitalControl.A, TimeSpan.FromMilliseconds(20)),
                    new AutomationCommand.Capture("failed-frame"),
                ]),
                CancellationToken.None);
            TestAssert.Require(result.Outcome == AutomationOutcome.ScreenshotFailed
                && result.Captures is [{ Name: "failed-frame", Screenshot: null, Failure: not null }]
                && screenshotFailureOutput.States[^1] == ControllerState.Neutral,
                "screenshot failure did not stop, report the failed capture, and neutralize input");
        }
    }

    private sealed class FakeOutput : IControllerOutput
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

    private sealed class UnusedScreenshots : IScreenshotSource
    {
        public Screenshot CaptureLatest() => throw new InvalidOperationException("No capture expected.");
    }

    private sealed class FailingScreenshots : IScreenshotSource
    {
        public Screenshot CaptureLatest() =>
            throw new ScreenshotUnavailableException("The test frame is unavailable.");
    }
}