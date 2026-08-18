using System;
using System.Diagnostics;

namespace ExileInput;

/// <summary>
/// Who is allowed to move the mouse right now.
///
/// There is one cursor and there may be several plugins. Two of them driving it at once does not
/// degrade gracefully - it produces a click at the average of two intentions, which lands on
/// neither and is indistinguishable from a misread rect.
///
/// So a caller takes the cursor before using it and gives it back afterwards, and every call names
/// the holder. A call from anyone else is refused rather than queued: the refused caller keeps its
/// own state and tries again next tick, which is what all of them do already when a click does not
/// go through, so nothing new has to be written to handle it.
/// </summary>
public sealed class Lease
{
    private readonly Stopwatch _since = new();
    private string _holder;

    /// <summary>
    /// How long a holder may go without calling anything before the lease is taken off it.
    ///
    /// The reason there is an expiry at all: a plugin that throws mid-gesture, gets disabled, or is
    /// reloaded by the updater never reaches its release. Without a timeout that is the cursor gone
    /// for the session, and the failure looks like every other plugin quietly doing nothing -
    /// which is the worst kind of fault to diagnose, because nothing anywhere reports an error.
    ///
    /// Generous, because it must never expire mid-gesture. A move waits on the game to catch up and
    /// a click waits on a confirmation; five seconds is far longer than either and far shorter than
    /// a session.
    /// </summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Who holds it, or null when it is free.</summary>
    public string Holder => Stale ? null : _holder;

    private bool Stale => _holder != null && _since.Elapsed > Expiry;

    /// <summary>
    /// Claims the cursor, or says no.
    ///
    /// Taking it twice is not an error - a machine that asks on every tick rather than remembering
    /// whether it already holds it is doing something reasonable, and refusing that would only
    /// teach callers to keep a flag this class already keeps for them.
    /// </summary>
    public bool Take(string who)
    {
        if (string.IsNullOrWhiteSpace(who))
            return false;

        if (Holder != null && Holder != who)
            return false;

        _holder = who;
        _since.Restart();
        return true;
    }

    /// <summary>Whether this caller may act, refreshing the lease if so.</summary>
    public bool Held(string who)
    {
        if (string.IsNullOrWhiteSpace(who) || Holder != who)
            return false;

        _since.Restart();
        return true;
    }

    /// <summary>
    /// Gives the cursor back.
    ///
    /// Only the holder may, so a plugin tidying up on shutdown cannot release a lease another one
    /// took in the meantime.
    /// </summary>
    public void Release(string who)
    {
        if (Holder != who)
            return;

        _holder = null;
        _since.Reset();
    }

    /// <summary>Takes it off whoever has it, for a stop key or the plugin unloading.</summary>
    public void Break()
    {
        _holder = null;
        _since.Reset();
    }
}
