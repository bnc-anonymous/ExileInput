using System.Collections.Concurrent;
using System.Windows.Forms;

namespace ExileInput;

/// <summary>What the keyboard hook does with one key event: let it on to Windows, or swallow it, and whether to stop the holder.</summary>
internal readonly record struct KeyVerdict(bool Swallow, bool StopHolder)
{
    public static readonly KeyVerdict Pass = new(false, false);
}

/// <summary>
/// **The player's keys while ExileInput holds a modifier.** A caller holding Ctrl, Shift or Alt - a step plan's Alt over
/// a header, held for seconds; a ctrl run carried across clicks; a click's modifier lead and hold; a loiter's keys -
/// turns every key the player presses into a combination as Windows and the game see it: F4, Currency Exchange Flipper's
/// stop key, became Alt+F4 and closed the game, and Alt+Enter, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc and the game's own
/// Ctrl and Shift combinations are no better. One rule, with no list of shortcuts: while ExileInput holds a modifier
/// (InputOut.ModifierHeld), every key-down from the player's own keyboard is swallowed by KeyWatch's hook, before Windows
/// or the game sees it, and stops the holder with PlayerPressedAKey, naming the key; the stop lets the modifiers go.
///
/// **No key is exempt, modifiers included.** A player's Shift under the plugin's Ctrl is Ctrl+Shift, which switches the
/// keyboard layout and is half of Ctrl+Shift+Esc; Alt+Shift switches it too; the Windows key opens Start. A player who
/// touches the keyboard while the plugin holds a modifier is taking over, as a moved mouse is.
///
/// **No half keystrokes.** A key whose down was swallowed has its repeats and its up swallowed too, until the up, even
/// once the modifiers are let go, so nothing ever sees an up without its down, or a repeat of a press it never saw - a
/// held F4 must not go on as F4 the moment Alt is up. A key already down to Windows when the modifier went down - the
/// player walking on W - is different: Windows and the game saw its down, so its up must pass or the game keeps it held;
/// its repeats under the modifier are still swallowed (a held F4 repeating under Alt is Alt+F4 too). The hook tells the
/// two apart with GetAsyncKeyState, which a low-level hook reads before the event it is called for is applied
/// (alreadyDown); were that ever wrong, the cost is an up let through alone, which does nothing. A swallowed key's up never
/// seen - the secure desktop takes the keyboard - is forgotten after ForgetAfterMs.
///
/// Keys ExileInput sends (own) always pass: the injected flag on Windows input, Boards.IsOwnKey on the boards. With no
/// modifier held, and no swallowed key's up outstanding, every event passes and the hook goes on as before.
/// </summary>
internal sealed class KeyGuard
{
    /// <summary>How long a swallowed key waits for its up before it is forgotten, from its last down or repeat.</summary>
    public const int ForgetAfterMs = 5000;

    /// <summary>Keys whose down was swallowed and whose up has not been seen, by virtual-key code, with when last seen.</summary>
    private readonly ConcurrentDictionary<int, long> _swallowed = new();

    /// <summary>Whether no swallowed key's up is outstanding, so the hook may come off.</summary>
    public bool Empty => _swallowed.IsEmpty;

    /// <summary>
    /// The verdict on one key event, remembering what it must: key, a virtual-key code; down, a press or repeat, else an up;
    /// own, sent by ExileInput; modifierHeld, ExileInput holding Ctrl, Shift or Alt now; alreadyDown, the key down to
    /// Windows before this event (asked only of a down); now, in milliseconds.
    /// </summary>
    public KeyVerdict OnKey(int key, bool down, bool own, bool modifierHeld, bool alreadyDown, long now)
    {
        if (own)
            return KeyVerdict.Pass;

        var swallowedDown = _swallowed.TryGetValue(key, out var seen) && now - seen < ForgetAfterMs;

        if (!down)
        {
            // The up of a swallowed press: swallowed with it. Of anything else, or of one forgotten: on to Windows.
            return _swallowed.TryRemove(key, out _) && swallowedDown ? new KeyVerdict(true, false) : KeyVerdict.Pass;
        }

        if (modifierHeld)
        {
            // Its up is swallowed too, unless Windows saw its down before: then the up must reach the game.
            if (swallowedDown || !alreadyDown)
                _swallowed[key] = now;

            return new KeyVerdict(true, true);
        }

        // The modifiers let go, the player still on a key whose press was swallowed: its repeats are part of that press.
        if (swallowedDown)
        {
            _swallowed[key] = now;
            return new KeyVerdict(true, false);
        }

        _swallowed.TryRemove(key, out _);
        return KeyVerdict.Pass;
    }

    /// <summary>Forgets swallowed keys whose up has not come in ForgetAfterMs. See OnKey.</summary>
    public void ForgetStale(long now)
    {
        foreach (var (key, seen) in _swallowed)
            if (now - seen >= ForgetAfterMs)
                _swallowed.TryRemove(key, out _);
    }

    /// <summary>Forgets every swallowed key.</summary>
    public void Clear() => _swallowed.Clear();

    /// <summary>Whether this is a modifier ExileInput guards for: Shift, Ctrl or Alt, generic, left or right.</summary>
    public static bool IsModifier(Keys key) =>
        (key & Keys.KeyCode) is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.ControlKey or Keys.LControlKey
            or Keys.RControlKey or Keys.Menu or Keys.LMenu or Keys.RMenu;
}
