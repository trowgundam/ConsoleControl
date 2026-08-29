using System.Collections.Immutable;
using System.Threading.Channels;

using Avalonia.Input;

using ConsoleControl.Client;
using ConsoleControl.Core;
using ConsoleControl.Input.Sdl;

namespace ConsoleControl.Gui;

public sealed record InputSourceOption(
    string Id,
    string DisplayName,
    InputProfileKey ProfileKey,
    GamepadDeviceId? GamepadId)
{
    public static InputSourceOption Keyboard { get; } =
        new("keyboard", "Keyboard", InputProfileKey.Keyboard, null);
}

public enum CapturedHostControlKind
{
    Digital,
    Axis,
}

public readonly record struct CapturedHostControl(
    HostControlId Control,
    CapturedHostControlKind Kind);

internal sealed class InputForwarder : IAsyncDisposable
{
    private readonly IControlSession _control;
    private readonly SdlGamepadManager _gamepads;
    private readonly GuiConfigurationStore _configurationStore;
    private readonly Channel<Func<Task>> _commands = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _snapshotGate = new();
    private readonly ControllerStateMailbox _mailbox = new();
    private readonly HashSet<HostControlId> _pressedKeys = [];
    private readonly Dictionary<Guid, CanonicalDigitalControl> _overlays = [];
    private readonly Task _writer;
    private HostInputSnapshot _latestSnapshot = HostInputSnapshot.Empty;
    private ImmutableArray<InputProfile> _profiles;
    private InputSourceOption? _selected;
    private InputProfile? _profile;
    private ControllerState _primary = ControllerState.Neutral;
    private ControllerState _lastSent = ControllerState.Neutral;
    private TaskCompletionSource<CapturedHostControl>? _capture;
    private HostInputSnapshot _captureBaseline = HostInputSnapshot.Empty;
    private bool _keyboardFocused;
    private long _gamepadSelectionGeneration;
    private bool _disposed;

    public InputForwarder(
        IControlSession control,
        SdlGamepadManager gamepads,
        GuiConfigurationStore configurationStore)
    {
        _control = control;
        _gamepads = gamepads;
        _configurationStore = configurationStore;
        _profiles = configurationStore.Current.Profiles;
        _gamepads.DevicesChanged += OnDevicesChanged;
        _gamepads.SnapshotChanged += OnGamepadSnapshot;
        _gamepads.SelectedDeviceDisconnected += OnGamepadDisconnected;
        _control.ConnectionStateChanged += OnConnectionStateChanged;
        _writer = Task.Run(RunWriterAsync);
    }

    public event EventHandler? SourcesChanged;
    public event EventHandler<string>? StatusChanged;

    public IReadOnlyList<InputSourceOption> Sources
    {
        get
        {
            List<InputSourceOption> sources =
            [InputSourceOption.Keyboard, .. _gamepads.Devices.Select(device => new InputSourceOption(
                $"gamepad:{device.Id.Value}", device.DisplayName, device.ProfileKey, device.Id))];
            if (_selected is { GamepadId: not null } selected && sources.All(source => source.Id != selected.Id))
            {
                sources.Add(selected with { DisplayName = $"{selected.DisplayName} (disconnected)" });
            }
            return sources;
        }
    }

    public InputSourceOption? Selected => _selected;
    public InputProfile? ActiveProfile => _profile;

    public async Task SelectSourceAsync(InputSourceOption source, CancellationToken cancellationToken)
    {
        await EnqueueAndWaitAsync(async () =>
        {
            _primary = ControllerState.Neutral;
            await SendComposedAsync().ConfigureAwait(false);
            long selectionGeneration = await _gamepads.SelectAsync(
                null,
                CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _gamepadSelectionGeneration, selectionGeneration);
            _selected = source;
            lock (_snapshotGate)
            {
                _profile = FindProfile(source.ProfileKey);
                _pressedKeys.Clear();
                _latestSnapshot = HostInputSnapshot.Empty;
            }
            _mailbox.Reset(ControllerState.Neutral);

            if (source.GamepadId is not null)
            {
                selectionGeneration = await _gamepads.SelectAsync(
                    source.GamepadId,
                    CancellationToken.None).ConfigureAwait(false);
                Interlocked.Exchange(ref _gamepadSelectionGeneration, selectionGeneration);
            }

            StatusChanged?.Invoke(this, $"Input: {source.DisplayName}");
        }, cancellationToken).ConfigureAwait(false);
    }

    public bool SetKey(PhysicalKey key, bool pressed)
    {
        if (_selected?.ProfileKey.Kind != InputSourceKind.Keyboard)
        {
            return false;
        }

        HostControlId id = DefaultInputProfiles.KeyboardId(key);
        if (pressed && TryCompleteCapture(new(id, CapturedHostControlKind.Digital)))
        {
            return true;
        }

        if (!_keyboardFocused)
        {
            return false;
        }

        HostInputSnapshot? snapshot = null;
        lock (_snapshotGate)
        {
            bool changed = pressed ? _pressedKeys.Add(id) : _pressedKeys.Remove(id);
            if (changed)
            {
                snapshot = new(_pressedKeys.ToImmutableHashSet(),
                    ImmutableDictionary<HostControlId, float>.Empty);
            }
        }

        if (snapshot is not null)
        {
            QueueSnapshot(snapshot);
        }
        return true;
    }

    public Task SetKeyboardFocusAsync(bool focused, CancellationToken cancellationToken) =>
        EnqueueAndWaitAsync(async () =>
        {
            _keyboardFocused = focused;
            if (!focused && _selected?.ProfileKey.Kind == InputSourceKind.Keyboard)
            {
                lock (_snapshotGate)
                {
                    _pressedKeys.Clear();
                    _latestSnapshot = HostInputSnapshot.Empty;
                }
                _mailbox.Reset(ControllerState.Neutral);
                _primary = ControllerState.Neutral;
                await SendComposedAsync().ConfigureAwait(false);
            }
        }, cancellationToken);

    public async Task<CapturedHostControl> CaptureNextInputAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<CapturedHostControl> capture =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_snapshotGate)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("Another mapping capture is already active.");
            }

            _capture = capture;
            _captureBaseline = _latestSnapshot;
        }

        await EnqueueAndWaitAsync(async () =>
        {
            _mailbox.Reset(ControllerState.Neutral);
            _primary = ControllerState.Neutral;
            await SendComposedAsync().ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            lock (_snapshotGate)
            {
                if (ReferenceEquals(_capture, capture))
                {
                    _capture = null;
                    capture.TrySetCanceled(cancellationToken);
                }
            }
        });
        return await capture.Task.ConfigureAwait(false);
    }

    public async Task PulseAsync(
        CanonicalDigitalControl control,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        await EnqueueAndWaitAsync(async () =>
        {
            _overlays.Add(id, control);
            await SendComposedAsync().ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EnqueueAndWaitAsync(async () =>
            {
                _overlays.Remove(id);
                await SendComposedAsync().ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task SetOverlayAsync(
        Guid id,
        CanonicalDigitalControl control,
        bool pressed,
        CancellationToken cancellationToken) => EnqueueAndWaitAsync(async () =>
        {
            if (pressed)
            {
                _overlays[id] = control;
            }
            else
            {
                _overlays.Remove(id);
            }
            await SendComposedAsync().ConfigureAwait(false);
        }, cancellationToken);

    public async Task SaveAndActivateProfileAsync(InputProfile profile, CancellationToken cancellationToken)
    {
        GuiConfiguration saved = await _configurationStore.SaveProfileAsync(
            profile, cancellationToken).ConfigureAwait(false);
        await EnqueueAndWaitAsync(async () =>
        {
            _profiles = saved.Profiles;
            if (_selected?.ProfileKey == profile.Key)
            {
                HostInputSnapshot snapshot;
                lock (_snapshotGate)
                {
                    _profile = profile;
                    snapshot = _latestSnapshot;
                }

                _primary = InputMapper.Map(profile, snapshot);
                _mailbox.Reset(_primary);
                await SendComposedAsync().ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await EnqueueAndWaitAsync(async () =>
        {
            _mailbox.Reset(ControllerState.Neutral);
            _primary = ControllerState.Neutral;
            _overlays.Clear();
            await SendComposedAsync().ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
        _commands.Writer.Complete();
        await _writer.ConfigureAwait(false);
        _gamepads.DevicesChanged -= OnDevicesChanged;
        _gamepads.SnapshotChanged -= OnGamepadSnapshot;
        _gamepads.SelectedDeviceDisconnected -= OnGamepadDisconnected;
        _control.ConnectionStateChanged -= OnConnectionStateChanged;
        await _gamepads.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private InputProfile FindProfile(InputProfileKey key) =>
        _profiles.FirstOrDefault(profile => profile.Key == key)
        ?? DefaultInputProfiles.For(key);

    private void OnDevicesChanged(object? sender, EventArgs eventArgs) =>
        SourcesChanged?.Invoke(this, EventArgs.Empty);

    private void OnGamepadSnapshot(object? sender, GamepadSnapshot snapshot)
    {
        if (IsCurrentGamepadSnapshot(
            _selected,
            Interlocked.Read(ref _gamepadSelectionGeneration),
            snapshot))
        {
            CapturedHostControl? captured = null;
            lock (_snapshotGate)
            {
                if (_capture is not null)
                {
                    HostControlId? pressed = snapshot.State.Pressed.Except(_captureBaseline.Pressed)
                        .Select(control => (HostControlId?)control)
                        .FirstOrDefault();
                    if (pressed is not null)
                    {
                        captured = new(pressed.Value, CapturedHostControlKind.Digital);
                    }
                    else
                    {
                        HostControlId? movedAxis = snapshot.State.Axes
                            .Where(axis => Math.Abs(axis.Value -
                                (_captureBaseline.Axes.TryGetValue(axis.Key, out float baseline) ? baseline : 0f)) >= 0.6f)
                            .Select(axis => (HostControlId?)axis.Key)
                            .FirstOrDefault();
                        if (movedAxis is not null)
                        {
                            captured = new(movedAxis.Value, CapturedHostControlKind.Axis);
                        }
                    }

                }
            }

            if (captured is not null && TryCompleteCapture(captured.Value))
            {
                lock (_snapshotGate)
                {
                    _latestSnapshot = snapshot.State;
                }

                return;
            }

            QueueSnapshot(snapshot.State);
        }
    }

    internal static bool IsCurrentGamepadSnapshot(
        InputSourceOption? selected,
        long selectionGeneration,
        GamepadSnapshot snapshot) =>
        selected?.GamepadId == snapshot.Device &&
        selectionGeneration == snapshot.SelectionGeneration;

    private bool TryCompleteCapture(CapturedHostControl control)
    {
        TaskCompletionSource<CapturedHostControl>? capture;
        lock (_snapshotGate)
        {
            capture = _capture;
            _capture = null;
        }

        return capture?.TrySetResult(control) == true;
    }

    private void OnGamepadDisconnected(object? sender, GamepadDeviceId device)
    {
        if (_selected?.GamepadId == device)
        {
            _ = EnqueueAndWaitAsync(async () =>
            {
                lock (_snapshotGate)
                {
                    _latestSnapshot = HostInputSnapshot.Empty;
                }
                _mailbox.Reset(ControllerState.Neutral);
                _primary = ControllerState.Neutral;
                _overlays.Clear();
                await SendComposedAsync().ConfigureAwait(false);
                StatusChanged?.Invoke(this, "Selected controller disconnected");
                SourcesChanged?.Invoke(this, EventArgs.Empty);
            }, CancellationToken.None);
        }
    }

    private void OnConnectionStateChanged(object? sender, ControlConnectionState state)
    {
        if (_disposed)
        {
            return;
        }

        if (state != ControlConnectionState.Ready)
        {
            _ = EnqueueAndWaitAsync(() =>
            {
                lock (_snapshotGate)
                {
                    _pressedKeys.Clear();
                    _latestSnapshot = HostInputSnapshot.Empty;
                }
                _mailbox.Reset(ControllerState.Neutral);
                _primary = ControllerState.Neutral;
                _lastSent = ControllerState.Neutral;
                _overlays.Clear();
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        StatusChanged?.Invoke(this, state switch
        {
            ControlConnectionState.Ready => "Ready",
            ControlConnectionState.WaitingForBridge => "Controller bridge disconnected; reconnecting...",
            ControlConnectionState.WaitingForControl => "Control is held by another client; waiting...",
            ControlConnectionState.Reconnecting => "Daemon disconnected; reconnecting...",
            ControlConnectionState.Stopped => "Controller forwarding stopped",
            _ => "Connecting to daemon...",
        });
    }

    private void QueueSnapshot(HostInputSnapshot snapshot)
    {
        ControllerState? mapped = null;
        lock (_snapshotGate)
        {
            _latestSnapshot = snapshot;
            if (_profile is not null)
            {
                mapped = InputMapper.Map(_profile, snapshot);
            }
        }

        if (mapped is { } state && _mailbox.Publish(state))
        {
            _commands.Writer.TryWrite(DrainSnapshotsAsync);
        }
    }

    private async Task DrainSnapshotsAsync()
    {
        while (_mailbox.TryTake(out ControllerState mapped))
        {
            if (mapped != _primary)
            {
                _primary = mapped;
                await SendComposedAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SendComposedAsync()
    {
        ControllerState state = ControllerStateComposer.AddDigitalControls(
            _primary, _overlays.Values);
        if (state == _lastSent)
        {
            return;
        }

        await _control.SetStateAsync(state, _stop.Token).ConfigureAwait(false);
        _lastSent = state;
    }

    private Task EnqueueAndWaitAsync(Func<Task> command, CancellationToken cancellationToken)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(async () =>
            {
                try
                {
                    await command().ConfigureAwait(false);
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                    StatusChanged?.Invoke(this, $"Input failed: {exception.Message}");
                }
            }))
        {
            completion.SetException(new ObjectDisposedException(nameof(InputForwarder)));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private async Task RunWriterAsync()
    {
        await foreach (Func<Task> command in _commands.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            try
            {
                await command().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                StatusChanged?.Invoke(this, $"Input failed: {exception.Message}");
            }
        }
    }
}