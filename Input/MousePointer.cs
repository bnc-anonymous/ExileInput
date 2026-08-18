using ExileCore;
using System;
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

    /// <summary>Where the cursor was last sent, in client space.</summary>
    public Vector2 Expected { get; private set; }

    /// <summary>Whether a move has been asked for and not yet confirmed.</summary>
    public bool Awaiting { get; private set; }

    /// <summary>
    /// A point to aim at inside a rect, spread about its centre.
    ///
    /// Clamped at ninety-five per cent rather than a hundred so that even at the widest setting the
    /// point stays off the boundary, where a rect that is a pixel generous starts landing outside
    /// the thing it describes.
    /// </summary>
    public Vector2 PointIn(RectangleF rect)
    {
        var spread = Math.Clamp(SpreadPercent, 0f, 95f) / 100f;

        return new Vector2(
            rect.X + rect.Width * (0.5f + (float)(_rng.NextDouble() - 0.5) * spread),
            rect.Y + rect.Height * (0.5f + (float)(_rng.NextDouble() - 0.5) * spread));
    }

    /// <summary>Picks a point inside the rect, sends the cursor there, and returns the point.</summary>
    public Vector2 Aim(RectangleF rect)
    {
        var target = PointIn(rect);
        Place(target);
        return target;
    }

    /// <summary>
    /// Sends the cursor to a client-space point and records that it is on its way.
    ///
    /// The window origin is added here and nowhere else. A caller that has already added it is
    /// aiming at a point twice as far from the top left as it meant to, which on a 4K monitor is
    /// off the screen entirely.
    /// </summary>
    public void Place(Vector2 client)
    {
        var origin = _gc.Window.GetWindowRectangle().TopLeft;

        Input.SetCursorPos(new Vector2(origin.X + client.X, origin.Y + client.Y));

        Expected = client;
        Awaiting = true;
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

        if (Drift() > TolerancePx)
            return false;

        Awaiting = false;
        return true;
    }

    /// <summary>Forgets any outstanding move, for a machine giving up on what it was doing.</summary>
    public void Forget() => Awaiting = false;
}
