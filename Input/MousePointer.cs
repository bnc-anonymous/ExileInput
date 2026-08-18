using ExileCore;
using System;
using System.Windows.Forms;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput;

/// <summary>
/// Where the cursor is being sent, and whether it has got there.
///
/// One per state machine rather than a static, because each keeps its own expectation and its own
/// tolerance - forty pixels suits a stash tab, twelve suits an inventory cell, and a shared figure
/// would be wrong for one of them.
///
/// Everything here is in client space, which is what every call site already has: element rects
/// come off GetClientRectCache and the game reports its own cursor as MousePosX/MousePosY. Only the
/// actual move needs screen coordinates, and that translation happens once, here.
///
/// That translation is the reason this exists at all. Of the five machines that moved a cursor,
/// three added the window origin before calling SetCursorPos and two did not - and both then
/// compared against the game's client-space cursor, so the comparisons agreed while the moves did
/// not. The two that skipped it aim correctly only while the window sits at the top left of the
/// screen, which fullscreen guarantees and windowed play does not. Nobody noticed because nobody
/// plays windowed.
/// </summary>
internal sealed class MousePointer
{
    private readonly GameController _gc;
    private readonly Random _rng;

    public MousePointer(GameController gc, Random rng = null)
    {
        _gc = gc;
        _rng = rng ?? new Random();
    }

    /// <summary>
    /// How much of a target to aim within, as a percentage of its size.
    ///
    /// Nought is dead centre every time, which is both conspicuous and brittle - a rect read a
    /// pixel off centre is still hit anywhere inside it. A hundred is the whole rect including its
    /// edges, which will miss where the rect is drawn larger than the thing it represents.
    /// </summary>
    public float SpreadPercent { get; set; } = 60f;

    /// <summary>How near the cursor must be to where it was sent before it counts as arrived.</summary>
    public int TolerancePx { get; set; } = 12;

    /// <summary>The shortest a journey may take, however near the target.</summary>
    public float MinTravelMs { get; set; } = 60f;

    /// <summary>Milliseconds added per square root of a pixel travelled. Nought teleports.</summary>
    public float TravelMsPerRootPx { get; set; } = 8f;

    /// <summary>How far the path bows off the straight line, as a percentage of its length.</summary>
    public float ArcPercent { get; set; } = 15f;

    /// <summary>The journey in progress, or null when the cursor is where it was put.</summary>
    private Travel _travel;

    /// <summary>What to press partway through, and where.</summary>
    private Keys[] _modifiers = [];
    private float _modifierAt;
    private bool _modifiersDown;

    /// <summary>Told when the journey presses its modifier, so somebody can release it later.</summary>
    public Action<Keys> OnModifier { get; set; }

    /// <summary>
    /// Whether the modifiers this journey pressed are still down.
    ///
    /// Must be told when they are let go, or it goes on saying yes to a click that has none - which
    /// is a bare click, and a bare right click on a currency item uses it rather than moving it.
    /// It said yes for the whole session once, because pressing set it and nothing cleared it.
    /// </summary>
    public bool ModifiersReady => _modifiersDown;

    /// <summary>Told that whatever the journey pressed has been released.</summary>
    public void ModifiersLetGo() => _modifiersDown = false;

    /// <summary>Where the cursor was last sent, in client space.</summary>
    public Vector2 Expected { get; private set; }

    /// <summary>Whether a move has been asked for and not yet confirmed.</summary>
    public bool Awaiting { get; private set; }

    /// <summary>How far off the target a landing may be and still count, for the current move.</summary>
    internal float Accepts => Math.Min(TolerancePx, _bound);

    /// <summary>
    /// A point to aim at inside a rect, spread about its centre.
    ///
    /// Clamped at ninety-five per cent rather than a hundred so that even at the widest setting the
    /// point stays off the boundary, where a rect that is a pixel generous starts landing outside
    /// the thing it describes.
    /// </summary>
    public Vector2 PointIn(RectangleF rect) => PointIn(rect, SpreadPercent, _rng);

    /// <summary>The same, without needing a pointer or a game to ask it.</summary>
    internal static Vector2 PointIn(RectangleF rect, float spreadPercent, Random rng)
    {
        var spread = Math.Clamp(spreadPercent, 0f, 95f) / 100f;

        return new Vector2(
            rect.X + rect.Width * (0.5f + (float)(rng.NextDouble() - 0.5) * spread),
            rect.Y + rect.Height * (0.5f + (float)(rng.NextDouble() - 0.5) * spread));
    }

    /// <summary>
    /// How far a point sits from the nearest edge of the rect it was chosen inside.
    ///
    /// The whole of the rect bound, in one place with nothing else in it, because the arithmetic
    /// is the part that can be wrong. Taking the minimum across both axes and not each separately
    /// is the bit worth pinning: a tolerance is a radius, so a wide short row has to bind on its
    /// height or a landing within tolerance sideways is still out of the row vertically.
    /// </summary>
    internal static float BoundFor(RectangleF rect, Vector2 target) =>
        Math.Max(TightestPx, Math.Min(
            Math.Min(target.X - rect.Left, rect.Right - target.X),
            Math.Min(target.Y - rect.Top, rect.Bottom - target.Y)));

    /// <summary>
    /// How near the target must be reached when the target came from a rect - never further than
    /// its own edge.
    ///
    /// A tolerance is a guess about how exactly the game reports its cursor, and it is set per
    /// machine by whoever wrote that machine. Forty pixels was a reasonable guess for a stash tab
    /// and a bad one for a currency exchange row, which is fifty-four pixels tall: a cursor forty
    /// pixels from a point thirteen pixels inside the row is outside the row, and a click there
    /// hits whatever is underneath. That is the click that missed the "I want" box.
    ///
    /// Rather than tuning eight numbers against the smallest thing each machine might click, bound
    /// the tolerance by the rect being aimed at. Then within-tolerance implies inside-the-rect by
    /// construction, and it stays true for whatever gets clicked next.
    ///
    /// float.MaxValue when the caller gave a bare point, because there is no rect to be inside of.
    /// </summary>
    private float _bound = float.MaxValue;

    /// <summary>
    /// The least the rect may tighten the tolerance to.
    ///
    /// A rect a few pixels tall would otherwise demand a landing exact to a pixel, and if the game
    /// ever rounds its readback that is a move that can never arrive - the caller waits out its
    /// timeout and stops, which is a worse outcome than the loose click this is here to prevent.
    /// </summary>
    internal const float TightestPx = 3f;

    /// <summary>Picks a point inside the rect, sends the cursor there, and returns the point.</summary>
    public Vector2 Aim(RectangleF rect)
    {
        var target = PointIn(rect);

        Place(target);

        // After Place, which resets it: the bound belongs to this target, not the last one.
        _bound = BoundFor(rect, target);

        return target;
    }

    /// <summary>
    /// Sends the cursor to a client-space point and records that it is on its way.
    ///
    /// The window origin is added here and nowhere else. A caller that has already added it is
    /// aiming at a point twice as far from the top left as it meant to, which on a 4K monitor is
    /// off the screen entirely.
    /// </summary>
    public void Place(Vector2 client, params Keys[] modifiers)
    {
        Expected = client;
        Awaiting = true;
        _bound = float.MaxValue;

        _modifiers = modifiers ?? [];
        _modifierAt = Travel.ModifierAt(_rng);
        _modifiersDown = false;

        // Nought means teleport, which is what this did before travel existed and is worth keeping
        // - it is the setting to reach for when something is going wrong and the question is
        // whether the path is why.
        if (TravelMsPerRootPx <= 0)
        {
            _travel = null;
            PressModifiers();
            Put(client);
            return;
        }

        _travel = new Travel(Where(), client, MinTravelMs, TravelMsPerRootPx, ArcPercent, _rng);
    }

    /// <summary>
    /// Advances the journey. Called every tick by whoever owns this pointer.
    ///
    /// The modifier goes down partway rather than on arrival, which is the whole point of the
    /// journey existing: it overlaps the press with the approach the way a hand does, and it gives
    /// the key the length of the approach to register rather than making the pointer wait on the
    /// target while it confirms.
    /// </summary>
    public void Step()
    {
        if (_travel == null)
            return;

        if (!_modifiersDown && _travel.Progress >= _modifierAt)
            PressModifiers();

        Put(_travel.Now());

        if (_travel.Done)
            _travel = null;
    }

    private void PressModifiers()
    {
        _modifiersDown = true;

        foreach (var key in _modifiers)
            OnModifier?.Invoke(key);
    }

    /// <summary>Where the cursor is now, in client space.</summary>
    private Vector2 Where()
    {
        try
        {
            return new Vector2(_gc.IngameState.MousePosX, _gc.IngameState.MousePosY);
        }
        catch
        {
            return Expected;
        }
    }

    /// <summary>The one place the window origin is added.</summary>
    private void Put(Vector2 client)
    {
        var origin = _gc.Window.GetWindowRectangle().TopLeft;
        Input.SetCursorPos(new Vector2(origin.X + client.X, origin.Y + client.Y));
    }

    /// <summary>How far the cursor is from where it was sent, in client-space pixels.</summary>
    public float Drift()
    {
        try
        {
            var now = new Vector2(_gc.IngameState.MousePosX, _gc.IngameState.MousePosY);
            return Vector2.Distance(now, Expected);
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>
    /// Whether the cursor has reached where it was sent.
    ///
    /// Clears the waiting flag once it has, so a caller can ask on every tick and act on the first
    /// tick that answers yes. Asking before the game has caught up compares against where the
    /// cursor used to be - which is why a click sent in the same breath as the move lands wherever
    /// the pointer was resting, and why every first attempt used to fail and every retry work.
    /// </summary>
    public bool Arrived()
    {
        if (!Awaiting)
            return true;

        // Still travelling is not "did not arrive". The drift is large on purpose for as long as
        // the journey is running, and a caller asking every tick would otherwise read its own
        // movement as a failure to move.
        if (_travel != null)
            return false;

        if (Drift() > Accepts)
            return false;

        Awaiting = false;
        return true;
    }

    /// <summary>Forgets any outstanding move, for a machine giving up on what it was doing.</summary>
    public void Forget()
    {
        Awaiting = false;
        _travel = null;
        _modifiersDown = false;
    }
}
