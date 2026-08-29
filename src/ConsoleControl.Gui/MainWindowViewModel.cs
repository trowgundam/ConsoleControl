using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using ConsoleControl.Client;
using ConsoleControl.Core;
using ConsoleControl.Input.Sdl;

namespace ConsoleControl.Gui;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IConsoleSession _session;
    private readonly GuiConfigurationStore _configuration;
    private IControlSession? _control;
    private InputForwarder? _forwarder;
    private InputSourceOption? _selectedInputSource;
    private VideoSource? _selectedVideoSource;
    private ControllerBridge? _selectedControllerBridge;
    private VideoPresenter? _videoPresenter;
    private Bitmap? _videoImage;
    private ulong _videoRevision;
    private bool _loadingVideoSources;
    private bool _loadingControllerBridges;
    private ulong _controllerBridgeRevision;
    private PendingControlRequest? _pendingControlRequest;
    private readonly CancellationTokenSource _statusStop = new();
    private Task? _statusTask;
    private string _videoStatusText = "Finding video sources...";
    private string _statusText = "Connecting to daemon...";
    private bool _keyboardFocused;
    private InputProfileKey? _preferredInputProfile;
    private long _inputSelectionGeneration;
    private bool _disposed;

    internal MainWindowViewModel(IConsoleSession session, GuiConfigurationStore configuration)
    {
        _session = session;
        _configuration = configuration;
        _preferredInputProfile = configuration.Current.LastInputSource;
        PulseControlCommand = new AsyncCommand<CanonicalDigitalControl>(PulseControlAsync);
        ToggleControlCommand = new AsyncCommand(ToggleControlAsync);
        DeclineControlRequestCommand = new AsyncCommand(DeclineControlRequestAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PulseControlCommand { get; }
    public ICommand ToggleControlCommand { get; }
    public ICommand DeclineControlRequestCommand { get; }

    public bool HasControl => _control is not null;
    public string ControlActionText => HasControl ? "Release Control" : "Take Control";
    public bool HasPendingControlRequest => _pendingControlRequest is not null;
    public string PendingControlRequestReason => _pendingControlRequest?.Reason ?? string.Empty;

    public ObservableCollection<InputSourceOption> InputSources { get; } = [];
    public ObservableCollection<VideoSource> VideoSources { get; } = [];
    public ObservableCollection<ControllerBridge> ControllerBridges { get; } = [];
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
            _preferredInputProfile = value?.ProfileKey;
            OnPropertyChanged();
            if (value is not null && _forwarder is not null)
            {
                long generation = Interlocked.Increment(ref _inputSelectionGeneration);
                _ = SelectInputSourceAndPersistAsync(value, generation);
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

    public ControllerBridge? SelectedControllerBridge
    {
        get => _selectedControllerBridge;
        set
        {
            if (_selectedControllerBridge == value)
            {
                return;
            }
            _selectedControllerBridge = value;
            OnPropertyChanged();
            if (!_loadingControllerBridges && value is not null)
            {
                _ = SelectControllerBridgeAsync(value);
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
        await InitializeControllerBridgesAsync();
        await InitializeVideoAsync();
        try
        {
            ConsoleStatus status = await _session.GetStatusAsync(CancellationToken.None);
            ApplyPendingControlRequest(status.PendingControlRequest);
            _statusTask = MonitorStatusAsync(_statusStop.Token);
            if (status.ControlOwner == ControlOwner.None)
            {
                await TakeControlAsync();
            }
            else
            {
                StatusText = "Another client has control. Choose Take Control to preempt automation.";
            }
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
        _statusStop.Cancel();
        if (_statusTask is not null)
        {
            try
            {
                await _statusTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _statusStop.Dispose();
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

    public bool SetKey(PhysicalKey key, bool pressed)
    {
        if (_disposed)
        {
            return false;
        }
        return _forwarder?.SetKey(key, pressed) == true;
    }

    public Task SetKeyboardFocusAsync(bool focused)
    {
        _keyboardFocused = focused;
        return !_disposed
            ? _forwarder?.SetKeyboardFocusAsync(focused, CancellationToken.None) ?? Task.CompletedTask
            : Task.CompletedTask;
    }

    internal async Task RememberWindowAsync(GuiWindowState window)
    {
        try
        {
            await _configuration.RememberWindowAsync(window, CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusText = $"Window state was not saved: {exception.Message}";
        }
    }

    public async Task SetOnScreenControlAsync(
        Guid holdId,
        CanonicalDigitalControl control,
        bool pressed)
    {
        if (_disposed || _forwarder is null || _control is null)
        {
            return;
        }
        try
        {
            await _forwarder.SetOverlayAsync(
                holdId,
                control,
                pressed,
                CancellationToken.None);
            StatusText = pressed ? $"{GetLabel(control)} held" : "Ready";
        }
        catch (Exception exception)
        {
            StatusText = $"Input failed: {exception.Message}";
        }
    }

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

    private async Task ToggleControlAsync()
    {
        if (_control is null)
        {
            await TakeControlAsync();
        }
        else
        {
            await ReleaseControlAsync();
        }
    }

    private async Task TakeControlAsync()
    {
        StatusText = "Taking control...";
        try
        {
            _control = await _session.TakeControlAsync(
                ControlPriority.InteractiveUser,
                CancellationToken.None);
            _forwarder = new(
                _control,
                new SdlGamepadManager(),
                _configuration);
            _forwarder.SourcesChanged += OnSourcesChanged;
            _forwarder.StatusChanged += OnForwardingStatusChanged;
            RefreshInputSources(activateSelection: false);
            if (SelectedInputSource is { } source)
            {
                await _forwarder.SelectSourceAsync(source, CancellationToken.None);
            }
            await _forwarder.SetKeyboardFocusAsync(_keyboardFocused, CancellationToken.None);
            StatusText = FormatConnectionState(_control.ConnectionState);
        }
        catch (Exception exception)
        {
            StatusText = $"Could not take control: {exception.Message}";
            if (_forwarder is not null)
            {
                await _forwarder.DisposeAsync();
                _forwarder = null;
            }
            if (_control is not null)
            {
                await _control.DisposeAsync();
                _control = null;
            }
        }
        finally
        {
            OnPropertyChanged(nameof(HasControl));
            OnPropertyChanged(nameof(ControlActionText));
        }
    }

    private async Task ReleaseControlAsync()
    {
        StatusText = "Releasing control...";
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
            IControlSession control = _control;
            _control = null;
            await control.DisposeAsync();
        }
        InputSources.Clear();
        _selectedInputSource = null;
        OnPropertyChanged(nameof(SelectedInputSource));
        OnPropertyChanged(nameof(HasControl));
        OnPropertyChanged(nameof(ControlActionText));
        StatusText = "Observing. Choose Take Control to send input.";
    }

    private async Task DeclineControlRequestAsync()
    {
        if (_pendingControlRequest is not { } request)
        {
            return;
        }
        try
        {
            await _session.DeclineControlRequestAsync(request.Id, CancellationToken.None);
            ApplyPendingControlRequest(null);
            StatusText = "Agent control request declined";
        }
        catch (Exception exception)
        {
            StatusText = $"Could not decline control request: {exception.Message}";
        }
    }

    private async Task MonitorStatusAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                ConsoleStatus status = await _session.GetStatusAsync(cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ApplyPendingControlRequest(status.PendingControlRequest));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Existing video and control reconnect paths report daemon availability.
            }
        }
    }

    private void ApplyPendingControlRequest(PendingControlRequest? request)
    {
        if (_pendingControlRequest == request)
        {
            return;
        }
        _pendingControlRequest = request;
        OnPropertyChanged(nameof(HasPendingControlRequest));
        OnPropertyChanged(nameof(PendingControlRequestReason));
    }

    private static string GetLabel(CanonicalDigitalControl control) => control switch
    {
        CanonicalDigitalControl.DPadUp => "D-pad up",
        CanonicalDigitalControl.DPadRight => "D-pad right",
        CanonicalDigitalControl.DPadDown => "D-pad down",
        CanonicalDigitalControl.DPadLeft => "D-pad left",
        CanonicalDigitalControl.LeftStickClick => "L3",
        CanonicalDigitalControl.RightStickClick => "R3",
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

    private async Task SelectInputSourceAndPersistAsync(InputSourceOption source, long generation)
    {
        await SelectInputSourceAsync(source);
        if (generation != Interlocked.Read(ref _inputSelectionGeneration))
        {
            return;
        }
        try
        {
            await _configuration.RememberInputSourceAsync(source.ProfileKey, CancellationToken.None);
        }
        catch (Exception exception)
        {
            StatusText = $"Input selected, but its preference was not saved: {exception.Message}";
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

    private async Task InitializeControllerBridgesAsync()
    {
        try
        {
            ControllerBridgeInventory inventory = await _session.GetControllerBridgeInventoryAsync(
                CancellationToken.None);
            _loadingControllerBridges = true;
            ControllerBridges.Clear();
            foreach (ControllerBridge bridge in inventory.Bridges)
            {
                ControllerBridges.Add(bridge);
            }
            _controllerBridgeRevision = inventory.Revision;
            _selectedControllerBridge = ControllerBridges.FirstOrDefault(bridge =>
                bridge.Id == inventory.SelectedBridgeId);
            OnPropertyChanged(nameof(SelectedControllerBridge));
            _loadingControllerBridges = false;
            StatusText = inventory.Status;
        }
        catch (Exception exception)
        {
            _loadingControllerBridges = false;
            StatusText = $"Controller bridge scan failed: {exception.Message}";
        }
    }

    private async Task SelectControllerBridgeAsync(ControllerBridge bridge)
    {
        try
        {
            ControllerBridgeSelection selection = await _session.SelectControllerBridgeAsync(
                bridge.Id,
                _controllerBridgeRevision,
                CancellationToken.None);
            _controllerBridgeRevision = selection.Revision;
            StatusText = selection.Status;
        }
        catch (Exception exception)
        {
            StatusText = $"Controller bridge selection failed: {exception.Message}";
            await InitializeControllerBridgesAsync();
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
        Dispatcher.UIThread.Post(() => RefreshInputSources());

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

    private void RefreshInputSources(bool activateSelection = true)
    {
        InputSourceOption? current = SelectedInputSource;
        InputSources.Clear();
        foreach (InputSourceOption source in _forwarder?.Sources ?? [])
        {
            InputSources.Add(source);
        }

        _selectedInputSource = ChooseInputSource(InputSources, current, _preferredInputProfile);
        OnPropertyChanged(nameof(SelectedInputSource));
        if (activateSelection && _selectedInputSource is { } selected && selected != current)
        {
            _ = SelectInputSourceAsync(selected);
        }
    }

    internal static InputSourceOption? ChooseInputSource(
        IEnumerable<InputSourceOption> sources,
        InputSourceOption? current,
        InputProfileKey? preferred)
    {
        InputSourceOption[] available = sources.ToArray();
        return preferred is { } key
            ? available.FirstOrDefault(source => source.ProfileKey == key)
                ?? available.FirstOrDefault(source => source.Id == current?.Id)
                ?? available.FirstOrDefault(source => source.ProfileKey == InputProfileKey.Keyboard)
            : available.FirstOrDefault(source => source.Id == current?.Id)
                ?? available.FirstOrDefault(source => source.ProfileKey == InputProfileKey.Keyboard)
                ?? available.FirstOrDefault();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}