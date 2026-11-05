using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ExileInput;

/// <summary>What a step plan's runner does to the world: ExileInput's pointer, keys and buttons, or a test's fakes.</summary>
internal interface IStepIo
{
    TimeSpan Now { get; }

    /// <summary>Sets off for a stop along its planned journey, these keys going down its lead before landing.</summary>
    void Travel(int stop, Keys[] leadKeys);

    /// <summary>Whether the journey in hand has put the cursor on its point.</summary>
    bool Arrived();

    void KeyDown(Keys key);

    void KeyUp(Keys key);

    /// <summary>Whether a key reads as down: a click is not pressed on a modifier that has not taken.</summary>
    bool KeyIsDown(Keys key);

    void Press(bool right, int holdMs);

    /// <summary>Whether a click's button is down now, its hold not over.</summary>
    bool PressHeld { get; }

    /// <summary>Idles round a stop's target while it holds. See Stop.Wander.</summary>
    void Wander(int stop);

    void StopWander();

    /// <summary>Lets these keys go once any click's button is up and a moment after, or now with none down.</summary>
    void LetGoAfterPress(IReadOnlyList<Keys> keys);
}

/// <summary>
/// Runs a step plan a little each tick: the journey to each stop, its keys and button at their anchors, a wait for the
/// caller at a Gate or a Hold, and on to the next. Keys it pressed are its own until an Up for them; a drop - the caller
/// abandoning, a stop, the cursor given back - lets go of all of them, after any button is up. See docs/plan-steps.md.
/// </summary>
internal sealed class StepRunner
{
    private readonly StepPlan _plan;
    private readonly IStepIo _io;

    /// <summary>The keys this plan has put down and not yet let go.</summary>
    private readonly HashSet<Keys> _held = [];

    private bool _setOff;
    private bool _arrivalDone;
    private bool _pressed;
    private TimeSpan _since;
    private TimeSpan? _upAt;

    /// <summary>How long the keys a click waits on may take to read as down before the plan is dropped.</summary>
    public int ConfirmMs { get; set; } = 200;

    public StepRunner(StepPlan plan, IStepIo io)
    {
        _plan = plan;
        _io = io;
        Phase = StepPhase.Travelling;
    }

    public int Index { get; private set; }

    public StepPhase Phase { get; private set; }

    /// <summary>Why the plan was dropped; "" otherwise.</summary>
    public string Why { get; private set; } = "";

    public IReadOnlyCollection<Keys> HeldKeys => _held;

    public bool Ended => Phase is StepPhase.Done or StepPhase.Dropped;

    /// <summary>Whether the cursor is on its way to the stop in hand, for drawing the way still ahead.</summary>
    public bool Travelling => Phase == StepPhase.Travelling && _setOff;

    /// <summary>Stops added to the plan after it ran out go on from where it ended. See StepPlan.Extend.</summary>
    public void Extended()
    {
        if (Phase == StepPhase.Done && Index < _plan.Stops.Count)
        {
            Phase = StepPhase.Travelling;
            Reset();
        }
    }

    /// <summary>Past the Gate or Hold the plan waits at. False when it waits at neither.</summary>
    public bool Proceed()
    {
        switch (Phase)
        {
            case StepPhase.AtGate:
                Phase = StepPhase.Pressing;
                return true;

            case StepPhase.Holding:
                Phase = StepPhase.Leaving;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Ends the plan, letting go of every key it holds once any button is up - unless letGo is false, for a caller whose
    /// keys are being let go already (the cursor given back, a stop: they share the one bag).
    /// </summary>
    public void Drop(string why, bool letGo = true)
    {
        if (Ended)
            return;

        if (Index < _plan.Stops.Count && _plan.Stops[Index].Wander)
            _io.StopWander();

        if (letGo)
            _io.LetGoAfterPress(_held.ToArray());

        _held.Clear();
        Why = why ?? "";
        Phase = StepPhase.Dropped;
    }

    /// <summary>Moves the plan on as far as it can this tick.</summary>
    public void Step()
    {
        // A few transitions a tick at most, so instant ones (a stop with nothing to wait on) flow without looping for ever.
        for (var k = 0; k < 8 && !Ended; k++)
        {
            if (!Advance())
                return;
        }
    }

    private bool Advance()
    {
        var now = _io.Now;
        var stop = _plan.Stops[Index];
        var journey = _plan.Journeys[Index];

        switch (Phase)
        {
            case StepPhase.Travelling:
                if (!_setOff)
                {
                    foreach (var e in stop.KeysAt(StepAnchor.BeforeArrival).Where(e => !e.Down))
                        Up(e.Input);

                    // Keys already down - held from an earlier stop - are not pressed again on the way.
                    var lead = stop.LeadKeys.Where(k => !_held.Contains(k)).Distinct().ToArray();

                    _io.Travel(Index, lead);

                    foreach (var key in lead)
                        _held.Add(key);

                    _setOff = true;
                    return false;
                }

                if (!_io.Arrived())
                    return false;

                Phase = stop.Gate ? StepPhase.AtGate : StepPhase.Pressing;
                return !stop.Gate;

            case StepPhase.AtGate:
            case StepPhase.Holding:
                return false;

            case StepPhase.Pressing:
                if (!_arrivalDone)
                {
                    foreach (var e in stop.KeysAt(StepAnchor.OnArrival))
                    {
                        if (e.Down)
                            Down(e.Input);
                        else
                            Up(e.Input);
                    }

                    _arrivalDone = true;
                    _since = now;
                }

                if (stop.HasButton)
                {
                    if (!_pressed)
                    {
                        // Not on a modifier that has not taken: a ctrl+click sent as a bare click is a different click.
                        if (_held.Any(k => !_io.KeyIsDown(k)))
                        {
                            if ((now - _since).TotalMilliseconds > ConfirmMs)
                                Drop($"{string.Join("+", _held.Where(k => !_io.KeyIsDown(k)))} would not read as down");

                            return false;
                        }

                        // The click before still holds its button: this one waits for it to come up.
                        if (_io.PressHeld)
                            return false;

                        _io.Press(stop.RightButton, journey.HoldMs);
                        _pressed = true;
                        return false;
                    }

                    if (_io.PressHeld)
                        return false;

                    _upAt ??= now;

                    if (stop.KeysAt(StepAnchor.AfterPress).Any() && (now - _upAt.Value).TotalMilliseconds < journey.ReleaseMs)
                        return false;
                }

                foreach (var e in stop.KeysAt(StepAnchor.AfterPress))
                {
                    if (e.Down)
                        Down(e.Input);
                    else
                        Up(e.Input);
                }

                if (stop.Hold)
                {
                    Phase = StepPhase.Holding;

                    if (stop.Wander)
                        _io.Wander(Index);

                    return false;
                }

                Phase = StepPhase.Leaving;
                return true;

            case StepPhase.Leaving:
                if (stop.Wander)
                    _io.StopWander();

                // As the next journey sets off: a key let go here comes up with the move, not before it.
                foreach (var e in stop.KeysAt(StepAnchor.OnLeave))
                {
                    if (e.Down)
                        Down(e.Input);
                    else
                        Up(e.Input);
                }

                _plan.MarkDone(Index);
                Index++;

                if (Index >= _plan.Stops.Count)
                {
                    // Nothing outlives the plan: a key never let go comes up once any button is.
                    _io.LetGoAfterPress(_held.ToArray());
                    _held.Clear();
                    Phase = StepPhase.Done;
                    return false;
                }

                Phase = StepPhase.Travelling;
                Reset();
                return true;
        }

        return false;
    }

    private void Reset()
    {
        _setOff = false;
        _arrivalDone = false;
        _pressed = false;
        _upAt = null;
    }

    private void Down(Keys key)
    {
        if (_held.Add(key))
            _io.KeyDown(key);
    }

    private void Up(Keys key)
    {
        if (_held.Remove(key))
            _io.KeyUp(key);
    }
}
