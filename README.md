# ExileInput

One owner for the mouse and keyboard, shared by every plugin that wants them. For Path of Exile 1
and ExileApi; the Path of Exile 2 version is [ExileInput2](https://github.com/bnc-anonymous/ExileInput2).

Install it with **PluginUpdater** — paste this repository's URL into its Add tab. The csproj sits at
the repo root, which is where ExileApi looks once the clone lands in `Plugins/Source`.

## Integrating

There is nothing to reference. Copy **`Client/ExileInputClient.cs`** into your own source, change
the namespace, and give it a name unique to your plugin:

```csharp
_input = new ExileInputClient(GameController, "MyPlugin");

if (_input.WasStopped())         // the player took the mouse back; stop what you were doing
{
    _input.Release();            // and say so, or Take goes on refusing
    return;
}

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

## Taking the mouse back

**Moving the mouse is the stop.** The cursor turning up somewhere this plugin never sent it means a
hand moved it, and everything halts: the journey, the gesture, any key being held. The plugin that
was driving loses the claim and `Take` refuses it until it calls `Release`, so a hand coming to rest
does not hand the cursor straight back.

There is no panic key and there is not meant to be one. Reaching for the mouse is what anyone
already does when something on screen is doing something they did not ask for; a key they have to
remember is a key they will not reach for in the moment they need it.

The same stop covers the game losing focus, the game taking typed input, and a cursor that will not
go where it is sent. `Stopped()` says which — check it on every tick of a sequence. A caller that
ignores it stops working rather than fighting the player for the cursor, which is the safe way round
for it to be wrong, but it stops silently.

### Keys pressed while a modifier is held

While ExileInput holds **Ctrl, Shift or Alt** for any caller - a step plan's key events, a click's modifier lead and
hold, a ctrl run carried across clicks, a loiter's keys, `Hold` - every key the player presses on their own keyboard is
**swallowed**: a low-level keyboard hook drops it, and its release, before Windows or the game sees either. Otherwise
the player's F4 under the plugin's Alt is Alt+F4 and the game closes; Alt+Enter, Alt+Tab, Ctrl+Esc, Ctrl+Shift+Esc and
the game's own Ctrl and Shift combinations are no better. One rule, no list of shortcuts, and no key exempt - modifiers
included, as Ctrl+Shift and Alt+Shift switch the keyboard layout.

The holder is stopped with `PlayerPressedAKey`, whether or not it asked for `StopOnPlayerKeys`, the key is named in
`StopDetail()` ("the player pressed F4 while ExileInput held a modifier; ..."), `SwallowedKey()` returns it, and every
modifier is let go at once. A swallowed key never reaches a hotkey check, so a plugin whose stop key it was compares
`SwallowedKey()` with its key and treats a match as that key pressed:

```csharp
if (_input.Stopped() == "PlayerPressedAKey" && _input.SwallowedKey() == stopKey)
    StopEverything();
```

Keys ExileInput sends always pass: on Windows input they carry the injected flag; with hardware input the keyboard
board's keys reach Windows as a keyboard's, and the hook tells them from the player's by what the board holds for
ExileInput. A key the player was already holding when the modifier went down keeps its release, so the game does not
think it held; its repeats are swallowed. A modifier is not pressed at all if the hook cannot be put in. Callers that
never hold a modifier see no change. Bridge version 4.

All of this is a guess about lag away from being useless, so it is under test: `Tests/StopCases.cs`
is the table, and case 2 is the one that matters. The game reports its cursor several frames behind
where it was put, so mid-journey the reported position and the last sent position are never equal -
and a check written as "is the cursor where I last put it" stops on the first step of every journey
it ever makes and blames the player for it.

## Hardware input

**Use Hardware Input** (under Advanced) sends every key, click and cursor move through the
[hardware-input](../hardware-input) passthrough boards, which sit between the real keyboard and mouse
and the PC, so the game sees only the real devices. Nothing changes for a consumer.

- The plugin builds against `hardware-input/src/HardwareInput` when that repository sits beside this
  one, or in the HUD's own folder (beside `Plugins`, never inside `Plugins\Source`: the HUD compiles
  every project it finds there, and meeting `HardwareInput.csproj` twice fails its whole build), or
  wherever `HardwareInputProject` points. Without it the plugin still builds, and the box says
  hardware input is not available.
- Ticking it with no board's Debug Probe plugged in leaves it unticked, with a warning. If the boards
  fail to open, or are lost later, it unticks itself and says why, in the menu and the log.
- The mouse board's own word that the player moved or clicked stops the caller, as a moved mouse
  does. Each board stops only for its own device, so walking on WASD does not cut a click short.
- The keyboard board merges the player's keys with injected ones rather than refusing injection
  while a real key is held, so a flask still goes out while walking on W. Keys stop a caller only
  when it asks, as before.
- **Reroute Other Plugin Inputs** (under Advanced) sends the keys, clicks, wheel and cursor moves of the HUD's other
  plugins - ReAgent's flask presses, a plugin using `ExileCore.Input` - on the boards instead, while
  the game is in front. It patches the two system calls every way of sending ends in,
  `win32u!NtUserSendInput` and `NtUserSetCursorPos`, in the HUD's process only, so nothing is missed
  whichever wrapper a plugin uses. Input goes to the boards on the plugin's own thread, at once:
  nothing waits on ExileInput's claims or blocks. Typed text and keys the board cannot send go the
  old way. The patch is refused, with a warning, if the stubs are not the shape expected.
- On boards older than firmware 0.3, a key or button let go within 20 ms of being pressed is held
  until then: those boards send what is held in each USB report, and a press shorter than one never
  reached the game. From 0.3 the boards report every press and release themselves, and nothing is
  held back.
- Key and mouse blocks become the boards' locks. The block's cancel key ends one. The keys still held
  when it ends come back from the boards' resume list: letters, digits, Ctrl, Shift, Alt and Space.
- Mouse 4 and Mouse 5 never count as the player taking over: they are hotkeys (AutoExpedition's action
  key is Mouse 4). Moving the mouse, the wheel, and the left and right buttons do.
- The board moves the cursor by counts, not to a point, so each point is steered onto from the
  Windows cursor. Paths, timing, and the follow and arrival checks are still this plugin's.

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

## Step plans

`PlanSteps` (bridge version 3) hands ExileInput a list of **stops** to run itself: a journey to each, and keys and
buttons going down and up at moments tied to it - before arrival (with a lead), on arrival, after the button comes up,
or on leaving for the next stop. A key that goes down stays down until an Up for it, so a ctrl+click run is one ctrl
down and one up, and Alt held over a header is Alt down on arrival and up on leaving. A stop can wait for the caller:
a **Gate** before its arrival events (check what is under the cursor), a **Hold** after them (keys held, until a price
loads); `Proceed()` moves it on, `StepWhere()` says where it is, `AbandonSteps()` ends it. Anything still held when the
plan ends, is dropped or the cursor is given back is let go, after any button is up. Drawn like a click plan, with each
event where it happens, a line in the held colour for a key held, and a key held at the stop the plan is at drawn there
in the held colour (green by default) until it comes up; a Gate and a Hold draw nothing of their own.

Several keys at one anchor go out **in the order given, paced like typing**: a Key Hold between a key's Down and its
Up, a Key Gap between one key's Up and the next one's Down (both settings, drawn afresh each time like Click Hold), and
Modifier Timing after ctrl goes down before the first key it holds and before it comes up after the last. A stop with
neither a point nor a rectangle (`PlannedStop.Here`, a NaN point) does not move the cursor at all, so keyboard input
can be planned and drawn as steps; `PlannedStop.Typed` writes out a chord:

```csharp
_input.PlanSteps([
    PlannedStop.At(searchBox, new StepEvent(Keys.LButton, true, StepAnchor.OnArrival)),
    PlannedStop.Here([.. PlannedStop.Typed([Keys.LControlKey], Keys.A), .. PlannedStop.Typed([Keys.LControlKey], Keys.V)]),
], show: true);
```

The click calls are unchanged. A caller has one plan at a time: a step plan replaces a click plan and the other way
round, and a move, drag or loiter of the caller's own drops its step plan. See `docs/plan-steps.md`.

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

- **A cursor that stops following looks exactly like a cursor somebody is holding.** From in here
  they are the same situation, and in both of them the right thing to do is stop rather than carry
  on clicking.

## Tests

`Tests/` is a console runner rather than a test framework, because the useful output is the whole
table side by side and a framework reports the first failure and hides the rest.

```bash
export exapiPackage="<your ExileApi folder>"
dotnet run --project Tests/Tests.csproj
```

What it covers is the reasoning rather than the input: how far a landing may be off, who is allowed
to move the mouse, what shape a journey has, and whether a click may go down on a modifier already
held. Every bug this plugin has had was one of those - a stale flag, a guard that could not fire, a
tolerance measured against the wrong thing - and not one was a mistake about `SetCursorPos`.
