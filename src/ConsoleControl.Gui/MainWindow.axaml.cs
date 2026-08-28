using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ConsoleControl.Gui;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.InitializeAsync();
                await viewModel.SetKeyboardFocusAsync(IsActive);
            }
        };
        KeyDown += (_, eventArgs) => ViewModel?.SetKey(eventArgs.PhysicalKey, true);
        KeyUp += (_, eventArgs) => ViewModel?.SetKey(eventArgs.PhysicalKey, false);
        Activated += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetKeyboardFocusAsync(true);
            }
        };
        Deactivated += async (_, _) =>
        {
            if (ViewModel is not null)
            {
                await ViewModel.SetKeyboardFocusAsync(false);
            }
        };
        Closing += async (_, eventArgs) =>
        {
            if (_allowClose || ViewModel is null)
            {
                return;
            }

            eventArgs.Cancel = true;
            Hide();
            await ViewModel.DisposeAsync();
            _allowClose = true;
            Close();
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void OpenMapping_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel?.Forwarder is not { ActiveProfile: not null } forwarder)
        {
            return;
        }

        MappingWindow window = new()
        {
            DataContext = new MappingWindowViewModel(forwarder),
        };
        await window.ShowDialog<bool>(this);
    }
}
