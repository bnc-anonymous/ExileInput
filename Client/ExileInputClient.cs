using ExileCore;
using System;
using System.Windows.Forms;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput.Client;

/// <summary>
/// How a plugin talks to ExileInput. Copy this file into your own source and change the namespace.
///
/// Not referenced, copied. There is deliberately no assembly to share: everything crossing the
/// bridge is a type both sides already have, so there is no DLL in the HUD root, no version to keep
/// in step, and no cast to fail at load. The cost is this one file living in each consumer, and it
/// is small enough and stable enough to be the better end of that trade.
///
/// Give it a name unique to your plugin. That name is the claim on the cursor - calls made while
/// somebody else holds it return false rather than happening late, which is the same answer your
/// state machine already gets when a click does not go through.
///
/// <code>
/// _input = new ExileInputClient(GameController, "CurrencyExchangeFlipper");
///
/// if (!_input.Take())          // somebody else has it; try again next tick
///     return;
///
/// _input.MoveTo(button.GetClientRectCache);
/// // ... later ticks ...
/// if (_input.Arrived())
///     _input.Click(ctrl: true, right: true);
/// // ... when the machine finishes or gives up ...
/// _input.Release();
/// </code>
///
/// Everything degrades to false when the plugin is not installed, which <see cref="Available"/>
/// reports so a caller can fall back to driving ExileCore.Input itself.
/// </summary>
public sealed class ExileInputClient
{
    private readonly GameController _gc;
    private readonly string _me;

    private Func<string, bool> _take;
    private Func<string, bool> _release;
    private Func<string, RectangleF, bool, bool, bool> _moveTo;
    private Func<string, Vector2, bool, bool, bool> _moveToPoint;
    private Func<string, bool> _arrived;
    private Func<string, float> _drift;
    private Func<string, bool> _aiming;
    private Func<string, bool, bool, bool, bool> _click;
    private Func<string, Keys, bool> _tap;
    private Func<string, Keys, bool> _hold;
    private Func<string, Keys, bool> _let;
    private Func<string, bool> _releaseKeys;
    private Func<string, Vector2> _expected;
    private Func<string, bool> _forget;
    private Func<string, int, bool> _setTolerance;

    private bool _looked;

    public ExileInputClient(GameController gc, string name)
    {
        _gc = gc;
        _me = name;
    }

    /// <summary>Whether the input plugin is installed and answering.</summary>
    public bool Available
    {
        get
        {
            Look();
            return _take != null;
        }
    }

    /// <summary>
    /// Finds the bridge methods, once.
    ///
    /// Once rather than per call, and lazily rather than in a constructor: plugins initialise in an
    /// order nobody controls, so a lookup at construction can run before the input plugin has
    /// registered anything and cache a null for the session.
    /// </summary>
    private void Look()
    {
        if (_looked)
            return;

        _looked = true;

        try
        {
            var b = _gc?.PluginBridge;

            if (b == null)
                return;

            _take = b.GetMethod<Func<string, bool>>("ExileInput.Take");
            _release = b.GetMethod<Func<string, bool>>("ExileInput.Release");
            _moveTo = b.GetMethod<Func<string, RectangleF, bool, bool, bool>>("ExileInput.MoveTo");
            _moveToPoint = b.GetMethod<Func<string, Vector2, bool, bool, bool>>("ExileInput.MoveToPoint");
            _arrived = b.GetMethod<Func<string, bool>>("ExileInput.Arrived");
            _drift = b.GetMethod<Func<string, float>>("ExileInput.Drift");
            _aiming = b.GetMethod<Func<string, bool>>("ExileInput.Aiming");
            _click = b.GetMethod<Func<string, bool, bool, bool, bool>>("ExileInput.Click");
            _tap = b.GetMethod<Func<string, Keys, bool>>("ExileInput.Tap");
            _hold = b.GetMethod<Func<string, Keys, bool>>("ExileInput.Hold");
            _let = b.GetMethod<Func<string, Keys, bool>>("ExileInput.Let");
            _releaseKeys = b.GetMethod<Func<string, bool>>("ExileInput.ReleaseKeys");
            _expected = b.GetMethod<Func<string, Vector2>>("ExileInput.Expected");
            _forget = b.GetMethod<Func<string, bool>>("ExileInput.Forget");
            _setTolerance = b.GetMethod<Func<string, int, bool>>("ExileInput.SetTolerance");
        }
        catch
        {
            // A bridge that will not answer is the same as one that is not there.
        }
    }

    /// <summary>
    /// Claims the cursor. Safe to call every tick; false means somebody else has it.
    /// </summary>
    public bool Take()
    {
        Look();
        return _take?.Invoke(_me) ?? false;
    }

    /// <summary>Hands the cursor back, releasing any keys this plugin was holding.</summary>
    public bool Release()
    {
        Look();
        return _release?.Invoke(_me) ?? false;
    }

    /// <summary>
    /// Sends the cursor to a spread point inside a rect.
    ///
    /// Say here what the click will need held, not at the click. The modifier then goes down
    /// partway through the approach rather than on arrival, which is both what a hand does and the
    /// faster of the two - the key gets the length of the journey to register, instead of the
    /// pointer sitting on the target while it is confirmed.
    /// </summary>
    public bool MoveTo(RectangleF rect, bool ctrl = false, bool shift = false)
    {
        Look();
        return _moveTo?.Invoke(_me, rect, ctrl, shift) ?? false;
    }

    /// <summary>Sends the cursor to a client-space point.</summary>
    public bool MoveTo(Vector2 client, bool ctrl = false, bool shift = false)
    {
        Look();
        return _moveToPoint?.Invoke(_me, client, ctrl, shift) ?? false;
    }

    /// <summary>Whether the cursor has reached where it was sent.</summary>
    public bool Arrived()
    {
        Look();
        return _arrived?.Invoke(_me) ?? false;
    }

    /// <summary>How far the cursor is from where it was sent, in client pixels.</summary>
    public float Drift()
    {
        Look();
        return _drift?.Invoke(_me) ?? 0f;
    }

    /// <summary>Whether a move has been asked for and not yet confirmed.</summary>
    public bool Aiming()
    {
        Look();
        return _aiming?.Invoke(_me) ?? false;
    }

    /// <summary>
    /// Clicks, with modifiers confirmed down first.
    ///
    /// False where the modifier could not be confirmed and the click would have gone bare, which on
    /// a currency item is the difference between putting it away and picking it up. Keep your state
    /// and try again rather than treating it as done.
    /// </summary>
    public bool Click(bool ctrl = false, bool shift = false, bool right = false)
    {
        Look();
        return _click?.Invoke(_me, ctrl, shift, right) ?? false;
    }

    /// <summary>Where the cursor was last sent, in client space.</summary>
    public Vector2 Expected()
    {
        Look();
        return _expected?.Invoke(_me) ?? Vector2.Zero;
    }

    /// <summary>Abandons an outstanding move, for a caller giving up on what it was doing.</summary>
    public bool Forget()
    {
        Look();
        return _forget?.Invoke(_me) ?? false;
    }

    /// <summary>
    /// How near the cursor must get before <see cref="Arrived"/> is true, for this caller.
    ///
    /// Overrides the shared setting, which cannot suit every target: forty pixels is reasonable on
    /// a stash tab and most of a row on a 54 pixel list entry.
    /// </summary>
    public bool SetTolerance(int px)
    {
        Look();
        return _setTolerance?.Invoke(_me, px) ?? false;
    }

    /// <summary>Presses and releases a key.</summary>
    public bool Tap(Keys key)
    {
        Look();
        return _tap?.Invoke(_me, key) ?? false;
    }

    /// <summary>Holds a key down until <see cref="Let"/> or <see cref="ReleaseKeys"/>.</summary>
    public bool Hold(Keys key)
    {
        Look();
        return _hold?.Invoke(_me, key) ?? false;
    }

    /// <summary>Lets go of one key.</summary>
    public bool Let(Keys key)
    {
        Look();
        return _let?.Invoke(_me, key) ?? false;
    }

    /// <summary>Lets go of every key this plugin is holding.</summary>
    public bool ReleaseKeys()
    {
        Look();
        return _releaseKeys?.Invoke(_me) ?? false;
    }
}
