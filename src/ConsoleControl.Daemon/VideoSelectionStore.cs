using System.Text.Json;

using ConsoleControl.Core;

namespace ConsoleControl.Daemon;

internal sealed class VideoSelectionStore
{
    private readonly string _path;

    public VideoSelectionStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ConsoleControl",
            "video-selection.json");
    }

    public async Task<(VideoSourceId? SourceId, ulong Revision)> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return (null, 0);
        }
        await using FileStream stream = File.OpenRead(_path);
        StoredSelection? stored = await JsonSerializer.DeserializeAsync<StoredSelection>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return stored is null
            ? (null, 0)
            : (string.IsNullOrWhiteSpace(stored.SourceId) ? null : new(stored.SourceId), stored.Revision);
    }

    public async Task WriteAsync(
        VideoSourceId sourceId,
        ulong revision,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        string temporary = _path + ".tmp";
        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, new StoredSelection(1, sourceId.Value, revision),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record StoredSelection(int SchemaVersion, string SourceId, ulong Revision);
}