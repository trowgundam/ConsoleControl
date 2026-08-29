using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

using Avalonia.Input;

using ConsoleControl.Core;
using ConsoleControl.Input.Sdl;

namespace ConsoleControl.Gui;

public sealed class MappingWindowViewModel : INotifyPropertyChanged
{
    private readonly InputForwarder _forwarder;
    private readonly CancellationTokenSource _captureStop = new();
    private InputProfile _draft;
    private CanonicalDigitalControl _selectedTarget = CanonicalDigitalControl.A;
    private string _statusText = "Click a Switch button, then press the host input to bind.";

    internal MappingWindowViewModel(InputForwarder forwarder)
    {
        _forwarder = forwarder;
        _draft = forwarder.ActiveProfile
            ?? throw new InvalidOperationException("Select an input source before editing its mapping.");
        CaptureCommand = new AsyncCommand<CanonicalDigitalControl>(CaptureAsync);
        CaptureStickCommand = new AsyncCommand<CanonicalStick>(CaptureStickAsync);
        RemoveBindingCommand = new AsyncCommand(RemoveBindingAsync);
        ResetCommand = new AsyncCommand(ResetAsync);
        RefreshCurrentBindings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand CaptureCommand { get; }
    public ICommand CaptureStickCommand { get; }
    public ICommand RemoveBindingCommand { get; }
    public ICommand ResetCommand { get; }
    public ObservableCollection<MappingBindingRow> CurrentBindings { get; } = [];
    public MappingBindingRow? SelectedBinding { get; set; }
    public string ProfileName => _draft.Name;

    public string SelectedTargetLabel => Label(_selectedTarget);

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool HandleKey(PhysicalKey key) => _forwarder.SetKey(key, true);

    public void CancelCapture() => _captureStop.Cancel();

    public async Task SaveAsync()
    {
        try
        {
            await _forwarder.SaveAndActivateProfileAsync(_draft, CancellationToken.None);
            StatusText = "Mapping saved";
        }
        catch (Exception exception)
        {
            StatusText = $"Save failed: {exception.Message}";
            throw;
        }
    }

    private async Task CaptureAsync(CanonicalDigitalControl target)
    {
        _selectedTarget = target;
        OnPropertyChanged(nameof(SelectedTargetLabel));
        RefreshCurrentBindings();
        StatusText = $"Waiting for a host button for {Label(target)}…";
        try
        {
            CapturedHostControl captured = await _forwarder.CaptureNextInputAsync(_captureStop.Token);
            if (captured.Kind == CapturedHostControlKind.Axis &&
                target is not CanonicalDigitalControl.LeftTrigger and not CanonicalDigitalControl.RightTrigger)
            {
                StatusText = "Axis movement can currently be assigned only to ZL or ZR.";
                return;
            }

            AddBinding(captured, target);
            StatusText = $"Bound {DisplayName(captured.Control)} to {Label(target)}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Capture cancelled";
        }
        catch (Exception exception)
        {
            StatusText = $"Capture failed: {exception.Message}";
        }
    }

    private async Task CaptureStickAsync(CanonicalStick target)
    {
        StatusText = $"Waiting for a host stick for {target} stick…";
        try
        {
            CapturedHostControl captured = await _forwarder.CaptureNextInputAsync(_captureStop.Token);
            if (captured.Kind != CapturedHostControlKind.Axis ||
                !SdlGamepadManager.TryGetStickPair(captured.Control, out HostControlId xAxis, out HostControlId yAxis))
            {
                StatusText = "Move a left or right host stick to bind a Switch stick.";
                return;
            }

            StickBinding binding = new(
                xAxis,
                yAxis,
                target,
                AxisTransform.StickDefault,
                AxisTransform.StickDefault with { Inverted = true });
            _draft = _draft with
            {
                StickBindings = _draft.StickBindings
                    .Where(existing => existing.Target != target)
                    .Append(binding)
                    .ToImmutableArray(),
            };
            StatusText = $"Bound {DisplayName(xAxis)}/{DisplayName(yAxis)} to {target} stick";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Capture cancelled";
        }
        catch (Exception exception)
        {
            StatusText = $"Capture failed: {exception.Message}";
        }
    }

    private Task RemoveBindingAsync()
    {
        if (SelectedBinding is null)
        {
            return Task.CompletedTask;
        }

        if (SelectedBinding.Kind == CapturedHostControlKind.Axis)
        {
            CanonicalTrigger trigger = _selectedTarget == CanonicalDigitalControl.LeftTrigger
                ? CanonicalTrigger.Left
                : CanonicalTrigger.Right;
            _draft = _draft with
            {
                TriggerBindings = _draft.TriggerBindings
                    .Where(binding => binding.Target != trigger)
                    .ToImmutableArray(),
            };
        }
        else
        {
            HostControlId source = SelectedBinding.Source;
            ImmutableArray<DigitalBinding> bindings = _draft.DigitalBindings
                .Select(binding => binding.Source == source
                    ? binding with { Targets = binding.Targets.Remove(_selectedTarget) }
                    : binding)
                .Where(binding => !binding.Targets.IsEmpty)
                .ToImmutableArray();
            _draft = _draft with { DigitalBindings = bindings };
        }
        SelectedBinding = null;
        RefreshCurrentBindings();
        StatusText = $"Removed binding from {Label(_selectedTarget)}";
        return Task.CompletedTask;
    }

    private Task ResetAsync()
    {
        _draft = DefaultInputProfiles.For(_draft.Key);
        OnPropertyChanged(nameof(ProfileName));
        RefreshCurrentBindings();
        StatusText = "Default mapping loaded. Choose Save to persist it.";
        return Task.CompletedTask;
    }

    private void AddBinding(CapturedHostControl captured, CanonicalDigitalControl target)
    {
        if (captured.Kind == CapturedHostControlKind.Axis)
        {
            CanonicalTrigger trigger = target == CanonicalDigitalControl.LeftTrigger
                ? CanonicalTrigger.Left
                : CanonicalTrigger.Right;
            TriggerBinding binding = new(
                captured.Control,
                trigger,
                AxisTransform.TriggerDefault,
                0.5f);
            _draft = _draft with
            {
                TriggerBindings = _draft.TriggerBindings
                    .Where(existing => existing.Target != trigger)
                    .Append(binding)
                    .ToImmutableArray(),
            };
            RefreshCurrentBindings();
            return;
        }

        HostControlId source = captured.Control;
        DigitalBinding? existing = _draft.DigitalBindings.FirstOrDefault(binding => binding.Source == source);
        ImmutableArray<DigitalBinding> bindings;
        if (existing is null)
        {
            bindings = _draft.DigitalBindings.Add(new(source, [target]));
        }
        else if (existing.Targets.Contains(target))
        {
            return;
        }
        else
        {
            bindings = _draft.DigitalBindings
                .Replace(existing, existing with { Targets = existing.Targets.Add(target) });
        }

        _draft = _draft with { DigitalBindings = bindings };
        RefreshCurrentBindings();
    }

    private void RefreshCurrentBindings()
    {
        CurrentBindings.Clear();
        foreach (DigitalBinding binding in _draft.DigitalBindings.Where(
            binding => binding.Targets.Contains(_selectedTarget)))
        {
            CurrentBindings.Add(new(binding.Source, DisplayName(binding.Source), CapturedHostControlKind.Digital));
        }

        CanonicalTrigger? trigger = _selectedTarget switch
        {
            CanonicalDigitalControl.LeftTrigger => CanonicalTrigger.Left,
            CanonicalDigitalControl.RightTrigger => CanonicalTrigger.Right,
            _ => null,
        };
        if (trigger is not null)
        {
            foreach (TriggerBinding binding in _draft.TriggerBindings.Where(
                binding => binding.Target == trigger))
            {
                CurrentBindings.Add(new(
                    binding.Source,
                    $"{DisplayName(binding.Source)} (axis)",
                    CapturedHostControlKind.Axis));
            }
        }
    }

    private static string DisplayName(HostControlId control)
    {
        int separator = control.Value.LastIndexOf('.');
        return separator >= 0 ? control.Value[(separator + 1)..] : control.Value;
    }

    private static string Label(CanonicalDigitalControl control) => control switch
    {
        CanonicalDigitalControl.LeftShoulder => "L",
        CanonicalDigitalControl.RightShoulder => "R",
        CanonicalDigitalControl.LeftTrigger => "ZL",
        CanonicalDigitalControl.RightTrigger => "ZR",
        CanonicalDigitalControl.LeftStickClick => "L3",
        CanonicalDigitalControl.RightStickClick => "R3",
        CanonicalDigitalControl.DPadUp => "D-pad up",
        CanonicalDigitalControl.DPadRight => "D-pad right",
        CanonicalDigitalControl.DPadDown => "D-pad down",
        CanonicalDigitalControl.DPadLeft => "D-pad left",
        _ => control.ToString(),
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record MappingBindingRow(
    HostControlId Source,
    string DisplayName,
    CapturedHostControlKind Kind);