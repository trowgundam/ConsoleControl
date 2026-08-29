using System.Collections.Immutable;

using ConsoleControl.Core;
using ConsoleControl.Mcp;

using SkiaSharp;

internal static class ScreenshotLibraryChecks
{
    public static void Run()
    {
        DateTimeOffset now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(now);
        ScreenshotLibrary library = new(
            time,
            new ScreenshotRetentionOptions(2, 16 * 1024 * 1024, TimeSpan.FromMinutes(5)));

        Screenshot source = CreateScreenshot(sequence: 41);
        RetainedRendering low = library.AddAndRender(source, ScreenshotFidelity.Low);
        TestAssert.Require(low.Width == 640 && low.Height == 360,
            "low fidelity did not render at 640x360");
        TestAssert.Require(!low.Jpeg.SequenceEqual(source.Jpeg),
            "low fidelity returned the full-resolution source bytes");

        ScreenshotRenderResult highResult = library.Render(low.Id, ScreenshotFidelity.High);
        TestAssert.Require(highResult is ScreenshotRenderResult.Found high
            && high.Rendering.Id == low.Id
            && high.Rendering.Jpeg.SequenceEqual(source.Jpeg),
            "high fidelity did not return exact original bytes for the same screenshot ID");

        time.Advance(TimeSpan.FromMinutes(5));
        TestAssert.Require(library.Render(low.Id, ScreenshotFidelity.Medium)
                is ScreenshotRenderResult.Unavailable { Reason: ScreenshotUnavailableReason.Expired },
            "expired screenshot did not return an explicit expired outcome");

        RetainedRendering first = library.AddAndRender(CreateScreenshot(42), ScreenshotFidelity.Low);
        RetainedRendering second = library.AddAndRender(CreateScreenshot(43), ScreenshotFidelity.Low);
        RetainedRendering third = library.AddAndRender(CreateScreenshot(44), ScreenshotFidelity.Low);
        TestAssert.Require(library.Render(first.Id, ScreenshotFidelity.Low)
                is ScreenshotRenderResult.Unavailable { Reason: ScreenshotUnavailableReason.Evicted },
            "entry-count eviction did not preserve a useful eviction outcome");
        TestAssert.Require(library.Render(second.Id, ScreenshotFidelity.Low) is ScreenshotRenderResult.Found
            && library.Render(third.Id, ScreenshotFidelity.Low) is ScreenshotRenderResult.Found,
            "entry-count eviction removed a retained screenshot");

        ImmutableArray<RetainedRendering> batch = library.AddBatchAndRender(
            [CreateScreenshot(45), CreateScreenshot(46)],
            ScreenshotFidelity.Medium);
        TestAssert.Require(batch.Length == 2
            && batch.All(rendering => rendering.Width == 1280 && rendering.Height == 720)
            && batch.All(rendering =>
                library.Render(rendering.Id, ScreenshotFidelity.High) is ScreenshotRenderResult.Found),
            "a macro screenshot batch was not retained atomically");

        try
        {
            library.AddBatchAndRender(
                [CreateScreenshot(47), CreateScreenshot(48), CreateScreenshot(49)],
                ScreenshotFidelity.Low);
            throw new InvalidOperationException("an oversized screenshot batch was retained");
        }
        catch (ScreenshotRetentionException exception)
        {
            TestAssert.Require(exception.Message.Contains("Capture fewer screenshots", StringComparison.Ordinal),
                "retention failure did not tell the agent how to recover");
        }
    }

    private static Screenshot CreateScreenshot(ulong sequence)
    {
        using SKBitmap bitmap = new(1920, 1080);
        bitmap.Erase(new SKColor((byte)sequence, 40, 80));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return new(1, sequence, new(1920, 1080, 60), DateTimeOffset.UtcNow, encoded.ToArray());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}