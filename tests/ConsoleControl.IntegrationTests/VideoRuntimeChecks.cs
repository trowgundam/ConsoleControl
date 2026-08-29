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
            int enumerationsAfterInitialization = adapter.EnumerationCount;
            VideoCaptureStatus cachedStatus = runtime.GetStatus();
            Require(adapter.EnumerationCount == enumerationsAfterInitialization
                && cachedStatus.SelectedSourceId is null
                && cachedStatus.CaptureState == VideoCaptureState.SelectionRequired,
                "cached video status unexpectedly enumerated capture hardware");
            VideoInventory initial = await runtime.GetInventoryAsync(CancellationToken.None);
            Require(initial.SelectedSourceId is null && initial.Revision == 0 && adapter.OpenCount == 0,
                "video initialization silently selected a capture source");

            FakeVideoAdapter failedAdapter = new([first]);
            await using VideoRuntime failedRuntime = new(
                failedAdapter,
                new FailingVideoSelectionStore(),
                new VideoStreamAddress(new Uri("http://127.0.0.1:5042/video/live.mjpeg")));
            try
            {
                await failedRuntime.SelectAsync(first.Id, 0, CancellationToken.None);
                throw new InvalidOperationException("a failed video persistence write was ignored");
            }
            catch (IOException)
            {
            }
            VideoInventory afterFailedWrite = await failedRuntime.GetInventoryAsync(CancellationToken.None);
            Require(afterFailedWrite.SelectedSourceId is null
                && afterFailedWrite.Revision == 0
                && failedAdapter.OpenCount == 0,
                "a failed persistence write changed the live video selection");

            VideoSelection firstSelection = await runtime.SelectAsync(
                first.Id,
                initial.Revision,
                CancellationToken.None);
            Require(firstSelection.SourceId == first.Id && firstSelection.Revision == 1,
                "explicit video selection did not persist the selected source");

            await WaitUntilAsync(
                () => adapter.OpenCount >= 1 && adapter.Current is not null,
                TimeSpan.FromSeconds(1));
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
            Screenshot firstCopy = runtime.CaptureLatest();
            firstCopy.Jpeg[2] = 0x7F;
            Screenshot secondCopy = runtime.CaptureLatest();
            Require(secondCopy.Jpeg[2] == 3,
                "a screenshot caller could mutate the retained video frame");

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
            await WaitUntilAsync(
                () => adapter.OpenCount >= 2 && adapter.Current is not null &&
                    !ReferenceEquals(adapter.Current, session),
                TimeSpan.FromSeconds(2));
            VideoFrameSubscription replacementSubscription = runtime.Subscribe();
            await adapter.Current!.WriteAsync([0xFF, 0xD8, 0x04, 0xFF, 0xD9]);
            EncodedVideoFrame recovered = await replacementSubscription.WaitForNextAsync(
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            Require(recovered.Jpeg[2] == 4,
                "video capture did not publish a frame after its source returned");

            VideoSelection selected = await runtime.SelectAsync(
                second.Id,
                firstSelection.Revision,
                CancellationToken.None);
            Require(selected.SourceId == second.Id && selected.Revision == 2,
                "video source selection did not advance its revision");
            try
            {
                await runtime.SelectAsync(first.Id, firstSelection.Revision, CancellationToken.None);
                throw new InvalidOperationException("a stale video source revision was accepted");
            }
            catch (VideoSelectionConflictException)
            {
            }
            FakeVideoAdapter missingAdapter = new([first]);
            await using VideoRuntime missingRuntime = new(
                missingAdapter,
                new VideoSelectionStore(selectionPath),
                new VideoStreamAddress(new Uri("http://127.0.0.1:5042/video/live.mjpeg")));
            await missingRuntime.InitializeAsync(CancellationToken.None);
            VideoInventory missing = await missingRuntime.GetInventoryAsync(CancellationToken.None);
            Require(missing.SelectedSourceId == second.Id
                && missing.Revision == 2
                && missingAdapter.OpenCount == 0,
                "video initialization replaced an unavailable persisted source");

            missingAdapter.SetSources([first, second]);
            await WaitUntilAsync(
                () => missingAdapter.OpenCount >= 1 && missingAdapter.LastOpened?.Id == second.Id,
                TimeSpan.FromSeconds(2));
            Require(missingAdapter.LastOpened?.Id == second.Id,
                "video recovery did not wait for the exact persisted source");
            VideoInventory recoveredInventory = await missingRuntime.GetInventoryAsync(CancellationToken.None);
            Require(recoveredInventory.SelectedSourceId == second.Id && recoveredInventory.Revision == 2,
                "video recovery changed the persisted selection");
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
        private ImmutableArray<VideoSource> _sources = sources;

        public FakeVideoSession? Current { get; private set; }
        public VideoSource? LastOpened { get; private set; }
        public bool Available { get; set; } = true;
        public int OpenCount { get; private set; }
        public int EnumerationCount { get; private set; }

        public Task<ImmutableArray<VideoSource>> GetSourcesAsync(CancellationToken cancellationToken)
        {
            EnumerationCount++;
            return Task.FromResult(_sources);
        }

        public void SetSources(ImmutableArray<VideoSource> value) => _sources = value;

        public Task<IVideoCaptureSession> OpenAsync(
            VideoSource source,
            CancellationToken cancellationToken)
        {
            if (!Available)
            {
                throw new IOException("The fake capture source is disconnected.");
            }
            OpenCount++;
            LastOpened = source;
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

    private sealed class FailingVideoSelectionStore : IVideoSelectionStore
    {
        public Task<(VideoSourceId? SourceId, ulong Revision)> ReadAsync(
            CancellationToken cancellationToken) => Task.FromResult<(VideoSourceId?, ulong)>((null, 0));

        public Task WriteAsync(
            VideoSourceId sourceId,
            ulong revision,
            CancellationToken cancellationToken) => throw new IOException("write failed");
    }
}