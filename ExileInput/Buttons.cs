using ExileCore;
using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace ExileInput;

/// <summary>
/// One place where a modified click is sent, because there were three and they disagreed.
///
/// Lifted from the currency flipper, where the reasoning below was paid for. The shuttle paused
/// between pressing control and clicking; the collector and the roller sent both in the same breath.
/// Only the two without the pause ever turned a ctrl+right click into a bare right-click - which on
/// a Chaos Orb does not fail quietly, it uses the orb: off the stack and onto the cursor, where it
/// blocks every click after it.
///
/// A pause was the first answer, and reading the key state is the better one where it can be read:
/// control being down is a fact rather than something to wait out. So it is asked, and a click that
/// would go bare is not sent - a click not sent costs an attempt, where a click sent bare costs an
/// orb on the cursor and a run untangled by hand.
///
/// Where it cannot be read the pause is what is left, and that is a fallback rather than a failure.
/// An injected key does not always show up in any state we can see, and refusing on that reading is
/// not caution - it stopped every ctrl+click in the plugin, four times in a row, until the exchange
/// gave up on being opened. A check that never answers is not evidence of anything, so it is asked
/// once, believed if it ever answers, and dropped if it never does.
///
/// Note what this costs: <see cref="Send"/> blocks its caller for the confirmation poll and the
/// holds either side of the press, which on a bad reading is the better part of half a second on
/// the render thread. That is the price of doing the whole gesture at the destination. Holding the
/// modifier across the approach instead removes it, and is what <see cref="Pointer"/> exists to
/// make possible.
/// </summary>
public static class Buttons
{
    /// <summary>How long to wait for the game to agree the modifier is held.</summary>
    private const int ConfirmMs = 200;

    /// <summary>How often to ask while waiting.</summary>
    private const int PollMs = 5;

    /// <summary>
    /// Sends a click, holding control across it when asked, and says whether it went.
    ///
    /// False only where the key state is readable here and says control is not down - a real no,
    /// and the click would have been the wrong one. The caller keeps its state and tries again
    /// rather than treating a gesture that did not happen as one that did.
    /// </summary>
    public static bool Send(bool ctrl, bool right, int holdMs, bool shift = false)
    {
        var hold = Math.Clamp(holdMs, 0, 200);

        if (!ctrl && !shift)
        {
            Click(right);
            return true;
        }

        try
        {
            if (shift)
                Input.KeyDown(Keys.LShiftKey);

            Input.KeyDown(Keys.LControlKey);

            var seen = Confirmed();

            if (seen)
            {
                _readable = true;
            }
            else if (_readable == true)
            {
                // It has answered before, so this is a real no rather than a check that does not
                // work here. Nothing is clicked, and the caller keeps the click for another go.
                return false;
            }
            else
            {
                // Never once answered. Which means the state cannot be read from here rather than
                // that control is not down - and refusing on that reading is how a check meant to
                // prevent one bad click prevented every good one instead.
                _readable = false;
            }

            // Given a moment either way: the game noticing the key and the game acting on a click
            // that arrives with it are not the same instant.
            if (hold > 0)
                Thread.Sleep(hold);

            Click(right);

            if (hold > 0)
                Thread.Sleep(hold);

            return true;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// Sends a click and nothing else, for a caller already holding whatever it needs held.
    ///
    /// The half of <see cref="Send"/> that is just the press. A caller that pressed its modifier
    /// during the approach has already had the whole approach to confirm it, so there is nothing
    /// left here to wait for - and nothing here releases anything, because the caller decides when
    /// the modifier comes up.
    /// </summary>
    public static void Press(bool right) => Click(right);

    /// <summary>
    /// Whether the key state can be read at all from here.
    ///
    /// Null until something has been tried. Injected keys do not always show up in the state the
    /// game exposes, and where they never do the confirmation is not evidence of anything - so it
    /// is asked once, believed if it ever answers, and quietly dropped if it never does.
    /// </summary>
    private static bool? _readable;

    /// <summary>Whether the key state has ever been readable, for saying so on a panel.</summary>
    public static bool? Readable => _readable;

    /// <summary>
    /// Whether a modifier reads as held, asked both ways it might be visible.
    ///
    /// Public because the point of holding a modifier across an approach is to have somewhere to do
    /// this that is not the moment of the click.
    /// </summary>
    public static bool ControlIsDown() => Confirmed();

    /// <summary>
    /// Lets go of control, whatever happened.
    ///
    /// Worth calling on any path out. A control left down turns every click the player makes
    /// afterwards into a ctrl+click, which in a stash or an inventory moves things.
    /// </summary>
    public static void Release()
    {
        try
        {
            Input.KeyUp(Keys.LControlKey);

            // Shift as well, unconditionally. A shift left down turns a click into a whole-stack
            // move in a stash and into a link in chat, and the whole point of this method is that
            // it does not have to know what was pressed to be worth calling.
            Input.KeyUp(Keys.LShiftKey);
        }
        catch
        {
            // Releasing something never held is harmless; failing to try is not.
        }
    }

    /// <summary>
    /// Whether control reads as held, asked both ways it might be visible.
    ///
    /// The Windows async state first, since that is where an injected key lands if it was injected
    /// at all, and the game's own view second. Either answering yes is a yes.
    ///
    /// Waits only briefly, and only while there is a chance of an answer: once it is known that
    /// neither can see it, the poll is pointless and the wait is pure delay on every click.
    /// </summary>
    private static bool Confirmed()
    {
        var until = _readable == false ? 0 : ConfirmMs;
        var since = Stopwatch.StartNew();

        do
        {
            try
            {
                if ((GetAsyncKeyState(VkControl) & 0x8000) != 0)
                    return true;

                if (Input.IsKeyDown(Keys.LControlKey))
                    return true;
            }
            catch
            {
                return false;
            }

            if (until == 0)
                break;

            Thread.Sleep(PollMs);
        }
        while (since.ElapsedMilliseconds < until);

        return false;
    }

    private const int VkControl = 0x11;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static void Click(bool right)
    {
        if (right)
        {
            Input.RightDown();
            Input.RightUp();
        }
        else
        {
            Input.LeftDown();
            Input.LeftUp();
        }
    }
}
