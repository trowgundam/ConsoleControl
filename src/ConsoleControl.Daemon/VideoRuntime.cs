using System.Collections.Immutable;

using ConsoleControl.Core;
using ConsoleControl.Video.FFmpeg;

namespace ConsoleControl.Daemon;

internal sealed class VideoRuntime(
    IVideoCaptureAdapter adapter,
    VideoSelectionStore store,
    VideoStreamAddress streamAddress) : IAsyncDisposable, IScreenshotSource
{
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly LatestFrameHub _frames = new();
    private IVideoCaptureSession? _session;
    private CancellationTokenSource? _captureStop;
    private Task? _captureTask;
    private VideoSourceId? _selected;
    private ulong _revision;
    private ulong _generation;
    private ulong _sequence;
    private string _status = "No video source selected";
    private VideoCaptureStatus _captureStatus = new(
        null,
        HardwareAvailability.Unknown,
        VideoCaptureState.SelectionRequired,
        null,
        null,
        "No video source is selected.");
    private bool _disposed;

    public VideoCaptureStatus GetStatus() => Volatile.Read(ref _captureStatus);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            (VideoSourceId? persisted, ulong revision) = await store.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            _revision = revision;
            ImmutableArray<VideoSource> sources = await adapter.GetSourcesAsync(cancellationToken)
                .ConfigureAwait(false);
            VideoSource? initial = persisted is { } id
                ? sources.FirstOrDefault(source => source.Id == id) ?? sources.FirstOrDefault()
                : sources.FirstOrDefault();
            if (initial is not null)
            {
                await SelectCoreAsync(initial, persist: persisted != initial.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _status = $"Video startup failed: {exception.Message}";
            SetCaptureStatus(VideoCaptureState.Faulted, _status);
        }
    }

    public async Task<VideoInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<VideoSource> sources = await GetSourcesAsync(cancellationToken)
            .ConfigureAwait(false);
        HardwareAvailability availability = _selected is null
            ? HardwareAvailability.Unknown
            : sources.Any(source => source.Id == _selected)
                ? HardwareAvailability.Available
                : HardwareAvailability.Unavailable;
        _captureStatus = _captureStatus with { Availability = availability };
        return new(sources, _selected, _revision, streamAddress.Uri, _status);
    }

    public async Task<VideoSelection> SelectAsync(
        VideoSourceId sourceId,
        ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (expectedRevision != _revision)
            {
                throw new VideoSelectionConflictException(
                    $"Video selection revision {expectedRevision} is stale; current revision is {_revision}.");
            }
            ImmutableArray<VideoSource> sources = await GetSourcesAsync(cancellationToken)
                .ConfigureAwait(false);
            VideoSource source = sources.FirstOrDefault(candidate => candidate.Id == sourceId)
                ?? throw new ArgumentException("The selected video source is unavailable.", nameof(sourceId));
            if (_selected == sourceId && _captureTask is not null)
            {
                return new(sourceId, _revision, _status);
            }
            await SelectCoreLockedAsync(source, persist: true, cancellationToken).ConfigureAwait(false);
            return new(sourceId, _revision, _status);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    public VideoFrameSubscription Subscribe() => _frames.Subscribe();

    public Screenshot CaptureLatest() => _frames.CopyLatest();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _selectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCaptureAsync().ConfigureAwait(false);
            _frames.Complete();
        }
        finally
        {
            _selectionGate.Release();
            _selectionGate.Dispose();
        }
    }

    private async Task SelectCoreAsync(
        VideoSource source,
        bool persist,
        CancellationToken cancellationToken)
    {
        await _selectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SelectCoreLockedAsync(source, persist, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private async Task<ImmutableArray<VideoSource>> GetSourcesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await adapter.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ConsoleOperationException(
                ConsoleFailureCode.VideoSourceInventoryFailed,
                $"Video source inventory failed: {exception.Message}",
                retryable: true,
                exception);
        }
    }

    private async Task SelectCoreLockedAsync(
        VideoSource source,
        bool persist,
        CancellationToken cancellationToken)
    {
        _status = $"Opening {source.DisplayName}";
        _captureStatus = new(
            source.Id,
            HardwareAvailability.Available,
            VideoCaptureState.Starting,
            source.PreferredMode,
            null,
            _status);
        await StopCaptureAsync().ConfigureAwait(false);
        _selected = source.Id;
        _generation++;
        if (persist)
        {
            _revision++;
            await store.WriteAsync(source.Id, _revision, cancellationToken).ConfigureAwait(false);
        }
        _frames.Clear();
        _captureStop = new();
        _captureTask = RunCaptureAsync(
            source,
            _generation,
            _captureStop.Token);
    }

    private async Task RunCaptureAsync(
        VideoSource source,
        ulong generation,
        CancellationToken cancellationToken)
    {
        IVideoCaptureSession? session = null;
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested && generation == _generation)
        {
            if (session is null)
            {
                TimeSpan delay = TimeSpan.FromMilliseconds(
                    Math.Min(2000, 200 * (1 << Math.Min(Math.Max(failures - 1, 0), 3))));
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    session = await adapter.OpenAsync(source, cancellationToken).ConfigureAwait(false);
                    if (generation != _generation)
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                        return;
                    }
                    _session = session;
                    _status = $"Reconnected: {source.DisplayName}, {source.PreferredMode}";
                    SetCaptureStatus(VideoCaptureState.Starting, _status, source.PreferredMode);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    failures++;
                    _status = $"Video disconnected; reconnecting: {exception.Message}";
                    SetCaptureStatus(VideoCaptureState.Reconnecting, _status, source.PreferredMode);
                    continue;
                }
            }

            try
            {
                await foreach (byte[] jpeg in session.ReadFramesAsync(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (generation != _generation)
                    {
                        return;
                    }
                    failures = 0;
                    _status = $"Ready: {source.DisplayName}, {source.PreferredMode}";
                    EncodedVideoFrame frame = new(
                        generation,
                        ++_sequence,
                        source.PreferredMode,
                        DateTimeOffset.UtcNow,
                        jpeg);
                    _captureStatus = new(
                        source.Id,
                        HardwareAvailability.Available,
                        VideoCaptureState.Streaming,
                        source.PreferredMode,
                        frame.ReceivedAt,
                        _status);
                    _frames.Publish(frame);
                }
                throw new EndOfStreamException("FFmpeg video capture ended.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failures++;
                _status = $"Video disconnected; reconnecting: {exception.Message}";
                SetCaptureStatus(VideoCaptureState.Reconnecting, _status, source.PreferredMode);
                _frames.Interrupt();
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }
                session = null;
            }

        }
    }

    private void SetCaptureStatus(
        VideoCaptureState state,
        string detail,
        VideoMode? mode = null) =>
        _captureStatus = _captureStatus with
        {
            SelectedSourceId = _selected,
            CaptureState = state,
            ActiveMode = mode ?? _captureStatus.ActiveMode,
            Detail = detail,
        };

    private async Task StopCaptureAsync()
    {
        CancellationTokenSource? stop = _captureStop;
        Task? task = _captureTask;
        _captureStop = null;
        _captureTask = null;
        _session = null;
        stop?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        stop?.Dispose();
    }
}

internal sealed record VideoStreamAddress(Uri Uri);

internal sealed class VideoSelectionConflictException(string message) : Exception(message);

internal readonly record struct LatestFrame(ulong Revision, EncodedVideoFrame Frame);

internal sealed class VideoStreamInterruptedException : Exception;

internal sealed class ScreenshotUnavailableException(string message) : Exception(message);

internal sealed class VideoFrameSubscription(
    LatestFrameHub hub,
    ulong interruption)
{
    private ulong _revision;

    public async ValueTask<EncodedVideoFrame> WaitForNextAsync(CancellationToken cancellationToken)
    {
        LatestFrame latest = await hub.WaitForNextAsync(
            _revision,
            interruption,
            cancellationToken).ConfigureAwait(false);
        _revision = latest.Revision;
        return latest.Frame;
    }
}

internal sealed class LatestFrameHub
{
    private readonly object _gate = new();
    private ulong _revision;
    private ulong _interruption;
    private EncodedVideoFrame? _latest;
    private TaskCompletionSource _changed = NewSignal();
    private bool _completed;

    public void Publish(EncodedVideoFrame frame)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }
            _latest = frame;
            _revision++;
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult();
    }

    public VideoFrameSubscription Subscribe()
    {
        lock (_gate)
        {
            return new(this, _interruption);
        }
    }

    public Screenshot CopyLatest()
    {
        lock (_gate)
        {
            if (_latest is null)
            {
                throw new ScreenshotUnavailableException("No current video frame is available.");
            }

            return new Screenshot(
                _latest.Generation,
                _latest.Sequence,
                _latest.Mode,
                _latest.ReceivedAt,
                _latest.Jpeg.ToArray());
        }
    }

    public void Clear()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _latest = null;
            _revision++;
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult();
    }

    public void Interrupt()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            _latest = null;
            _revision++;
            _interruption++;
            signal = _changed;
            _changed = NewSignal();
        }
        signal.TrySetResult();
    }

    public async ValueTask<LatestFrame> WaitForNextAsync(
        ulong observedRevision,
        ulong observedInterruption,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_completed)
                {
                    throw new OperationCanceledException("Video capture stopped.", cancellationToken);
                }
                if (_interruption != observedInterruption)
                {
                    throw new VideoStreamInterruptedException();
                }
                if (_latest is not null && _revision != observedRevision)
                {
                    return new(_revision, _latest);
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            _changed.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}