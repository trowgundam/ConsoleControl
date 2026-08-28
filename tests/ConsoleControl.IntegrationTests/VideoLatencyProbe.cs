using System.Diagnostics;
using ConsoleControl.Client;
using ConsoleControl.Core;

internal static class VideoLatencyProbe
{
    private const int Width = 160;
    private const int Height = 90;
    private const int FrameBytes = Width * Height;

    public static async Task RunAsync(Uri daemonUri, Uri streamUri)
    {
        await using GrpcConsoleSession session = GrpcConsoleSession.Connect(daemonUri);
        await using IControlSession control = await session.TakeControlAsync(
            ControlPriority.InteractiveUser,
            CancellationToken.None);
        await using DecodedVideoStream video = await DecodedVideoStream.StartAsync(streamUri);

        Console.WriteLine("Collecting a 30-frame visual-noise baseline...");
        (byte[] baseline, double noise) = await ReadBaselineAsync(video);
        double threshold = Math.Max(1.0, noise * 5);
        Console.WriteLine($"baseline_max_mad={noise:F3} threshold={threshold:F3}");

        for (int trial = 1; trial <= 3; trial++)
        {
            TimeSpan right = await MeasureAsync(
                video, control, baseline, CanonicalDigitalControl.DPadRight, threshold);
            await DrainAsync(video, 90);
            (baseline, noise) = await ReadBaselineAsync(video);
            threshold = Math.Max(1.0, noise * 5);
            TimeSpan left = await MeasureAsync(
                video, control, baseline, CanonicalDigitalControl.DPadLeft, threshold);
            Console.WriteLine($"trial={trial} dpad_right_ms={right.TotalMilliseconds:F1} " +
                              $"dpad_left_ms={left.TotalMilliseconds:F1}");
            await DrainAsync(video, 90);
            (baseline, noise) = await ReadBaselineAsync(video);
            threshold = Math.Max(1.0, noise * 5);
        }
    }

    private static async Task<(byte[] Frame, double MaximumNoise)> ReadBaselineAsync(
        DecodedVideoStream video)
    {
        byte[] previous = await video.ReadFrameAsync();
        double maximumNoise = 0;
        for (int index = 1; index < 30; index++)
        {
            byte[] current = await video.ReadFrameAsync();
            maximumNoise = Math.Max(maximumNoise, MeanAbsoluteDifference(previous, current));
            previous = current;
        }
        return (previous, maximumNoise);
    }

    private static async Task<TimeSpan> MeasureAsync(
        DecodedVideoStream video,
        IControlSession control,
        byte[] baseline,
        CanonicalDigitalControl direction,
        double threshold)
    {
        ControllerState pressed = direction == CanonicalDigitalControl.DPadRight
            ? ControllerState.Neutral with { DPad = HatPosition.Right }
            : ControllerState.Neutral with { DPad = HatPosition.Left };
        long started = Stopwatch.GetTimestamp();
        await control.SetStateAsync(pressed, CancellationToken.None);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            while (true)
            {
                byte[] frame = await video.ReadFrameAsync(timeout.Token);
                if (MeanAbsoluteDifference(baseline, frame) >= threshold)
                {
                    return Stopwatch.GetElapsedTime(started);
                }
            }
        }
        finally
        {
            await control.SetStateAsync(ControllerState.Neutral, CancellationToken.None);
        }
    }

    private static async Task DrainAsync(DecodedVideoStream video, int frameCount)
    {
        for (int index = 0; index < frameCount; index++)
        {
            await video.ReadFrameAsync();
        }
    }

    private static double MeanAbsoluteDifference(byte[] left, byte[] right)
    {
        long total = 0;
        for (int index = 0; index < left.Length; index++)
        {
            total += Math.Abs(left[index] - right[index]);
        }
        return total / (double)left.Length;
    }

    private sealed class DecodedVideoStream : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _stderr;

        private DecodedVideoStream(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public static async Task<DecodedVideoStream> StartAsync(Uri streamUri)
        {
            ProcessStartInfo start = new("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            string[] arguments =
            [
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-fflags", "nobuffer", "-flags", "low_delay",
                "-f", "mpjpeg", "-i", streamUri.ToString(),
                "-vf", $"scale={Width}:{Height},format=gray",
                "-an", "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1",
            ];
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the latency decoder.");
            DecodedVideoStream result = new(process);
            using CancellationTokenSource startup = new(TimeSpan.FromSeconds(3));
            await result.ReadFrameAsync(startup.Token);
            return result;
        }

        public async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            byte[] frame = GC.AllocateUninitializedArray<byte>(FrameBytes);
            await _process.StandardOutput.BaseStream.ReadExactlyAsync(frame, cancellationToken);
            return frame;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            await _process.WaitForExitAsync();
            await _stderr;
            _process.Dispose();
        }
    }
}
