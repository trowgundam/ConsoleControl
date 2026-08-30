using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace ConsoleControl.Gui;

internal enum CatppuccinFlavorSelection
{
    FollowDesktop,
    Latte,
    Frappe,
    Macchiato,
    Mocha,
}

internal enum CatppuccinFlavor
{
    Latte,
    Frappe,
    Macchiato,
    Mocha,
}

internal enum CatppuccinAccent
{
    Rosewater,
    Flamingo,
    Pink,
    Mauve,
    Red,
    Maroon,
    Peach,
    Yellow,
    Green,
    Teal,
    Sky,
    Sapphire,
    Blue,
    Lavender,
}

internal readonly record struct AppearancePreference(
    CatppuccinFlavorSelection Flavor,
    CatppuccinAccent Accent)
{
    public static AppearancePreference Default { get; } = new(
        CatppuccinFlavorSelection.FollowDesktop,
        CatppuccinAccent.Blue);

    public AppearancePreference Validate()
    {
        if (!Enum.IsDefined(Flavor) || !Enum.IsDefined(Accent))
        {
            throw new InvalidDataException("The GUI appearance preference is invalid.");
        }
        return this;
    }
}

internal sealed record AppearanceOption<T>(T Value, string DisplayName) where T : struct, Enum
{
    public override string ToString() => DisplayName;
}

internal sealed record AccentOption(
    CatppuccinAccent Value,
    string DisplayName,
    IBrush PreviewBrush);

internal readonly record struct SemanticPalette(
    Color Background,
    Color SecondaryBackground,
    Color Panel,
    Color RaisedPanel,
    Color Border,
    Color Text,
    Color MutedText,
    Color Accent,
    Color OnAccent,
    Color Success,
    Color Warning,
    Color Error,
    Color VideoBackground);

internal readonly record struct ResolvedAppearance(
    CatppuccinFlavor Flavor,
    CatppuccinAccent Accent,
    SemanticPalette Palette,
    ThemeVariant RequestedThemeVariant);

internal interface IAppearancePaletteProvider
{
    ResolvedAppearance Resolve(AppearancePreference preference, bool desktopIsDark);
}

internal sealed class CatppuccinPaletteProvider : IAppearancePaletteProvider
{
    public ResolvedAppearance Resolve(AppearancePreference preference, bool desktopIsDark)
    {
        preference.Validate();
        (CatppuccinFlavor flavor, ThemeVariant requestedThemeVariant) = preference.Flavor switch
        {
            CatppuccinFlavorSelection.FollowDesktop => (
                desktopIsDark ? CatppuccinFlavor.Mocha : CatppuccinFlavor.Latte,
                ThemeVariant.Default),
            CatppuccinFlavorSelection.Latte => (CatppuccinFlavor.Latte, ThemeVariant.Light),
            CatppuccinFlavorSelection.Frappe => (CatppuccinFlavor.Frappe, ThemeVariant.Dark),
            CatppuccinFlavorSelection.Macchiato => (CatppuccinFlavor.Macchiato, ThemeVariant.Dark),
            CatppuccinFlavorSelection.Mocha => (CatppuccinFlavor.Mocha, ThemeVariant.Dark),
            _ => throw new InvalidDataException("The Catppuccin flavor is invalid."),
        };
        FlavorColors colors = GetColors(flavor);
        Color accent = colors.Accent(preference.Accent);
        return new(flavor, preference.Accent, new(
            colors.Base,
            colors.Mantle,
            colors.Surface0,
            colors.Surface1,
            colors.Surface2,
            colors.Text,
            colors.Subtext0,
            accent,
            colors.Base,
            colors.Green,
            colors.Yellow,
            colors.Red,
            colors.Crust),
            requestedThemeVariant);
    }

    // Values come from Catppuccin palette 1.8.0. The project and palette are MIT licensed.
    private static FlavorColors GetColors(CatppuccinFlavor flavor) => flavor switch
    {
        CatppuccinFlavor.Latte => new()
        {
            Base = Hex("#eff1f5"),
            Mantle = Hex("#e6e9ef"),
            Crust = Hex("#dce0e8"),
            Surface0 = Hex("#ccd0da"),
            Surface1 = Hex("#bcc0cc"),
            Surface2 = Hex("#acb0be"),
            Text = Hex("#4c4f69"),
            Subtext0 = Hex("#6c6f85"),
            Red = Hex("#d20f39"),
            Yellow = Hex("#df8e1d"),
            Green = Hex("#40a02b"),
            Rosewater = Hex("#dc8a78"),
            Flamingo = Hex("#dd7878"),
            Pink = Hex("#ea76cb"),
            Mauve = Hex("#8839ef"),
            Maroon = Hex("#e64553"),
            Peach = Hex("#fe640b"),
            Teal = Hex("#179299"),
            Sky = Hex("#04a5e5"),
            Sapphire = Hex("#209fb5"),
            Blue = Hex("#1e66f5"),
            Lavender = Hex("#7287fd"),
        },
        CatppuccinFlavor.Frappe => new()
        {
            Base = Hex("#303446"),
            Mantle = Hex("#292c3c"),
            Crust = Hex("#232634"),
            Surface0 = Hex("#414559"),
            Surface1 = Hex("#51576d"),
            Surface2 = Hex("#626880"),
            Text = Hex("#c6d0f5"),
            Subtext0 = Hex("#a5adce"),
            Red = Hex("#e78284"),
            Yellow = Hex("#e5c890"),
            Green = Hex("#a6d189"),
            Rosewater = Hex("#f2d5cf"),
            Flamingo = Hex("#eebebe"),
            Pink = Hex("#f4b8e4"),
            Mauve = Hex("#ca9ee6"),
            Maroon = Hex("#ea999c"),
            Peach = Hex("#ef9f76"),
            Teal = Hex("#81c8be"),
            Sky = Hex("#99d1db"),
            Sapphire = Hex("#85c1dc"),
            Blue = Hex("#8caaee"),
            Lavender = Hex("#babbf1"),
        },
        CatppuccinFlavor.Macchiato => new()
        {
            Base = Hex("#24273a"),
            Mantle = Hex("#1e2030"),
            Crust = Hex("#181926"),
            Surface0 = Hex("#363a4f"),
            Surface1 = Hex("#494d64"),
            Surface2 = Hex("#5b6078"),
            Text = Hex("#cad3f5"),
            Subtext0 = Hex("#a5adcb"),
            Red = Hex("#ed8796"),
            Yellow = Hex("#eed49f"),
            Green = Hex("#a6da95"),
            Rosewater = Hex("#f4dbd6"),
            Flamingo = Hex("#f0c6c6"),
            Pink = Hex("#f5bde6"),
            Mauve = Hex("#c6a0f6"),
            Maroon = Hex("#ee99a0"),
            Peach = Hex("#f5a97f"),
            Teal = Hex("#8bd5ca"),
            Sky = Hex("#91d7e3"),
            Sapphire = Hex("#7dc4e4"),
            Blue = Hex("#8aadf4"),
            Lavender = Hex("#b7bdf8"),
        },
        CatppuccinFlavor.Mocha => new()
        {
            Base = Hex("#1e1e2e"),
            Mantle = Hex("#181825"),
            Crust = Hex("#11111b"),
            Surface0 = Hex("#313244"),
            Surface1 = Hex("#45475a"),
            Surface2 = Hex("#585b70"),
            Text = Hex("#cdd6f4"),
            Subtext0 = Hex("#a6adc8"),
            Red = Hex("#f38ba8"),
            Yellow = Hex("#f9e2af"),
            Green = Hex("#a6e3a1"),
            Rosewater = Hex("#f5e0dc"),
            Flamingo = Hex("#f2cdcd"),
            Pink = Hex("#f5c2e7"),
            Mauve = Hex("#cba6f7"),
            Maroon = Hex("#eba0ac"),
            Peach = Hex("#fab387"),
            Teal = Hex("#94e2d5"),
            Sky = Hex("#89dceb"),
            Sapphire = Hex("#74c7ec"),
            Blue = Hex("#89b4fa"),
            Lavender = Hex("#b4befe"),
        },
        _ => throw new InvalidDataException("The Catppuccin flavor is invalid."),
    };

    private static Color Hex(string value) => Color.Parse(value);

    private sealed class FlavorColors
    {
        public required Color Base { get; init; }
        public required Color Mantle { get; init; }
        public required Color Crust { get; init; }
        public required Color Surface0 { get; init; }
        public required Color Surface1 { get; init; }
        public required Color Surface2 { get; init; }
        public required Color Text { get; init; }
        public required Color Subtext0 { get; init; }
        public required Color Red { get; init; }
        public required Color Yellow { get; init; }
        public required Color Green { get; init; }
        public required Color Rosewater { get; init; }
        public required Color Flamingo { get; init; }
        public required Color Pink { get; init; }
        public required Color Mauve { get; init; }
        public required Color Maroon { get; init; }
        public required Color Peach { get; init; }
        public required Color Teal { get; init; }
        public required Color Sky { get; init; }
        public required Color Sapphire { get; init; }
        public required Color Blue { get; init; }
        public required Color Lavender { get; init; }

        public Color Accent(CatppuccinAccent accent) => accent switch
        {
            CatppuccinAccent.Rosewater => Rosewater,
            CatppuccinAccent.Flamingo => Flamingo,
            CatppuccinAccent.Pink => Pink,
            CatppuccinAccent.Mauve => Mauve,
            CatppuccinAccent.Red => Red,
            CatppuccinAccent.Maroon => Maroon,
            CatppuccinAccent.Peach => Peach,
            CatppuccinAccent.Yellow => Yellow,
            CatppuccinAccent.Green => Green,
            CatppuccinAccent.Teal => Teal,
            CatppuccinAccent.Sky => Sky,
            CatppuccinAccent.Sapphire => Sapphire,
            CatppuccinAccent.Blue => Blue,
            CatppuccinAccent.Lavender => Lavender,
            _ => throw new InvalidDataException("The Catppuccin accent is invalid."),
        };
    }
}

internal sealed class AppearanceController : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly ReadOnlyCollection<AppearanceOption<CatppuccinFlavorSelection>> Flavors =
        Array.AsReadOnly(new AppearanceOption<CatppuccinFlavorSelection>[]
        {
            new(CatppuccinFlavorSelection.FollowDesktop, "Follow desktop"),
            new(CatppuccinFlavorSelection.Latte, "Latte"),
            new(CatppuccinFlavorSelection.Frappe, "Frappé"),
            new(CatppuccinFlavorSelection.Macchiato, "Macchiato"),
            new(CatppuccinFlavorSelection.Mocha, "Mocha"),
        });
    private readonly Application _application;
    private readonly GuiConfigurationStore _configuration;
    private readonly IAppearancePaletteProvider _palettes;
    private readonly Channel<AppearancePreference> _preferences = Channel.CreateBounded<AppearancePreference>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly Task _persistenceTask;
    private AppearancePreference _current;
    private AppearanceOption<CatppuccinFlavorSelection> _selectedFlavor;
    private readonly ObservableCollection<AccentOption> _accentChoices = [];
    private AccentOption? _selectedAccent;
    private string _statusText = "Changes apply immediately.";
    private bool _disposed;

    public AppearanceController(
        Application application,
        GuiConfigurationStore configuration,
        IAppearancePaletteProvider palettes,
        AppearancePreference initial)
    {
        _application = application;
        _configuration = configuration;
        _palettes = palettes;
        _current = initial.Validate();
        _selectedFlavor = Flavors.Single(option => option.Value == initial.Flavor);
        _application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Apply();
        RefreshAccentChoices();
        _persistenceTask = PersistPreferencesAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<AppearanceOption<CatppuccinFlavorSelection>> FlavorChoices => Flavors;
    public IReadOnlyList<AccentOption> AccentChoices => _accentChoices;

    public AppearanceOption<CatppuccinFlavorSelection> SelectedFlavor
    {
        get => _selectedFlavor;
        set
        {
            if (value == _selectedFlavor)
            {
                return;
            }
            _selectedFlavor = value;
            Select(_current with { Flavor = value.Value });
            RefreshAccentChoices();
            OnPropertyChanged();
        }
    }

    public AccentOption? SelectedAccent
    {
        get => _selectedAccent;
        set
        {
            if (value is null || value == _selectedAccent)
            {
                return;
            }
            _selectedAccent = value;
            Select(_current with { Accent = value.Value });
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _preferences.Writer.TryComplete();
        await _persistenceTask;
    }

    private void Select(AppearancePreference preference)
    {
        _current = preference.Validate();
        Apply();
        StatusText = "Saving appearance...";
        _preferences.Writer.TryWrite(_current);
    }

    private void Apply()
    {
        bool desktopIsDark = _application.ActualThemeVariant != ThemeVariant.Light;
        ResolvedAppearance resolved = _palettes.Resolve(_current, desktopIsDark);
        _application.RequestedThemeVariant = resolved.RequestedThemeVariant;
        SetBrush("Cc.Background", resolved.Palette.Background);
        SetBrush("Cc.SecondaryBackground", resolved.Palette.SecondaryBackground);
        SetBrush("Cc.Panel", resolved.Palette.Panel);
        SetBrush("Cc.RaisedPanel", resolved.Palette.RaisedPanel);
        SetBrush("Cc.Border", resolved.Palette.Border);
        SetBrush("Cc.Text", resolved.Palette.Text);
        SetBrush("Cc.MutedText", resolved.Palette.MutedText);
        SetBrush("Cc.Accent", resolved.Palette.Accent);
        SetBrush("Cc.OnAccent", resolved.Palette.OnAccent);
        SetBrush("Cc.Success", resolved.Palette.Success);
        SetBrush("Cc.Warning", resolved.Palette.Warning);
        SetBrush("Cc.Error", resolved.Palette.Error);
        SetBrush("Cc.VideoBackground", resolved.Palette.VideoBackground);
    }

    private void RefreshAccentChoices()
    {
        bool desktopIsDark = _application.ActualThemeVariant != ThemeVariant.Light;
        _accentChoices.Clear();
        foreach (CatppuccinAccent accent in Enum.GetValues<CatppuccinAccent>())
        {
            Color color = _palettes.Resolve(
                _current with { Accent = accent },
                desktopIsDark).Palette.Accent;
            _accentChoices.Add(new(accent, accent.ToString(), new SolidColorBrush(color)));
        }
        _selectedAccent = _accentChoices.Single(option => option.Value == _current.Accent);
        OnPropertyChanged(nameof(SelectedAccent));
    }

    private void SetBrush(string key, Color color) =>
        _application.Resources[key] = new SolidColorBrush(color);

    private async Task PersistPreferencesAsync()
    {
        await foreach (AppearancePreference preference in _preferences.Reader.ReadAllAsync())
        {
            AppearancePreference latest = preference;
            while (_preferences.Reader.TryRead(out AppearancePreference queued))
            {
                latest = queued;
            }
            try
            {
                await _configuration.RememberAppearanceAsync(latest, CancellationToken.None);
                StatusText = "Appearance saved.";
            }
            catch (Exception exception)
            {
                StatusText = $"Appearance was not saved: {exception.Message}";
            }
        }
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs eventArgs)
    {
        if (_current.Flavor == CatppuccinFlavorSelection.FollowDesktop)
        {
            Apply();
            RefreshAccentChoices();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}