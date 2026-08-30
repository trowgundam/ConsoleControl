using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

using ConsoleControl.Core;

namespace ConsoleControl.Gui;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private double _normalWidth;
    private double _normalHeight;
    private readonly Dictionary<IPointer, (Guid Id, CanonicalDigitalControl Control, Button Button)>
        _pointerHolds = [];

    public MainWindow() : this(GuiWindowState.Default)
    {
    }

    internal MainWindow(GuiWindowState savedWindow)
    {
        InitializeComponent();
        Width = savedWindow.Width;
        Height = savedWindow.Height;
        _normalWidth = savedWindow.Width;
        _normalHeight = savedWindow.Height;
        if (savedWindow.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalWidth = Bounds.Width;
                _normalHeight = Bounds.Height;
            }
        };
        AddHandler(
            PointerPressedEvent,
            ControllerButton_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            PointerReleasedEvent,
            ControllerButton_PointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Opened += async (_, _) =>
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.InitializeAsync();
                await viewModel.SetKeyboardFocusAsync(IsActive);
            }
        };
        AddHandler(
            KeyDownEvent,
            (_, eventArgs) => eventArgs.Handled = ViewModel?.SetKey(eventArgs.PhysicalKey, true) == true,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            KeyUpEvent,
            (_, eventArgs) => eventArgs.Handled = ViewModel?.SetKey(eventArgs.PhysicalKey, false) == true,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
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
            GuiWindowState window = new(
                _normalWidth,
                _normalHeight,
                WindowState == WindowState.Maximized);
            Hide();
            await ViewModel.RememberWindowAsync(window);
            await ViewModel.DisposeAsync();
            _allowClose = true;
            Close();
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void ControllerButton_PointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (FindControllerButton(eventArgs.Source) is not
            { CommandParameter: CanonicalDigitalControl control } button ||
            ViewModel is null ||
            _pointerHolds.ContainsKey(eventArgs.Pointer))
        {
            return;
        }

        Guid id = Guid.NewGuid();
        _pointerHolds.Add(eventArgs.Pointer, (id, control, button));
        button.AddHandler(
            PointerCaptureLostEvent,
            ControllerButton_PointerCaptureLost,
            RoutingStrategies.Direct,
            handledEventsToo: true);
        eventArgs.Pointer.Capture(button);
        eventArgs.Handled = true;
        _ = ViewModel.SetOnScreenControlAsync(id, control, pressed: true);
    }

    private void ControllerButton_PointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        eventArgs.Handled = ReleasePointerHold(eventArgs.Pointer);
    }

    private void ControllerButton_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs) =>
        ReleasePointerHold(eventArgs.Pointer);

    private bool ReleasePointerHold(IPointer pointer)
    {
        if (!_pointerHolds.Remove(
                pointer,
                out (Guid Id, CanonicalDigitalControl Control, Button Button) hold))
        {
            return false;
        }
        hold.Button.RemoveHandler(PointerCaptureLostEvent, ControllerButton_PointerCaptureLost);
        pointer.Capture(null);
        if (ViewModel is { } viewModel)
        {
            _ = viewModel.SetOnScreenControlAsync(hold.Id, hold.Control, pressed: false);
        }
        return true;
    }

    private static Button? FindControllerButton(object? source)
    {
        Button? button = source as Button;
        if (button is null && source is StyledElement element)
        {
            button = element.FindLogicalAncestorOfType<Button>();
        }
        return button?.Classes.Contains("controller") == true ? button : null;
    }

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

    private async void OpenSettings_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (ViewModel is null)
        {
            return;
        }

        SessionSettingsWindow window = new() { DataContext = ViewModel };
        await window.ShowDialog(this);
    }

    private void CycleControllerLayout_Click(object? sender, RoutedEventArgs eventArgs)
    {
        ViewModel?.CycleOnScreenControllerLayout();
    }
}