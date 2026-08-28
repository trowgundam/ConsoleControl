using System.Collections.Immutable;
using System.Text;
using ConsoleControl.Core;
using SDL3;

namespace ConsoleControl.Input.Sdl;

public readonly record struct GamepadDeviceId(uint Value);

public sealed record GamepadDevice(
    GamepadDeviceId Id,
    string DisplayName,
    string Guid)
{
    public InputProfileKey ProfileKey => new(InputSourceKind.Gamepad, Guid);
}

public sealed record GamepadSnapshot(GamepadDeviceId Device, HostInputSnapshot State);

public sealed class SdlGamepadManager : IAsyncDisposable
{
    private static readonly SDL.GamepadButton[] Buttons =
        Enum.GetValues<SDL.GamepadButton>()
            .Where(value => value is not SDL.GamepadButton.Invalid and not SDL.GamepadButton.Count)
            .ToArray();

    private static readonly SDL.GamepadAxis[] Axes =
        Enum.GetValues<SDL.GamepadAxis>()
            .Where(value => value is not SDL.GamepadAxis.Invalid and not SDL.GamepadAxis.Count)
            .ToArray();

    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private IReadOnlyList<GamepadDevice> _devices = [];
    private GamepadDeviceId? _selected;
    private nint _gamepad;

    public SdlGamepadManager()
    {
        if (!SDL.Init(SDL.InitFlags.Gamepad))
        {
            throw new InvalidOperationException($"SDL gamepad initialization failed: {SDL.GetError()}");
        }

        _loop = Task.Run(PollAsync);
    }

    public event EventHandler? DevicesChanged;
    public event EventHandler<GamepadSnapshot>? SnapshotChanged;
    public event EventHandler<GamepadDeviceId>? SelectedDeviceDisconnected;

    public IReadOnlyList<GamepadDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    public static IReadOnlyList<(HostControlId Id, string Name)> StandardButtons { get; } =
        Buttons.Select(button => (ButtonId(button), button.ToString())).ToArray();

    public Task SelectAsync(GamepadDeviceId? device, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            CloseSelected();
            _selected = device;
            if (device is not null)
            {
                _gamepad = SDL.OpenGamepad(device.Value.Value);
                if (_gamepad == 0)
                {
                    _selected = null;
                    throw new InvalidOperationException($"SDL could not open the gamepad: {SDL.GetError()}");
                }
            }
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            CloseSelected();
        }

        SDL.QuitSubSystem(SDL.InitFlags.Gamepad);
        _stop.Dispose();
    }

    public static HostControlId ButtonId(SDL.GamepadButton button) =>
        new($"gamepad.button.{button.ToString().ToLowerInvariant()}");

    public static HostControlId AxisId(SDL.GamepadAxis axis) =>
        new($"gamepad.axis.{axis.ToString().ToLowerInvariant()}");

    public static bool TryGetStickPair(
        HostControlId movedAxis,
        out HostControlId xAxis,
        out HostControlId yAxis)
    {
        HostControlId leftX = AxisId(SDL.GamepadAxis.LeftX);
        HostControlId leftY = AxisId(SDL.GamepadAxis.LeftY);
        if (movedAxis == leftX || movedAxis == leftY)
        {
            xAxis = leftX;
            yAxis = leftY;
            return true;
        }

        HostControlId rightX = AxisId(SDL.GamepadAxis.RightX);
        HostControlId rightY = AxisId(SDL.GamepadAxis.RightY);
        if (movedAxis == rightX || movedAxis == rightY)
        {
            xAxis = rightX;
            yAxis = rightY;
            return true;
        }

        xAxis = default;
        yAxis = default;
        return false;
    }

    private async Task PollAsync()
    {
        int catalogCountdown = 0;
        while (!_stop.IsCancellationRequested)
        {
            SDL.UpdateGamepads();
            if (catalogCountdown-- <= 0)
            {
                RefreshDevices();
                catalogCountdown = 60;
            }

            GamepadSnapshot? snapshot = ReadSelected();
            if (snapshot is not null)
            {
                SnapshotChanged?.Invoke(this, snapshot);
            }

            await Task.Delay(8, _stop.Token).ConfigureAwait(false);
        }
    }

    private void RefreshDevices()
    {
        uint[] ids = SDL.GetGamepads(out _) ?? [];
        IReadOnlyList<GamepadDevice> devices = ids
            .Select(id => new GamepadDevice(
                new(id),
                SDL.GetGamepadNameForID(id) ?? $"Gamepad {id}",
                GetGuid(id)))
            .ToArray();

        GamepadDeviceId? disconnected = null;
        bool changed;
        lock (_gate)
        {
            changed = !_devices.SequenceEqual(devices);
            _devices = devices;
            if (_selected is { } selected && devices.All(device => device.Id != selected))
            {
                disconnected = selected;
                CloseSelected();
                _selected = null;
            }
        }

        if (changed)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (disconnected is not null)
        {
            SelectedDeviceDisconnected?.Invoke(this, disconnected.Value);
        }
    }

    private GamepadSnapshot? ReadSelected()
    {
        lock (_gate)
        {
            if (_gamepad == 0)
            {
                return null;
            }

            ImmutableHashSet<HostControlId>.Builder pressed = ImmutableHashSet.CreateBuilder<HostControlId>();
            foreach (SDL.GamepadButton button in Buttons)
            {
                if (SDL.GetGamepadButton(_gamepad, button))
                {
                    pressed.Add(ButtonId(button));
                }
            }

            ImmutableDictionary<HostControlId, float>.Builder axes =
                ImmutableDictionary.CreateBuilder<HostControlId, float>();
            foreach (SDL.GamepadAxis axis in Axes)
            {
                short raw = SDL.GetGamepadAxis(_gamepad, axis);
                axes[AxisId(axis)] = axis is SDL.GamepadAxis.LeftTrigger or SDL.GamepadAxis.RightTrigger
                    ? Math.Clamp(raw / 32767f, 0f, 1f)
                    : raw < 0 ? raw / 32768f : raw / 32767f;
            }

            return new(_selected!.Value, new(pressed.ToImmutable(), axes.ToImmutable()));
        }
    }

    private static string GetGuid(uint id)
    {
        byte[] bytes = new byte[33];
        SDL.GUIDToString(SDL.GetGamepadGUIDForID(id), bytes, bytes.Length);
        int length = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }

    private void CloseSelected()
    {
        if (_gamepad != 0)
        {
            SDL.CloseGamepad(_gamepad);
            _gamepad = 0;
        }
    }
}
