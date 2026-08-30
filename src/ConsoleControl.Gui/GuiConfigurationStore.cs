using System.Collections.Immutable;
using System.Text.Json;

using ConsoleControl.Core;

namespace ConsoleControl.Gui;

internal readonly record struct GuiWindowState(double Width, double Height, bool Maximized)
{
    public static GuiWindowState Default { get; } = new(1100, 760, false);

    public GuiWindowState Validate() => double.IsFinite(Width) && double.IsFinite(Height) &&
                                        Width >= 760 && Height >= 680
        ? this
        : throw new InvalidDataException("Saved window dimensions are invalid.");
}

internal sealed record GuiConfiguration(
    ImmutableArray<InputProfile> Profiles,
    InputProfileKey? LastInputSource,
    GuiWindowState Window,
    AppearancePreference Appearance);

internal sealed class GuiConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string _path;
    private readonly string _lockPath;
    private readonly string _legacyProfilesPath;
    private readonly string _legacyPreferencesPath;
    private GuiConfiguration _current = new(
        [],
        null,
        GuiWindowState.Default,
        AppearancePreference.Default);

    public GuiConfigurationStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ConsoleControl",
            "gui-configuration.json");
        _lockPath = _path + ".lock";
        string directory = Path.GetDirectoryName(_path)!;
        _legacyProfilesPath = Path.Combine(directory, "input-profiles.json");
        _legacyPreferencesPath = Path.Combine(directory, "gui-preferences.json");
    }

    public GuiConfiguration Current => Volatile.Read(ref _current);

    public GuiConfiguration Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using FileStream configurationLock = AcquireLock();
        StoredConfiguration stored;
        if (File.Exists(_path))
        {
            stored = ReadStored(_path);
        }
        else
        {
            stored = ImportLegacy();
            WriteStored(stored);
        }
        GuiConfiguration configuration = ToDomain(stored);
        Volatile.Write(ref _current, configuration);
        return configuration;
    }

    public Task<GuiConfiguration> SaveProfileAsync(InputProfile profile, CancellationToken cancellationToken)
    {
        profile.Validate();
        return MutateAsync(stored => stored with
        {
            Profiles = stored.Profiles
                .Where(existing => existing.Key != profile.Key)
                .Append(profile)
                .OrderBy(existing => existing.Key.Kind)
                .ThenBy(existing => existing.Name, StringComparer.Ordinal)
                .ToImmutableArray(),
        }, cancellationToken);
    }

    public Task<GuiConfiguration> RememberInputSourceAsync(
        InputProfileKey source,
        CancellationToken cancellationToken)
    {
        if (source.Kind == InputSourceKind.Unspecified || string.IsNullOrWhiteSpace(source.HardwareId))
        {
            throw new ArgumentException("The input source preference is invalid.", nameof(source));
        }
        return MutateAsync(stored => stored with { LastInputSource = source }, cancellationToken);
    }

    public Task<GuiConfiguration> RememberWindowAsync(
        GuiWindowState window,
        CancellationToken cancellationToken) =>
        MutateAsync(stored => stored with { Window = window.Validate() }, cancellationToken);

    public Task<GuiConfiguration> RememberAppearanceAsync(
        AppearancePreference appearance,
        CancellationToken cancellationToken)
    {
        appearance.Validate();
        return MutateAsync(stored => stored with
        {
            Appearance = ToStored(appearance),
        }, cancellationToken);
    }

    private async Task<GuiConfiguration> MutateAsync(
        Func<StoredConfiguration, StoredConfiguration> mutation,
        CancellationToken cancellationToken)
    {
        await using FileStream configurationLock = await AcquireLockAsync(cancellationToken)
            .ConfigureAwait(false);
        StoredConfiguration stored = File.Exists(_path)
            ? ReadStored(_path)
            : ImportLegacy();
        StoredConfiguration updated = mutation(stored) with { Revision = checked(stored.Revision + 1) };
        WriteStored(updated);
        GuiConfiguration configuration = ToDomain(updated);
        Volatile.Write(ref _current, configuration);
        return configuration;
    }

    private StoredConfiguration ImportLegacy()
    {
        ImmutableArray<InputProfile> profiles = [];
        if (File.Exists(_legacyProfilesPath))
        {
            using FileStream stream = File.OpenRead(_legacyProfilesPath);
            LegacyInputConfiguration legacy = JsonSerializer.Deserialize<LegacyInputConfiguration>(
                stream, JsonOptions) ?? throw new InvalidDataException("The legacy input profile file is empty.");
            profiles = legacy.Profiles;
        }
        InputProfileKey? lastInputSource = null;
        if (File.Exists(_legacyPreferencesPath))
        {
            using FileStream stream = File.OpenRead(_legacyPreferencesPath);
            LegacyPreferences legacy = JsonSerializer.Deserialize<LegacyPreferences>(stream, JsonOptions)
                ?? throw new InvalidDataException("The legacy GUI preference file is empty.");
            if (legacy.SchemaVersion != 1)
            {
                throw new InvalidDataException("The legacy GUI preferences use an unsupported schema.");
            }
            lastInputSource = legacy.LastInputSource is { } source
                ? new(source.Kind, source.HardwareId)
                : null;
        }
        return Validate(new(
            2,
            0,
            profiles,
            lastInputSource,
            GuiWindowState.Default,
            ToStored(AppearancePreference.Default)));
    }

    private static GuiConfiguration ToDomain(StoredConfiguration stored) => new(
        stored.Profiles,
        stored.LastInputSource,
        stored.Window,
        ToDomain(stored.Appearance!));

    private static StoredConfiguration ReadStored(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Validate(JsonSerializer.Deserialize<StoredConfiguration>(stream, JsonOptions)
            ?? throw new InvalidDataException("The GUI configuration file is empty."));
    }

    private static StoredConfiguration Validate(StoredConfiguration stored)
    {
        stored = stored.SchemaVersion switch
        {
            1 => stored with
            {
                SchemaVersion = 2,
                Appearance = ToStored(AppearancePreference.Default),
            },
            2 when stored.Appearance is not null => stored,
            2 => throw new InvalidDataException("The GUI configuration has no appearance preference."),
            _ => throw new InvalidDataException("The GUI configuration uses an unsupported schema."),
        };
        if (stored.SchemaVersion != 2)
        {
            throw new InvalidDataException("The GUI configuration uses an unsupported schema.");
        }
        if (stored.Profiles.IsDefault)
        {
            throw new InvalidDataException("The GUI configuration has no profile collection.");
        }
        foreach (InputProfile profile in stored.Profiles)
        {
            profile.Validate();
        }
        if (stored.Profiles.Select(profile => profile.Key).Distinct().Count() != stored.Profiles.Length)
        {
            throw new InvalidDataException("The GUI configuration contains duplicate input profiles.");
        }
        if (stored.LastInputSource is { } source &&
            (source.Kind == InputSourceKind.Unspecified || string.IsNullOrWhiteSpace(source.HardwareId)))
        {
            throw new InvalidDataException("The GUI configuration has an invalid input source preference.");
        }
        stored.Window.Validate();
        ToDomain(stored.Appearance!);
        return stored;
    }

    private static StoredAppearance ToStored(AppearancePreference appearance) => new(
        appearance.Flavor.ToString(),
        appearance.Accent.ToString());

    private static AppearancePreference ToDomain(StoredAppearance appearance)
    {
        if (!Enum.TryParse(appearance.Flavor, ignoreCase: false, out CatppuccinFlavorSelection flavor) ||
            !Enum.TryParse(appearance.Accent, ignoreCase: false, out CatppuccinAccent accent))
        {
            throw new InvalidDataException("The GUI configuration has an invalid appearance preference.");
        }
        return new AppearancePreference(flavor, accent).Validate();
    }

    private void WriteStored(StoredConfiguration stored)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, stored, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private FileStream AcquireLock()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            try
            {
                return new(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed record StoredConfiguration(
        int SchemaVersion,
        ulong Revision,
        ImmutableArray<InputProfile> Profiles,
        InputProfileKey? LastInputSource,
        GuiWindowState Window,
        StoredAppearance? Appearance);
    private sealed record StoredAppearance(string Flavor, string Accent);
    private sealed record LegacyInputConfiguration(ulong Revision, ImmutableArray<InputProfile> Profiles);
    private sealed record LegacyPreferences(int SchemaVersion, LegacyInputSource? LastInputSource);
    private sealed record LegacyInputSource(InputSourceKind Kind, string HardwareId);
}