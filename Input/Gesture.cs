using System;

namespace ExileInput;

/// <summary>
/// Which way a click goes out, once the cursor has arrived.
///
/// One line of decision, given its own file because it is the line that produced the worst bug in
/// this plugin so far and the only way to check it is to state it apart from the pressing.
/// </summary>
internal static class Gesture
{
    /// <summary>
    /// Whether the button may go straight down on a modifier the approach already pressed.
    ///
    /// The whole point of pressing a modifier in flight is that by the time the cursor lands there
    /// is nothing left to wait for. But "already pressed" has to mean the key is down *now*, and
    /// the first version of this meant two weaker things instead.
    ///
    /// It asked the pointer whether it had pressed anything, which stayed true after the keys were
    /// let go - so a second click with no move in between took the fast path with nothing held at
    /// all. And it guarded that with a readback that is null until the slow path has run once,
    /// which on a machine that takes the fast path from the start never happens. Two conditions,
    /// neither of which could fail when it mattered.
    ///
    /// The result was a bare left click on Faustus. On a currency item the difference between
    /// ctrl+right and right is the difference between putting it away and picking it up, so this
    /// fails closed: anything short of "the key is down and we can see it" goes the long way, which
    /// confirms or refuses rather than assuming.
    /// </summary>
    /// <param name="controlIsDown">
    /// What the keyboard actually reports, asked for rather than handed over.
    ///
    /// A function and not a bool because the answer is expensive: it polls the key for up to a
    /// tenth of a second before giving up. Passing it in already evaluated reads it on every click
    /// including the plain ones, which do not care what control is doing and would each sit through
    /// that poll for an answer nothing looks at.
    ///
    /// So the order of the conditions is not just tidiness, and the test for it is not pedantry.
    /// Only consulted when control is actually wanted - shift has no readback here, so a shift
    /// gesture rests on the pointer's own account of itself.
    /// </param>
    public static bool StraightIn(bool ctrl, bool shift, bool modifiersReady, Func<bool> controlIsDown) =>
        (ctrl || shift) && modifiersReady && (!ctrl || controlIsDown());
}
