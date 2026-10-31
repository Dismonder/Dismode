namespace Dismode.Core.Interaction;

public enum EdgePanelState { Closed, Opening, Open, Closing }

public sealed class EdgePanelStateMachine(TimeProvider clock)
{
    public static TimeSpan AnimationDuration => TimeSpan.FromMilliseconds(240);
    public static TimeSpan CloseAnimationDuration => TimeSpan.FromMilliseconds(160);
    public static TimeSpan HoverDwellDuration => TimeSpan.FromMilliseconds(250);
    public static TimeSpan LeaveGraceDuration => TimeSpan.FromMilliseconds(700);

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private long _transitionStarted;
    private long _pointerChanged;
    private double _transitionFrom;
    private bool _inside;
    private bool _leavePending;
    private bool _waitForLeave;

    public EdgePanelState State { get; private set; }
    public double Reveal { get; private set; }
    public bool AnimationsEnabled { get; set; } = true;
    public bool IsDialogOpen { get; private set; }
    public bool HasPointerEntered => _inside || _leavePending;
    public bool NeedsTimer => State != EdgePanelState.Closed || (_inside && !_waitForLeave);

    public void SetDialogOpen(bool isOpen)
    {
        if (IsDialogOpen == isOpen)
        {
            return;
        }

        IsDialogOpen = isOpen;
        _pointerChanged = _clock.GetTimestamp();
    }

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
        if (IsDialogOpen)
        {
            return;
        }

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
        if (IsDialogOpen)
        {
            return;
        }

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
            double progress = !AnimationsEnabled ? 1 : Math.Clamp(_clock.GetElapsedTime(_transitionStarted).TotalMilliseconds
                / (State == EdgePanelState.Opening ? AnimationDuration : CloseAnimationDuration).TotalMilliseconds, 0, 1);
            double eased = State == EdgePanelState.Opening
                ? (progress >= 1 ? 1 : 1 - Math.Pow(2, -10 * progress)) : progress * progress * progress;
            Reveal = _transitionFrom + ((State == EdgePanelState.Opening ? 1 : 0) - _transitionFrom) * eased;
            if (progress >= 1)
            {
                State = State == EdgePanelState.Opening ? EdgePanelState.Open : EdgePanelState.Closed;
                _pointerChanged = _clock.GetTimestamp();
            }
        }

        double dwell = _clock.GetElapsedTime(_pointerChanged).TotalMilliseconds;
        if (State == EdgePanelState.Closed && _inside && !_waitForLeave && dwell >= HoverDwellDuration.TotalMilliseconds)
        {
            Transition(EdgePanelState.Opening);
        }
        else if (State == EdgePanelState.Open && !IsDialogOpen && _leavePending && !_inside && dwell >= LeaveGraceDuration.TotalMilliseconds)
        {
            Transition(EdgePanelState.Closing);
        }
    }

    private void Transition(EdgePanelState next)
    {
        _transitionFrom = Reveal;
        _transitionStarted = _clock.GetTimestamp();
        State = next;
        if (!AnimationsEnabled)
        {
            Reveal = next == EdgePanelState.Opening ? 1 : 0;
            State = next == EdgePanelState.Opening ? EdgePanelState.Open : EdgePanelState.Closed;
            _pointerChanged = _clock.GetTimestamp();
        }
    }
}
