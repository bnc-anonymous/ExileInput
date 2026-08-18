using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace ExileInput;

public class ExileInputSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Aim Within (%)",
        "How much of a target to aim inside, as a percentage of its size.\n\n" +
        "Nought is dead centre every time, which is both conspicuous and brittle - a rect read a " +
        "pixel off centre is still hit anywhere inside it. High values reach the edges, where a " +
        "rect drawn slightly larger than the thing it represents starts missing.\n\n" +
        "One figure for every plugin that asks, which is the point of this living here.")]
    public RangeNode<int> SpreadPercent { get; set; } = new RangeNode<int>(50, 0, 95);

    [Menu("Arrived Within (px)",
        "How near the cursor must be to where it was sent before a click is allowed.\n\n" +
        "Not zero: the game reports its cursor a frame or two behind, and rounding means the pixel " +
        "you set is not always the pixel read back. Too large and a click is accepted while the " +
        "pointer is still a row away from the thing it is aimed at - on a 54 pixel list row, forty " +
        "is most of a row.")]
    public RangeNode<int> TolerancePx { get; set; } = new RangeNode<int>(12, 1, 100);

    [Menu("Give Up Waiting After (ms)",
        "How long the cursor may fail to arrive before the caller is told it did not.")]
    public RangeNode<int> ArriveTimeoutMs { get; set; } = new RangeNode<int>(1500, 100, 10000);

    [Menu("Hold A Modifier For (ms)",
        "How long control or shift is held either side of a click.\n\n" +
        "Small and not optional. On a currency item the difference between ctrl+right and right is " +
        "the difference between putting it away and picking it up.")]
    public RangeNode<int> ModifierHoldMs { get; set; } = new RangeNode<int>(60, 0, 200);

    [Menu("Drop An Idle Claim After (s)",
        "How long a plugin may hold the cursor without using it before the claim is taken off it.\n\n" +
        "There is one cursor and there may be several plugins, so one takes it at a time. A plugin " +
        "that throws mid-gesture or is reloaded never reaches its release, and without this that " +
        "is the cursor gone for the session - with no error anywhere, just every other plugin " +
        "quietly doing nothing.\n\n" +
        "Long enough never to expire mid-gesture: a move waits on the game and a click waits on a " +
        "confirmation, and both are far under a second.")]
    public RangeNode<int> ClaimExpirySeconds { get; set; } = new RangeNode<int>(5, 1, 60);
}
