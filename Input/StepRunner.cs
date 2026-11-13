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

    /// <summary>
    /// How far through its time the journey in hand is, 0 to 1 - nought while it waits to set off; NaN with none under
    /// way. When EnRoute events go out.
    /// </summary>
    float JourneyProgress { get; }

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
/// caller at a Gate or a Hold, and on to the next. Several keys at one anchor go out in the order given, each its pace
/// after the one before (StepPlan.Paces); a Here stop has no journey. Keys it pressed are its own until an Up for them; a
/// drop - the caller abandoning, a stop, the cursor given back - lets go of all of them, after any button is up. See
/// docs/plan-steps.md.
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
    private bool _leaving;
    private TimeSpan _setOffAt;
    private int _hereLeadMs;

    /// <summary>The stop's EnRoute events sent, by event index, and when. See EnRoute.</summary>
    private readonly Dictionary<int, TimeSpan> _enRouteSent = [];

    /// <summary>The anchor whose keys are going out, how many have, when the last did, and since when one has waited on a modifier.</summary>
    private StepAnchor? _sequence;
    private int _sequenceNext;
    private TimeSpan _sequenceLast;
    private TimeSpan? _confirmSince;

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

    /// <summary>Whether the plan is at the stop in hand - landed and not yet gone - for drawing the keys it holds there.</summary>
    public bool AtStop => Phase is StepPhase.AtGate or StepPhase.Pressing or StepPhase.Holding or StepPhase.Leaving;

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
                    // Keys where the cursor is wait for the click before to be done with: typing over a held button is
                    // not what a hand does.
                    if (stop.Here && _io.PressHeld)
                        return false;

                    foreach (var e in stop.KeysAt(StepAnchor.BeforeArrival).Where(e => !e.Down))
                        Up(e.Input);

                    // Keys already down - held from an earlier stop - are not pressed again on the way.
                    var lead = stop.LeadKeys.Where(k => !_held.Contains(k)).Distinct().ToArray();

                    _setOff = true;
                    _setOffAt = now;

                    // A Here stop moves nowhere: its lead keys go down now, and it arrives their lead later.
                    if (stop.Here)
                    {
                        foreach (var key in lead)
                            Down(key);

                        _hereLeadMs = lead.Length > 0 ? journey.LeadMs : 0;
                        return true;
                    }

                    _io.Travel(Index, lead);

                    foreach (var key in lead)
                        _held.Add(key);

                    return false;
                }

                if (stop.Here ? (now - _setOffAt).TotalMilliseconds < _hereLeadMs : !_io.Arrived())
                {
                    EnRoute(stop, now, false);
                    return false;
                }

                // An EnRoute event not yet out - a tap's Up a Key Hold on - holds the stop here until it is: the Gate and
                // the arrival keys come after it, as a hand's would.
                if (!EnRoute(stop, now, true))
                    return false;

                Phase = stop.Gate ? StepPhase.AtGate : StepPhase.Pressing;
                return !stop.Gate;

            case StepPhase.AtGate:
            case StepPhase.Holding:
                return false;

            case StepPhase.Pressing:
                if (!_arrivalDone)
                {
                    if (!Sequence(stop, StepAnchor.OnArrival, now))
                        return false;

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

                if (!Sequence(stop, StepAnchor.AfterPress, now))
                    return false;

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
                if (!_leaving)
                {
                    if (stop.Wander)
                        _io.StopWander();

                    _leaving = true;
                }

                // As the next journey sets off: a key let go here comes up with the move, not before it. Several go out
                // at their paces first, and the move sets off with the last.
                if (!Sequence(stop, StepAnchor.OnLeave, now))
                    return false;

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

    /// <summary>
    /// The stop's EnRoute events, in the order planned: each once the journey is as far through its time as the event's
    /// moment is through the planned journey's - by the pointer's own clock, so a journey longer or shorter than planned,
    /// or slowed by a stall, keeps its events at the same share of the way - or once landed; an Up after a Down also no
    /// sooner than its Key Hold after the Down really went. True once all are out. A Down of a key the plan already holds
    /// sends nothing; its Up lets the key go all the same.
    /// </summary>
    private bool EnRoute(Stop stop, TimeSpan now, bool landed)
    {
        if (!stop.HasEnRoute)
            return true;

        var moments = _plan.EnRoutes[Index];
        var journeyMs = _plan.Journeys[Index].Path.Ms;
        var progress = _io.JourneyProgress;
        var all = true;

        foreach (var i in Enumerable.Range(0, stop.Events.Length).Where(i => !float.IsNaN(moments[i].AtMs))
                     .OrderBy(i => moments[i].AtMs))
        {
            if (_enRouteSent.ContainsKey(i))
                continue;

            var moment = moments[i];
            var due = landed || journeyMs > 0f && !float.IsNaN(progress) && progress >= moment.AtMs / journeyMs;

            if (due && moment.After >= 0)
                due = _enRouteSent.TryGetValue(moment.After, out var downAt) && (now - downAt).TotalMilliseconds >= moment.AfterMs;

            if (!due)
            {
                all = false;
                continue;
            }

            var e = stop.Events[i];

            if (e.Down)
                Down(e.Input);
            else
                Up(e.Input);

            _enRouteSent[i] = now;
        }

        return all;
    }

    private void Reset()
    {
        _enRouteSent.Clear();
        _setOff = false;
        _arrivalDone = false;
        _pressed = false;
        _upAt = null;
        _leaving = false;
        _sequence = null;
    }

    /// <summary>
    /// The stop's keys at this anchor, in the order given, each once its pace has passed since the one before; true once
    /// all are out. One key alone goes at once, as ever. In a sequence, a key that is not a modifier waits, as a click does,
    /// for the modifiers held to read as down - ctrl+A sent as a bare A types an A - and the plan is dropped if they do
    /// not within ConfirmMs.
    /// </summary>
    private bool Sequence(Stop stop, StepAnchor anchor, TimeSpan now)
    {
        if (_sequence != anchor)
        {
            _sequence = anchor;
            _sequenceNext = 0;
            _sequenceLast = now;
            _confirmSince = null;
        }

        var events = stop.Events;
        var paces = _plan.Paces[Index];
        var count = events.Count(e => !e.IsButton && e.Anchor == anchor);
        var k = 0;

        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];

            if (e.IsButton || e.Anchor != anchor || k++ < _sequenceNext)
                continue;

            if ((now - _sequenceLast).TotalMilliseconds < paces[i])
                return false;

            if (count > 1 && e.Down && !StepPlan.IsModifier(e.Input))
            {
                var untaken = _held.Where(h => StepPlan.IsModifier(h) && !_io.KeyIsDown(h)).ToArray();

                if (untaken.Length > 0)
                {
                    _confirmSince ??= now;

                    if ((now - _confirmSince.Value).TotalMilliseconds > ConfirmMs)
                        Drop($"{string.Join("+", untaken)} would not read as down");

                    return false;
                }
            }

            if (e.Down)
                Down(e.Input);
            else
                Up(e.Input);

            _sequenceNext++;
            _sequenceLast = now;
            _confirmSince = null;
        }

        return true;
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
