# hop

**Click anything on Windows without touching the mouse.** Press a hotkey, every
clickable thing gets a letter, type the letter.

[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![build](https://github.com/BeForce1/hop/actions/workflows/build.yml/badge.svg)](https://github.com/BeForce1/hop/actions/workflows/build.yml)
[![size](https://img.shields.io/badge/binary-16%20KB-brightgreen)](#build)
[![deps](https://img.shields.io/badge/dependencies-none-brightgreen)](#build)

<!-- TODO: record a 5s GIF and drop it here. This is the single highest-value thing
     left to do for this repo - see "Recording the demo" at the bottom. -->

```
                    ┌─────────────────────────────────────────┐
   Ctrl+Alt+Space → │  [f] File   [g] Edit   [h] View         │
                    │                                         │
                    │   ┌──────────┐        ┌──────────┐      │
                    │   │ [j] Save │        │ [k] Open │      │
                    │   └──────────┘        └──────────┘      │
                    │                                         │
   press  j      →  │   ...clicked Save. No mouse involved.   │
                    └─────────────────────────────────────────┘
```

macOS has [Homerow](https://homerow.app) for this, paid. Windows has one
experimental AutoHotkey script. So: a real implementation, free, in one file.

## Install

No installer, no runtime, no admin:

```powershell
git clone https://github.com/BeForce1/hop && cd hop
.\build.ps1
.\hop.exe
```

Then press **Ctrl+Alt+Space** in any window. A tray icon tells you which hotkey
bound — it tries five and takes the first one that's free.

Autostart it:

```powershell
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' hop "$PWD\hop.exe"
```

## Controls

| Key | Does |
|---|---|
| `Ctrl+Alt+Space` | Label every clickable thing in the focused window |
| `f g h j k l …` | Type a label to click it |
| `W A S D` / arrows | Steer the **green** selection to the nearest target that way |
| `Enter` / `Space` | Click the green one |
| `Backspace` | Un-type a keystroke |
| `Esc` | Cancel |

Two ways to land on something: **type its label** if you can see it, or **steer** if
you'd rather look than read. The selected target turns green and gets its whole
control outlined, so you can see exactly what you're about to click.

Labels never use `w a s d` — those steer. That costs 3 of 26 single-key labels (22
remain, then 484 two-key combos), which is the price of WASD not being ambiguous
with a label on every keystroke.

## Build

`csc.exe` ships **inside Windows** at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`,
so this compiles on a clean machine with nothing installed — no SDK, no NuGet, no
project file. Output is a 16 KB exe that needs no runtime, because .NET Framework 4.x
is part of the OS.

One source file, ~300 lines. `build.ps1` is 21 lines.

## See what it finds

```powershell
.\hop.exe --dump 3      # focus a window, wait 3s, print every target detected
```

```
hwnd 132816 -> 21 clickable in 142ms
  f   MenuItem     [297,297 44x44]     System
  g   Button       [1953,295 94x57]    Minimize
  h   TabItem      [476,312 124x64]    notes.md
  j   MenuItem     [314,376 82x64]     File
  k   Button       [1900,376 112x64]   Writing tools
  l   Button       [1278,1047 323x64]  OK
```

Useful for filing a bug: run it against the window that misbehaved and paste the output.

## How it clicks

UI Automation first, synthetic mouse last:

1. `Edit` controls get `SetFocus()` — you want the caret, not a click
2. `InvokePattern` — buttons, links, menu items
3. `TogglePattern` — checkboxes
4. `ExpandCollapsePattern` — combo boxes, tree nodes
5. `SelectionItemPattern` — list items, tabs
6. Otherwise: move the cursor and click for real

Pattern invocation doesn't move your mouse and works even when the window isn't
focused, which is why it's tried first.

The one performance trick that matters: a **`CacheRequest`** batches every property
read into a single cross-process call. Without it, a busy window takes seconds;
with it, Notepad enumerates in 142 ms. There's also a hard 1.5 s budget and a
484-element cap — past that, targets are dropped rather than making you wait.

## Prior art

- **[Homerow](https://homerow.app)** (macOS, paid) — the thing this imitates. Better polished.
- **[vimium-everywhere](https://github.com/phil294/vimium-everywhere)** — the only comparable
  thing on Windows. An AutoHotkey script its own README calls unstable.
- **[Vimium](https://vimium.github.io/) / Vimium-C** — same idea, browsers only, excellent at it.
- **PowerToys Mouse Jump** — teleports the cursor to a screen region. Different problem.

If you want this *inside a browser only*, use Vimium. It's better at that than hop is.

## Limits

Stated up front so nobody has to discover them:

- **Chrome needs its accessibility tree awake.** It builds one lazily when a UIA
  client asks, so the *first* `Ctrl+Alt+Space` in a Chrome window may find little
  and the second finds everything. This is the biggest open issue.
- **`ControlType.Custom` is excluded.** Electron apps expose thousands of Custom
  nodes and including them buries the real controls. If something has no label, this
  is usually why.
- **WASD steers the selection; it does not scroll the page.** Only on-screen controls
  are ever labelled, so anything below the fold is invisible until you scroll there.
- **Steering is geometric, not tab-order.** Nearest target in the direction pressed,
  penalising sideways drift 3:1. Good on grids and columns, mediocre on scattered layouts.
- **Per-monitor mixed DPI can misplace labels.** `SetProcessDPIAware()` handles
  uniform scaling, not per-monitor v2.
- **Left click and focus only.** No right-click, no drag, no scroll.

## Recording the demo

The GIF is worth more than everything else in this file. Keep it under 5 seconds:

1. [ScreenToGif](https://www.screentogif.com/) (free, open source), 800 px wide, 15 fps
2. Open a busy window — Settings, or a file manager
3. Press the hotkey, pause a beat on the labels, press one letter, done
4. Save under 2 MB so GitHub inlines it, drop it at the top of this file

## License

MIT — see [LICENSE](LICENSE).
