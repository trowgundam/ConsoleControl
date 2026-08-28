using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ConsoleControl.Client;
using ConsoleControl.Core;
using ConsoleControl.Input.Sdl;
using Avalonia.Input;
using Avalonia.Threading;

namespace ConsoleControl.Gui;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IConsoleSession _session;
    private IControlSession? _control;
    private InputForwarder? _forwarder;
    private InputSourceOption? _selectedInputSource;
    private string _statusText = "Connecting to daemon...";
    private bool _disposed;

    public MainWindowViewModel(IConsoleSession session)
    {
        _session = session;
        PulseControlCommand = new AsyncCommand<CanonicalDigitalControl>(PulseControlAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PulseControlCommand { get; }

    public ObservableCollection<InputSourceOption> InputSources { get; } = [];
    internal InputForwarder? Forwarder => _forwarder;

    public InputSourceOption? SelectedInputSource
    {
        get => _selectedInputSource;
        set
        {
            if (_selectedInputSource == value)
            {
                return;
            }

            _selectedInputSource = value;
            OnPropertyChanged();
            if (value is not null && _forwarder is not null)
            {
                _ = SelectInputSourceAsync(value);
            }
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
            InputConfiguration configuration =
                await _session.GetInputConfigurationAsync(CancellationToken.None);
            SdlGamepadManager gamepads = new();
            _forwarder = new(_session, _control, gamepads, configuration);
            _forwarder.SourcesChanged += OnSourcesChanged;
            _forwarder.StatusChanged += OnForwardingStatusChanged;
            RefreshInputSources();
            SelectedInputSource = InputSources[0];
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
        if (_forwarder is not null)
        {
            InputForwarder forwarder = _forwarder;
            _forwarder = null;
            forwarder.SourcesChanged -= OnSourcesChanged;
            forwarder.StatusChanged -= OnForwardingStatusChanged;
            await forwarder.DisposeAsync();
        }

        if (_control is not null)
        {
            await _control.DisposeAsync();
            _control = null;
        }

        await _session.DisposeAsync();
    }

    public void SetKey(PhysicalKey key, bool pressed)
    {
        if (!_disposed)
        {
            _forwarder?.SetKey(key, pressed);
        }
    }

    public Task SetKeyboardFocusAsync(bool focused) =>
        !_disposed
            ? _forwarder?.SetKeyboardFocusAsync(focused, CancellationToken.None) ?? Task.CompletedTask
            : Task.CompletedTask;

    private async Task PulseControlAsync(CanonicalDigitalControl control)
    {
        if (_control is null)
        {
            StatusText = "Control is unavailable";
            return;
        }

        StatusText = $"{GetLabel(control)} pressed";
        try
        {
            if (_forwarder is null)
            {
                throw new InvalidOperationException("Input forwarding is unavailable.");
            }

            await _forwarder.PulseAsync(control, TimeSpan.FromMilliseconds(80), CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusText = $"Input failed: {exception.Message}";
            return;
        }
        StatusText = "Ready";
    }

    private static string GetLabel(CanonicalDigitalControl control) => control switch
    {
        CanonicalDigitalControl.DPadUp => "D-pad up",
        CanonicalDigitalControl.DPadRight => "D-pad right",
        CanonicalDigitalControl.DPadDown => "D-pad down",
        CanonicalDigitalControl.DPadLeft => "D-pad left",
        _ => control.ToString(),
    };

    private async Task SelectInputSourceAsync(InputSourceOption source)
    {
        try
        {
            await _forwarder!.SelectSourceAsync(source, CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusText = $"Input selection failed: {exception.Message}";
        }
    }

    private void OnSourcesChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(RefreshInputSources);

    private void OnForwardingStatusChanged(object? sender, string status) =>
        Dispatcher.UIThread.Post(() => StatusText = status);

    private void RefreshInputSources()
    {
        string? selectedId = SelectedInputSource?.Id;
        InputSources.Clear();
        foreach (InputSourceOption source in _forwarder?.Sources ?? [])
        {
            InputSources.Add(source);
        }

        _selectedInputSource = InputSources.FirstOrDefault(source => source.Id == selectedId)
            ?? InputSources.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedInputSource));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
