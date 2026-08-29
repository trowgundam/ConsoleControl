using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ConsoleControl.Gui;

public sealed partial class MappingWindow : Window
{
    public MappingWindow()
    {
        InitializeComponent();
        AddHandler(
            KeyDownEvent,
            (_, eventArgs) =>
                eventArgs.Handled = (DataContext as MappingWindowViewModel)?
                    .HandleKey(eventArgs.PhysicalKey) == true,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Closing += (_, _) => (DataContext as MappingWindowViewModel)?.CancelCapture();
    }

    private async void Save_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MappingWindowViewModel viewModel)
        {
            try
            {
                await viewModel.SaveAsync();
                Close(true);
            }
            catch
            {
            }
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs eventArgs)
    {
        (DataContext as MappingWindowViewModel)?.CancelCapture();
        Close(false);
    }
}