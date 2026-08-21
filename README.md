# ExileInput

One owner for the mouse and keyboard, shared by every plugin that wants them.

Install it with **PluginUpdater** — paste this repository's URL into its Add tab. The csproj sits at
the repo root, which is where ExileAPI looks once the clone lands in `Plugins/Source`.

## Integrating

There is nothing to reference. Copy **`Client/ExileInputClient.cs`** into your own source, change
the namespace, and give it a name unique to your plugin:

```csharp
_input = new ExileInputClient(GameController, "MyPlugin");

if (!_input.Take())              // somebody else has the cursor; try again next tick
    return;

_input.MoveTo(button.GetClientRectCache);
// ... on a later tick, once the game has caught up ...
if (_input.Arrived())
    _input.Click(ctrl: true, right: true);
// ... when your machine finishes or gives up ...
_input.Release();
```

## Modifiers

`ctrl` and `shift` have bool parameters because they are what most callers want. Anything else -
alt, or a combination the two bools cannot say - goes as a set of keys:

```csharp
// Shift keeps a currency on the cursor between applications; alt on top of it changes
// which currency is applied. Two bools could not express that at all.
_input.Click([Keys.LShiftKey, Keys.LMenu]);

_input.MoveTo(item.GetClientRectCache, [Keys.LShiftKey, Keys.LMenu]);
```

Every requested modifier is confirmed down before the button is pressed, not just control. A
modifier that silently failed to take does not produce a visible error — it produces a different
action on an expensive item.

The bool forms are not deprecated and are not going anywhere. They call the same code.

## Why nothing is shared but a file

Everything crossing the bridge is a type both sides already have — `RectangleF`, `Vector2`, `Keys`,
`bool`, `float`. So there is no DLL in the HUD root, no version to keep in step, and no cast to fail
at load. An interface would have meant all three.

If this plugin is not installed, `Available` is false and every call returns false, so a consumer can
fall back to driving `ExileCore.Input` itself. Nothing here is load-bearing for anyone.

## One at a time

There is one cursor and there may be several plugins. Two driving it at once does not degrade
gracefully — it produces a click at the average of two intentions, which lands on neither and looks
exactly like a misread rect.

So every call names its caller, and that name is the claim. Calls from anyone else are refused
rather than queued: callers already cope with a click that did not go through, and none of them cope
with one that happened later than they thought.

**The claim expires.** A plugin that throws mid-gesture, gets disabled, or is reloaded never reaches
its release — and without an expiry that is the cursor gone for the session, with no error anywhere,
just every other plugin quietly doing nothing. Five seconds by default: far longer than a move or a
click, far shorter than a session.

## What it knows that a fresh implementation would not

- **A ctrl+right click that loses its modifier does not fail quietly.** On a Chaos Orb it *uses* the
  orb, putting it on the cursor where it blocks every click after it. So the key state is read before
  pressing, with a timed hold as the fallback where the state cannot be read at all.
- **`SetCursorPos` takes screen coordinates; elements report client ones.** Getting that wrong is
  invisible in fullscreen and wrong by the window offset everywhere else. The translation happens in
  one place here.
- **A click sent in the same breath as the move lands where the cursor used to be.** The game reports
  its position a frame or two late, so arrival is confirmed before pressing.
- **A key held and forgotten costs more than a failed action.** Control left down turns the player's
  next click into a ctrl+click, which in a stash moves items. Every key is tracked and released
  together.

## Status

Consolidation. Movement is still a single `SetCursorPos` — the cursor arrives instantly and the
caller waits for the game to agree. Interpolated travel, with modifiers held across the approach
rather than pressed on arrival, is the next piece.
