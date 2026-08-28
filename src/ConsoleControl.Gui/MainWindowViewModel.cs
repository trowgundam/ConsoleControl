using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ConsoleControl.Client;
using ConsoleControl.Core;

namespace ConsoleControl.Gui;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IConsoleSession _session;
    private IControlSession? _control;
    private string _statusText = "Connecting to daemon...";
    private bool _disposed;

    public MainWindowViewModel(IConsoleSession session)
    {
        _session = session;
        PulseControlCommand = new AsyncCommand<DigitalControl>(PulseControlAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PulseControlCommand { get; }

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

    public async Task InitializeAsync()
    {
        try
        {
            ConsoleStatus status = await _session.GetStatusAsync(CancellationToken.None);
            if (!status.BridgeConnected)
            {
                StatusText = status.Detail;
                return;
            }

            _control = await _session.TakeControlAsync(
                ControlPriority.InteractiveUser,
                CancellationToken.None);
            StatusText = "Ready";
        }
        catch (Exception exception)
        {
            StatusText = $"Unavailable: {exception.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_control is not null)
        {
            await _control.DisposeAsync();
            _control = null;
        }

        await _session.DisposeAsync();
    }

    private async Task PulseControlAsync(DigitalControl control)
    {
        if (_control is null)
        {
            StatusText = "Control is unavailable";
            return;
        }

        StatusText = $"{GetLabel(control)} pressed";
        try
        {
            ControllerState pressed = CreatePressedState(control);
            await _control.SetStateAsync(pressed, CancellationToken.None);
            await Task.Delay(80);
        }
        catch (Exception exception)
        {
            StatusText = $"Input failed: {exception.Message}";
            return;
        }
        finally
        {
            try
            {
                await _control.SetStateAsync(ControllerState.Neutral, CancellationToken.None);
            }
            catch (Exception exception)
            {
                StatusText = $"Neutral failed: {exception.Message}";
            }
        }

        StatusText = "Ready";
    }

    private static ControllerState CreatePressedState(DigitalControl control) => control switch
    {
        DigitalControl.A => WithButton(GameButtons.A),
        DigitalControl.B => WithButton(GameButtons.B),
        DigitalControl.X => WithButton(GameButtons.X),
        DigitalControl.Y => WithButton(GameButtons.Y),
        DigitalControl.L => WithButton(GameButtons.LeftShoulder),
        DigitalControl.R => WithButton(GameButtons.RightShoulder),
        DigitalControl.ZL => WithButton(GameButtons.LeftTrigger),
        DigitalControl.ZR => WithButton(GameButtons.RightTrigger),
        DigitalControl.Minus => WithButton(GameButtons.Minus),
        DigitalControl.Plus => WithButton(GameButtons.Plus),
        DigitalControl.Home => WithButton(GameButtons.Home),
        DigitalControl.Capture => WithButton(GameButtons.Capture),
        DigitalControl.DPadUp => WithDPad(HatPosition.Up),
        DigitalControl.DPadRight => WithDPad(HatPosition.Right),
        DigitalControl.DPadDown => WithDPad(HatPosition.Down),
        DigitalControl.DPadLeft => WithDPad(HatPosition.Left),
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, null),
    };

    private static ControllerState WithButton(GameButtons button) =>
        ControllerState.Neutral with { Buttons = button };

    private static ControllerState WithDPad(HatPosition position) =>
        ControllerState.Neutral with { DPad = position };

    private static string GetLabel(DigitalControl control) => control switch
    {
        DigitalControl.DPadUp => "D-pad up",
        DigitalControl.DPadRight => "D-pad right",
        DigitalControl.DPadDown => "D-pad down",
        DigitalControl.DPadLeft => "D-pad left",
        _ => control.ToString(),
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum DigitalControl
{
    A,
    B,
    X,
    Y,
    L,
    R,
    ZL,
    ZR,
    Minus,
    Plus,
    Home,
    Capture,
    DPadUp,
    DPadRight,
    DPadDown,
    DPadLeft,
}
