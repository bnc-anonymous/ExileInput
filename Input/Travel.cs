using System;
using System.Diagnostics;
using Vector2 = System.Numerics.Vector2;

namespace ExileInput;

/// <summary>
/// One journey of the cursor, from wherever it was to wherever it is going.
///
/// A teleport is not a fast movement, it is the absence of one - and it is the single most obvious
/// thing about automated input, because a human hand cannot do it. Everything else here is detail
/// on top of that.
///
/// Three things shape the path, and each is answering a different way a straight constant-speed
/// line reads wrong:
///
/// **Duration grows with distance, but slowly.** A hand crossing the screen does not take ten times
/// as long as one crossing a tenth of it - reaching is mostly aiming, and aiming costs about the
/// same either way. The square root is the cheap approximation of that; Fitts would have the log of
/// the distance over the target size, and the difference between the two is far smaller than the
/// jitter around either.
///
/// **The path bows.** A wrist and elbow rotate, so the hand travels an arc; only a mouse dragged
/// along a ruler goes straight. The bow is a fraction of the distance and its direction is chosen
/// per journey, so two moves between the same points do not trace the same line.
///
/// **The speed is not constant.** Movement accelerates away from rest and decelerates into the
/// target, which is what smoothstep does. A linear ramp arrives at full speed and stops dead, which
/// looks worse than a teleport rather than better - it is a thing no physical object does.
/// </summary>
internal sealed class Travel
{
    private readonly Stopwatch _since = Stopwatch.StartNew();
    private readonly Vector2 _from;
    private readonly Vector2 _to;
    private readonly Vector2 _bow;
    private readonly float _ms;

    public Travel(Vector2 from, Vector2 to, float minMs, float msPerRootPx, float arcPercent,
        Random rng)
    {
        _from = from;
        _to = to;

        var line = to - from;
        var distance = line.Length();

        _ms = Math.Max(1f, minMs + (float)Math.Sqrt(distance) * msPerRootPx);

        // Perpendicular to the line, a fraction of its length, either side at random. Scaled by the
        // distance rather than fixed, or a short hop would bow as far as a long sweep and look like
        // a flick rather than a reach.
        if (distance > 1f)
        {
            var perpendicular = new Vector2(-line.Y, line.X) / distance;
            var reach = distance * arcPercent / 100f;

            _bow = perpendicular * reach * (float)(rng.NextDouble() * 2 - 1);
        }
    }

    /// <summary>How long this journey was given, in milliseconds.</summary>
    public float Ms => _ms;

    /// <summary>How far through the journey, from nought to one.</summary>
    public float Progress => ProgressAt((float)_since.Elapsed.TotalMilliseconds);

    public bool Done => Progress >= 1f;

    /// <summary>How far through the journey a given elapsed time is.</summary>
    public float ProgressAt(float ms) => Math.Clamp(ms / _ms, 0f, 1f);

    /// <summary>Where the cursor should be this instant.</summary>
    public Vector2 Now() => At(Progress);

    /// <summary>
    /// Where the cursor should be a given fraction of the way through.
    ///
    /// Split out from Now so the path can be examined without waiting for it. A journey is the one
    /// thing here that cannot be checked by looking at it - it is a few hundred milliseconds of
    /// cursor somewhere off screen - so the alternative to driving it by hand is not testing it.
    /// </summary>
    public Vector2 At(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        // Smoothstep: still at both ends, quickest in the middle.
        var eased = t * t * (3f - 2f * t);

        // A quadratic bezier through a control point off the midpoint, which is the cheapest curve
        // that starts and ends exactly where it must. The bow is doubled because a bezier only
        // reaches half way to its control point.
        var control = (_from + _to) / 2f + _bow * 2f;

        var a = Vector2.Lerp(_from, control, eased);
        var b = Vector2.Lerp(control, _to, eased);

        return Vector2.Lerp(a, b, eased);
    }

    /// <summary>
    /// Where a modifier should go down, as a fraction of the journey.
    ///
    /// Not at the start and not on arrival. Reaching for control before deciding where to click is
    /// not what a hand does, and pressing it on arrival is the tell this exists to remove - it
    /// makes the gesture strictly sequential, one thing at a time, in a way nothing physical is.
    ///
    /// The last third, varied per journey. It also happens to be the reliable choice: the key needs
    /// confirming before the click, and confirming it during the approach costs nothing where
    /// confirming it on arrival costs up to two hundred milliseconds of poll with the pointer
    /// sitting still on the target.
    /// </summary>
    public static float ModifierAt(Random rng) => 0.55f + (float)rng.NextDouble() * 0.3f;
}
