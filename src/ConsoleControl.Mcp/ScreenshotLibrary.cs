using System.Collections.Immutable;

using ConsoleControl.Core;

using SkiaSharp;

namespace ConsoleControl.Mcp;

internal enum ScreenshotFidelity
{
    Low,
    Medium,
    High,
}

internal readonly record struct ScreenshotId(string Value)
{
    public override string ToString() => Value;
}

internal sealed record ScreenshotRetentionOptions(
    int MaximumEntries,
    long MaximumOriginalBytes,
    TimeSpan Lifetime)
{
    public static ScreenshotRetentionOptions Default { get; } =
        new(16, 64 * 1024 * 1024, TimeSpan.FromMinutes(5));
}

internal sealed record RetainedRendering(
    ScreenshotId Id,
    ulong Generation,
    ulong Sequence,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt,
    ushort SourceWidth,
    ushort SourceHeight,
    int Width,
    int Height,
    ScreenshotFidelity Fidelity,
    byte[] Jpeg);

internal enum ScreenshotUnavailableReason
{
    Expired,
    Evicted,
    NotFound,
}

internal abstract record ScreenshotRenderResult
{
    internal sealed record Found(RetainedRendering Rendering) : ScreenshotRenderResult;
    internal sealed record Unavailable(
        ScreenshotUnavailableReason Reason,
        string Detail) : ScreenshotRenderResult;
}

internal sealed class ScreenshotRetentionException(string message) : Exception(message);

internal sealed class ScreenshotRenderException(ScreenshotId id, string message)
    : Exception(message)
{
    public ScreenshotId Id { get; } = id;
}

internal sealed class ScreenshotLibrary(
    TimeProvider timeProvider,
    ScreenshotRetentionOptions options)
{
    private const int MaximumTombstones = 32;
    private readonly object _gate = new();
    private readonly Dictionary<ScreenshotId, Entry> _entries = [];
    private readonly LinkedList<ScreenshotId> _insertionOrder = [];
    private readonly Dictionary<ScreenshotId, Tombstone> _tombstones = [];
    private readonly LinkedList<ScreenshotId> _tombstoneOrder = [];
    private long _retainedBytes;

    public ScreenshotLibrary()
        : this(TimeProvider.System, ScreenshotRetentionOptions.Default)
    {
    }

    public RetainedRendering AddAndRender(Screenshot source, ScreenshotFidelity fidelity) =>
        AddBatchAndRender([source], fidelity)[0];

    public ImmutableArray<RetainedRendering> AddBatchAndRender(
        IReadOnlyList<Screenshot> sources,
        ScreenshotFidelity fidelity)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        long batchBytes = sources.Sum(source => (long)source.Jpeg.Length);
        if (sources.Count > options.MaximumEntries || batchBytes > options.MaximumOriginalBytes)
        {
            throw new ScreenshotRetentionException(
                $"The screenshot batch needs {sources.Count} entries and {batchBytes} bytes, " +
                $"but the MCP cache allows {options.MaximumEntries} entries and " +
                $"{options.MaximumOriginalBytes} bytes. Capture fewer screenshots in one sequence.");
        }

        Entry[] added;
        lock (_gate)
        {
            PurgeExpired(now);
            while (_entries.Count + sources.Count > options.MaximumEntries ||
                   _retainedBytes + batchBytes > options.MaximumOriginalBytes)
            {
                EvictOldest(now);
            }

            added = sources.Select(source => CreateEntry(source, now)).ToArray();
            foreach (Entry entry in added)
            {
                _entries.Add(entry.Id, entry);
                _insertionOrder.AddLast(entry.Id);
                _retainedBytes += entry.OriginalJpeg.Length;
            }
        }

        ImmutableArray<RetainedRendering>.Builder renderings =
            ImmutableArray.CreateBuilder<RetainedRendering>(added.Length);
        foreach (Entry entry in added)
        {
            renderings.Add(RenderEntry(entry, fidelity));
        }
        return renderings.MoveToImmutable();
    }

    public ScreenshotRenderResult Render(ScreenshotId id, ScreenshotFidelity fidelity)
    {
        Entry? entry;
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            PurgeExpired(now);
            if (!_entries.TryGetValue(id, out entry))
            {
                ScreenshotUnavailableReason reason = _tombstones.TryGetValue(id, out Tombstone? tombstone)
                    ? tombstone.Reason
                    : ScreenshotUnavailableReason.NotFound;
                return new ScreenshotRenderResult.Unavailable(reason, reason switch
                {
                    ScreenshotUnavailableReason.Expired =>
                        $"Screenshot '{id}' expired. Capture a new screenshot before continuing.",
                    ScreenshotUnavailableReason.Evicted =>
                        $"Screenshot '{id}' was evicted to keep the MCP cache within its memory limits. Capture a new screenshot before continuing.",
                    _ =>
                        $"Screenshot '{id}' is not retained by this MCP process. It may be invalid or from an earlier process. Capture a new screenshot before continuing.",
                });
            }
        }
        return new ScreenshotRenderResult.Found(RenderEntry(entry, fidelity));
    }

    private Entry CreateEntry(Screenshot source, DateTimeOffset now) => new(
        new($"ss_{Guid.NewGuid():N}"),
        source.Generation,
        source.Sequence,
        source.Mode,
        source.ReceivedAt,
        now + options.Lifetime,
        source.Jpeg.ToArray());

    private static RetainedRendering RenderEntry(Entry entry, ScreenshotFidelity fidelity)
    {
        if (fidelity == ScreenshotFidelity.High)
        {
            return CreateRendering(
                entry,
                fidelity,
                entry.SourceMode.Width,
                entry.SourceMode.Height,
                entry.OriginalJpeg.ToArray());
        }

        try
        {
            using SKBitmap source = SKBitmap.Decode(entry.OriginalJpeg)
                ?? throw new InvalidDataException("The retained JPEG could not be decoded.");
            (int maximumWidth, int maximumHeight, int quality) = fidelity switch
            {
                ScreenshotFidelity.Low => (640, 360, 70),
                ScreenshotFidelity.Medium => (1280, 720, 80),
                _ => throw new ArgumentOutOfRangeException(nameof(fidelity)),
            };
            double scale = Math.Min(
                1d,
                Math.Min((double)maximumWidth / source.Width, (double)maximumHeight / source.Height));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using SKBitmap resized = new(width, height, source.ColorType, source.AlphaType);
            if (!source.ScalePixels(resized, new SKSamplingOptions(SKCubicResampler.Mitchell)))
            {
                throw new InvalidDataException("The retained JPEG could not be resized.");
            }
            using SKImage image = SKImage.FromBitmap(resized);
            using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality)
                ?? throw new InvalidDataException("The resized screenshot could not be encoded.");
            return CreateRendering(entry, fidelity, width, height, encoded.ToArray());
        }
        catch (Exception exception) when (exception is not ScreenshotRenderException)
        {
            throw new ScreenshotRenderException(
                entry.Id,
                $"Screenshot '{entry.Id}' is retained, but {fidelity.ToString().ToLowerInvariant()} rendering failed: {exception.Message} Try high fidelity to retrieve the exact original JPEG.");
        }
    }

    private static RetainedRendering CreateRendering(
        Entry entry,
        ScreenshotFidelity fidelity,
        int width,
        int height,
        byte[] jpeg) => new(
            entry.Id,
            entry.Generation,
            entry.Sequence,
            entry.ReceivedAt,
            entry.ExpiresAt,
            entry.SourceMode.Width,
            entry.SourceMode.Height,
            width,
            height,
            fidelity,
            jpeg);

    private void PurgeExpired(DateTimeOffset now)
    {
        while (_insertionOrder.First is { } node &&
               _entries[node.Value].ExpiresAt <= now)
        {
            Remove(node.Value, ScreenshotUnavailableReason.Expired, now);
        }
        PurgeTombstones(now);
    }

    private void EvictOldest(DateTimeOffset now)
    {
        ScreenshotId id = _insertionOrder.First?.Value
            ?? throw new InvalidOperationException("The screenshot cache cannot satisfy its limits.");
        Remove(id, ScreenshotUnavailableReason.Evicted, now);
    }

    private void Remove(
        ScreenshotId id,
        ScreenshotUnavailableReason reason,
        DateTimeOffset now)
    {
        Entry entry = _entries[id];
        _entries.Remove(id);
        _insertionOrder.Remove(id);
        _retainedBytes -= entry.OriginalJpeg.Length;
        _tombstones[id] = new(reason, now + options.Lifetime);
        _tombstoneOrder.AddLast(id);
        while (_tombstoneOrder.Count > MaximumTombstones)
        {
            ScreenshotId oldest = _tombstoneOrder.First!.Value;
            _tombstoneOrder.RemoveFirst();
            _tombstones.Remove(oldest);
        }
    }

    private void PurgeTombstones(DateTimeOffset now)
    {
        while (_tombstoneOrder.First is { } node &&
               _tombstones[node.Value].ExpiresAt <= now)
        {
            _tombstones.Remove(node.Value);
            _tombstoneOrder.RemoveFirst();
        }
    }

    private sealed record Entry(
        ScreenshotId Id,
        ulong Generation,
        ulong Sequence,
        VideoMode SourceMode,
        DateTimeOffset ReceivedAt,
        DateTimeOffset ExpiresAt,
        byte[] OriginalJpeg);

    private sealed record Tombstone(
        ScreenshotUnavailableReason Reason,
        DateTimeOffset ExpiresAt);
}