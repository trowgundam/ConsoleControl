using System.Text.Json;

using ConsoleControl.Client;
using ConsoleControl.Core;
using ConsoleControl.Mcp;

using ModelContextProtocol.Protocol;

using SkiaSharp;

internal static class AutomationControlChecks
{
    public static async Task RunAsync()
    {
        FakeConsoleSession console = new();
        await using AutomationControl control = new(console);
        Require(await control.RequestControlAsync("initial", CancellationToken.None),
            "the initial automation lease was not acquired");
        Require(control.HasControl, "automation did not report its live lease");

        console.Current!.Complete(AutomationSessionEndReason.Preempted);
        await console.Current.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => !control.HasControl, TimeSpan.FromSeconds(1));
        Require(await control.RequestControlAsync("reacquire", CancellationToken.None),
            "automation treated a completed lease as still owned");
        Require(console.RequestCount == 2,
            "automation reported control without making a second acquisition request");

        console.Current.Result = CreateCaptureResult();
        ConsoleTools tools = new(control, new ScreenshotLibrary());
        CallToolResult sequenceResult = await tools.RunSequence(
            [new("screenshot", Name: "checkpoint")],
            startScreenshot: false,
            endScreenshot: false,
            CancellationToken.None);
        Require(sequenceResult.Content is [TextContentBlock],
            "a sequence capture returned inline image content instead of metadata only");
        TextContentBlock sequenceText = (TextContentBlock)sequenceResult.Content[0];
        using JsonDocument sequenceEnvelope = JsonDocument.Parse(sequenceText.Text);
        JsonElement capture = sequenceEnvelope.RootElement.GetProperty("data")
            .GetProperty("captures")[0];
        Require(capture.GetProperty("screenshot_id").GetString()?.StartsWith("ss_", StringComparison.Ordinal) == true,
            "a sequence capture did not return its retained screenshot ID");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource stop = new(timeout);
        while (!condition())
        {
            await Task.Delay(10, stop.Token);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static AutomationResult CreateCaptureResult()
    {
        using SKBitmap bitmap = new(1920, 1080);
        bitmap.Erase(new SKColor(20, 40, 80));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        Screenshot screenshot = new(
            1, 42, new(1920, 1080, 60), DateTimeOffset.UtcNow, encoded.ToArray());
        return new(
            AutomationOutcome.Completed,
            TimeSpan.FromMilliseconds(10),
            [new("checkpoint", TimeSpan.Zero, TimeSpan.Zero, screenshot, null)],
            null);
    }

    private sealed class FakeConsoleSession : IConsoleSession
    {
        public FakeAutomationSession? Current { get; private set; }
        public int RequestCount { get; private set; }

        public Task<IAutomationSession> RequestAutomationControlAsync(
            string reason,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Current = new();
            return Task.FromResult<IAutomationSession>(Current);
        }

        public Task<ConsoleStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<VideoInventory> GetVideoInventoryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ControllerBridgeInventory> GetControllerBridgeInventoryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ControllerBridgeSelection> SelectControllerBridgeAsync(ControllerBridgeId bridgeId, ulong expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<VideoSelection> SelectVideoSourceAsync(VideoSourceId sourceId, ulong expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<Screenshot> GetScreenshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> DeclineControlRequestAsync(Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IControlSession> TakeControlAsync(ControlPriority priority, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAutomationSession : IAutomationSession
    {
        private readonly TaskCompletionSource<AutomationSessionEnd> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AutomationSessionEnd> Completion => _completion.Task;
        public AutomationResult? Result { get; set; }

        public void Complete(AutomationSessionEndReason reason) =>
            _completion.TrySetResult(new(reason, null));

        public Task<AutomationResult> RunAsync(
            AutomationSequence sequence,
            CancellationToken cancellationToken) => Task.FromResult(
                Result ?? throw new NotSupportedException());

        public ValueTask DisposeAsync()
        {
            Complete(AutomationSessionEndReason.Released);
            return ValueTask.CompletedTask;
        }
    }
}