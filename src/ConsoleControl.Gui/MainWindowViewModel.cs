using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ConsoleControl.Client;
using ConsoleControl.Core;
using ConsoleControl.Input.Sdl;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ConsoleControl.Gui;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IConsoleSession _session;
    private IControlSession? _control;
    private InputForwarder? _forwarder;
    private InputSourceOption? _selectedInputSource;
    private VideoSource? _selectedVideoSource;
    private VideoPresenter? _videoPresenter;
    private Bitmap? _videoImage;
    private ulong _videoRevision;
    private bool _loadingVideoSources;
    private string _videoStatusText = "Finding video sources...";
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
    public ObservableCollection<VideoSource> VideoSources { get; } = [];
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

    public VideoSource? SelectedVideoSource
    {
        get => _selectedVideoSource;
        set
        {
            if (_selectedVideoSource == value)
            {
                return;
            }
            _selectedVideoSource = value;
            OnPropertyChanged();
            if (!_loadingVideoSources && value is not null)
            {
                _ = SelectVideoSourceAsync(value);
            }
        }
    }

    public Bitmap? VideoImage
    {
        get => _videoImage;
        private set
        {
            Bitmap? previous = _videoImage;
            _videoImage = value;
            OnPropertyChanged();
            previous?.Dispose();
        }
    }

    public string VideoStatusText
    {
        get => _videoStatusText;
        private set
        {
            if (_videoStatusText == value)
            {
                return;
            }
            _videoStatusText = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        await InitializeVideoAsync();
        try
        {
            ConsoleStatus status = await _session.GetStatusAsync(CancellationToken.None);
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
            StatusText = status.BridgeConnected
                ? FormatConnectionState(_control.ConnectionState)
                : status.Detail;
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
        if (_videoPresenter is not null)
        {
            await _videoPresenter.DisposeAsync();
            _videoPresenter = null;
        }
        await Dispatcher.UIThread.InvokeAsync(() => VideoImage = null);
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

    private async Task InitializeVideoAsync()
    {
        try
        {
            VideoInventory inventory = await _session.GetVideoInventoryAsync(CancellationToken.None);
            _loadingVideoSources = true;
            VideoSources.Clear();
            foreach (VideoSource source in inventory.Sources)
            {
                VideoSources.Add(source);
            }
            _videoRevision = inventory.Revision;
            _selectedVideoSource = VideoSources.FirstOrDefault(source =>
                source.Id == inventory.SelectedSourceId);
            OnPropertyChanged(nameof(SelectedVideoSource));
            _loadingVideoSources = false;
            VideoStatusText = inventory.Status;
            await StartVideoPresenterAsync(inventory.LiveStreamUri);
        }
        catch (Exception exception)
        {
            _loadingVideoSources = false;
            VideoStatusText = $"Video unavailable: {exception.Message}";
        }
    }

    private async Task SelectVideoSourceAsync(VideoSource source)
    {
        try
        {
            VideoSelection selection = await _session.SelectVideoSourceAsync(
                source.Id,
                _videoRevision,
                CancellationToken.None);
            _videoRevision = selection.Revision;
            VideoStatusText = selection.Status;
        }
        catch (Exception exception)
        {
            VideoStatusText = $"Video selection failed: {exception.Message}";
            await InitializeVideoAsync();
        }
    }

    private async Task StartVideoPresenterAsync(Uri streamUri)
    {
        if (_videoPresenter is not null)
        {
            await _videoPresenter.DisposeAsync();
        }
        _videoPresenter = new(
            bitmap => Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                {
                    bitmap.Dispose();
                    return;
                }
                VideoImage = bitmap;
                VideoStatusText = SelectedVideoSource is null
                    ? "Video ready"
                    : $"Video ready: {SelectedVideoSource.DisplayName}";
            }),
            status => Dispatcher.UIThread.Post(() => VideoStatusText = status),
            () => Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    VideoImage = null;
                }
            }));
        _videoPresenter.Start(streamUri);
    }

    private void OnSourcesChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(RefreshInputSources);

    private void OnForwardingStatusChanged(object? sender, string status) =>
        Dispatcher.UIThread.Post(() => StatusText = status);

    private static string FormatConnectionState(ControlConnectionState state) => state switch
    {
        ControlConnectionState.Ready => "Ready",
        ControlConnectionState.WaitingForBridge => "Controller bridge disconnected; reconnecting...",
        ControlConnectionState.WaitingForControl => "Control is held by another client; waiting...",
        ControlConnectionState.Reconnecting => "Daemon disconnected; reconnecting...",
        ControlConnectionState.Stopped => "Controller forwarding stopped",
        _ => "Connecting to daemon...",
    };

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
