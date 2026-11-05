using ExileCore;
using System.Windows.Forms;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput;

/// <summary>
/// Where every key, button and cursor move this plugin sends goes out: to the hardware-input boards while they are in use
/// (see Boards), and through ExileCore.Input and PhysicalKeys otherwise, as before. One place, so a key cannot go down
/// one way and come up the other.
/// </summary>
internal static class InputOut
{
    /// <summary>
    /// The modifiers this plugin holds down, whoever asked - a caller's held keys, a step plan's key events, a gesture's
    /// modifier lead and hold, a loiter's keys - as one bit per key: Shift, Ctrl and Alt, generic, left and right. Set
    /// before the press goes out and cleared after the let-go, so KeyWatch's hook swallows the player's keys for the whole
    /// time Windows sees one of this plugin's modifiers down. See KeyGuard.
    /// </summary>
    private static int _modifiersHeld;

    /// <summary>Whether this plugin holds Ctrl, Shift or Alt down now. See KeyGuard.</summary>
    public static bool ModifierHeld => System.Threading.Volatile.Read(ref _modifiersHeld) != 0;

    private static int ModifierBit(Keys key) => (key & Keys.KeyCode) switch
    {
        Keys.ShiftKey => 1 << 0,
        Keys.LShiftKey => 1 << 1,
        Keys.RShiftKey => 1 << 2,
        Keys.ControlKey => 1 << 3,
        Keys.LControlKey => 1 << 4,
        Keys.RControlKey => 1 << 5,
        Keys.Menu => 1 << 6,
        Keys.LMenu => 1 << 7,
        Keys.RMenu => 1 << 8,
        _ => 0,
    };

    /// <summary>
    /// Whether this plugin holds this modifier, or the same one on either side or generic: Windows reads it down for this
    /// plugin, not for the player, so a player's press of it is a fresh press. See KeyWatch.GuardSwallows.
    /// </summary>
    public static bool HoldsSameModifier(Keys key)
    {
        var family = (key & Keys.KeyCode) switch
        {
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => 0b111,
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => 0b111 << 3,
            Keys.Menu or Keys.LMenu or Keys.RMenu => 0b111 << 6,
            _ => 0,
        };

        return (System.Threading.Volatile.Read(ref _modifiersHeld) & family) != 0;
    }

    private static void MarkModifier(int bit, bool down)
    {
        if (bit == 0)
            return;

        int was, now;

        do
        {
            was = System.Threading.Volatile.Read(ref _modifiersHeld);
            now = down ? was | bit : was & ~bit;
        } while (System.Threading.Interlocked.CompareExchange(ref _modifiersHeld, now, was) != was);
    }

    /// <summary>
    /// Whether a press of this key may go out: any key but a modifier, and a modifier only with KeyWatch's hook in,
    /// swallowing the player's keys (see KeyGuard). A modifier the hook could not be put in for is not pressed: Alt down
    /// with the player's keys reaching Windows is how F4 became Alt+F4 and closed the game.
    /// </summary>
    private static bool MayPress(Keys key)
    {
        var bit = ModifierBit(key);

        if (bit == 0)
            return true;

        // Marked first, so the hook's thread, starting or about to come off, sees it and stays.
        MarkModifier(bit, true);

        if (KeyWatch.Guard())
            return true;

        MarkModifier(bit, false);
        return false;
    }

    /// <summary>Presses a key the way ExileCore.Input does, or on the keyboard board.</summary>
    public static void KeyDown(Keys key)
    {
        if (!MayPress(key))
            return;

        if (!Boards.InUse || !Boards.Key(key, true))
            Input.KeyDown(key);
    }

    /// <summary>Lets go of a key the way ExileCore.Input does, or on the keyboard board.</summary>
    public static void KeyUp(Keys key)
    {
        try
        {
            if (!Boards.InUse || !Boards.Key(key, false))
                Input.KeyUp(key);
        }
        finally
        {
            MarkModifier(ModifierBit(key), false);
        }
    }

    /// <summary>Presses a key with its own scan code (see PhysicalKeys), or on the keyboard board, where every key is a keyboard's.</summary>
    public static void PhysicalDown(int key)
    {
        if (!MayPress((Keys)key))
            return;

        if (!Boards.InUse || !Boards.Key((Keys)key, true))
            PhysicalKeys.Down(key);
    }

    /// <summary>Lets go of a key with its own scan code (see PhysicalKeys), or on the keyboard board.</summary>
    public static void PhysicalUp(int key)
    {
        try
        {
            if (!Boards.InUse || !Boards.Key((Keys)key, false))
                PhysicalKeys.Up(key);
        }
        finally
        {
            MarkModifier(ModifierBit((Keys)key), false);
        }
    }

    /// <summary>
    /// Lets go of every modifier - Shift, Ctrl and Alt, generic, left and right - whoever pressed it. See
    /// ExileInput.StopForSwallowedKeys.
    /// </summary>
    public static void LetGoOfModifiers()
    {
        foreach (var key in new[]
                 {
                     Keys.LMenu, Keys.RMenu, Keys.Menu,
                     Keys.LControlKey, Keys.RControlKey, Keys.ControlKey,
                     Keys.LShiftKey, Keys.RShiftKey, Keys.ShiftKey,
                 })
        {
            try
            {
                KeyUp(key);
            }
            catch
            {
                // Best effort, one key at a time; the mark is cleared either way.
            }
        }
    }

    /// <summary>Forgets every modifier as held, for the boards letting go of everything at once. See HeldKeys.ReleaseEverything.</summary>
    public static void ForgetModifiers() => System.Threading.Interlocked.Exchange(ref _modifiersHeld, 0);

    /// <summary>Presses or lets go of the left or right mouse button.</summary>
    public static void Button(bool right, bool down)
    {
        if (Boards.InUse)
            Boards.Button(right, down);
        else if (right && down)
            Input.RightDown();
        else if (right)
            Input.RightUp();
        else if (down)
            Input.LeftDown();
        else
            Input.LeftUp();
    }

    /// <summary>Sends the cursor to a screen point: at once, or steered there by the mouse board.</summary>
    public static void CursorTo(Vector2 screen)
    {
        if (Boards.InUse)
            Boards.CursorTo((int)screen.X, (int)screen.Y);
        else
            Input.SetCursorPos(screen);
    }
}
