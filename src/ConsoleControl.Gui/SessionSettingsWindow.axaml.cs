using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ConsoleControl.Gui;

public sealed partial class SessionSettingsWindow : Window
{
    public SessionSettingsWindow() => InitializeComponent();

    private void Close_Click(object? sender, RoutedEventArgs eventArgs) => Close();
}