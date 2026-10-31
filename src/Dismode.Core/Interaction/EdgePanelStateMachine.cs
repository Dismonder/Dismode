namespace Dismode.Core.Interaction;

public enum EdgePanelState { Closed, Opening, Open, Closing }

public sealed class EdgePanelStateMachine(TimeProvider clock)
{
    public static TimeSpan AnimationDuration => TimeSpan.FromMilliseconds(180);
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private long _transitionStarted;
    private long _pointerChanged;
    private double _transitionFrom;
    private bool _inside;
    private bool _leavePending;
    private bool _waitForLeave;

    public EdgePanelState State { get; private set; }
    public double Reveal { get; private set; }

    public void SetPointerInside(bool inside)
    {
        if (_inside == inside)
        {
            return;
        }

        _inside = inside;
        _leavePending = !inside;
        _pointerChanged = _clock.GetTimestamp();
        if (!inside)
        {
            _waitForLeave = false;
        }
        else if (State == EdgePanelState.Closing && !_waitForLeave)
        {
            Transition(EdgePanelState.Opening);
        }
    }

    public void Toggle()
    {
        if (State is EdgePanelState.Open or EdgePanelState.Opening)
        {
            Close();
        }
        else
        {
            _waitForLeave = false;
            _leavePending = false;
            Transition(EdgePanelState.Opening);
        }
    }

    public void Close()
    {
        _waitForLeave = _inside;
        if (State is not (EdgePanelState.Closed or EdgePanelState.Closing))
        {
            Transition(EdgePanelState.Closing);
        }
    }

    public void Tick()
    {
        if (State is EdgePanelState.Opening or EdgePanelState.Closing)
        {
            double progress = Math.Clamp(_clock.GetElapsedTime(_transitionStarted).TotalMilliseconds
                / AnimationDuration.TotalMilliseconds, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            Reveal = _transitionFrom + ((State == EdgePanelState.Opening ? 1 : 0) - _transitionFrom) * eased;
            if (progress >= 1)
            {
                State = State == EdgePanelState.Opening ? EdgePanelState.Open : EdgePanelState.Closed;
                _pointerChanged = _clock.GetTimestamp();
            }
        }

        double dwell = _clock.GetElapsedTime(_pointerChanged).TotalMilliseconds;
        if (State == EdgePanelState.Closed && _inside && !_waitForLeave && dwell >= 250)
        {
            Transition(EdgePanelState.Opening);
        }
        else if (State == EdgePanelState.Open && _leavePending && !_inside && dwell >= 700)
        {
            Transition(EdgePanelState.Closing);
        }
    }

    private void Transition(EdgePanelState next)
    {
        _transitionFrom = Reveal;
        _transitionStarted = _clock.GetTimestamp();
        State = next;
    }
}
