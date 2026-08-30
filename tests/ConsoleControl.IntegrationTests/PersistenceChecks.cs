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
        VerifyGuiSchemaOneMigration();
        VerifyInvalidGuiAppearanceRejected();
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
            TestAssert.Require(initial.Profiles.IsEmpty && initial.LastInputSource is null,
                "a missing GUI configuration did not produce defaults");
            TestAssert.Require(initial.Appearance == AppearancePreference.Default,
                "a missing GUI configuration did not use the desktop-aware Catppuccin default");

            InputProfile profile = DefaultInputProfiles.For(
                new(InputSourceKind.Gamepad, "steam-controller-guid"));
            await first.SaveProfileAsync(profile, CancellationToken.None);

            GuiConfigurationStore second = new(path);
            second.Load();
            await second.RememberInputSourceAsync(profile.Key, CancellationToken.None);
            await first.RememberWindowAsync(new(1280, 720, true), CancellationToken.None);
            _ = new GuiWindowState(840, 680, false).Validate();
            AppearancePreference appearance = new(
                CatppuccinFlavorSelection.Macchiato,
                CatppuccinAccent.Mauve);
            await second.RememberAppearanceAsync(appearance, CancellationToken.None);

            GuiConfiguration reloaded = new GuiConfigurationStore(path).Load();
            TestAssert.Require(reloaded.Profiles is [var reloadedProfile]
                    && reloadedProfile.Key == profile.Key
                    && reloadedProfile.DigitalBindings.Length == profile.DigitalBindings.Length
                    && reloadedProfile.StickBindings.Length == profile.StickBindings.Length
                    && reloadedProfile.TriggerBindings.Length == profile.TriggerBindings.Length,
                "a GUI profile did not round-trip");
            TestAssert.Require(reloaded.LastInputSource == profile.Key,
                "concurrent GUI preference writes lost the selected source");
            TestAssert.Require(reloaded.Window == new GuiWindowState(1280, 720, true),
                "concurrent GUI preference writes lost the window state");
            TestAssert.Require(reloaded.Appearance == appearance,
                "concurrent GUI preference writes lost the appearance");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void VerifyGuiSchemaOneMigration()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"consolecontrol-gui-v1-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "gui-configuration.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 1,
                  "revision": 4,
                  "profiles": [],
                  "lastInputSource": null,
                  "window": { "width": 1000, "height": 900, "maximized": false }
                }
                """);
            GuiConfiguration migrated = new GuiConfigurationStore(path).Load();
            TestAssert.Require(migrated.Appearance == AppearancePreference.Default,
                "schema 1 GUI configuration did not receive the Catppuccin default");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyInvalidGuiAppearanceRejected()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"consolecontrol-gui-theme-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "gui-configuration.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": 2,
                  "revision": 0,
                  "profiles": [],
                  "lastInputSource": null,
                  "window": { "width": 1000, "height": 900, "maximized": false },
                  "appearance": { "flavor": "Espresso", "accent": "Blue" }
                }
                """);
            try
            {
                new GuiConfigurationStore(path).Load();
                throw new InvalidOperationException("GUI configuration accepted an unknown color scheme");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
            TestAssert.Require(migrated.Profiles is [var migratedProfile]
                    && migratedProfile.Key == profile.Key
                    && migratedProfile.DigitalBindings.Length == profile.DigitalBindings.Length
                    && migratedProfile.StickBindings.Length == profile.StickBindings.Length
                    && migratedProfile.TriggerBindings.Length == profile.TriggerBindings.Length
                    && migrated.LastInputSource == profile.Key,
                "legacy GUI input settings were not imported");
            TestAssert.Require(File.Exists(legacyProfiles) && File.Exists(legacyPreferences),
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
            await File.WriteAllTextAsync(path, """{"SchemaVersion":3,"Revision":0}""");
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
}