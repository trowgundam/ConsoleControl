using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using ConsoleControl.Core;

namespace ConsoleControl.Video.FFmpeg;

public sealed class FfmpegVideoCaptureAdapter : IVideoCaptureAdapter
{
    private const string ByIdDirectory = "/dev/v4l/by-id";

    public Task<ImmutableArray<VideoSource>> GetSourcesAsync(CancellationToken cancellationToken) =>
        LinuxVideoSourceEnumerator.GetSourcesAsync(cancellationToken);

    public async Task<IVideoCaptureSession> OpenAsync(
        VideoSource source,
        CancellationToken cancellationToken)
    {
        string node = Path.Combine(ByIdDirectory, source.Id.Value);
        if (!File.Exists(node))
        {
            throw new FileNotFoundException("The selected video source is unavailable.", node);
        }
        FfmpegCaptureSession session = new(node, source.PreferredMode);
        await session.StartAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private sealed class FfmpegCaptureSession(string node, VideoMode mode) : IVideoCaptureSession
    {
        private readonly CancellationTokenSource _stop = new();
        private Process? _process;
        private Task? _stderr;
        private MultipartJpegReader? _reader;
        private byte[]? _firstFrame;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            string[] arguments =
            [
                "-nostdin", "-hide_banner", "-loglevel", "warning",
                "-fflags", "nobuffer", "-flags", "low_delay",
                "-f", "v4l2", "-input_format", "mjpeg",
                "-video_size", $"{mode.Width}x{mode.Height}",
                "-framerate", mode.FramesPerSecond.ToString(),
                "-i", node, "-map", "0:v:0", "-an", "-c:v", "copy",
                "-f", "mpjpeg", "-boundary_tag", "consolecontrol-frame", "pipe:1",
            ];
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start FFmpeg.");
            _stderr = DrainStderrAsync(_process.StandardError, _stop.Token);
            _reader = new(_process.StandardOutput.BaseStream);
            using CancellationTokenSource startup =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                _firstFrame = await _reader.ReadAsync(startup.Token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("FFmpeg ended before its first video frame.");
                ValidateFrameMode(_firstFrame);
            }
            catch
            {
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async IAsyncEnumerable<byte[]> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_process is null, this);
            if (_firstFrame is not null)
            {
                yield return _firstFrame;
                _firstFrame = null;
            }
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            while (!linked.IsCancellationRequested)
            {
                byte[]? frame = await _reader!.ReadAsync(linked.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    yield break;
                }
                ValidateFrameMode(frame);
                yield return frame;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Process? process = Interlocked.Exchange(ref _process, null);
            if (process is null)
            {
                return;
            }
            _stop.Cancel();
            try
            {
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            if (_stderr is not null)
            {
                try
                {
                    await _stderr.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            process.Dispose();
            _stop.Dispose();
        }

        private static async Task DrainStderrAsync(StreamReader stderr, CancellationToken cancellationToken)
        {
            int retainedBytes = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }
                retainedBytes += line.Length;
                if (retainedBytes > 32 * 1024)
                {
                    retainedBytes = 0;
                }
            }
        }

        private void ValidateFrameMode(byte[] jpeg)
        {
            (ushort width, ushort height) = JpegDimensions.Read(jpeg);
            if (width != mode.Width || height != mode.Height)
            {
                throw new InvalidDataException(
                    $"Capture returned {width}x{height}; requested {mode.Width}x{mode.Height}.");
            }
        }
    }
}
