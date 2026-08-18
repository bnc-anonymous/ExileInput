using System;
using System.Collections.Generic;

namespace ExileInput.Tests;

/// <param name="Ready">What the pointer says about the modifier it pressed in flight.</param>
/// <param name="ControlDown">What the keyboard reports, or null if it must not be asked.</param>
internal sealed record GestureCase(
    string Name, bool Ctrl, bool Shift, bool Ready, bool? ControlDown, bool WantStraight,
    string Reason);

/// <summary>
/// Whether a click may go straight down on a modifier the approach already pressed.
///
/// This is the one line in the plugin with a known cost of being wrong. On a currency item the
/// difference between ctrl+right and right is the difference between putting it away and picking
/// it up, and the first version of this got it wrong in two ways at once and sent a bare left click
/// at Faustus with a stack on the cursor.
///
/// Case 4 is that bug. Case 1 is the other half of the same line: the readback must not be asked
/// for when nothing wants it, because asking costs up to a tenth of a second of polling and a plain
/// click would sit through it for an answer nothing reads.
/// </summary>
internal static class GestureCases
{
    internal static readonly GestureCase[] All =
    [
        new("1  A plain left click never asks about control", false, false, false, null, false,
            "not a tidiness point. The readback polls the key for up to a tenth of a second before " +
            "giving up, so a click that does not care what control is doing must not wait on it - " +
            "and every ordinary click is one of those.\n\n" +
            "        This case exists because the extraction that made this function testable " +
            "passed the readback in already evaluated, which compiled, read identically, and put " +
            "that poll in front of every click in the plugin"),

        new("2  Control pressed in flight and confirmed down goes straight in",
            true, false, true, true, true,
            "the whole reason the modifier goes down partway through the journey. It has had the " +
            "length of the approach to register, so there is nothing left to wait for on arrival - " +
            "which is both what a hand looks like and, incidentally, faster"),

        new("3  Control pressed in flight but not actually down goes the long way",
            true, false, true, false, false,
            "fails closed. The long way presses control itself and confirms it, so the worst case " +
            "is a slower click rather than a wrong one"),

        new("4  The Faustus bug: a stale ready flag with control long since released",
            true, false, true, false, false,
            "the pointer said it had pressed a modifier, which stayed true after the keys were let " +
            "go, so a later click with no move in between took the fast path with nothing held. " +
            "The guard meant to catch that was a readback that is null until the slow path has run " +
            "once - and a machine taking the fast path from the start never runs it.\n\n" +
            "        Two conditions, neither of which could fail when it mattered. Same shape as " +
            "case 3 and kept apart from it because the two were argued separately"),

        new("5  Nothing pressed in flight goes the long way without asking",
            true, false, false, null, false,
            "there is no modifier to trust, so there is nothing to confirm either. Asking anyway " +
            "would pay the poll to learn what we already know"),

        new("6  Shift alone rests on the pointer's own account", false, true, true, null, true,
            "shift has no readback here, so there is nothing to cross-check it against. Worth " +
            "stating rather than leaving implicit: this is the one modifier taken on trust, and if " +
            "a shift gesture ever goes out bare this case is where the assumption is written down"),

        new("7  Control and shift together still turn on control", true, true, true, false, false,
            "the readback covers control, and control is the one that matters - a ctrl+shift click " +
            "that loses its control is a plain shift click, which in a stash moves a whole stack"),
    ];

    internal static int Run()
    {
        var failed = 0;

        Console.WriteLine("--- whether a click may go straight down ----------------------------");
        Console.WriteLine();

        foreach (var c in All)
        {
            var asked = false;

            var got = Gesture.StraightIn(c.Ctrl, c.Shift, c.Ready, () =>
            {
                asked = true;
                return c.ControlDown ?? false;
            });

            // A case that gives no readback is saying it must not be consulted, which is as much
            // of the answer as the answer is.
            var ok = got == c.WantStraight && (c.ControlDown != null || !asked);

            if (!ok)
                failed++;

            var wants = new List<string> { c.WantStraight ? "straight in" : "the long way" };
            if (c.ControlDown == null)
                wants.Add("without asking the keyboard");

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{c.Name}");
            Console.WriteLine($"        given {(c.Ctrl ? "ctrl" : "")}{(c.Shift ? "+shift" : "")}" +
                              $"{(!c.Ctrl && !c.Shift ? "a plain click" : "")}, " +
                              $"pointer says {(c.Ready ? "pressed" : "nothing pressed")}, " +
                              $"keyboard says {c.ControlDown switch
                              {
                                  true => "control down",
                                  false => "control up",
                                  null => "(must not be asked)",
                              }}");
            Console.WriteLine($"        want  {string.Join(", ", wants)}");
            Console.WriteLine($"        got   {(got ? "straight in" : "the long way")}" +
                              $"{(asked ? ", after asking the keyboard" : ", without asking")}");
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        Console.WriteLine($"{All.Length - failed}/{All.Length} match");
        return failed;
    }
}
