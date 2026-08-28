using System.Collections.Immutable;

namespace ConsoleControl.Core;

public readonly record struct VideoSourceId
{
    public VideoSourceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct VideoMode(ushort Width, ushort Height, ushort FramesPerSecond)
{
    public override string ToString() => $"{Width}x{Height} {FramesPerSecond} fps";
}

public sealed record VideoSource(
    VideoSourceId Id,
    string DisplayName,
    VideoMode PreferredMode);

public sealed record VideoInventory(
    ImmutableArray<VideoSource> Sources,
    VideoSourceId? SelectedSourceId,
    ulong Revision,
    Uri LiveStreamUri,
    string Status);

public sealed record VideoSelection(
    VideoSourceId SourceId,
    ulong Revision,
    string Status);

public sealed record EncodedVideoFrame(
    ulong Generation,
    ulong Sequence,
    VideoMode Mode,
    DateTimeOffset ReceivedAt,
    byte[] Jpeg);