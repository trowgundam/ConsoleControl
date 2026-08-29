using System.Collections.Immutable;
using System.Text.Json;

using ConsoleControl.Core;
using ConsoleControl.Daemon;
using ConsoleControl.Gui;

internal static class PersistenceChecks
{
    public static async Task RunAsync()
    {
        await RequireUnknownSchemaRejectedAsync(
            async path =>
            {
                await new VideoSelectionStore(path).ReadAsync(CancellationToken.None);
            },
            "video selection");
        await RequireUnknownSchemaRejectedAsync(
            async path =>
            {
                await new ControllerBridgeSelectionStore(path).ReadAsync(CancellationToken.None);
            },
            "controller bridge selection");

        await VerifyGuiConfigurationAsync();
        await VerifyLegacyGuiConfigurationMigrationAsync();
        await RequireUnknownSchemaRejectedAsync(
            path => Task.Run(() => new GuiConfigurationStore(path).Load()),
            "GUI configuration");
    }

    private static async Task VerifyGuiConfigurationAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"consolecontrol-gui-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "gui-configuration.json");
        try
        {
            GuiConfigurationStore first = new(path);
            GuiConfiguration initial = first.Load();
            Require(initial.Profiles.IsEmpty && initial.LastInputSource is null,
                "a missing GUI configuration did not produce defaults");

            InputProfile profile = DefaultInputProfiles.For(
                new(InputSourceKind.Gamepad, "steam-controller-guid"));
            await first.SaveProfileAsync(profile, CancellationToken.None);

            GuiConfigurationStore second = new(path);
            second.Load();
            await second.RememberInputSourceAsync(profile.Key, CancellationToken.None);
            await first.RememberWindowAsync(new(1280, 720, true), CancellationToken.None);

            GuiConfiguration reloaded = new GuiConfigurationStore(path).Load();
            Require(reloaded.Profiles is [var reloadedProfile]
                    && reloadedProfile.Key == profile.Key
                    && reloadedProfile.DigitalBindings.Length == profile.DigitalBindings.Length
                    && reloadedProfile.StickBindings.Length == profile.StickBindings.Length
                    && reloadedProfile.TriggerBindings.Length == profile.TriggerBindings.Length,
                "a GUI profile did not round-trip");
            Require(reloaded.LastInputSource == profile.Key,
                "concurrent GUI preference writes lost the selected source");
            Require(reloaded.Window == new GuiWindowState(1280, 720, true),
                "concurrent GUI preference writes lost the window state");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task VerifyLegacyGuiConfigurationMigrationAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"consolecontrol-gui-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "gui-configuration.json");
        string legacyProfiles = Path.Combine(directory, "input-profiles.json");
        string legacyPreferences = Path.Combine(directory, "gui-preferences.json");
        InputProfile profile = DefaultInputProfiles.For(new(InputSourceKind.Gamepad, "legacy-guid"));
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        try
        {
            await File.WriteAllTextAsync(legacyProfiles, JsonSerializer.Serialize(
                new { Revision = 3UL, Profiles = ImmutableArray.Create(profile) }, json));
            await File.WriteAllTextAsync(legacyPreferences, JsonSerializer.Serialize(
                new { SchemaVersion = 1, LastInputSource = profile.Key }, json));

            GuiConfiguration migrated = new GuiConfigurationStore(path).Load();
            Require(migrated.Profiles is [var migratedProfile]
                    && migratedProfile.Key == profile.Key
                    && migratedProfile.DigitalBindings.Length == profile.DigitalBindings.Length
                    && migratedProfile.StickBindings.Length == profile.StickBindings.Length
                    && migratedProfile.TriggerBindings.Length == profile.TriggerBindings.Length
                    && migrated.LastInputSource == profile.Key,
                "legacy GUI input settings were not imported");
            Require(File.Exists(legacyProfiles) && File.Exists(legacyPreferences),
                "legacy GUI input settings were deleted during import");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task RequireUnknownSchemaRejectedAsync(
        Func<string, Task> read,
        string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"consolecontrol-schema-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"SchemaVersion":2,"Revision":0}""");
            try
            {
                await read(path);
                throw new InvalidOperationException($"{name} accepted an unknown schema");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}