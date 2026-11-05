# Planned steps: moves with events

A design for generalising ExileInput's click plan, for ExileInput2 and ExileInput1 alike (the two share the plan, stroke
and pointer code line for line; only graphics types and names differ). Draft for review, 2026-11-05.

## Why

The plan's unit today is a **click**: a journey to a rectangle or point, a button, and modifiers pressed with a lead
before the press. Anything that is not a click lives beside the plan, and each case has needed its own machinery:

| Need | How it is done today |
|---|---|
| Ctrl held across consecutive ctrl+clicks | `CarryModifiers` → `Stroke.HoldingAfterClick` + `HandOver`; `Carried` decided at plan time, fixed up at run time when the carry fails (then ctrl is pressed with no C drawn) |
| A loiter holding ctrl for the click it waits on | `_loiterKeys`, `EndLoiterKeys`, `Stroke.KeepHeld`, `Buttons.Release(keep)`, cleanup in Tick |
| A loiter along a planned journey | `_loiterOnPlanned`; the final approach after it is unplanned and undrawn |
| Hover and hold a key without clicking (Alt on the market ratio) | Not expressible: done beside the plan, undrawn |
| Where a modifier comes up | Drawn at a fixed offset from the click, not where or when it happens |

Two key bags (the pointer's `HeldKeys` and Stroke's `Buttons._holding`) and the caller repeating its right button and
modifiers at `Click` (the plan's own are used only for drawing) are where the bare-click bugs came from.

All of these are the same thing described differently: **the cursor goes somewhere, and keys and buttons go down and
up at moments tied to that journey.** A key going up belongs to whatever comes *next* (leaving), not to the click it
was pressed for. A click-shaped plan cannot say that.

## The model

A plan is a list of **stops**. Each stop is a journey to a target and the **events** tied to it.

```
Stop
  Target      rectangle, or point (as today)
  Events      key or button, down or up, each at an anchor
  Gate        wait for the caller before the arrival events (confirm what is under the cursor)
  Hold        wait for the caller after the arrival events, before leaving
  Idle        while held: Still (default) or Wander (a loiter)
  ShortestMoveMs   as today
```

```
Event
  Input       a key (Ctrl, Shift, Alt, any key) or a button (Left, Right)
  Edge        Down or Up
  Anchor      BeforeArrival   - a lead before the cursor lands (drawn from Modifier Timing), as modifiers go down now
              OnArrival       - once landed (and past the Gate)
              AfterPress      - after this stop's button has come up (drawn from Modifier Timing), as modifiers come up now
              OnLeave         - as the journey to the next stop sets off
```

Rules:

- **A key that goes down stays down** until an Up for it at a later anchor, in this stop or a later one. Carrying ctrl
  across five ctrl+clicks is simply five stops with one Down at the first and one Up after the last: no carry logic.
- **A button press** is `Left Down` on arrival and `Left Up` after the click hold (Click Hold settings). Buttons do not
  stay down across stops (a drag would be a later addition, if ever wanted).
- **Every event's moment is drawn when the plan is made**, so what is drawn is what happens, up marks included.
- **One key bag.** Keys a plan holds are the plan's; Release, a stop, a lapsed claim or a dropped plan lets go of all of
  them (after any button is up, as Give does now).

The familiar gestures, written out:

| Gesture | Stop events |
|---|---|
| Click | Left Down OnArrival, Left Up (after hold) |
| Ctrl+click | Ctrl Down BeforeArrival; Left Down/Up; Ctrl Up AfterPress |
| Five ctrl+clicks | Ctrl Down BeforeArrival on the first; Left Down/Up on each; Ctrl Up AfterPress on the last |
| Right click | Right Down/Up |
| Alt rest on the market ratio | Alt Down OnArrival; Hold (still); Alt Up OnLeave |
| Hover, no keys (heading somewhere early) | no events; Hold |
| A loiter waiting on a ctrl+click | Ctrl Down BeforeArrival; Hold (Wander); Left Down/Up when continued; Ctrl Up AfterPress |

## Who drives

Today the caller drives every click: `MoveTo` (matched to the queue by exact rectangle or point equality), wait for
`Arrived`, `Click` with the modifiers repeated. Under the new model **ExileInput runs the stops itself**, on its input
thread, and the caller steps in only where the plan says so:

- **Gate** (before the arrival events): ExileInput lands, then waits for `Proceed()`. The caller checks what is under
  the cursor (the flipper's "the cursor arrived over something else") and proceeds, or `Abandon(reason)`s.
- **Hold** (after the arrival events): ExileInput waits, keys held, until `Proceed()` - "until the price loads".
- Stops with neither run straight through: the clicks of a deposit run go out at human pace with nothing to ask.

Calls (bridge version 3; the client degrades to false on an older ExileInput, as now):

```
bool PlanSteps(Stop[] stops, bool show)   plan and draw; replaces any queue; empty drops it
bool AddSteps(Stop[] stops)               append, continuing the curve and held keys (as ExtendPlan)
bool Proceed()                            pass the Gate or Hold the plan is waiting at
StepState Where()                         index of the current stop, and phase: Travelling, AtGate, Pressing,
                                          Holding, Leaving, Done, Dropped - with why when dropped
bool Abandon(string why)                  let go of everything held, fade the plan (as a dropped one)
```

Player input still stops the plan as it stops a move now (mouse, keys, the boards' word that the player touched the
mouse); hardware boards and Windows input are the output path as before (`InputOut`).

## Drawing

Per stop: its path (current one thick, others thin, done ones fading - as now), and a mark per event at the place on
the path it happens: **bright for down, dim for up**; `L`/`R` for buttons, `C`/`S`/`A` (or the key's name) for keys.
A key held across stops is drawn as a thin line beside the path from its Down to its Up, so "ctrl held for these five"
reads at a glance. A Gate is drawn as `?` at the target, a Hold as `||`. The Plan Show settings (buttons, modifiers,
numbers, fade) apply as now.

## Compatibility

`PlanClicks`, `ExtendPlan`, `MoveTo`, `Click`, `Loiter`, `ClickOnArrival` and the rest stay, unchanged, so every
plugin using them keeps working. Phased:

1. **Add** the step model beside the click plan: the planner (pure, table-tested), the runner on the input thread, the
   drawing, the bridge calls. The click plan is untouched.
2. **Move the flipper** to `PlanSteps`: its click queue, the Alt rest, ctrl+click runs, the head-start moves.
3. **Later, optionally**, re-express `PlanClicks` and Loiter-with-keys on the step model and retire `CarryModifiers`,
   `_loiterKeys`, `KeepHeld` and the second key bag - only once the step model has run for a while.

## Tests

The planner is pure, so it gets tables as the current plan does (`TravelCases`): each event's time and place from its
anchor; held keys spanning stops with one Down and one Up; a ctrl run carrying ctrl; an Alt rest pressing only after
arrival and letting go on leaving; up marks where the release happens. The runner gets cases with a fake clock and fake
buttons (as `StrokeCases`): Gate and Hold waiting for Proceed, player input dropping the plan and letting keys go,
Abandon, and Release mid-press keeping a key down until the button is up.

## Decisions (2026-11-05)

1. **ExileInput runs the stops**; the caller steps in only at Gates and Holds.
2. **A Hold idles still** by default, with Wander (a loiter) as an option per stop.
3. **Phase 3 waits until after the league** (2026-11-13), and then only with AutoExpedition tested alongside: its
   reforging loiter holding ctrl and its ctrl deposit runs are exactly the cases the old special cases exist for.
