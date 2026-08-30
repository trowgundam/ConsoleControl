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
    private static readonly TimeSpan ControllerWriteTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ControllerDisposeTimeout = TimeSpan.FromSeconds(3);
    private readonly SdlGamepadManager _gamepads;
    private readonly GuiConfigurationStore _configurationStore;
    private readonly Channel<Func<Task>> _commands = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _snapshotGate = new();
    private readonly HashSet<HostControlId> _pressedKeys = [];
    private readonly Task _writer;
    private HostInputSnapshot _latestSnapshot = HostInputSnapshot.Empty;
    private ImmutableArray<InputProfile> _profiles;
    private InputSourceOption? _selected;
    private InputProfile? _profile;
    private ControlAttachment? _attachment;
    private TaskCompletionSource<CapturedHostControl>? _capture;
    private HostInputSnapshot _captureBaseline = HostInputSnapshot.Empty;
    private bool _keyboardFocused;
    private bool _mappedInputActive;
    private long _gamepadSelectionGeneration;
    private bool _disposed;

    public InputForwarder(
        SdlGamepadManager gamepads,
        GuiConfigurationStore configurationStore)
    {
        _gamepads = gamepads;
        _configurationStore = configurationStore;
        _profiles = configurationStore.Current.Profiles;
        _gamepads.DevicesChanged += OnDevicesChanged;
        _gamepads.SnapshotChanged += OnGamepadSnapshot;
        _gamepads.SelectedDeviceDisconnected += OnGamepadDisconnected;
        _writer = Task.Run(RunWriterAsync);
    }

    private sealed class ControlAttachment(IControlSession session)
    {
        public IControlSession Session { get; } = session;
        public ControllerStateMailbox Mailbox { get; } = new();
        public Dictionary<Guid, CanonicalDigitalControl> Overlays { get; } = [];
        public ControllerState Primary { get; set; } = ControllerState.Neutral;
        public ControllerState? LastSent { get; set; }
        public EventHandler<ControlConnectionState>? ConnectionHandler { get; set; }
        public HashSet<HostControlId> SuppressedKeys { get; } = [];
        public bool SuppressGamepadUntilNeutral { get; set; }
    }

    public event EventHandler? SourcesChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler? InputActivityDetected;

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
    public bool HasAttachedControl => Volatile.Read(ref _attachment) is not null;

    public async Task AttachControlAsync(
        IControlSession control,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        try
        {
            await EnqueueAndWaitAsync(async () =>
            {
                if (_attachment is not null)
                {
                    throw new InvalidOperationException("A control lease is already attached.");
                }

                ControlAttachment attachment = new(control);
                attachment.ConnectionHandler = (_, state) => OnConnectionStateChanged(attachment, state);
                control.ConnectionStateChanged += attachment.ConnectionHandler;
                lock (_snapshotGate)
                {
                    PrepareAttachmentInputBoundary(attachment);
                    _attachment = attachment;
                }
                try
                {
                    await SendComposedAsync(
                        attachment,
                        force: true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    _attachment = null;
                    control.ConnectionStateChanged -= attachment.ConnectionHandler;
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await control.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task DetachControlAsync(CancellationToken cancellationToken) =>
        EnqueueAndWaitAsync(DetachControlCoreAsync, cancellationToken);

    public async Task SelectSourceAsync(InputSourceOption source, CancellationToken cancellationToken)
    {
        await EnqueueAndWaitAsync(async () =>
        {
            await NeutralizeCurrentAttachmentAsync().ConfigureAwait(false);
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
                _mappedInputActive = false;
                if (_attachment is { } attachment)
                {
                    attachment.SuppressedKeys.Clear();
                    attachment.SuppressGamepadUntilNeutral = source.GamepadId is not null;
                }
            }
            Volatile.Read(ref _attachment)?.Mailbox.Reset(ControllerState.Neutral);

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

        bool hasAttachedControl = HasAttachedControl;
        lock (_snapshotGate)
        {
            if (_attachment?.SuppressedKeys.Contains(id) == true)
            {
                if (!pressed)
                {
                    _attachment.SuppressedKeys.Remove(id);
                }
                return hasAttachedControl;
            }
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
        return hasAttachedControl;
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
                    _mappedInputActive = false;
                }
                await NeutralizeCurrentAttachmentAsync().ConfigureAwait(false);
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

        try
        {
            await EnqueueAndWaitAsync(async () =>
            {
                await NeutralizeCurrentAttachmentAsync().ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            return await capture.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_snapshotGate)
            {
                if (ReferenceEquals(_capture, capture))
                {
                    _capture = null;
                }
            }
        }
    }

    public async Task PulseAsync(
        CanonicalDigitalControl control,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        ControlAttachment? attachment = null;
        await EnqueueAndWaitAsync(async () =>
        {
            attachment = _attachment
                ?? throw new InvalidOperationException("A control lease is required to send input.");
            attachment.Overlays.Add(id, control);
            await SendComposedAsync(attachment).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EnqueueAndWaitAsync(async () =>
            {
                if (attachment is not null && ReferenceEquals(_attachment, attachment))
                {
                    attachment.Overlays.Remove(id);
                    await SendComposedAsync(attachment).ConfigureAwait(false);
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task SetOverlayAsync(
        Guid id,
        CanonicalDigitalControl control,
        bool pressed,
        CancellationToken cancellationToken) => EnqueueAndWaitAsync(async () =>
        {
            ControlAttachment attachment = _attachment
                ?? throw new InvalidOperationException("A control lease is required to send input.");
            bool changed;
            if (pressed)
            {
                changed = !attachment.Overlays.TryGetValue(id, out CanonicalDigitalControl existing) ||
                    existing != control;
                attachment.Overlays[id] = control;
            }
            else
            {
                changed = attachment.Overlays.Remove(id);
            }
            if (changed)
            {
                await SendComposedAsync(attachment).ConfigureAwait(false);
            }
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

                if (_attachment is { } attachment)
                {
                    ControllerState mapped = InputMapper.Map(profile, snapshot);
                    if (_selected?.GamepadId is not null)
                    {
                        (mapped, attachment.SuppressGamepadUntilNeutral) =
                            ApplyGamepadLeaseBoundary(mapped, suppressUntilNeutral: true);
                    }
                    attachment.Primary = mapped;
                    attachment.Mailbox.Reset(attachment.Primary);
                    await SendComposedAsync(attachment).ConfigureAwait(false);
                }
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
        await EnqueueAndWaitAsync(DetachControlCoreAsync, CancellationToken.None).ConfigureAwait(false);
        _commands.Writer.Complete();
        await _writer.ConfigureAwait(false);
        _gamepads.DevicesChanged -= OnDevicesChanged;
        _gamepads.SnapshotChanged -= OnGamepadSnapshot;
        _gamepads.SelectedDeviceDisconnected -= OnGamepadDisconnected;
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
                    _mappedInputActive = false;
                    if (_attachment is { } attachment)
                    {
                        attachment.SuppressGamepadUntilNeutral = true;
                    }
                }
                await NeutralizeCurrentAttachmentAsync().ConfigureAwait(false);
                StatusChanged?.Invoke(this, "Selected controller disconnected");
                SourcesChanged?.Invoke(this, EventArgs.Empty);
            }, CancellationToken.None);
        }
    }

    private void OnConnectionStateChanged(ControlAttachment expected, ControlConnectionState state)
    {
        if (_disposed)
        {
            return;
        }

        if (state != ControlConnectionState.Ready)
        {
            _ = EnqueueAndWaitAsync(() =>
            {
                if (!ReferenceEquals(_attachment, expected))
                {
                    return Task.CompletedTask;
                }
                lock (_snapshotGate)
                {
                    PrepareAttachmentInputBoundary(expected);
                }
                expected.Mailbox.Reset(ControllerState.Neutral);
                expected.Primary = ControllerState.Neutral;
                expected.LastSent = null;
                expected.Overlays.Clear();
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        if (!ReferenceEquals(Volatile.Read(ref _attachment), expected))
        {
            return;
        }
        StatusChanged?.Invoke(this, ControlConnectionStatus.For(state));
    }

    private void QueueSnapshot(HostInputSnapshot snapshot)
    {
        ControllerState? mapped = null;
        ControlAttachment? attachment = null;
        bool activityStarted = false;
        lock (_snapshotGate)
        {
            _latestSnapshot = snapshot;
            attachment = Volatile.Read(ref _attachment);
            if (_capture is null && _profile is not null)
            {
                mapped = InputMapper.Map(_profile, snapshot);
                if (attachment is not null && _selected?.GamepadId is not null)
                {
                    (mapped, attachment.SuppressGamepadUntilNeutral) = ApplyGamepadLeaseBoundary(
                        mapped.Value,
                        attachment.SuppressGamepadUntilNeutral);
                }
                bool inputActive = mapped != ControllerState.Neutral;
                activityStarted = inputActive && !_mappedInputActive;
                _mappedInputActive = inputActive;
            }
        }

        if (activityStarted)
        {
            InputActivityDetected?.Invoke(this, EventArgs.Empty);
        }
        if (mapped is { } state && attachment is not null && attachment.Mailbox.Publish(state))
        {
            _commands.Writer.TryWrite(() => DrainSnapshotsAsync(attachment));
        }
    }

    internal static (ControllerState State, bool SuppressUntilNeutral) ApplyGamepadLeaseBoundary(
        ControllerState mapped,
        bool suppressUntilNeutral) => suppressUntilNeutral && mapped != ControllerState.Neutral
            ? (ControllerState.Neutral, true)
            : (mapped, false);

    private async Task DrainSnapshotsAsync(ControlAttachment expected)
    {
        while (ReferenceEquals(_attachment, expected) &&
               expected.Mailbox.TryTake(out ControllerState mapped))
        {
            if (mapped != expected.Primary)
            {
                expected.Primary = mapped;
                await SendComposedAsync(expected).ConfigureAwait(false);
            }
        }
    }

    private async Task SendComposedAsync(
        ControlAttachment expected,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(_attachment, expected))
        {
            return;
        }
        ControllerState state = ControllerStateComposer.AddDigitalControls(
            expected.Primary, expected.Overlays.Values);
        if (!force && state == expected.LastSent)
        {
            return;
        }

        expected.LastSent = await TryWriteStateAsync(
            expected.Session,
            state,
            cancellationToken).ConfigureAwait(false)
            ? state
            : null;
    }

    private async Task NeutralizeCurrentAttachmentAsync()
    {
        if (_attachment is not { } attachment)
        {
            return;
        }

        await NeutralizeAttachmentAsync(
            attachment,
            force: false,
            _stop.Token).ConfigureAwait(false);
    }

    private async Task NeutralizeAttachmentAsync(
        ControlAttachment attachment,
        bool force,
        CancellationToken cancellationToken)
    {
        attachment.Mailbox.Reset(ControllerState.Neutral);
        attachment.Primary = ControllerState.Neutral;
        attachment.Overlays.Clear();
        if (!force &&
            (!ReferenceEquals(_attachment, attachment) ||
             attachment.LastSent == ControllerState.Neutral))
        {
            return;
        }

        attachment.LastSent = await TryWriteStateAsync(
            attachment.Session,
            ControllerState.Neutral,
            cancellationToken).ConfigureAwait(false)
            ? ControllerState.Neutral
            : null;
    }

    private async Task<bool> TryWriteStateAsync(
        IControlSession session,
        ControllerState state,
        CancellationToken cancellationToken)
    {
        if (session.ConnectionState != ControlConnectionState.Ready)
        {
            return false;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            _stop.Token,
            cancellationToken);
        timeout.CancelAfter(ControllerWriteTimeout);
        try
        {
            Task write = session.SetStateAsync(state, timeout.Token);
            return await CompletesWithinAsync(
                write,
                ControllerWriteTimeout,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task DetachControlCoreAsync()
    {
        if (_attachment is not { } attachment)
        {
            return;
        }

        _attachment = null;
        if (attachment.ConnectionHandler is not null)
        {
            attachment.Session.ConnectionStateChanged -= attachment.ConnectionHandler;
        }
        ClearTransientInput();
        try
        {
            await NeutralizeAttachmentAsync(
                attachment,
                force: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await CompletesWithinAsync(
                attachment.Session.DisposeAsync().AsTask(),
                ControllerDisposeTimeout).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> CompletesWithinAsync(
        Task operation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void ClearTransientInput()
    {
        lock (_snapshotGate)
        {
            _pressedKeys.Clear();
            _latestSnapshot = HostInputSnapshot.Empty;
            _mappedInputActive = false;
        }
    }

    private void PrepareAttachmentInputBoundary(ControlAttachment attachment)
    {
        attachment.SuppressedKeys.Clear();
        if (_selected?.ProfileKey.Kind == InputSourceKind.Keyboard)
        {
            attachment.SuppressedKeys.UnionWith(_pressedKeys);
        }
        attachment.SuppressGamepadUntilNeutral = _selected?.GamepadId is not null;
        _pressedKeys.Clear();
        _latestSnapshot = HostInputSnapshot.Empty;
        _mappedInputActive = false;
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