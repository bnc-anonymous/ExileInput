# ExileInput

One place for the input a plugin sends to Path of Exile, so several plugins do not each keep their
own copy of the same hard-won details.

Built as a plain library into the ExileApi root, beside `ItemFilterLibrary.dll`, and referenced the
same way `ExileCore` is:

```xml
<Reference Include="ExileInput">
  <HintPath>$(exapiPackage)\ExileInput.dll</HintPath>
  <Private>False</Private>
</Reference>
```

`Private` false matters. Two copies loaded at once would each keep their own record of which keys
are held, and the whole point of `HeldKeys` is that there is one.

## What is here

**`Buttons`** — a click, with a modifier confirmed down before it is sent. This exists because a
ctrl+right click that loses its modifier is not a click that fails quietly: on a Chaos Orb it *uses*
the orb, putting it on the cursor where it blocks every click after it. So the key state is read
before pressing, with a timed hold as the fallback where the state cannot be read at all.

**`Pointer`** — where the cursor is being sent and whether it has got there. Client space
throughout; the window origin is added in exactly one place. Instance per state machine, because
the arrival tolerance that suits a stash tab is not the one that suits an inventory cell.

**`HeldKeys`** — what this plugin is holding down, so all of it can be let go. A control left down
turns the player's next click into a ctrl+click, which in a stash moves items.

## Why it was pulled out

The currency flipper had five state machines each moving a cursor and clicking. Consolidating them
surfaced two things that a single copy could not have hidden:

- **Three of the five added the window origin before `SetCursorPos` and two did not.** All five then
  compared against the game's client-space cursor, so the comparisons agreed while the moves did
  not. The two that skipped it aim correctly only while the game window sits at the top left of the
  screen. Fullscreen guarantees that; windowed play does not.
- **A guard against clicking through an overlay existed in one machine only.** Two others reach into
  the same inventory underneath the same panel and had no such check.

Neither is the kind of thing that gets noticed while the rule lives in five places.

## Status

Consolidation of the existing behaviour. Movement is still a single `SetCursorPos` - the cursor
arrives instantly and the caller waits for the game to agree. Interpolated travel, with modifiers
held across the approach rather than pressed on arrival, is the next piece.
