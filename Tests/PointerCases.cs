using System;
using System.Collections.Generic;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput.Tests;

/// <param name="From">Where the cursor is when the move is asked for.</param>
/// <param name="StayPut">Whether the setting is on.</param>
internal sealed record PointerCase(
    string Name, Vector2 From, bool StayPut, bool WantMoved, string Reason)
{
    /// <summary>Whether the modifier must already be down by the time the cursor is there.</summary>
    public bool Modifier { get; init; }

    /// <summary>The tolerance the machine asks for, before the rect gets a say.</summary>
    public int Tolerance { get; init; } = 40;

    /// <summary>
    /// What the tolerance must end up no higher than.
    ///
    /// A ceiling and not a figure, because the aim point moves. Twenty-seven is the bound at dead
    /// centre of a 54px row; any other point in it sits nearer an edge and binds tighter. The one
    /// thing that must not happen is the forty the machine asked for surviving unchanged.
    /// </summary>
    public float? AcceptsAtMost { get; init; }
}

/// <summary>
/// A pointer aimed at a rect, driven through the same calls the plugin makes.
///
/// Which is the point of it. There was already a table for how near a landing must be, every case
/// of which passed, and the bound it described had never once been applied to anything: Move picked
/// its own point and called Place, Place resets the bound, and only Aim ever set it - so Aim was
/// dead code and the tests were checking arithmetic nobody ran.
///
/// A table that calls the sums directly can only ever say the sums are right. These go through the
/// pointer, with the game replaced by two functions, so a wiring mistake has somewhere to show up.
///
/// The stay-put cases are the other half: a hand already resting on a button does not lift off and
/// land again on it before pressing, and a forty pixel twitch inside the same row is a movement
/// with nothing behind it.
/// </summary>
internal static class PointerCases
{
    private static readonly RectangleF Row = new(1000f, 300f, 288f, 54f);

    internal static readonly PointerCase[] All =
    [
        new("1  The rect tightens the tolerance, through the call the plugin actually makes",
            new Vector2(200f, 900f), false, true,
            "the case that was missing. The machine asks for forty pixels on a row fifty-four " +
            "tall, and the bound has to bring that down - which the arithmetic did correctly all " +
            "along, in a method nothing called")
        {
            AcceptsAtMost = 27f,
        },

        new("2  Already on the row, so the cursor stays where it is",
            new Vector2(1100f, 320f), true, false,
            "what the setting is for. The cursor is on the row; moving it to a different pixel of " +
            "the same row achieves nothing, reads worse than staying still, and costs a journey"),

        new("3  Off the row, so it moves however comfortable it is where it is",
            new Vector2(200f, 900f), true, true,
            "staying put is about already being there, not about preferring not to move"),

        new("4  Just inside the edge counts as outside, and it moves",
            new Vector2(1001f, 320f), true, true,
            "one pixel inside the rect. A rect is often drawn a shade larger than the thing it " +
            "stands for, and the aim spread exists to keep off exactly that margin - so staying " +
            "here would trade a small movement for a click on the boundary, which is the trade " +
            "the whole morning went into not making"),

        new("5  With the setting off it moves even when it is already there",
            new Vector2(1100f, 320f), false, true,
            "the escape hatch. Somebody debugging a missed click should be able to take this out " +
            "of the picture without reasoning about when it does and does not apply"),

        new("6  Staying put still presses the modifier",
            new Vector2(1100f, 320f), true, false,
            "the click has to go out as a ctrl+click whether or not there was a journey to press " +
            "the key during. On a currency item the difference between ctrl+right and right is " +
            "the difference between putting it away and picking it up, and \"we were already " +
            "there\" is not a reason to send the wrong one")
        {
            Modifier = true,
        },
    ];

    internal static int Run()
    {
        var failed = 0;

        Console.WriteLine("--- a pointer aimed at a rect ---------------------------------------");
        Console.WriteLine();

        foreach (var c in All)
        {
            var at = c.From;
            var now = TimeSpan.Zero;
            var sent = new List<Vector2>();
            var pressed = new List<System.Windows.Forms.Keys>();

            var pointer = new MousePointer(null, new Random(20260818))
            {
                Cursor = () => at,
                Send = to => { sent.Add(to); at = to; },
                StayPut = c.StayPut,
                TolerancePx = c.Tolerance,
                SpreadPercent = 50f,
                MinTravelMs = 60f,
                TravelMsPerRootPx = 8f,
                ArcPercent = 15f,
                OnModifier = k => pressed.Add(k),
                Clock = () => now,
            };

            var modifiers = c.Modifier
                ? new[] { System.Windows.Forms.Keys.LControlKey }
                : [];

            pointer.Aim(Row, modifiers);

            // Run the journey out, the way the plugin's own tick does - a step and 16ms, over and
            // over, until it says it is there. Two hundred ticks is over three seconds of game
            // time against journeys that take a few hundred milliseconds.
            for (var i = 0; i < 200 && !pointer.Arrived(); i++)
            {
                pointer.Step();
                now += TimeSpan.FromMilliseconds(16);
            }

            var moved = sent.Count > 0;
            var landed = at;

            var ok = moved == c.WantMoved
                     && MousePointer.Holds(Row, landed)
                     && (c.AcceptsAtMost == null || pointer.Accepts <= c.AcceptsAtMost.Value + 0.01f)
                     && (!c.Modifier || pressed.Count == 1)
                     && pointer.Arrived();

            if (!ok)
                failed++;

            var wants = new List<string>
            {
                c.WantMoved ? "the cursor moved" : "the cursor left alone",
                "and on the row either way",
            };

            if (c.AcceptsAtMost != null)
                wants.Add($"a tolerance no higher than {c.AcceptsAtMost:0.##}px");
            if (c.Modifier)
                wants.Add("the modifier pressed");

            Console.WriteLine($"{(ok ? "  ok  " : "FAIL  ")}{c.Name}");
            Console.WriteLine($"        given the cursor at ({c.From.X:0},{c.From.Y:0}), a " +
                              $"{Row.Width:0}x{Row.Height:0} row at ({Row.Left:0},{Row.Top:0}), " +
                              $"stay put {(c.StayPut ? "on" : "off")}");
            Console.WriteLine($"        want  {string.Join(", ", wants)}");
            Console.WriteLine($"        got   {(moved ? $"{sent.Count} steps to ({landed.X:0},{landed.Y:0})" : "no movement")}, " +
                              $"tolerance {pointer.Accepts:0.##}px, " +
                              $"{pressed.Count} modifier(s) pressed, " +
                              $"{(MousePointer.Holds(Row, landed) ? "on the row" : "OFF THE ROW")}");
            Console.WriteLine($"        why   {c.Reason}");
            Console.WriteLine();
        }

        Console.WriteLine($"{All.Length - failed}/{All.Length} match");
        return failed;
    }
}
