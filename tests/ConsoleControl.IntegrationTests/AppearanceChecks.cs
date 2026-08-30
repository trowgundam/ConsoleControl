using Avalonia.Media;
using Avalonia.Styling;

using ConsoleControl.Gui;

internal static class AppearanceChecks
{
    public static void Run()
    {
        CatppuccinPaletteProvider provider = new();

        ResolvedAppearance light = provider.Resolve(AppearancePreference.Default, desktopIsDark: false);
        TestAssert.Require(
            light.Flavor == CatppuccinFlavor.Latte &&
            light.Accent == CatppuccinAccent.Blue &&
            light.Palette.Background == Color.Parse("#eff1f5") &&
            light.Palette.Accent == Color.Parse("#1e66f5") &&
            light.RequestedThemeVariant == ThemeVariant.Default,
            "the light desktop default was not Catppuccin Latte Blue");

        ResolvedAppearance dark = provider.Resolve(AppearancePreference.Default, desktopIsDark: true);
        TestAssert.Require(
            dark.Flavor == CatppuccinFlavor.Mocha &&
            dark.Accent == CatppuccinAccent.Blue &&
            dark.Palette.Background == Color.Parse("#1e1e2e") &&
            dark.Palette.Accent == Color.Parse("#89b4fa") &&
            dark.RequestedThemeVariant == ThemeVariant.Default,
            "the dark desktop default was not Catppuccin Mocha Blue");

        TestAssert.Require(
            provider.Resolve(
                new(CatppuccinFlavorSelection.Latte, CatppuccinAccent.Blue),
                desktopIsDark: true).RequestedThemeVariant == ThemeVariant.Light &&
            provider.Resolve(
                new(CatppuccinFlavorSelection.Frappe, CatppuccinAccent.Blue),
                desktopIsDark: false).RequestedThemeVariant == ThemeVariant.Dark,
            "an explicit Catppuccin flavor did not select its matching Avalonia theme variant");

        foreach (CatppuccinFlavorSelection flavor in Enum.GetValues<CatppuccinFlavorSelection>())
        {
            foreach (CatppuccinAccent accent in Enum.GetValues<CatppuccinAccent>())
            {
                ResolvedAppearance resolved = provider.Resolve(new(flavor, accent), desktopIsDark: true);
                TestAssert.Require(
                    resolved.Palette.Accent.A == byte.MaxValue &&
                    resolved.Palette.Background.A == byte.MaxValue &&
                    resolved.Palette.Text.A == byte.MaxValue,
                    $"{flavor} {accent} did not resolve to an opaque semantic palette");
            }
        }
    }
}