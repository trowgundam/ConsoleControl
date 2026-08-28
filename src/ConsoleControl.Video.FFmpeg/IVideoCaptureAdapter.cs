using System.Collections.Immutable;

using ConsoleControl.Core;

namespace ConsoleControl.Video.FFmpeg;

public interface IVideoCaptureAdapter
{
    Task<ImmutableArray<VideoSource>> GetSourcesAsync(CancellationToken cancellationToken);

    Task<IVideoCaptureSession> OpenAsync(
        VideoSource source,
        CancellationToken cancellationToken);
}

public interface IVideoCaptureSession : IAsyncDisposable
{
    IAsyncEnumerable<byte[]> ReadFramesAsync(CancellationToken cancellationToken);
}