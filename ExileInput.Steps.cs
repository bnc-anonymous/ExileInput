using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput;

/// <summary>
/// Step plans: a caller's stops, run by ExileInput - journeys with keys and buttons at moments tied to them, waiting for
/// the caller only at a Gate or a Hold. Beside the click plan, which is unchanged; a caller has one or the other, the newer
/// replacing the older. See docs/plan-steps.md, StepPlan and StepRunner.
/// </summary>
public partial class ExileInput
{
    /// <summary>Each caller's step plan and its runner, while it holds the cursor.</summary>
    private readonly Dictionary<string, (StepPlan Plan, StepRunner Runner)> _steps = new();

    /// <summary>Step plans ended, fading, with when. See FadeSteps.</summary>
    private readonly List<(StepPlan Plan, int Index, DateTime Since)> _fadingSteps = new();

    /// <summary>Stop flags, as they cross the bridge.</summary>
    private const int GateFlag = 1, HoldFlag = 2, WanderFlag = 4;

    private void SaveStepMethods()
    {
        var b = GameController.PluginBridge;

        // Stops flattened to arrays, as PlanClicks crosses: each stop's events are the next eventCounts[i] of the event
        // arrays; flags per stop are 1 Gate, 2 Hold, 4 Wander. See ExileInputClient.PlanSteps.
        b.SaveMethod("ExileInput.PlanSteps",
            (string who, RectangleF[] rects, Vector2[] points, int[] eventCounts, Keys[] eventInputs, bool[] eventDowns,
                int[] eventAnchors, int[] flags, int[] shortestMoveMs, bool show) =>
                Gated(() => PlanSteps(who, Stops(rects, points, eventCounts, eventInputs, eventDowns, eventAnchors, flags, shortestMoveMs), show)));
        b.SaveMethod("ExileInput.AddSteps",
            (string who, RectangleF[] rects, Vector2[] points, int[] eventCounts, Keys[] eventInputs, bool[] eventDowns,
                int[] eventAnchors, int[] flags, int[] shortestMoveMs) =>
                Gated(() => AddSteps(who, Stops(rects, points, eventCounts, eventInputs, eventDowns, eventAnchors, flags, shortestMoveMs))));
        b.SaveMethod("ExileInput.Proceed", (string who) => Gated(() => ProceedSteps(who)));

        // Where the plan is: the stop in hand and the phase, as ints (StepPhase); why it was dropped, read apart.
        b.SaveMethod("ExileInput.StepState", (string who) => Gated(() => StepState(who)));
        b.SaveMethod("ExileInput.StepWhy", (string who) => Gated(() => StepWhy(who)));
        b.SaveMethod("ExileInput.AbandonSteps", (string who, string why) => Gated(() => AbandonSteps(who, why)));
    }

    /// <summary>The stops from their flattened arrays; null when they do not add up.</summary>
    private static List<Stop> Stops(RectangleF[] rects, Vector2[] points, int[] eventCounts, Keys[] eventInputs, bool[] eventDowns,
        int[] eventAnchors, int[] flags, int[] shortestMoveMs)
    {
        rects ??= [];
        points ??= [];
        eventCounts ??= [];
        eventInputs ??= [];
        eventDowns ??= [];
        eventAnchors ??= [];
        flags ??= [];

        var count = Math.Max(rects.Length, points.Length);
        var stops = new List<Stop>(count);
        var e = 0;

        for (var i = 0; i < count; i++)
        {
            var n = i < eventCounts.Length ? eventCounts[i] : 0;

            if (n < 0 || e + n > eventInputs.Length || e + n > eventDowns.Length || e + n > eventAnchors.Length)
                return null;

            var events = new StepEvent[n];

            for (var k = 0; k < n; k++, e++)
                events[k] = new StepEvent(eventInputs[e], eventDowns[e], (StepAnchor)Math.Clamp(eventAnchors[e], 0, 3));

            var flag = i < flags.Length ? flags[i] : 0;

            stops.Add(new Stop(i < rects.Length ? rects[i] : default,
                i < points.Length ? points[i] : new Vector2(float.NaN, float.NaN), events,
                (flag & GateFlag) != 0, (flag & HoldFlag) != 0, (flag & WanderFlag) != 0,
                shortestMoveMs != null && i < shortestMoveMs.Length ? shortestMoveMs[i] : -1));
        }

        return stops;
    }

    /// <summary>
    /// Plans these stops and starts them: replaces any step plan (letting go of what it held) or click plan of this
    /// caller's. No stops drops the plan. False without the cursor, or for stops that cannot be planned.
    /// </summary>
    private bool PlanSteps(string who, List<Stop> stops, bool show)
    {
        if (!_lease.Held(who) || stops == null)
            return false;

        if (stops.Count == 0)
        {
            DropSteps(who, "dropped by the caller");
            return true;
        }

        var shortest = stops.Select(s => s.ShortestMoveMs).ToArray();
        var plan = StepPlan.Make(PointerFor(who), Cursor(), stops, show, () => HumanTiming.Ms(Settings.UsedClickHold),
            () => HumanTiming.Ms(Settings.UsedModifierTiming), ShortestOf(shortest), out _);

        if (plan == null)
            return false;

        // One plan a caller: the click plan it had gives way, and so does a loiter's held keys.
        if (_plans.Remove(who, out var clicks))
        {
            NotePlanEnd(who, clicks, "replaced by a step plan");

            if (clicks.Show)
                _fading.Add((clicks, DateTime.UtcNow, true));
        }

        EndLoiterKeys(who, Modifier.None);
        _loiterOnPlanned.Remove(who);
        DropSteps(who, "replaced by a new step plan");

        _steps[who] = (plan, new StepRunner(plan, new StepIo(this, who, plan)));
        return true;
    }

    /// <summary>Stops added to the end of a caller's step plan, going on from its last journey and the keys it holds.</summary>
    private bool AddSteps(string who, List<Stop> stops)
    {
        if (!_lease.Held(who) || stops == null || !_steps.TryGetValue(who, out var running) || running.Runner.Phase == StepPhase.Dropped)
            return false;

        if (stops.Count == 0)
            return true;

        running.Plan.Extend(PointerFor(who), stops, () => HumanTiming.Ms(Settings.UsedClickHold),
            () => HumanTiming.Ms(Settings.UsedModifierTiming), ShortestOf(stops.Select(s => s.ShortestMoveMs).ToArray()));
        running.Runner.Extended();
        return true;
    }

    private bool ProceedSteps(string who) =>
        _lease.Held(who) && _steps.TryGetValue(who, out var running) && running.Runner.Proceed();

    /// <summary>The stop in hand and the phase; [0, None] with no plan.</summary>
    private int[] StepState(string who) =>
        who != null && _steps.TryGetValue(who, out var running)
            ? [running.Runner.Index, (int)running.Runner.Phase]
            : [0, (int)StepPhase.None];

    private string StepWhy(string who) =>
        who != null && _steps.TryGetValue(who, out var running) ? running.Runner.Why : _stepWhy.GetValueOrDefault(who ?? "", "");

    /// <summary>Why each caller's last step plan was dropped, kept after it is gone so the caller can still read it.</summary>
    private readonly Dictionary<string, string> _stepWhy = new();

    private bool AbandonSteps(string who, string why)
    {
        if (!_lease.Held(who) || !_steps.ContainsKey(who))
            return false;

        DropSteps(who, string.IsNullOrWhiteSpace(why) ? "abandoned by the caller" : why);
        return true;
    }

    /// <summary>
    /// Ends a caller's step plan, letting go of what it holds (unless letGo is false: FadePlan's callers - Give, Abandon,
    /// a lapsed claim - let go of the caller's keys themselves), and fades its drawing. Nothing with none.
    /// </summary>
    private void DropSteps(string who, string why, bool letGo = true)
    {
        if (who == null || !_steps.Remove(who, out var running))
            return;

        running.Runner.Drop(why, letGo);
        _stepWhy[who] = running.Runner.Why;
        _planEnds.Add((who, $"{DateTime.Now:HH:mm:ss.fff} step plan {why}; at stop {running.Runner.Index + 1} of {running.Plan.Stops.Count}"));

        if (_planEnds.Count > PlanEndsKept)
            _planEnds.RemoveAt(0);

        if (running.Plan.Show)
            _fadingSteps.Add((running.Plan, running.Runner.Index, DateTime.UtcNow));
    }

    /// <summary>The holder's step plan moved on: from Tick, and from the input thread while it moves the cursor.</summary>
    private void StepSteps(string who)
    {
        if (!_steps.TryGetValue(who, out var running))
            return;

        // Run through, it is kept, so the caller can read Done, until it plans again or gives the cursor back.
        running.Runner.Step();
    }

    private void RenderSteps(ClickPlan.Colours colours, ClickPlan.Marks marks, DateTime now)
    {
        _fadingSteps.RemoveAll(f => now - f.Since >= FadeFor);

        if (marks.Fade)
        {
            foreach (var (plan, index, since) in _fadingSteps)
                plan.Draw(Graphics, colours, marks, index, false, 1f - (float)((now - since).TotalMilliseconds / FadeFor.TotalMilliseconds));
        }

        if (_lease.Holder is { } holder && _steps.TryGetValue(holder, out var running) && running.Plan.Show &&
            running.Runner.Phase != StepPhase.Done)
        {
            var pointer = PointerFor(holder);

            running.Plan.Draw(Graphics, colours, marks, running.Runner.Index, running.Runner.Travelling, 1f,
                new ClickPlan.CursorTrail(pointer.Trail(), pointer.JourneyProgress));
        }
    }

    /// <summary>A step plan's hands: the caller's pointer, its held keys (the one bag), and the buttons.</summary>
    private sealed class StepIo(ExileInput plugin, string who, StepPlan plan) : IStepIo
    {
        public TimeSpan Now => TimeSpan.FromMilliseconds(Environment.TickCount64);

        public void Travel(int stop, Keys[] leadKeys)
        {
            var pointer = plugin.PointerFor(who);
            var journey = plan.Journeys[stop];
            var lead = journey.LeadMs > 0 ? journey.LeadMs : HumanTiming.Ms(plugin.Settings.UsedModifierTiming);

            if (plan.Stops[stop].PointGiven)
                pointer.PlaceAlong(journey.To, leadKeys, journey.Drift, lead, journey.MinMs, journey.MsPerRootPx,
                    journey.Control1, journey.Control2, journey.Shape);
            else
                pointer.AimAlong(journey.Rect, journey.To, leadKeys, journey.Drift, lead, journey.MinMs, journey.MsPerRootPx,
                    journey.Control1, journey.Control2, journey.Shape);
        }

        public bool Arrived() => plugin.PointerFor(who).Arrived();

        public void KeyDown(Keys key) => plugin.KeysFor(who).Hold(key);

        public void KeyUp(Keys key) => plugin.KeysFor(who).Let(key);

        public bool KeyIsDown(Keys key) => Buttons.IsDown(key);

        public void Press(bool right, int holdMs)
        {
            Buttons.Press(right, holdMs);
            plugin.PointerFor(who).Clicked();
        }

        public bool PressHeld => Buttons.PressHeld;

        public void Wander(int stop)
        {
            var journey = plan.Journeys[stop];
            var rect = plan.Stops[stop].Rect;
            var radius = plan.Stops[stop].PointGiven || rect.Width <= 0 ? 4f : Math.Max(2f, Math.Min(rect.Width, rect.Height) * 0.15f);

            plugin.PointerFor(who).LoiterOnArrival(journey.To, radius);
        }

        public void StopWander() => plugin.PointerFor(who).StopLoitering();

        public void LetGoAfterPress(IReadOnlyList<Keys> keys)
        {
            if (keys.Count == 0)
                return;

            var held = plugin.KeysFor(who);

            // Ctrl let go before the button is up ends a ctrl+click as a plain one. As Give does.
            if (Buttons.PressHeld)
                Buttons.AfterPress(HumanTiming.Ms(plugin.Settings.UsedModifierTiming), () =>
                {
                    foreach (var key in keys)
                        held.Let(key);
                });
            else
                foreach (var key in keys)
                    held.Let(key);
        }
    }
}
