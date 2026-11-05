using ExileCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Color = SharpDX.Color;
using Graphics = ExileCore.Graphics;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput;

/// <summary>When an event of a stop happens, tied to its journey. See docs/plan-steps.md.</summary>
internal enum StepAnchor
{
    /// <summary>A lead before the cursor lands, from Modifier Timing: how a click's modifiers have always gone down.</summary>
    BeforeArrival = 0,

    /// <summary>Once landed, and past the stop's Gate.</summary>
    OnArrival = 1,

    /// <summary>After the stop's button has come up and Modifier Timing more; straight after arrival with no button.</summary>
    AfterPress = 2,

    /// <summary>As the journey to the next stop sets off, or as the plan ends after the last.</summary>
    OnLeave = 3,
}

/// <summary>Where a step plan has got to. Crosses the bridge as an int.</summary>
internal enum StepPhase
{
    None = 0,
    Travelling = 1,
    AtGate = 2,
    Pressing = 3,
    Holding = 4,
    Leaving = 5,
    Done = 6,
    Dropped = 7,
}

/// <summary>
/// A key or a button going down or up at an anchor. Buttons are Keys.LButton and Keys.RButton; a button going down is a
/// click, its button brought up after the Click Hold, so a button's Up is implied and one given is ignored, and a button
/// goes down on arrival whatever anchor it names. Buttons never stay down across stops.
/// </summary>
internal readonly record struct StepEvent(Keys Input, bool Down, StepAnchor Anchor)
{
    public bool IsButton => Input is Keys.LButton or Keys.RButton;
}

/// <summary>
/// One stop of a step plan: a journey to its rectangle, or its point where one is given (not NaN), and its events. With
/// neither - a NaN point and an empty rectangle - a stop is Here: no journey, the cursor left where it is, for keys alone.
/// Gate: wait for the caller before the arrival events. Hold: wait for the caller after them. Wander: idle round the
/// target while held rather than stay still. ShortestMoveMs: as PlanClicks', -1 for ExileInput's own.
/// </summary>
internal sealed record Stop(RectangleF Rect, Vector2 Point, StepEvent[] Events, bool Gate, bool Hold, bool Wander, int ShortestMoveMs)
{
    public bool PointGiven => !float.IsNaN(Point.X) && !float.IsNaN(Point.Y);

    /// <summary>Neither a point nor a rectangle: keys where the cursor is, with no journey. See StepPlan.Add.</summary>
    public bool Here => !PointGiven && (Rect.Width <= 0 || Rect.Height <= 0);

    public bool HasButton => Events.Any(e => e.IsButton && e.Down);

    public bool RightButton => Events.Any(e => e.Input == Keys.RButton && e.Down);

    /// <summary>The keys going down on the way here, with a lead: what the journey presses.</summary>
    public Keys[] LeadKeys => Events.Where(e => !e.IsButton && e.Down && e.Anchor == StepAnchor.BeforeArrival).Select(e => e.Input).ToArray();

    public IEnumerable<StepEvent> KeysAt(StepAnchor anchor) => Events.Where(e => !e.IsButton && e.Anchor == anchor);
}

/// <summary>
/// A plan of stops: journeys to targets, with keys and buttons going down and up at moments tied to each. A click is a
/// stop with a button; a ctrl+click run is stops holding ctrl from a Down on the first to an Up on the last; Alt held over
/// a header is a stop with Alt down on arrival, a Hold, and Alt up on leaving. ExileInput runs it (StepRunner); this is
/// the plan and its drawing. Journeys are planned as ClickPlan plans them - landings, the curve through them, drift,
/// swoops and overshoots, kept in the window - so a step plan moves as a click plan does. See docs/plan-steps.md.
/// </summary>
internal sealed class StepPlan
{
    /// <summary>
    /// An event placed on the plan: when, relative to its stop's arrival, in ms, and where it is drawn. Order: a key's place
    /// among the keys at its anchor, from 0, so a sequence - C, A, A, V, V, C - is drawn side by side in order.
    /// </summary>
    internal readonly record struct Placed(int Stop, Keys Input, bool Down, StepAnchor Anchor, float AtMs, Vector2 Where, bool Implied,
        int Order = 0);

    /// <summary>A key held from one placed event to another, for the line drawn beside the path.</summary>
    internal readonly record struct Held(Keys Key, int FromStop, Vector2 From, int ToStop, Vector2 To);

    public readonly List<Stop> Stops = [];

    /// <summary>Each stop's journey and timings: Path, LeadMs (its lead keys'), HoldMs (its button's), ReleaseMs (its AfterPress delay).</summary>
    public readonly List<ClickPlan.Planned> Journeys = [];

    /// <summary>
    /// Each stop's key pacing, per event: how long after the key before it at the same anchor it goes, in ms - nought for
    /// the first, for buttons and for BeforeArrival, whose keys go down together with the lead. See PacesOf.
    /// </summary>
    public readonly List<float[]> Paces = [];

    public bool Show;

    /// <summary>How this plan draws its timings: Click Hold, Modifier Timing, Key Hold and Key Gap, each a fresh draw.</summary>
    internal sealed record Timings(Func<int> HoldMs, Func<int> ModifierMs, Func<int> KeyHoldMs, Func<int> KeyGapMs);

    /// <summary>
    /// The plan for these stops, from the cursor. Null, with why, for no stops. A stop with neither a rectangle nor a
    /// point is Here: keys where the cursor is.
    /// </summary>
    public static StepPlan Make(MousePointer pointer, Vector2 cursor, IReadOnlyList<Stop> stops, bool show, Timings timings,
        Func<int, float> minMsOf, out string why, Vector2? previous = null)
    {
        why = null;

        if (stops == null || stops.Count == 0)
        {
            why = "no stops";
            return null;
        }

        var plan = new StepPlan { Show = show };
        plan.Add(pointer, cursor, stops, timings, minMsOf, previous);
        return plan;
    }

    /// <summary>Stops added to the end, their journeys going on from the last stop's curve. See ExileInput.AddSteps.</summary>
    public void Extend(MousePointer pointer, IReadOnlyList<Stop> stops, Timings timings, Func<int, float> minMsOf)
    {
        if (stops.Count == 0)
            return;

        var last = Journeys[^1];
        Add(pointer, last.To, stops, timings, minMsOf, last.From);
    }

    private void Add(MousePointer pointer, Vector2 from, IReadOnlyList<Stop> stops, Timings timings, Func<int, float> minMsOf,
        Vector2? previous)
    {
        // The journeys as a click plan would make them, for the stops that move: each lead keys' lead drawn now, each
        // button's hold and each AfterPress delay too. Lead keys are passed per stop with nothing "before" them, so a
        // stop's lead is its own. A Here stop moves nowhere, so the journeys either side of it join up as if it were not
        // there; shortest moves are still each stop's own.
        var moving = Enumerable.Range(0, stops.Count).Where(i => !stops[i].Here).ToArray();
        var journeys = moving.Length == 0
            ? null
            : ClickPlan.Make(pointer, from,
                moving.Select(i => stops[i].Rect).ToArray(),
                moving.Select(i => stops[i].PointGiven ? stops[i].Point : new Vector2(float.NaN, float.NaN)).ToArray(),
                moving.Select(i => stops[i].RightButton).ToArray(),
                moving.Select(i => stops[i].LeadKeys).ToArray(),
                Show, timings.HoldMs, timings.ModifierMs, k => minMsOf(moving[k]), previous);
        var m = 0;
        var at = from;

        for (var i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            ClickPlan.Planned item;

            if (stop.Here)
            {
                // Standing still where the stop before left the cursor: a journey of no length, so the timeline and the
                // drawing treat it as any other, and the runner does not move at all.
                var lead = stop.LeadKeys;

                item = new ClickPlan.Planned(default, false, at, at, 0f, 0f, 0f, at, at, new Travel(at, at, 1f, 0f, 0f, 0f),
                    lead.Length > 0 ? timings.ModifierMs() : 0, false, timings.HoldMs(), timings.ModifierMs(), stop.RightButton,
                    lead, Travel.Flourish.None);
            }
            else
            {
                item = journeys.Items[m++];

                // Click plan's carrying zeroes the lead of a stop whose lead keys repeat the one before; a step plan says
                // what goes down where, so a repeated Down is a fresh lead.
                if (item.LeadMs == 0 && item.Modifiers.Length > 0)
                    item = item with { LeadMs = timings.ModifierMs(), Carried = false };
            }

            Journeys.Add(item);
            Paces.Add(PacesOf(stop.Events, timings));
            at = item.To;
        }

        Stops.AddRange(stops);
    }

    /// <summary>The anchors whose keys go out one after another, paced like typing. BeforeArrival's go with the lead.</summary>
    internal static readonly StepAnchor[] Sequenced = [StepAnchor.OnArrival, StepAnchor.AfterPress, StepAnchor.OnLeave];

    /// <summary>
    /// How the keys at each anchor are paced, in the order given: between a key's Down and its Up, a Key Hold; between a
    /// key's Up and the next key's Down - and any other pair - a Key Gap; a modifier going down before a key that is not one
    /// gets Modifier Timing first, as a click's modifiers lead its press, and a modifier let go after a key's Up gets it too,
    /// as one comes up after a click's button. The first key at an anchor goes at once, so one key alone is as it was.
    /// </summary>
    internal static float[] PacesOf(StepEvent[] events, Timings timings)
    {
        var paces = new float[events.Length];

        foreach (var anchor in Sequenced)
        {
            StepEvent? before = null;

            for (var i = 0; i < events.Length; i++)
            {
                var e = events[i];

                if (e.IsButton || e.Anchor != anchor)
                    continue;

                if (before is { } b)
                {
                    paces[i] = b.Down && !e.Down && b.Input == e.Input ? timings.KeyHoldMs()
                        : b.Down && IsModifier(b.Input) && e.Down && !IsModifier(e.Input) ? timings.ModifierMs()
                        : !b.Down && !IsModifier(b.Input) && !e.Down && IsModifier(e.Input) ? timings.ModifierMs()
                        : timings.KeyGapMs();
                }

                before = e;
            }
        }

        return paces;
    }

    /// <summary>Ctrl, shift or alt, either hand.</summary>
    internal static bool IsModifier(Keys key) => Modifier.VirtualKey(key) != 0;

    /// <summary>How long a stop's keys at an anchor take from the first to the last, in ms.</summary>
    public float SequenceMs(int stop, StepAnchor anchor)
    {
        var events = Stops[stop].Events;
        var ms = 0f;

        for (var i = 0; i < events.Length; i++)
            if (!events[i].IsButton && events[i].Anchor == anchor)
                ms += Paces[stop][i];

        return ms;
    }

    /// <summary>
    /// Every event placed: when, relative to its stop's arrival (OnLeave after a Hold has no time, NaN), and where on the
    /// way it happens. A button's Up is implied, where the button comes up: on the next journey's creep, or on the point
    /// for the last stop. Keys at one anchor follow one another at their paces; a button goes down once the arrival keys
    /// are all out, and AfterPress and OnLeave each start where the anchor before them ends.
    /// </summary>
    public List<Placed> Timeline()
    {
        var placed = new List<Placed>();

        for (var i = 0; i < Stops.Count; i++)
        {
            var stop = Stops[i];
            var journey = Journeys[i];
            var next = i + 1 < Journeys.Count ? Journeys[i + 1] : null;
            var paces = Paces[i];
            var arrivalMs = SequenceMs(i, StepAnchor.OnArrival);
            var pressMs = arrivalMs + (stop.HasButton ? journey.HoldMs : 0f);
            var afterPressAt = arrivalMs + (stop.HasButton ? journey.HoldMs + journey.ReleaseMs : 0f);
            var leaveAt = stop.Hold ? float.NaN : afterPressAt + SequenceMs(i, StepAnchor.AfterPress);
            var along = new Dictionary<StepAnchor, (int Count, float Ms)>();

            // AfterPress: on the next journey, ReleaseMs after the button came up and it set off - unless this stop holds,
            // when nothing moves until the caller proceeds.
            var afterPressWhere = next != null && !stop.Hold && stop.HasButton
                ? next.Path.At(next.Path.ProgressAt(Math.Min(journey.ReleaseMs, next.Path.Ms)))
                : journey.To;

            for (var n = 0; n < stop.Events.Length; n++)
            {
                var e = stop.Events[n];

                if (e.IsButton)
                {
                    if (!e.Down)
                        continue;

                    placed.Add(new Placed(i, e.Input, true, StepAnchor.OnArrival, arrivalMs, journey.To, false));
                    placed.Add(new Placed(i, e.Input, false, StepAnchor.OnArrival, pressMs,
                        next != null && !stop.Hold ? next.Path.CreptTo : journey.To, true));
                    continue;
                }

                var (at, where) = e.Anchor switch
                {
                    StepAnchor.BeforeArrival when e.Down => (-(float)journey.LeadMs,
                        journey.Path.At(journey.Path.ProgressAt(Math.Max(0f, journey.Path.Ms - journey.LeadMs)))),
                    StepAnchor.BeforeArrival => (-journey.Path.Ms, journey.From),
                    StepAnchor.OnArrival => (0f, journey.To),
                    StepAnchor.AfterPress => (afterPressAt, afterPressWhere),
                    _ => (leaveAt, journey.To),
                };

                // Each key at a sequenced anchor its pace after the one before.
                var order = 0;

                if (e.Anchor != StepAnchor.BeforeArrival)
                {
                    var (count, ms) = along.GetValueOrDefault(e.Anchor);

                    order = count;
                    ms += paces[n];
                    at += ms;
                    along[e.Anchor] = (count + 1, ms);
                }

                placed.Add(new Placed(i, e.Input, e.Down, e.Anchor, at, where, false, order));
            }
        }

        return placed;
    }

    /// <summary>
    /// The keys held, each from its Down to the next Up for it, in stop and anchor order; one with no Up is held to the
    /// end of the plan, where the runner lets it go.
    /// </summary>
    public List<Held> HeldSpans()
    {
        var spans = new List<Held>();
        var keys = Timeline().Where(p => !p.Implied && p.Input is not (Keys.LButton or Keys.RButton))
            .OrderBy(p => p.Stop).ThenBy(p => (int)p.Anchor).ToList();

        for (var k = 0; k < keys.Count; k++)
        {
            if (!keys[k].Down)
                continue;

            var up = keys.Skip(k + 1).FirstOrDefault(p => p.Input == keys[k].Input && !p.Down);
            var end = up.Input == keys[k].Input && !up.Down
                ? (up.Stop, up.Where)
                : (Stops.Count - 1, Journeys[^1].To);

            spans.Add(new Held(keys[k].Input, keys[k].Stop, keys[k].Where, end.Item1, end.Item2));
        }

        return spans;
    }

    /// <summary>When each stop was done with, so a done stop fades rather than vanishes. See Draw.</summary>
    private readonly Dictionary<int, DateTime> _doneAt = [];

    private static readonly TimeSpan DoneFadeFor = TimeSpan.FromSeconds(1);

    /// <summary>Marks a stop as done with, now: its drawing fades from here.</summary>
    public void MarkDone(int stop) => _doneAt.TryAdd(stop, DateTime.UtcNow);

    private List<Placed> _timeline;
    private List<Held> _held;
    private int _drawnStops = -1;

    /// <summary>
    /// The paths and marks from the stop in hand on, at this opacity: each path (the current one bright, its way still
    /// ahead only while travelling, the cursor's trail behind it), each event where it happens - bright down, dim up; L or
    /// R for buttons, C, S or A or the key's name for keys - a thin line beside the way for a key held, ? at a Gate and ||
    /// at a Hold. Stops done with fade out over DoneFadeFor. Laid out as ClickPlan.Draw is, in its colours and marks.
    /// </summary>
    public void Draw(Graphics graphics, ClickPlan.Colours colours, ClickPlan.Marks marks, int current, bool travelling,
        float opacity = 1f, ClickPlan.CursorTrail? trail = null)
    {
        if (_drawnStops != Stops.Count)
        {
            _timeline = Timeline();
            _held = HeldSpans();
            _drawnStops = Stops.Count;
        }

        var now = DateTime.UtcNow;

        for (var i = 0; i < Stops.Count; i++)
        {
            Color state;
            var strength = opacity;

            if (i < current)
            {
                if (!marks.Fade || !_doneAt.TryGetValue(i, out var at) || now - at >= DoneFadeFor)
                    continue;

                state = colours.Done;
                strength *= 1f - (float)((now - at).TotalMilliseconds / DoneFadeFor.TotalMilliseconds);
            }
            else
            {
                state = i == current ? colours.Current : colours.Later;
            }

            DrawStop(graphics, i, state, colours.Later, marks, i == current, strength,
                i == current && travelling ? trail : null);
        }

        // The held keys, a thin line a little above the way, from where each goes down to where it comes up.
        foreach (var span in _held.Where(s => s.ToStop >= current))
        {
            var colour = Faded(colours.Later, ClickPlan.UpStrength, opacity);
            var points = new List<Vector2> { span.From };

            for (var i = span.FromStop + 1; i <= span.ToStop; i++)
                for (var k = 1; k <= 8; k++)
                    points.Add(Journeys[i].Path.At(k / 8f));

            points.Add(span.To);
            graphics.DrawPolyLine(points.Select(p => p + new Vector2(0f, -6f)).ToArray(), colour, 1f);
        }

        if (trail is { } behind && travelling)
            ClickPlan.DrawTrail(graphics, behind, colours.Current, opacity);
    }

    private void DrawStop(Graphics graphics, int i, Color state, Color ahead, ClickPlan.Marks marks, bool current, float opacity,
        ClickPlan.CursorTrail? trail)
    {
        var bright = Faded(state, 1f, opacity);
        var dim = Faded(state, ClickPlan.UpStrength, opacity);
        var journey = Journeys[i];
        var stop = Stops[i];
        var points = new Vector2[ClickPlan.PathSamples];

        if (trail is { } along)
        {
            var start = Math.Clamp(along.Progress, 0f, 1f);

            for (var k = 0; k < ClickPlan.PathSamples; k++)
                points[k] = journey.Path.At(start + (1f - start) * k / (ClickPlan.PathSamples - 1f));

            if (start < 1f)
                graphics.DrawPolyLine(points, Faded(ahead, 1f, opacity), 1.2f);
        }
        else
        {
            for (var k = 0; k < ClickPlan.PathSamples; k++)
                points[k] = journey.Path.At(k / (ClickPlan.PathSamples - 1f));

            graphics.DrawPolyLine(points, bright, current ? 2f : 1.2f);
        }

        // Keys a little above where they happen, buttons on it, so a key and a click on the same point both read. Keys at
        // one anchor after the first go to the right of the one before, so a sequence reads in order: C A A V V C.
        var across = new Dictionary<StepAnchor, float>();

        foreach (var e in _timeline.Where(p => p.Stop == i))
        {
            var button = e.Input is Keys.LButton or Keys.RButton;

            if (button ? !marks.Buttons : !marks.Modifiers)
                continue;

            var label = button ? e.Input == Keys.RButton ? "R" : "L" : KeyLabel(e.Input);
            var offset = button ? Vector2.Zero : new Vector2(0f, -12f);

            if (!button && e.Anchor != StepAnchor.BeforeArrival)
            {
                var x = e.Order == 0 ? 0f : across.GetValueOrDefault(e.Anchor);

                offset.X = x;
                across[e.Anchor] = x + graphics.MeasureText(label).X + 3f;
            }

            ClickPlan.Letter(graphics, label, e.Where + offset, e.Down ? bright : dim);
        }

        var size = graphics.MeasureText("M");

        if (marks.Numbers)
            ClickPlan.Letter(graphics, $"{i + 1}", journey.To - new Vector2(0f, size.Y + 2f), dim);

        if (stop.Gate)
            ClickPlan.Letter(graphics, "?", journey.To + new Vector2(size.X + 4f, 0f), bright);

        if (stop.Hold)
            ClickPlan.Letter(graphics, "||", journey.To + new Vector2(size.X + 4f, stop.Gate ? size.Y : 0f), bright);
    }

    /// <summary>C, S or A for the modifiers, as ClickPlan draws them; any other key by its name.</summary>
    internal static string KeyLabel(Keys key)
    {
        var letters = ClickPlan.ModifierLetters([key]);
        return letters.Length > 0 ? letters : (key & Keys.KeyCode).ToString();
    }

    private static Color Faded(Color colour, float strength, float opacity) =>
        ClickPlan.Argb((int)(colour.A * strength * Math.Clamp(opacity, 0f, 1f)), colour.R, colour.G, colour.B);
}
