using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ExileInput.Tests;

/// <summary>
/// The player's keys while ExileInput holds a modifier (KeyGuard): each case a run of key events through one guard, with
/// the verdicts wanted. The hook itself cannot run here; the decision it asks for can. A key let through while a modifier
/// is held is how F4 became Alt+F4 and closed the game; a key swallowed with nothing held is a keyboard that stopped
/// working; an up swallowed whose down was not, or the other way round, is a key the game thinks is held.
/// </summary>
internal static class KeyGuardCases
{
    /// <summary>
    /// One key event: the key, down or up, sent by ExileInput or not, a modifier held, already down to Windows, allowed by
    /// the holder (AllowPlayerKeys), and the time.
    /// </summary>
    private sealed record Event(Keys Key, bool Down, bool Own, bool Held, bool AlreadyDown, bool Allowed, long At, KeyVerdict Want);

    private static readonly KeyVerdict Pass = KeyVerdict.Pass;
    private static readonly KeyVerdict SwallowStop = new(true, true);
    private static readonly KeyVerdict Swallow = new(true, false);

    private static Event Down(Keys key, bool held, KeyVerdict want, long at = 0, bool own = false, bool alreadyDown = false,
        bool allowed = false) =>
        new(key, true, own, held, alreadyDown, allowed, at, want);

    private static Event Up(Keys key, bool held, KeyVerdict want, long at = 0, bool own = false, bool allowed = false) =>
        new(key, false, own, held, false, allowed, at, want);

    internal static int Run()
    {
        Console.WriteLine("--- the player's keys while ExileInput holds a modifier -----------");
        Console.WriteLine();

        var cases = new List<(string Name, string Why, Event[] Events)>
        {
            ("1  No modifier held: the player's F4 passes, down and up", "nothing changes for a caller that holds no modifier",
                [Down(Keys.F4, false, Pass), Up(Keys.F4, false, Pass)]),
            ("2  Alt held: the player's F4 is swallowed and stops the holder, and its up is swallowed",
                "Alt+F4 closes the game; nothing may see half a keystroke",
                [Down(Keys.F4, true, SwallowStop), Up(Keys.F4, true, Swallow)]),
            ("3  Ctrl held: Escape is swallowed and stops the holder", "Ctrl+Esc opens Start, Ctrl+Shift+Esc Task Manager",
                [Down(Keys.Escape, true, SwallowStop), Up(Keys.Escape, true, Swallow)]),
            ("4  Shift held: the player's Ctrl, a modifier, is swallowed too", "Ctrl+Shift switches the layout; no key is exempt",
                [Down(Keys.LControlKey, true, SwallowStop), Up(Keys.LControlKey, true, Swallow)]),
            ("5  ExileInput's own keys pass under its own modifier", "a step plan's keys under its Alt must still go out",
                [Down(Keys.A, true, Pass, own: true), Up(Keys.A, true, Pass, own: true)]),
            ("6  Swallowed under Alt, then Alt let go: repeats and the up are still swallowed",
                "a held F4 must not go on as F4 once Alt is up, and its up must not arrive alone",
                [Down(Keys.F4, true, SwallowStop, 0), Down(Keys.F4, false, Swallow, 500), Down(Keys.F4, false, Swallow, 530),
                    Up(Keys.F4, false, Swallow, 600), Down(Keys.F4, false, Pass, 900), Up(Keys.F4, false, Pass, 950)]),
            ("7  A key already down before Alt: repeats swallowed and stop, but its up passes",
                "the game saw W go down; swallowing its up would leave the character walking",
                [Down(Keys.W, true, SwallowStop, alreadyDown: true), Down(Keys.W, true, SwallowStop, 30, alreadyDown: true),
                    Up(Keys.W, true, Pass, 60)]),
            ("8  Swallowed, its up never seen: forgotten after ForgetAfterMs",
                "the secure desktop took the keyboard; F4 must not stay swallowed for ever",
                [Down(Keys.F4, true, SwallowStop, 0), Down(Keys.F4, false, Pass, KeyGuard.ForgetAfterMs + 1)]),
            ("9  Two keys under Alt: each swallowed and each stops", "the holder stops on the first; the second is no less dangerous",
                [Down(Keys.Return, true, SwallowStop), Down(Keys.Tab, true, SwallowStop), Up(Keys.Tab, false, Swallow),
                    Up(Keys.Return, false, Swallow)]),
            ("10 An up of a key never swallowed passes", "a key pressed before any modifier must come up in the game",
                [Up(Keys.Space, true, Pass)]),
            ("11 Shift held, Alt allowed: the player's Alt passes, down, repeats and up, and stops nothing",
                "CraftUntilHighlighted's player reads the advanced description under its held Shift",
                [Down(Keys.LMenu, true, Pass, 0, allowed: true), Down(Keys.LMenu, true, Pass, 30, allowed: true),
                    Up(Keys.LMenu, true, Pass, 60, allowed: true)]),
            ("12 Alt allowed: F4 pressed under it is still swallowed and stops the holder",
                "allowing Alt must not make Alt+F4 possible",
                [Down(Keys.LMenu, true, Pass, 0, allowed: true), Down(Keys.F4, true, SwallowStop, 10), Up(Keys.F4, true, Swallow, 20),
                    Up(Keys.LMenu, true, Pass, 30, allowed: true)]),
            ("13 A press swallowed before Alt was allowed: its repeats and up stay swallowed",
                "nothing may see the rest of a keystroke whose down it never saw",
                [Down(Keys.LMenu, true, SwallowStop, 0), Down(Keys.LMenu, true, SwallowStop, 30, allowed: true),
                    Up(Keys.LMenu, true, Swallow, 60, allowed: true)]),
            ("14 Alt allowed, already down when Shift went down: it passes, and so does its up",
                "the player was already holding Alt as the run began",
                [Down(Keys.LMenu, true, Pass, 0, alreadyDown: true, allowed: true), Up(Keys.LMenu, true, Pass, 30, allowed: true)]),
        };

        var failed = 0;

        foreach (var (name, why, events) in cases)
        {
            var guard = new KeyGuard();
            var got = new List<string>();
            var ok = true;

            foreach (var e in events)
            {
                var verdict = guard.OnKey((int)e.Key, e.Down, e.Own, e.Held, e.AlreadyDown, e.Allowed, e.At);

                ok &= verdict == e.Want;
                got.Add($"{e.Key}{(e.Down ? "v" : "^")}={Said(verdict)}{(verdict == e.Want ? "" : $"(want {Said(e.Want)})")}");
            }

            if (!ok)
                failed++;

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{name}");
            Console.WriteLine($"        got   {string.Join(", ", got)}");
            Console.WriteLine($"        why   {why}");
            Console.WriteLine();
        }

        var modifiers = new[]
        {
            Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey, Keys.ControlKey, Keys.LControlKey, Keys.RControlKey, Keys.Menu,
            Keys.LMenu, Keys.RMenu,
        };
        var others = new[] { Keys.F4, Keys.Return, Keys.Escape, Keys.Tab, Keys.Space, Keys.LWin, Keys.A, Keys.LButton };
        var sorted = Array.TrueForAll(modifiers, KeyGuard.IsModifier) && !Array.Exists(others, KeyGuard.IsModifier);

        if (!sorted)
            failed++;

        Console.WriteLine($"{(sorted ? "  ok  " : "FAIL  ")}15 Ctrl, Shift and Alt, either side or generic, are what is guarded for; nothing else");
        Console.WriteLine("        why   the stop lets these go; F4, Enter or the Windows key are not held for a caller");
        Console.WriteLine();

        return failed;
    }

    private static string Said(KeyVerdict v) => v.Swallow ? v.StopHolder ? "swallow+stop" : "swallow" : "pass";
}
