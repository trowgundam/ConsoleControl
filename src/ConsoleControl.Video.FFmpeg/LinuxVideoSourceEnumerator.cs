using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ConsoleControl.Core;

namespace ConsoleControl.Video.FFmpeg;

internal static partial class LinuxVideoSourceEnumerator
{
    private const string ByIdDirectory = "/dev/v4l/by-id";

    public static async Task<ImmutableArray<VideoSource>> GetSourcesAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ByIdDirectory))
        {
            return [];
        }

        ImmutableArray<VideoSource>.Builder sources = ImmutableArray.CreateBuilder<VideoSource>();
        foreach (string path in Directory.EnumerateFileSystemEntries(ByIdDirectory)
                     .Where(path => path.EndsWith("-video-index0", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            VideoSource? source = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            if (source is not null)
            {
                sources.Add(source);
            }
        }
        return sources.ToImmutable();
    }

    private static async Task<VideoSource?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("v4l2-ctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("--all");
        startInfo.ArgumentList.Add("--list-formats-ext");
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start v4l2-ctl.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || !output.Contains("'MJPG'", StringComparison.Ordinal))
        {
            return null;
        }

        string displayName = CardTypeRegex().Match(output) is { Success: true } card
            ? card.Groups[1].Value.Trim()
            : Path.GetFileName(path);
        VideoMode? mode = SelectMode(output);
        return mode is null
            ? null
            : new VideoSource(new(Path.GetFileName(path)), displayName, mode.Value);
    }

    private static VideoMode? SelectMode(string output)
    {
        Match format = MjpegFormatRegex().Match(output);
        if (!format.Success)
        {
            return null;
        }
        int mjpegStart = format.Index;
        int nextFormat = output.IndexOf("\n\t[", mjpegStart + 1, StringComparison.Ordinal);
        string mjpeg = nextFormat < 0 ? output[mjpegStart..] : output[mjpegStart..nextFormat];
        List<VideoMode> modes = [];
        ushort width = 0;
        ushort height = 0;
        foreach (string line in mjpeg.Split('\n'))
        {
            Match size = SizeRegex().Match(line);
            if (size.Success)
            {
                width = ushort.Parse(size.Groups[1].Value);
                height = ushort.Parse(size.Groups[2].Value);
                continue;
            }
            Match interval = FpsRegex().Match(line);
            if (interval.Success && width > 0 && height > 0)
            {
                double advertised = double.Parse(interval.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                ushort fps = (ushort)Math.Clamp(Math.Round(advertised), 1, 60);
                modes.Add(new(width, height, fps));
            }
        }
        return modes
            .Where(mode => mode.Width <= 1920 && mode.Height <= 1080)
            .OrderByDescending(mode => (long)mode.Width * mode.Height)
            .ThenByDescending(mode => mode.FramesPerSecond)
            .Cast<VideoMode?>()
            .FirstOrDefault();
    }

    [GeneratedRegex(@"Card type\s*:\s*(.+)")]
    private static partial Regex CardTypeRegex();

    [GeneratedRegex(@"\[\d+\]:\s*'MJPG'")]
    private static partial Regex MjpegFormatRegex();

    [GeneratedRegex(@"Size:\s+Discrete\s+(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\((\d+(?:\.\d+)?)\s+fps\)")]
    private static partial Regex FpsRegex();
}
