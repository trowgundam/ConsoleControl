using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using ConsoleControl.Core;
using ConsoleControl.Daemon;
using ConsoleControl.Video.FFmpeg;

internal static class VideoRuntimeChecks
{
    public static async Task RunAsync()
    {
        string selectionPath = Path.Combine(
            Path.GetTempPath(),
            $"consolecontrol-video-{Guid.NewGuid():N}.json");
        VideoSource first = new(new("capture-one"), "Capture one", new(1920, 1080, 60));
        VideoSource second = new(new("capture-two"), "Capture two", new(1280, 720, 60));
        FakeVideoAdapter adapter = new([first, second]);
        try
        {
            await using VideoRuntime runtime = new(
                adapter,
                new VideoSelectionStore(selectionPath),
                new VideoStreamAddress(new Uri("http://127.0.0.1:5042/video/live.mjpeg")));
            await runtime.InitializeAsync(CancellationToken.None);
            VideoInventory initial = await runtime.GetInventoryAsync(CancellationToken.None);
            Require(initial.SelectedSourceId == first.Id && initial.Revision == 1,
                "video initialization did not select and persist the first source");

            await WaitUntilAsync(() => adapter.OpenCount >= 1, TimeSpan.FromSeconds(1));
            FakeVideoSession session = adapter.Current!;
            VideoFrameSubscription subscription = runtime.Subscribe();
            await session.WriteAsync([0xFF, 0xD8, 0x01, 0xFF, 0xD9]);
            EncodedVideoFrame observed = await subscription.WaitForNextAsync(CancellationToken.None);
            await session.WriteAsync([0xFF, 0xD8, 0x02, 0xFF, 0xD9]);
            await session.WriteAsync([0xFF, 0xD8, 0x03, 0xFF, 0xD9]);
            await Task.Delay(20);
            EncodedVideoFrame latest = await subscription.WaitForNextAsync(CancellationToken.None);
            Require(latest.Jpeg[2] == 3,
                "the video hub retained a stale frame instead of the newest frame");

            adapter.Available = false;
            session.Complete();
            try
            {
                await subscription.WaitForNextAsync(CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(1));
                throw new InvalidOperationException("capture loss did not interrupt an existing viewer");
            }
            catch (VideoStreamInterruptedException)
            {
            }
            adapter.Available = true;
            await WaitUntilAsync(() => adapter.OpenCount >= 2, TimeSpan.FromSeconds(2));
            VideoFrameSubscription replacementSubscription = runtime.Subscribe();
            await adapter.Current!.WriteAsync([0xFF, 0xD8, 0x04, 0xFF, 0xD9]);
            EncodedVideoFrame recovered = await replacementSubscription.WaitForNextAsync(
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Require(recovered.Jpeg[2] == 4,
                "video capture did not publish a frame after its source returned");

            VideoSelection selected = await runtime.SelectAsync(
                second.Id,
                initial.Revision,
                CancellationToken.None);
            Require(selected.SourceId == second.Id && selected.Revision == 2,
                "video source selection did not advance its revision");
            try
            {
                await runtime.SelectAsync(first.Id, initial.Revision, CancellationToken.None);
                throw new InvalidOperationException("a stale video source revision was accepted");
            }
            catch (VideoSelectionConflictException)
            {
            }
        }
        finally
        {
            File.Delete(selectionPath);
        }
    }

    public static async Task VerifyMultipartFileAsync(string path, int minimumFrames)
    {
        await using FileStream stream = File.OpenRead(path);
        MultipartJpegReader reader = new(stream);
        int count = 0;
        while (await reader.ReadAsync(CancellationToken.None) is not null)
        {
            count++;
        }
        Require(count >= minimumFrames,
            $"multipart reader decoded {count} frames; expected at least {minimumFrames}");
    }

    public static void VerifyJpegDimensions()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x07, 0x08, 0x04, 0x38, 0x07, 0x80,
            0xFF, 0xD9,
        ];
        Require(JpegDimensions.Read(jpeg) == (1920, 1080),
            "JPEG SOF dimensions were parsed incorrectly");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= timeout)
            {
                throw new InvalidOperationException("Timed out waiting for video capture recovery.");
            }
            await Task.Delay(20);
        }
    }

    private sealed class FakeVideoAdapter(ImmutableArray<VideoSource> sources) : IVideoCaptureAdapter
    {
        public FakeVideoSession? Current { get; private set; }
        public bool Available { get; set; } = true;
        public int OpenCount { get; private set; }

        public Task<ImmutableArray<VideoSource>> GetSourcesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(sources);

        public Task<IVideoCaptureSession> OpenAsync(
            VideoSource source,
            CancellationToken cancellationToken)
        {
            if (!Available)
            {
                throw new IOException("The fake capture source is disconnected.");
            }
            OpenCount++;
            Current = new();
            return Task.FromResult<IVideoCaptureSession>(Current);
        }
    }

    private sealed class FakeVideoSession : IVideoCaptureSession
    {
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>();

        public ValueTask WriteAsync(byte[] jpeg) => _frames.Writer.WriteAsync(jpeg);

        public void Complete() => _frames.Writer.TryComplete();

        public IAsyncEnumerable<byte[]> ReadFramesAsync(CancellationToken cancellationToken) =>
            _frames.Reader.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
