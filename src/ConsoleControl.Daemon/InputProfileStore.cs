using System.Collections.Immutable;
using System.Text.Json;

using ConsoleControl.Core;

namespace ConsoleControl.Daemon;

internal sealed class InputProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public InputProfileStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ConsoleControl",
            "input-profiles.json"))
    {
    }

    internal InputProfileStore(string path)
    {
        _path = path;
    }

    public async Task<InputConfiguration> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<InputConfiguration> SaveAsync(
        InputProfile profile,
        ulong expectedRevision,
        CancellationToken cancellationToken)
    {
        profile.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InputConfiguration current = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (current.Revision != expectedRevision)
            {
                throw new InputConfigurationConflictException(
                    $"Input configuration revision {expectedRevision} is stale; current revision is {current.Revision}.");
            }

            ImmutableArray<InputProfile> profiles = current.Profiles
                .Where(existing => existing.Key != profile.Key)
                .Append(profile)
                .OrderBy(existing => existing.Key.Kind)
                .ThenBy(existing => existing.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            InputConfiguration updated = new(current.Revision + 1, profiles);

            string? directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory!);
            string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using FileStream stream = new(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous);
                await JsonSerializer.SerializeAsync(stream, updated, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InputConfiguration> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new(0, []);
        }

        await using FileStream stream = File.OpenRead(_path);
        InputConfiguration configuration =
            await JsonSerializer.DeserializeAsync<InputConfiguration>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidDataException("The input profile file is empty.");
        foreach (InputProfile profile in configuration.Profiles)
        {
            profile.Validate();
        }

        return configuration;
    }
}

internal sealed class InputConfigurationConflictException(string message) : Exception(message);