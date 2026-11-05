using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput.Tests;

/// <summary>
/// A step plan's hands for a test: a clock the case moves, a journey of 100ms to each stop, keys that read as down once
/// pressed (lead keys once the journey has landed), a button held for its hold, and a log of what was done in order.
/// </summary>
internal sealed class FakeStepIo : IStepIo
{
    public readonly List<string> Log = [];
    public readonly List<double> At = [];

    /// <summary>What was done, and when on the clock.</summary>
    private void Note(string what)
    {
        Log.Add(what);
        At.Add(Ms);
    }

    /// <summary>When this was done, or NaN.</summary>
    public double When(string what) => Log.IndexOf(what) is var i and >= 0 ? At[i] : double.NaN;

    public readonly HashSet<Keys> Down = [];
    public double Ms;
    public bool KeysTake = true;

    private double _arriveAt = double.MaxValue;
    private Keys[] _lead = [];
    private double _pressUntil;
    private readonly List<Keys> _afterPress = [];

    public TimeSpan Now => TimeSpan.FromMilliseconds(Ms);

    public void Travel(int stop, Keys[] leadKeys)
    {
        Note($"travel {stop + 1}" + (leadKeys.Length > 0 ? $" lead {string.Join("+", leadKeys)}" : ""));
        _arriveAt = Ms + 100;
        _lead = leadKeys;
    }

    public bool Arrived()
    {
        if (Ms < _arriveAt)
            return false;

        foreach (var key in _lead)
            if (KeysTake)
                Down.Add(key);

        return true;
    }

    public void KeyDown(Keys key)
    {
        Note($"down {key}");

        if (KeysTake)
            Down.Add(key);
    }

    public void KeyUp(Keys key)
    {
        Note($"up {key}");
        Down.Remove(key);
    }

    public bool KeyIsDown(Keys key) => Down.Contains(key);

    public void Press(bool right, int holdMs)
    {
        Note(right ? "press R" : "press L");
        _pressUntil = Ms + holdMs;
    }

    public bool PressHeld => Ms < _pressUntil;

    public void Wander(int stop) => Note($"wander {stop + 1}");

    public void StopWander() => Note("stop wandering");

    public void LetGoAfterPress(IReadOnlyList<Keys> keys)
    {
        if (keys.Count == 0)
            return;

        if (PressHeld)
        {
            Note($"let go after the press: {string.Join("+", keys)}");
            _afterPress.AddRange(keys);
            return;
        }

        foreach (var key in keys)
            KeyUp(key);
    }

    /// <summary>The clock moved on by ms, a ms at a time, the runner stepped each, and deferred let-goes made once the button is up.</summary>
    public void Run(StepRunner runner, double ms)
    {
        for (var t = 0; t < ms; t++)
        {
            Ms++;

            if (!PressHeld && _afterPress.Count > 0)
            {
                foreach (var key in _afterPress)
                    KeyUp(key);

                _afterPress.Clear();
            }

            runner.Step();
        }
    }
}

internal static class StepCases
{
    private static readonly Keys Ctrl = Keys.LControlKey;
    private static readonly Keys Alt = Keys.LMenu;

    private static StepEvent Ev(Keys key, bool down, StepAnchor anchor) => new(key, down, anchor);

    private static StepEvent Click => new(Keys.LButton, true, StepAnchor.OnArrival);

    private static Stop At(float x, float y, params StepEvent[] events) =>
        new(default, new Vector2(x, y), events, false, false, false, -1);

    private static StepPlan Plan(params Stop[] stops) =>
        StepPlan.Make(new MousePointer(null, new Random(20261105)) { MinTravelMs = 60f, TravelMsPerRootPx = 6f, ArcPercent = 7f },
            new Vector2(100f, 100f), stops, true, new StepPlan.Timings(() => 64, () => 72, () => 40, () => 30), _ => 60f, out _);

    private static Stop Here(params StepEvent[] events) => new(default, new Vector2(float.NaN, float.NaN), events, false, false, false, -1);

    private static StepEvent On(Keys key, bool down) => new(key, down, StepAnchor.OnArrival);

    internal static int Run()
    {
        var failed = 0;
        var cases = 0;

        Console.WriteLine("--- step plans: moves with keys and buttons tied to them ----------------");
        Console.WriteLine();

        void Report(bool ok, string name, string want, string got, string why)
        {
            cases++;

            if (!ok)
                failed++;

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{name}");
            Console.WriteLine($"        want  {want}");
            Console.WriteLine($"        got   {got}");
            Console.WriteLine($"        why   {why}");
            Console.WriteLine();
        }

        // 1. A ctrl+click run: one ctrl down, with a lead on the way to the first; one up, after the last click's button.
        {
            var plan = Plan(
                At(300, 200, Ev(Ctrl, true, StepAnchor.BeforeArrival), Click),
                At(360, 210, Click),
                At(420, 260, Click, Ev(Ctrl, false, StepAnchor.AfterPress)));
            var ctrl = plan.Timeline().Where(p => p.Input == Ctrl).ToList();
            var downOk = ctrl.Count(p => p.Down) == 1 && ctrl.First(p => p.Down) is { Stop: 0, AtMs: -72f } d &&
                         Vector2.Distance(d.Where, plan.Journeys[0].To) > 1f;
            var upOk = ctrl.Count(p => !p.Down) == 1 && ctrl.First(p => !p.Down) is { Stop: 2, AtMs: 64f + 72f };
            var leadsOk = plan.Journeys[0].LeadMs == 72 && plan.Journeys[1].LeadMs == 0 && plan.Journeys[2].LeadMs == 0;
            var spanOk = plan.HeldSpans() is [{ FromStop: 0, ToStop: 2 }];

            Report(downOk && upOk && leadsOk && spanOk, "1  A ctrl+click run holds ctrl from one Down to one Up",
                "ctrl down 72ms before the first lands, on the way; up 64+72ms after the third lands; one span 1-3",
                $"downs {string.Join("/", ctrl.Where(p => p.Down).Select(p => $"stop {p.Stop + 1} at {p.AtMs}"))}, ups " +
                $"{string.Join("/", ctrl.Where(p => !p.Down).Select(p => $"stop {p.Stop + 1} at {p.AtMs}"))}, leads " +
                $"{string.Join("/", plan.Journeys.Select(j => j.LeadMs))}, spans {plan.HeldSpans().Count}",
                "carrying ctrl through a batch needed its own machinery in the click plan (CarryModifiers, HandOver); here it is two events");
        }

        // 2. Alt held over a header: nothing pressed on the way; down on arrival; up as the cursor leaves.
        {
            var plan = Plan(
                new Stop(default, new Vector2(500, 120),
                    [Ev(Alt, true, StepAnchor.OnArrival), Ev(Alt, false, StepAnchor.OnLeave)], false, true, false, -1),
                At(300, 300, Click));
            var alt = plan.Timeline().Where(p => p.Input == Alt).ToList();
            var ok = plan.Journeys[0].LeadMs == 0 && alt is [{ Down: true, AtMs: 0f, Stop: 0 }, { Down: false, Stop: 0 }] &&
                     float.IsNaN(alt[1].AtMs) && alt[0].Where == plan.Journeys[0].To;

            Report(ok, "2  Alt over a header goes down only on arrival, and up on leaving",
                "no lead on the way; Alt down at the header at 0ms; up on leaving, with no time while it holds",
                $"lead {plan.Journeys[0].LeadMs}, Alt {string.Join(", ", alt.Select(p => $"{(p.Down ? "down" : "up")} at {p.AtMs}"))}",
                "the market ratio shows its detailed view only when Alt goes down over it (the player, 2026-11-05)");
        }

        // 3. Up marks where the release happens: a click's button on the next journey's creep, the last on its point.
        {
            var plan = Plan(At(300, 200, Click), At(420, 260, Click));
            var ups = plan.Timeline().Where(p => p.Implied).ToList();
            var ok = ups.Count == 2 && ups[0].Where == plan.Journeys[1].Path.CreptTo && ups[1].Where == plan.Journeys[1].To &&
                     ups[0].AtMs == 64f;

            Report(ok, "3  A button's up is drawn where it comes up",
                "the first click's up on the second journey's creep, after its 64ms hold; the last on its own point",
                $"ups at {string.Join(", ", ups.Select(u => $"({u.Where.X:0},{u.Where.Y:0}) at {u.AtMs}"))}",
                "the click plan drew the modifier's up at a fixed offset, not where or when it happened");
        }

        // 4. A Gate waits for Proceed before pressing anything.
        {
            var plan = Plan(At(300, 200, Click) with { Gate = true });
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 300);
            var waited = runner.Phase == StepPhase.AtGate && !io.Log.Contains("press L");
            var proceeded = runner.Proceed();
            io.Run(runner, 300);
            var ok = waited && proceeded && io.Log.Contains("press L") && runner.Phase == StepPhase.Done;

            Report(ok, "4  A Gate waits for the caller before the click",
                "at the gate with nothing pressed after landing; the click once proceeded; done",
                $"waited {waited}, proceeded {proceeded}, did {string.Join(", ", io.Log)}, ended {runner.Phase}",
                "the caller checks what is under the cursor before anything is pressed - the flipper's \"arrived over something else\"");
        }

        // 5. A Hold keeps Alt down until Proceed; Alt comes up as the journey to the next stop sets off.
        {
            var plan = Plan(
                new Stop(default, new Vector2(500, 120), [Ev(Alt, true, StepAnchor.OnArrival), Ev(Alt, false, StepAnchor.OnLeave)],
                    false, true, false, -1),
                At(300, 300, Click));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 500);
            var held = runner.Phase == StepPhase.Holding && io.Down.Contains(Alt);
            runner.Proceed();
            io.Run(runner, 400);
            var upAt = io.Log.IndexOf("up LMenu");
            var travelAt = io.Log.IndexOf("travel 2");
            var ok = held && upAt >= 0 && travelAt == upAt + 1 && io.Log.IndexOf("down LMenu") > io.Log.IndexOf("travel 1") &&
                     runner.Phase == StepPhase.Done;

            Report(ok, "5  A Hold keeps Alt down until the caller proceeds, and lets it go with the move",
                "travel 1, down Alt, held until Proceed; up Alt then travel 2 in the same step; then the click",
                $"held {held}, did {string.Join(", ", io.Log)}, ended {runner.Phase}",
                "Alt let go before the pause, or the pause before the move, left a gap the player saw (2026-11-05)");
        }

        // 6. The ctrl run through the runner: ctrl given to the first journey's lead only, let go after the last button.
        {
            var plan = Plan(
                At(300, 200, Ev(Ctrl, true, StepAnchor.BeforeArrival), Click),
                At(360, 210, Click),
                At(420, 260, Click, Ev(Ctrl, false, StepAnchor.AfterPress)));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 1500);
            var travels = io.Log.Where(l => l.StartsWith("travel")).ToList();
            var ok = travels.SequenceEqual(["travel 1 lead LControlKey", "travel 2", "travel 3"]) &&
                     io.Log.Count(l => l == "press L") == 3 && io.Log.Last() == "up LControlKey" && runner.Phase == StepPhase.Done;

            Report(ok, "6  A ctrl+click run presses ctrl once and lets it go once",
                "ctrl in the first journey's lead only; three clicks; ctrl up last",
                $"did {string.Join(", ", io.Log)}",
                "a hand holds ctrl through a batch of ctrl+clicks: 34 deposit clicks in one press");
        }

        // 7. The player taking the mouse mid-Hold: the plan is dropped and Alt let go.
        {
            var plan = Plan(new Stop(default, new Vector2(500, 120), [Ev(Alt, true, StepAnchor.OnArrival)], false, true, true, -1));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 300);
            runner.Drop("PlayerMovedTheMouse");
            var ok = runner.Phase == StepPhase.Dropped && runner.Why == "PlayerMovedTheMouse" && !io.Down.Contains(Alt) &&
                     io.Log.Contains("wander 1") && io.Log.Contains("stop wandering");

            Report(ok, "7  Dropped mid-Hold, the plan lets go of what it holds",
                "dropped with the reason; Alt up; the wander stopped",
                $"ended {runner.Phase} ({runner.Why}), Alt down {io.Down.Contains(Alt)}, did {string.Join(", ", io.Log)}",
                "a key left down turns the player's next click into a different one");
        }

        // 8. Abandoned while a ctrl+click's button is down: ctrl stays until the button is up.
        {
            var plan = Plan(At(300, 200, Ev(Ctrl, true, StepAnchor.BeforeArrival), Click),
                At(420, 260, Click, Ev(Ctrl, false, StepAnchor.AfterPress)));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            while (!io.Log.Contains("press L") && io.Ms < 1000)
                io.Run(runner, 1);

            runner.Drop("abandoned by the caller");
            var keptDuringPress = io.PressHeld && io.Down.Contains(Ctrl);
            io.Run(runner, 200);
            var ok = keptDuringPress && !io.Down.Contains(Ctrl) && io.Log.Any(l => l.StartsWith("let go after the press"));

            Report(ok, "8  Abandoned mid-click, ctrl comes up after the button, not before",
                "ctrl still down while the button is; up once it is",
                $"kept during the press {keptDuringPress}, ctrl down after {io.Down.Contains(Ctrl)}, did {string.Join(", ", io.Log)}",
                "ctrl let go before the button is up ends a ctrl+click as a plain one (as Give does)");
        }

        // 9. A modifier that never reads as down: no click, the plan dropped.
        {
            var plan = Plan(At(300, 200, Ev(Ctrl, true, StepAnchor.BeforeArrival), Click));
            var io = new FakeStepIo { KeysTake = false };
            var runner = new StepRunner(plan, io);

            io.Run(runner, 600);
            var ok = runner.Phase == StepPhase.Dropped && !io.Log.Contains("press L") && runner.Why.Contains("would not read as down");

            Report(ok, "9  No click on a modifier that has not taken",
                "dropped, nothing pressed",
                $"ended {runner.Phase} ({runner.Why}), did {string.Join(", ", io.Log)}",
                "a ctrl+click sent as a bare click on Faustus is the exact thing the stroke's confirmation exists to prevent");
        }

        // 10. Keys still held when the plan runs out are let go.
        {
            var plan = Plan(At(300, 200, Ev(Keys.LShiftKey, true, StepAnchor.OnArrival)));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 400);
            var ok = runner.Phase == StepPhase.Done && !io.Down.Contains(Keys.LShiftKey);

            Report(ok, "10 Nothing outlives the plan",
                "shift down on arrival, up as the plan ends",
                $"ended {runner.Phase}, did {string.Join(", ", io.Log)}",
                "a caller that forgets the Up must not leave shift down for the player");
        }

        // 11. ctrl+A then ctrl+V where the cursor is, after a click: in the order given, paced like typing.
        {
            var plan = Plan(At(300, 200, Click),
                Here(On(Ctrl, true), On(Keys.A, true), On(Keys.A, false), On(Keys.V, true), On(Keys.V, false), On(Ctrl, false)));
            var keys = plan.Timeline().Where(p => p.Stop == 1).ToList();
            var times = keys.Select(p => p.AtMs).ToArray();
            var ok = times.SequenceEqual([0f, 72f, 112f, 142f, 182f, 254f]) &&
                     keys.Select(p => p.Order).SequenceEqual([0, 1, 2, 3, 4, 5]) &&
                     keys.All(p => p.Where == plan.Journeys[0].To) &&
                     plan.Journeys[1].From == plan.Journeys[1].To && plan.Journeys[1].To == plan.Journeys[0].To &&
                     plan.Timeline().Where(p => p.Stop == 0).Select(p => p.AtMs).SequenceEqual([0f, 64f]);

            Report(ok, "11 ctrl+A, ctrl+V go out in order, a hold within each key and a gap between",
                "ctrl 0, A down 72 (modifier lead), A up 112 (+40 hold), V down 142 (+30 gap), V up 182, ctrl up 254 (+72); " +
                "all on the click's point, drawn 0-5 in order; the click itself as before",
                $"{string.Join(", ", keys.Select(p => $"{StepPlan.KeyLabel(p.Input)}{(p.Down ? "v" : "^")} {p.AtMs} #{p.Order}"))}; " +
                $"click {string.Join("/", plan.Timeline().Where(p => p.Stop == 0).Select(p => p.AtMs))}",
                "keys sharing an anchor went out in one step - a chord, not typing - and were drawn on one spot");
        }

        // 12. A keys-only stop does not move the cursor: no journey, keys straight away.
        {
            var plan = Plan(Here(On(Keys.Escape, true), On(Keys.Escape, false), On(Keys.Space, true), On(Keys.Space, false)));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 300);
            var ok = plan.Journeys[0].To == new Vector2(100f, 100f) && !io.Log.Any(l => l.StartsWith("travel")) &&
                     io.Log.SequenceEqual(["down Escape", "up Escape", "down Space", "up Space"]) &&
                     io.When("down Escape") == 1 && io.When("down Space") - io.When("up Escape") == 30 &&
                     runner.Phase == StepPhase.Done;

            Report(ok, "12 A keys-only stop leaves the cursor where it is",
                "planned at the cursor (100,100); no travel; Escape tapped, a 30ms gap, Space tapped; done",
                $"at ({plan.Journeys[0].To.X},{plan.Journeys[0].To.Y}), did {string.Join(", ", io.Log.Select((l, i) => $"{l}@{io.At[i]}"))}, ended {runner.Phase}",
                "a key pressed in a stash or a panel needs no journey, and a move there would be one the player could see for nothing");
        }

        // 13. A lone Down stays held: ctrl down with A at one stop, through a Hold, let go after V at the next.
        {
            var plan = Plan(Here(On(Ctrl, true), On(Keys.A, true), On(Keys.A, false)) with { Hold = true },
                Here(On(Keys.V, true), On(Keys.V, false), On(Ctrl, false)));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 500);
            var held = runner.Phase == StepPhase.Holding && io.Down.Contains(Ctrl) && !io.Down.Contains(Keys.A);
            runner.Proceed();
            io.Run(runner, 500);
            var ok = held && runner.Phase == StepPhase.Done && !io.Down.Contains(Ctrl) &&
                     io.Log.SequenceEqual(["down LControlKey", "down A", "up A", "down V", "up V", "up LControlKey"]) &&
                     plan.HeldSpans().Any(s => s.Key == Ctrl && s.FromStop == 0 && s.ToStop == 1);

            Report(ok, "13 A key with no Up at its anchor stays held",
                "ctrl still down through the Hold, A up; then V and ctrl up; ctrl drawn held across both stops",
                $"held {held}, did {string.Join(", ", io.Log)}, ended {runner.Phase}",
                "a key that goes down stays down until an Up for it: sequencing changes when keys go, not how long they stay");
        }

        // 14. The runner follows the timeline: a click, then ctrl+A, ctrl+V once the button is up, at the planned paces.
        {
            var plan = Plan(At(300, 200, Click),
                Here(On(Ctrl, true), On(Keys.A, true), On(Keys.A, false), On(Ctrl, false),
                    On(Ctrl, true), On(Keys.V, true), On(Keys.V, false), On(Ctrl, false)));
            var io = new FakeStepIo();
            var runner = new StepRunner(plan, io);

            io.Run(runner, 1500);
            var keys = io.Log.Select((l, i) => (l, t: io.At[i])).Where(x => x.l.StartsWith("down") || x.l.StartsWith("up")).ToList();
            var gaps = keys.Zip(keys.Skip(1), (a, b) => b.t - a.t).ToArray();
            var pressAt = io.When("press L");
            var ok = io.Log.Count(l => l.StartsWith("travel")) == 1 && keys.Count == 8 &&
                     keys.Select(x => x.l).SequenceEqual(["down LControlKey", "down A", "up A", "up LControlKey",
                         "down LControlKey", "down V", "up V", "up LControlKey"]) &&
                     keys[0].t >= pressAt + 64 && gaps.SequenceEqual([72d, 40d, 72d, 30d, 72d, 40d, 72d]) &&
                     runner.Phase == StepPhase.Done;

            Report(ok, "14 The runner sends the keys at the planned paces, after the click before is done",
                "one travel; press, and once its 64ms are up: ctrl, +72 A, +40 A up, +72 ctrl up, +30 ctrl, +72 V, +40 V up, +72 ctrl up",
                $"press at {pressAt}; {string.Join(", ", keys.Select(x => $"{x.l}@{x.t}"))}; ended {runner.Phase}",
                "typing over a held button is not what a hand does, and the drawn timeline is only worth having if it is what happens");
        }

        // 15. Several keys at one anchor wait for a modifier that has not taken: no bare A, the plan dropped.
        {
            var plan = Plan(Here(On(Ctrl, true), On(Keys.A, true), On(Keys.A, false), On(Ctrl, false)));
            var io = new FakeStepIo { KeysTake = false };
            var runner = new StepRunner(plan, io);

            io.Run(runner, 600);
            var ok = runner.Phase == StepPhase.Dropped && !io.Log.Contains("down A") && runner.Why.Contains("would not read as down");

            Report(ok, "15 No typed key under a modifier that has not taken",
                "dropped, A never pressed",
                $"ended {runner.Phase} ({runner.Why}), did {string.Join(", ", io.Log)}",
                "ctrl+A sent as a bare A types an A into the search box");
        }

        // 16. Keys held at the stop the plan is at are drawn there: Alt at a Hold on its own mark; ctrl from a run's first
        //     stop on a later stop's point, where it has no mark.
        {
            var altPlan = Plan(
                new Stop(default, new Vector2(500, 120), [Ev(Alt, true, StepAnchor.OnArrival), Ev(Alt, false, StepAnchor.OnLeave)],
                    false, true, false, -1),
                At(300, 300, Click));
            var io = new FakeStepIo();
            var runner = new StepRunner(altPlan, io);

            io.Run(runner, 500);
            var altMarks = runner.AtStop ? altPlan.HeldMarks(runner.Index, runner.HeldKeys) : [];
            runner.Proceed();
            io.Run(runner, 400);
            var afterMarks = altPlan.HeldMarks(runner.Index, runner.HeldKeys);

            var ctrlPlan = Plan(
                At(300, 200, Ev(Ctrl, true, StepAnchor.BeforeArrival), Click),
                At(360, 210, Click),
                At(420, 260, Click, Ev(Ctrl, false, StepAnchor.AfterPress)));
            var ctrlMarks = ctrlPlan.HeldMarks(1, [Ctrl]);

            var ok = altMarks is [{ Key: var a, Mark: true }] && a == Alt && afterMarks.Count == 0 &&
                     ctrlMarks is [{ Key: var c, Mark: false, Where: var w }] && c == Ctrl && w == ctrlPlan.Journeys[1].To;

            Report(ok, "16 A key held at a stop is drawn there until it comes up",
                "Alt at the Hold on its own down mark; nothing once let go; ctrl at the run's second stop on its point",
                $"at the Hold {string.Join(",", altMarks.Select(m => $"{m.Key}{(m.Mark ? " on its mark" : "")}"))}; after {afterMarks.Count}; " +
                $"run {string.Join(",", ctrlMarks.Select(m => $"{m.Key} at ({m.Where.X:0},{m.Where.Y:0}){(m.Mark ? " on its mark" : "")}"))}",
                "? and || on every stop were clutter (the player, 2026-11-05); what the player needs to see is which key is down, and where");
        }

        // 17. A ctrl+click's held line follows the approach from where ctrl goes down to the click, not a chord across.
        {
            var plan = Plan(At(300, 200, Ev(Ctrl, true, StepAnchor.BeforeArrival), Click, Ev(Ctrl, false, StepAnchor.AfterPress)));
            var span = plan.HeldSpans().Single();
            var line = plan.HeldLine(span);
            var path = plan.Journeys[0].Path;
            var offPath = line.Max(p => Enumerable.Range(0, 201).Min(k => Vector2.Distance(path.At(k / 200f), p)));
            var ok = line.Count > 2 && line[0] == span.From && offPath < 3f;

            Report(ok, "17 A key held on the way to one stop is drawn along the way",
                "from ctrl's down mark along the approach to the click, every point within 3px of the path",
                $"{line.Count} points, furthest {offPath:0.0}px off the path",
                "the line from a C partway along the approach to Faustus was grey, thin and straight: nearly invisible (the player, 2026-11-05)");
        }

        Console.WriteLine($"{cases - failed}/{cases} match");
        return failed;
    }
}
