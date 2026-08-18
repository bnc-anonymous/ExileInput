using ExileCore;
using System.Windows.Forms;

namespace ExileInput;

/// <summary>
/// The button and the modifier keys, as things that can be done and asked about - and nothing else.
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
/// What is *not* here any more is the waiting. This used to own a Send that pressed the modifier,
/// polled up to 200ms for it to read back, held 60ms either side of the click and released - all on
/// the calling thread, which in this HUD is the one thread that draws every plugin. Better than a
/// third of a second of frozen overlay, for one click. The waiting happens across ticks in
/// <see cref="Stroke"/> now, and what is left here does its thing and returns.
/// </summary>
internal static class Buttons
{
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

    /// <summary>Remembers that the state answered, so a later silence is a real no.</summary>
    public static void Answered() => _readable = true;

    /// <summary>
    /// Remembers that it never answers here, so nothing waits on it again.
    ///
    /// Only called once a whole confirmation window has gone by with no answer, and never over the
    /// top of a yes. Learning it from the first tick that reads nothing would learn "unreadable"
    /// from a key that had simply not arrived yet, and then never ask again.
    /// </summary>
    public static void NeverAnswers() => _readable ??= false;

    /// <summary>
    /// Whether control reads as held, asked both ways it might be visible.
    ///
    /// The Windows async state first, since that is where an injected key lands if it was injected
    /// at all, and the game's own view second. Either answering yes is a yes.
    ///
    /// One look, no waiting. Somebody who wants to wait asks again next tick.
    /// </summary>
    public static bool ControlIsDown()
    {
        try
        {
            if ((GetAsyncKeyState(VkControl) & 0x8000) != 0)
                return true;

            return Input.IsKeyDown(Keys.LControlKey);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Presses the modifiers a gesture asked for, and only those.
    ///
    /// Only those is the change. The old Send pressed control whenever anything at all was wanted,
    /// so a shift-only click would have gone out as ctrl+shift. Nothing ever noticed, because
    /// nothing asks for shift without ctrl - the one caller passes ctrl: true, shift: wanted - but
    /// the pointer's own modifier list has always been right about this, and leaving the two to
    /// disagree is a trap laid for whoever first wants a plain shift-click.
    /// </summary>
    public static void Hold(bool ctrl, bool shift)
    {
        try
        {
            if (shift)
                Input.KeyDown(Keys.LShiftKey);

            if (ctrl)
                Input.KeyDown(Keys.LControlKey);
        }
        catch
        {
            // Nothing useful to do about it here. The confirmation is what decides whether a click
            // goes out, and it refuses on its own if this did not take.
        }
    }

    /// <summary>
    /// Sends a click and nothing else, for a caller already holding whatever it needs held.
    ///
    /// Nothing here releases anything, because the caller decides when the modifier comes up.
    /// </summary>
    public static void Press(bool right)
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

    private const int VkControl = 0x11;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
