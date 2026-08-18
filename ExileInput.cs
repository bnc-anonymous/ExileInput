using ExileCore;
using ExileCore.Shared;
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput;

/// <summary>
/// One owner for the mouse and keyboard, shared by every plugin that wants them.
///
/// Nothing here is referenced by its consumers. The whole surface goes over PluginBridge in types
/// both sides already have - RectangleF, Vector2, Keys, bool, float - so there is no assembly to
/// share, no DLL to drop in the HUD root, and no version to keep in step. A consumer copies
/// Client/ExileInputClient.cs into its own source and that is the entire integration.
///
/// That is not merely tidier than passing an interface across. An interface means both sides must
/// load the same assembly, which means a root DLL, which means the two can disagree about its
/// version and fail at a cast rather than at a name. Strings and framework types cannot.
///
/// Every call names its caller, and that name is the claim. A call from anyone who is not the
/// current holder is refused - not queued - because the callers all already cope with a click that
/// did not go through, and none of them cope with one that happened later than they thought.
///
/// If this plugin is not installed, a consumer's GetMethod comes back null and it can drive
/// ExileCore.Input itself as it always did. Nothing here is load-bearing for anyone.
/// </summary>
public class ExileInput : BaseSettingsPlugin<ExileInputSettings>
{
    private readonly Lease _lease = new();
    private readonly Dictionary<string, MousePointer> _pointers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HeldKeys> _keys = new(StringComparer.OrdinalIgnoreCase);

    public override bool Initialise()
    {
        var b = GameController.PluginBridge;

        // Claiming. Take is safe to call every tick rather than once, so a caller need not keep a
        // flag about whether it already holds the cursor.
        b.SaveMethod("ExileInput.Take", (string who) => _lease.Take(who));
        b.SaveMethod("ExileInput.Release", (string who) => Give(who));
        b.SaveMethod("ExileInput.Holder", () => _lease.Holder);

        // Aiming. A rect is what every caller has, since that is what an element reports.
        b.SaveMethod("ExileInput.MoveTo",
            (string who, RectangleF rect, bool ctrl, bool shift) => Move(who, rect, ctrl, shift));
        b.SaveMethod("ExileInput.MoveToPoint",
            (string who, Vector2 client, bool ctrl, bool shift) => MoveTo(who, client, ctrl, shift));
        b.SaveMethod("ExileInput.Arrived", (string who) => Arrived(who));
        b.SaveMethod("ExileInput.Drift", (string who) => Drift(who));
        b.SaveMethod("ExileInput.Aiming", (string who) => Aiming(who));
        b.SaveMethod("ExileInput.Expected", (string who) => Expected(who));
        b.SaveMethod("ExileInput.Forget", (string who) => Forget(who));

        // Per caller, over the shared default. The tolerance that suits a stash tab is not the one
        // that suits an inventory cell, and a single figure would be wrong for one of them - so the
        // setting is the default and a caller that knows better says so.
        b.SaveMethod("ExileInput.SetTolerance", (string who, int px) => SetTolerance(who, px));

        // Pressing.
        b.SaveMethod("ExileInput.Click",
            (string who, bool ctrl, bool shift, bool right) => Click(who, ctrl, shift, right));

        b.SaveMethod("ExileInput.Tap", (string who, Keys key) => Tap(who, key));
        b.SaveMethod("ExileInput.Hold", (string who, Keys key) => Hold(who, key));
        b.SaveMethod("ExileInput.Let", (string who, Keys key) => Let(who, key));
        b.SaveMethod("ExileInput.ReleaseKeys", (string who) => LetGo(who));

        return true;
    }

    /// <summary>The pointer belonging to one caller, made on first use.</summary>
    private MousePointer PointerFor(string who)
    {
        if (!_pointers.TryGetValue(who, out var pointer))
            _pointers[who] = pointer = new MousePointer(GameController);

        pointer.SpreadPercent = Settings.SpreadPercent.Value;
        pointer.MinTravelMs = Settings.MinTravelMs.Value;
        pointer.TravelMsPerRootPx = Settings.TravelMsPerRootPx.Value;
        pointer.ArcPercent = Settings.ArcPercent.Value;
        pointer.OnModifier ??= key => KeysFor(who).Hold(key);
        pointer.TolerancePx = _tolerances.TryGetValue(who, out var px) ? px : Settings.TolerancePx.Value;
        return pointer;
    }

    private HeldKeys KeysFor(string who)
    {
        if (!_keys.TryGetValue(who, out var keys))
            _keys[who] = keys = new HeldKeys();

        return keys;
    }

    private bool Move(string who, RectangleF rect, bool ctrl, bool shift)
    {
        if (!_lease.Held(who))
            return false;

        var pointer = PointerFor(who);
        pointer.Place(pointer.PointIn(rect), Modifiers(ctrl, shift));
        return true;
    }

    private bool MoveTo(string who, Vector2 client, bool ctrl, bool shift)
    {
        if (!_lease.Held(who))
            return false;

        PointerFor(who).Place(client, Modifiers(ctrl, shift));
        return true;
    }

    private static Keys[] Modifiers(bool ctrl, bool shift)
    {
        if (ctrl && shift) return [Keys.LControlKey, Keys.LShiftKey];
        if (ctrl) return [Keys.LControlKey];
        if (shift) return [Keys.LShiftKey];
        return [];
    }

    private bool Arrived(string who) => _lease.Held(who) && PointerFor(who).Arrived();

    private float Drift(string who) => _lease.Held(who) ? PointerFor(who).Drift() : 0f;

    private bool Aiming(string who) => _lease.Held(who) && PointerFor(who).Awaiting;

    private Vector2 Expected(string who) => _lease.Held(who) ? PointerFor(who).Expected : Vector2.Zero;

    private bool Forget(string who)
    {
        if (!_lease.Held(who))
            return false;

        PointerFor(who).Forget();
        return true;
    }

    private bool SetTolerance(string who, int px)
    {
        if (!_lease.Held(who) || px <= 0)
            return false;

        _tolerances[who] = px;
        return true;
    }

    /// <summary>What each caller asked for, since the shared default cannot suit every target.</summary>
    private readonly Dictionary<string, int> _tolerances = new(StringComparer.OrdinalIgnoreCase);

    private bool Click(string who, bool ctrl, bool shift, bool right)
    {
        if (!_lease.Held(who))
            return false;

        // Where the approach pressed the modifier, it has had the whole journey to register and is
        // still held - so the press goes straight in and the keys come up after. That is the point
        // of pressing it in flight: the confirmation poll happens while the cursor is moving rather
        // than while it sits on the target.
        if ((ctrl || shift) && PointerFor(who).ModifiersReady)
        {
            if (ctrl && !Buttons.ControlIsDown() && Buttons.Readable == true)
                return false;

            Buttons.Press(right);
            KeysFor(who).ReleaseAll();
            Buttons.Release();
            return true;
        }

        return Buttons.Send(ctrl, right, Settings.ModifierHoldMs.Value, shift);
    }

    private bool Tap(string who, Keys key)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).Tap(key);
        return true;
    }

    private bool Hold(string who, Keys key)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).Hold(key);
        return true;
    }

    private bool Let(string who, Keys key)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).Let(key);
        return true;
    }

    private bool LetGo(string who)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).ReleaseAll();
        return true;
    }

    /// <summary>
    /// Hands the cursor back, letting go of anything that caller was holding first.
    ///
    /// Releasing the claim without releasing the keys would leave a control down owned by nobody,
    /// which is the exact fault the tracker exists to prevent - and the next plugin to take the
    /// cursor would inherit it without any way of knowing.
    /// </summary>
    private bool Give(string who)
    {
        if (!_lease.Held(who))
            return false;

        KeysFor(who).ReleaseAll();
        Buttons.Release();
        _lease.Release(who);
        return true;
    }

    public override Job Tick()
    {
        _lease.Expiry = TimeSpan.FromSeconds(Settings.ClaimExpirySeconds.Value);

        // The journey, advanced for whoever holds the cursor. One place doing this is the reason
        // the plugin exists rather than a library: a passive helper would need every consumer to
        // remember to step its own movement, and the one that forgot would simply never arrive.
        if (_lease.Holder is { } driving)
            PointerFor(driving).Step();

        // A claim that has gone stale is one whose owner is not coming back, so whatever it was
        // holding comes up with it. Asked here rather than inside the lease because releasing keys
        // is not the lease's business, and a lease that reached into the keyboard would be two
        // things at once.
        if (_lease.Holder == null && _held != null)
        {
            KeysFor(_held).ReleaseAll();
            Buttons.Release();
            _held = null;
        }
        else if (_lease.Holder != null)
        {
            _held = _lease.Holder;
        }

        return null;
    }

    /// <summary>Who held the cursor last tick, so an expiry can be noticed and tidied after.</summary>
    private string _held;

    public override void OnUnload()
    {
        foreach (var keys in _keys.Values)
            keys.ReleaseAll();

        HeldKeys.ReleaseEverything();
        _lease.Break();
    }
}
