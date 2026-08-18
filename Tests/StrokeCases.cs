using System;
using System.Collections.Generic;

namespace ExileInput.Tests;

/// <summary>A keyboard that does what the case says and writes down what was asked of it.</summary>
internal sealed class FakeButtons : IButtons
{
    public readonly List<string> Log = [];

    /// <summary>What the readback says, and from which tick it starts saying it.</summary>
    public bool EverDown;

    public bool? State;

    public bool? Readable => State;

    public void Answered()
    {
        State = true;
        Log.Add("learn readable");
    }

    public void NeverAnswers()
    {
        State ??= false;
        Log.Add("learn unreadable");
    }

    public bool ControlIsDown()
    {
        Log.Add("ask");
        return EverDown;
    }

    public void Hold(bool ctrl, bool shift)
    {
        Log.Add($"hold {(ctrl ? "ctrl" : "")}{(ctrl && shift ? "+" : "")}{(shift ? "shift" : "")}");
    }

    public void Press(bool right) => Log.Add($"press {(right ? "right" : "left")}");

    public void Release() => Log.Add("release");
}

/// <param name="Down">Whether the readback ever says control is held.</param>
/// <param name="Readable">What the plugin has learned about the readback before this gesture.</param>
internal sealed record StrokeCase(
    string Name, bool Ctrl, bool Shift, bool Right, bool Down, bool? Readable, string Reason)
{
    /// <summary>When the button goes down, in milliseconds, or null if it must not.</summary>
    public int? ClickAt { get; init; }

    /// <summary>When the keys come up, or null if they must not have been pressed at all.</summary>
    public int? ReleaseAt { get; init; }

    /// <summary>Use the fast path instead, for a modifier the approach is already holding.</summary>
    public bool Straight { get; init; }

    /// <summary>Something the keyboard must never have been asked.</summary>
    public string Never { get; init; }
}

/// <summary>
/// A modified click, spread across ticks and blocking on none of them.
///
/// This used to be three sleeps in a row on the calling thread - press the modifier, poll up to
/// 200ms for it to read back, wait 60ms, click, wait 60ms, release. In this HUD the calling thread
/// is the single thread that draws every plugin, so that is better than a third of a second of
/// frozen overlay for one click, and the overlay is not ours to freeze.
///
/// The waiting is the same waiting; it just happens between ticks now. Which means the thing to
/// check is not that it got faster - it did not - but that every reason the old code waited is
/// still honoured. Case 4 is the one that matters most: where the key state cannot be read there is
/// no evidence, and the pause is all that stands between a ctrl+right click and a bare right click
/// that uses a Chaos Orb instead of moving it.
///
/// Time is driven by the test, so nothing here sleeps either - a suite that took a third of a
/// second per case would be a suite nobody runs.
/// </summary>
internal static class StrokeCases
{
    private const int TickMs = 16;
    private const int Ticks = 40;

    internal static readonly StrokeCase[] All =
    [
        new("1  A plain click goes out at once and holds nothing",
            false, false, false, false, null,
            "most clicks, and the one shape that never waited even under the old arrangement. " +
            "Nothing is held, so there is nothing to confirm and nothing to bring back up")
        {
            ClickAt = 0,
            Never = "ask",
        },

        new("2  A modifier that reads back at once clicks on the same tick",
            true, false, true, true, null,
            "the ordinary ctrl+right click. Control being down is a fact, and once it has been " +
            "read there is nothing left to wait for - the old code slept 60ms here anyway, before " +
            "a click it had already confirmed was safe")
        {
            ClickAt = 0,
            ReleaseAt = 64,
        },

        new("3  The modifier stays down for a moment after the click",
            true, false, true, true, null,
            "the half of the old gesture that was actually load-bearing. The click and the key " +
            "coming up in the same breath is how a ctrl+right click reaches the game as a right " +
            "click, and on a Chaos Orb that does not fail quietly - it uses the orb, onto the " +
            "cursor, where it blocks every click after it")
        {
            ClickAt = 0,
            ReleaseAt = 64,
        },

        new("4  Where the state cannot be read, the pause is what is left",
            true, false, true, false, null,
            "and this is the case the sleeps existed for. The readback never answers, so there is " +
            "no evidence either way; refusing on that reading stopped every ctrl+click in the " +
            "plugin four times running, and clicking immediately would send it bare. So it waits " +
            "out the confirmation window, learns that nothing answers here, and then clicks.\n\n" +
            "        208ms rather than 200 because a tick is 16ms and the window closes between " +
            "two of them - the wait can only ever be as fine as the tick that carries it")
        {
            ClickAt = 208,
            ReleaseAt = 272,
        },

        new("5  Having learned nothing answers, it stops waiting out the whole window",
            true, false, true, false, false,
            "the second click and every one after it. The confirmation is known to be useless " +
            "here, so what is left is the pause - and 60ms was the figure the old code used for " +
            "exactly this, which is why the setting is still called the modifier hold")
        {
            ClickAt = 64,
            ReleaseAt = 128,
        },

        new("6  A readback that has answered before and now says no refuses the click",
            true, false, true, false, true,
            "a real no rather than a check that does not work here, because it has worked before. " +
            "A click not sent costs an attempt and the caller tries again; a click sent bare costs " +
            "an orb on the cursor and a run untangled by hand.\n\n" +
            "        Note the shape of what it did: hold, ask until the window closes, release, " +
            "and then the whole thing again because the caller still wants its click. That loop " +
            "is the point - it ends when the caller's own timeout ends it, which knows what was " +
            "being clicked and why, where this only knows that control will not go down")
        {
            ClickAt = null,
            ReleaseAt = 208,
        },

        new("7  A modifier held across the approach clicks with nothing to confirm",
            true, false, true, true, true,
            "the whole point of pressing it in flight - it has had the length of the journey to " +
            "register, so arrival is not the moment to start asking. The hold afterwards is still " +
            "taken, which the fast path never used to do")
        {
            Straight = true,
            ClickAt = 0,
            ReleaseAt = 64,
            Never = "hold ctrl",
        },

        new("8  Shift alone presses shift alone", false, true, false, false, null,
            "the old Send pressed control whenever anything at all was wanted, so this would have " +
            "gone out as ctrl+shift. Nothing noticed because nothing asks for shift without ctrl, " +
            "but the pointer's own modifier list has always been right about it and having the two " +
            "disagree is a trap for whoever wants a plain shift-click first")
        {
            ClickAt = 0,
            ReleaseAt = 64,
            Never = "ask",
        },
    ];

    internal static int Run()
    {
        var failed = 0;

        Console.WriteLine("--- a modified click, spread across ticks ---------------------------");
        Console.WriteLine();

        foreach (var c in All)
        {
            var now = TimeSpan.Zero;
            var keys = new FakeButtons { EverDown = c.Down, State = c.Readable };
            var stroke = new Stroke(keys) { Now = () => now };

            int? clickedAt = null;
            int? releasedAt = null;
            var went = false;

            for (var t = 0; t < Ticks; t++)
            {
                var before = keys.Log.Count;

                if (!went)
                    went = c.Straight ? stroke.StraightIn(c.Right) : stroke.Push(c.Ctrl, c.Shift, c.Right);

                stroke.Step();

                for (var i = before; i < keys.Log.Count; i++)
                {
                    if (clickedAt == null && keys.Log[i].StartsWith("press"))
                        clickedAt = (int)now.TotalMilliseconds;

                    if (releasedAt == null && keys.Log[i] == "release")
                        releasedAt = (int)now.TotalMilliseconds;
                }

                now += TimeSpan.FromMilliseconds(TickMs);
            }

            var ok = clickedAt == c.ClickAt
                     && releasedAt == c.ReleaseAt
                     && (c.Never == null || !keys.Log.Contains(c.Never))
                     // Nothing may be left holding a modifier once a gesture has finished. Only
                     // where one does finish: a refused click is retried by its caller for as long
                     // as the caller keeps wanting it, so a stroke part-way through its third
                     // attempt is the loop working rather than a modifier left behind.
                     && (c.ClickAt == null || !stroke.Busy);

            if (!ok)
                failed++;

            var wants = new List<string>
            {
                c.ClickAt == null ? "no click at all" : $"the click at {c.ClickAt}ms",
                c.ReleaseAt == null ? "no keys held" : $"the keys up at {c.ReleaseAt}ms",
            };

            if (c.Never != null)
                wants.Add($"never \"{c.Never}\"");

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{c.Name}");
            Console.WriteLine($"        given {(c.Straight ? "a modifier already held, " : "")}" +
                              $"{(c.Ctrl ? "ctrl" : "")}{(c.Ctrl && c.Shift ? "+" : "")}{(c.Shift ? "shift" : "")}" +
                              $"{(!c.Ctrl && !c.Shift ? "no modifier" : "")}, " +
                              $"the readback {(c.Down ? "answering yes" : "silent")}, " +
                              $"known {c.Readable switch
                              {
                                  true => "readable",
                                  false => "unreadable",
                                  null => "untried",
                              }}");
            Console.WriteLine($"        want  {string.Join("; ", wants)}");
            Console.WriteLine($"        got   {(clickedAt == null ? "no click" : $"click at {clickedAt}ms")}, " +
                              $"{(releasedAt == null ? "no release" : $"keys up at {releasedAt}ms")}");
            Console.WriteLine($"        did   {string.Join(", ", keys.Log)}");
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        Console.WriteLine($"{All.Length - failed}/{All.Length} match");
        return failed;
    }
}
