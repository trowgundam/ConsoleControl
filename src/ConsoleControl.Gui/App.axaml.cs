using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ConsoleControl.Client;

namespace ConsoleControl.Gui;

public sealed partial class App : Application
{
    private MainWindowViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            GrpcConsoleSession session = GrpcConsoleSession.Connect(Program.DaemonUri);
            _viewModel = new MainWindowViewModel(session);
            desktop.MainWindow = new MainWindow { DataContext = _viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
