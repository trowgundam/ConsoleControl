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

internal enum OnScreenControllerLayout
{
    Compact,
    Full,
    Hidden,
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IConsoleSession _session;
    private readonly GuiConfigurationStore _configuration;
    private readonly AppearanceController _appearance;
    private readonly DaemonConnectionSupervisor _daemonSupervisor;
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
    private string _videoStatusText = "Finding video sources...";
    private string _videoHeadline = "Finding video sources";
    private string _statusText = "Connecting to daemon...";
    private bool _keyboardFocused;
    private InputProfileKey? _preferredInputProfile;
    private long _inputSelectionGeneration;
    private DaemonAvailability _daemonAvailability = DaemonAvailability.Connecting;
    private Uri? _presentedVideoUri;
    private bool _initialControlDecisionPending = true;
    private bool _initialized;
    private bool _needsControlAttention;
    private long _controlAttentionGeneration;
    private OnScreenControllerLayout _onScreenControllerLayout;
    private bool _disposed;

    internal MainWindowViewModel(
        IConsoleSession session,
        GuiConfigurationStore configuration,
        AppearanceController appearance)
    {
        _session = session;
        _configuration = configuration;
        _appearance = appearance;
        _daemonSupervisor = new(
            session,
            HandleDaemonConnectionEventAsync,
            new CoalescingRetryWaiter());
        _preferredInputProfile = configuration.Current.LastInputSource;
        PulseControlCommand = new AsyncCommand<CanonicalDigitalControl>(PulseControlAsync);
        ToggleControlCommand = new AsyncCommand(ToggleControlAsync);
        DeclineControlRequestCommand = new AsyncCommand(DeclineControlRequestAsync);
        RetryDaemonCommand = new AsyncCommand(RetryDaemonConnectionAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PulseControlCommand { get; }
    public ICommand ToggleControlCommand { get; }
    public ICommand DeclineControlRequestCommand { get; }
    public ICommand RetryDaemonCommand { get; }

    internal AppearanceController Appearance => _appearance;

    public bool HasControl => _control is { } control && IsActiveControlState(control.ConnectionState);
    public bool HasControlSession => _control is not null;
    public bool CanEditMapping => _forwarder?.ActiveProfile is not null;
    public string SessionHeadline => _daemonAvailability switch
    {
        DaemonAvailability.Connecting => "Connecting to daemon",
        DaemonAvailability.Unavailable => "Daemon unavailable",
        DaemonAvailability.Available when _control is { } control =>
            ControlSessionHeadline(control.ConnectionState),
        _ => "Observing",
    };
    public string SessionDetailText => FormatSessionDetailText(_daemonAvailability, StatusText);
    public bool CanRetryDaemonConnection =>
        !_disposed && _daemonAvailability == DaemonAvailability.Unavailable;
    public bool NeedsControlAttention => _needsControlAttention;
    public bool HasVideoFrame => VideoImage is not null;
    public bool HasPendingControlRequest => _pendingControlRequest is not null;
    public string PendingControlRequestReason => _pendingControlRequest?.Reason ?? string.Empty;

    public bool IsCompactControllerVisible =>
        _onScreenControllerLayout == OnScreenControllerLayout.Compact;
    public bool IsFullControllerVisible =>
        _onScreenControllerLayout == OnScreenControllerLayout.Full;
    public bool IsControllerControlsHidden =>
        _onScreenControllerLayout == OnScreenControllerLayout.Hidden;

    public ObservableCollection<InputSourceOption> InputSources { get; } = [];
    public ObservableCollection<VideoSource> VideoSources { get; } = [];
    public ObservableCollection<ControllerBridge> ControllerBridges { get; } = [];
    internal InputForwarder? Forwarder => _forwarder;

    internal void CycleOnScreenControllerLayout()
    {
        _onScreenControllerLayout = NextOnScreenControllerLayout(_onScreenControllerLayout);
        OnPropertyChanged(nameof(IsCompactControllerVisible));
        OnPropertyChanged(nameof(IsFullControllerVisible));
        OnPropertyChanged(nameof(IsControllerControlsHidden));
    }

    internal static OnScreenControllerLayout NextOnScreenControllerLayout(
        OnScreenControllerLayout current) => current switch
        {
            OnScreenControllerLayout.Compact => OnScreenControllerLayout.Full,
            OnScreenControllerLayout.Full => OnScreenControllerLayout.Hidden,
            OnScreenControllerLayout.Hidden => OnScreenControllerLayout.Compact,
            _ => throw new ArgumentOutOfRangeException(nameof(current), current, null),
        };

    internal static string FormatSessionDetailText(
        DaemonAvailability availability,
        string statusText) => availability == DaemonAvailability.Unavailable
            ? $"{statusText} Start the ConsoleControl daemon, or click the status to retry."
            : statusText;

    internal static bool IsActiveControlState(ControlConnectionState state) =>
        state == ControlConnectionState.Ready;

    internal static string ControlSessionHeadline(ControlConnectionState state) => state switch
    {
        ControlConnectionState.Connecting => "Taking control",
        ControlConnectionState.Ready => "You have control",
        ControlConnectionState.WaitingForBridge => "Waiting for bridge",
        ControlConnectionState.WaitingForControl => "Waiting for control",
        ControlConnectionState.Reconnecting => "Reconnecting control",
        ControlConnectionState.Stopped => "Control stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

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
            OnPropertyChanged(nameof(SessionDetailText));
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
            OnPropertyChanged(nameof(HasVideoFrame));
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

    public string VideoHeadline
    {
        get => _videoHeadline;
        private set
        {
            if (_videoHeadline == value)
            {
                return;
            }
            _videoHeadline = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await InitializeInputAsync();
        _daemonSupervisor.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearControlAttention();
        await _daemonSupervisor.DisposeAsync();
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
            forwarder.InputActivityDetected -= OnInputActivityDetected;
            if (_control is { } control)
            {
                control.ConnectionStateChanged -= OnControlConnectionStateChanged;
            }
            await forwarder.DisposeAsync();
            _control = null;
        }
        else if (_control is not null)
        {
            _control.ConnectionStateChanged -= OnControlConnectionStateChanged;
            await _control.DisposeAsync();
            _control = null;
        }

        await _session.DisposeAsync();
        await _appearance.DisposeAsync();
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
        if (_disposed || _forwarder is null || !HasControl)
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
            StatusText = pressed ? $"{ControllerControlLabels.For(control)} held" : "Ready";
        }
        catch (Exception exception)
        {
            StatusText = $"Input failed: {exception.Message}";
        }
    }

    private async Task PulseControlAsync(CanonicalDigitalControl control)
    {
        if (!HasControl)
        {
            StatusText = "Control is unavailable";
            return;
        }

        StatusText = $"{ControllerControlLabels.For(control)} pressed";
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
            InputForwarder forwarder = _forwarder
                ?? throw new InvalidOperationException("Local input is unavailable.");
            IControlSession control = await _session.TakeControlAsync(
                ControlPriority.InteractiveUser,
                CancellationToken.None);
            await forwarder.AttachControlAsync(control, CancellationToken.None);
            _control = control;
            control.ConnectionStateChanged += OnControlConnectionStateChanged;
            await forwarder.SetKeyboardFocusAsync(_keyboardFocused, CancellationToken.None);
            StatusText = ControlConnectionStatus.For(control.ConnectionState);
        }
        catch (Exception exception)
        {
            string error = exception.Message;
            if (_control is { } control)
            {
                control.ConnectionStateChanged -= OnControlConnectionStateChanged;
                try
                {
                    await _forwarder!.DetachControlAsync(CancellationToken.None);
                }
                catch (Exception cleanupException)
                {
                    error = $"{error} Cleanup also failed: {cleanupException.Message}";
                }
            }
            _control = null;
            StatusText = $"Could not take control: {error}";
        }
        finally
        {
            NotifyControlStateChanged();
            if (HasControl)
            {
                ClearControlAttention();
            }
        }
    }

    private async Task ReleaseControlAsync()
    {
        StatusText = "Releasing control...";
        if (_control is { } control)
        {
            control.ConnectionStateChanged -= OnControlConnectionStateChanged;
        }
        try
        {
            if (_forwarder is not null)
            {
                await _forwarder.DetachControlAsync(CancellationToken.None);
            }
            StatusText = "Observing. Choose Take Control to send input.";
        }
        catch (Exception exception)
        {
            StatusText = $"Control released, but cleanup failed: {exception.Message}";
        }
        finally
        {
            _control = null;
            NotifyControlStateChanged();
        }
    }

    private async Task InitializeInputAsync()
    {
        try
        {
            _forwarder = new(new SdlGamepadManager(), _configuration);
            _forwarder.SourcesChanged += OnSourcesChanged;
            _forwarder.StatusChanged += OnForwardingStatusChanged;
            _forwarder.InputActivityDetected += OnInputActivityDetected;
            RefreshInputSources(activateSelection: false);
            if (SelectedInputSource is { } source)
            {
                await _forwarder.SelectSourceAsync(source, CancellationToken.None);
            }
            OnPropertyChanged(nameof(CanEditMapping));
        }
        catch (Exception exception)
        {
            StatusText = $"Local input unavailable: {exception.Message}";
        }
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

    private async Task HandleDaemonConnectionEventAsync(
        DaemonConnectionEvent connectionEvent,
        CancellationToken cancellationToken)
    {
        switch (connectionEvent)
        {
            case DaemonConnectionEvent.Attempting:
                SetDaemonAvailability(DaemonAvailability.Connecting);
                StatusText = "Connecting to daemon...";
                break;
            case DaemonConnectionEvent.Unavailable unavailable:
                SetDaemonAvailability(DaemonAvailability.Unavailable);
                StatusText = $"Unavailable: {unavailable.Error.Message}";
                if (_videoPresenter is null)
                {
                    VideoHeadline = "Video unavailable";
                    VideoStatusText = "Start the ConsoleControl daemon to restore video.";
                }
                break;
            case DaemonConnectionEvent.RecoveredStatus recovered:
                await RestoreDaemonStateAsync(recovered.Status, cancellationToken);
                break;
            case DaemonConnectionEvent.ObservedStatus observed:
                ApplyPendingControlRequest(observed.Status.PendingControlRequest);
                break;
        }
    }

    private Task RetryDaemonConnectionAsync()
    {
        if (!CanRetryDaemonConnection)
        {
            return Task.CompletedTask;
        }

        SetDaemonAvailability(DaemonAvailability.Connecting);
        StatusText = "Connecting to daemon...";
        _daemonSupervisor.RetryNow();
        return Task.CompletedTask;
    }

    private async Task RestoreDaemonStateAsync(
        ConsoleStatus status,
        CancellationToken cancellationToken)
    {
        ControllerBridgeInventory bridges = await _session.GetControllerBridgeInventoryAsync(
            cancellationToken);
        VideoInventory video = await _session.GetVideoInventoryAsync(cancellationToken);
        ApplyControllerBridgeInventory(bridges);
        ApplyVideoInventory(video);
        ApplyPendingControlRequest(status.PendingControlRequest);
        await EnsureVideoPresenterAsync(video.LiveStreamUri);
        SetDaemonAvailability(DaemonAvailability.Available);

        if (!_initialControlDecisionPending)
        {
            return;
        }

        _initialControlDecisionPending = false;
        if (_control is null && status.ControlOwner == ControlOwner.None)
        {
            await TakeControlAsync();
        }
        else if (_control is null)
        {
            StatusText = "Another client has control. Choose Take Control to preempt automation.";
        }
    }

    private void SetDaemonAvailability(DaemonAvailability availability)
    {
        if (_daemonAvailability == availability)
        {
            return;
        }

        _daemonAvailability = availability;
        OnPropertyChanged(nameof(SessionHeadline));
        OnPropertyChanged(nameof(SessionDetailText));
        OnPropertyChanged(nameof(CanRetryDaemonConnection));
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
            ApplyVideoInventory(inventory);
            await EnsureVideoPresenterAsync(inventory.LiveStreamUri);
        }
        catch (Exception exception)
        {
            _loadingVideoSources = false;
            VideoHeadline = "Video unavailable";
            VideoStatusText = $"Video unavailable: {exception.Message}";
        }
    }

    private async Task InitializeControllerBridgesAsync()
    {
        try
        {
            ControllerBridgeInventory inventory = await _session.GetControllerBridgeInventoryAsync(
                CancellationToken.None);
            ApplyControllerBridgeInventory(inventory);
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
            VideoHeadline = "Waiting for video";
            VideoStatusText = selection.Status;
        }
        catch (Exception exception)
        {
            VideoHeadline = "Video source unavailable";
            VideoStatusText = $"Video selection failed: {exception.Message}";
            await InitializeVideoAsync();
        }
    }

    private void ApplyControllerBridgeInventory(ControllerBridgeInventory inventory)
    {
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

    private void ApplyVideoInventory(VideoInventory inventory)
    {
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
        VideoHeadline = _selectedVideoSource is null
            ? "Choose a video source"
            : "Waiting for video";
        VideoStatusText = inventory.Status;
    }

    private async Task EnsureVideoPresenterAsync(Uri streamUri)
    {
        if (_videoPresenter is not null && _presentedVideoUri == streamUri)
        {
            return;
        }
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
                VideoHeadline = "Video ready";
                VideoStatusText = SelectedVideoSource is null
                    ? "Video ready"
                    : $"Video ready: {SelectedVideoSource.DisplayName}";
            }),
            status => Dispatcher.UIThread.Post(() =>
            {
                VideoHeadline = "Reconnecting to video";
                VideoStatusText = status;
            }),
            () => Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    VideoImage = null;
                    VideoHeadline = "Reconnecting to video";
                }
            }));
        _presentedVideoUri = streamUri;
        _videoPresenter.Start(streamUri);
    }

    private void OnSourcesChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() => RefreshInputSources());

    private void OnForwardingStatusChanged(object? sender, string status) =>
        Dispatcher.UIThread.Post(() => StatusText = status);

    private void OnInputActivityDetected(object? sender, EventArgs eventArgs) =>
        Dispatcher.UIThread.Post(() => _ = ShowControlAttentionAsync());

    private void OnControlConnectionStateChanged(
        object? sender,
        ControlConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(_control, sender))
            {
                return;
            }

            NotifyControlStateChanged();
            if (IsActiveControlState(state))
            {
                ClearControlAttention();
            }
        });

    private void NotifyControlStateChanged()
    {
        OnPropertyChanged(nameof(HasControl));
        OnPropertyChanged(nameof(HasControlSession));
        OnPropertyChanged(nameof(SessionHeadline));
    }

    private async Task ShowControlAttentionAsync()
    {
        if (_disposed || HasControl || _daemonAvailability != DaemonAvailability.Available)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _controlAttentionGeneration);
        SetControlAttention(true);
        await Task.Delay(TimeSpan.FromMilliseconds(700));
        if (generation == Interlocked.Read(ref _controlAttentionGeneration))
        {
            SetControlAttention(false);
        }
    }

    private void ClearControlAttention()
    {
        Interlocked.Increment(ref _controlAttentionGeneration);
        SetControlAttention(false);
    }

    private void SetControlAttention(bool value)
    {
        if (_needsControlAttention == value)
        {
            return;
        }

        _needsControlAttention = value;
        OnPropertyChanged(nameof(NeedsControlAttention));
    }

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
        OnPropertyChanged(nameof(CanEditMapping));
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