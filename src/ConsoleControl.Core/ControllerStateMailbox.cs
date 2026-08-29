namespace ConsoleControl.Core;

public sealed class ControllerStateMailbox
{
    private readonly object _gate = new();
    private readonly Queue<ControllerState> _digitalTransitions = [];
    private ControllerState _accepted = ControllerState.Neutral;
    private ControllerState? _latestAnalog;
    private bool _drainScheduled;

    public ControllerState Current
    {
        get
        {
            lock (_gate)
            {
                return _accepted;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _digitalTransitions.Count > 0 || _latestAnalog is not null;
            }
        }
    }

    public bool NextIsNeutral
    {
        get
        {
            lock (_gate)
            {
                if (_digitalTransitions.TryPeek(out ControllerState digital))
                {
                    return digital == ControllerState.Neutral;
                }
                return _latestAnalog == ControllerState.Neutral;
            }
        }
    }

    public bool Publish(ControllerState state) => Publish(state, required: false);

    public bool PublishRequired(ControllerState state) => Publish(state, required: true);

    public bool TryTake(out ControllerState state)
    {
        lock (_gate)
        {
            if (_digitalTransitions.TryDequeue(out state))
            {
                return true;
            }
            if (_latestAnalog is { } analog)
            {
                _latestAnalog = null;
                state = analog;
                return true;
            }

            _drainScheduled = false;
            state = default;
            return false;
        }
    }

    public void Reset(ControllerState state)
    {
        lock (_gate)
        {
            _digitalTransitions.Clear();
            _latestAnalog = null;
            _accepted = state;
            _drainScheduled = false;
        }
    }

    private bool Publish(ControllerState state, bool required)
    {
        lock (_gate)
        {
            if (!required && state == _accepted)
            {
                return false;
            }

            if (required || state.Buttons != _accepted.Buttons || state.DPad != _accepted.DPad)
            {
                _digitalTransitions.Enqueue(state);
                _latestAnalog = null;
            }
            else
            {
                _latestAnalog = state;
            }
            _accepted = state;
            if (_drainScheduled)
            {
                return false;
            }

            _drainScheduled = true;
            return true;
        }
    }
}