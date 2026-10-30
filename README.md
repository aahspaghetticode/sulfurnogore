# SULFUR No Gore

Removes the blood and gore effects from SULFUR, with an in-game options page so you
can choose exactly what to turn off. Made for players who are sensitive to gore but
still want to play.

## What it removes

- Blood splatters and blood projectiles
- Gibs (body chunks) and gib explosions
- Melee slash marks and bullet-hole decals
- Blood hit effects when enemies take damage

## What it does **not** remove

- The red screen flash when you are hurt. (Removing it caused crashes, so it is left alone.)
- The low-health dark pulse.
- Anything not listed above. This mod only covers the effects that were found and tested,
  so some gore may still appear, for example in boss fights, the death screen, or special
  attacks. If you see any, please report it (see below).

## Requirements

- BepInEx 5 for SULFUR
- [SULFUR Native UI Lib](https://thunderstore.io/c/sulfur/p/ryuka_labs/SULFUR_Native_UI_Lib/) (required, it provides the options menu)

## Installation

Install with a mod manager (it will pull in the dependency), or manually:

1. Install BepInEx 5 and SULFUR Native UI Lib.
2. Copy `SulfurNoGore.dll` into `BepInEx/plugins/`.
3. Start the game.

## Configuration

Open **Options** and look for the **No Gore** page. It has:

- **Enabled**: master switch for everything.
- **Patched methods**: one toggle per patched game method. Switch one off if it causes
  problems, and the rest keep working. Changes apply immediately, no restart needed.

The same settings are saved in `BepInEx/config/com.henw.sulfurnogore.cfg`.

## Troubleshooting

- **Enemies freeze, you can't die, or the console is full of `NullReferenceException`:**
  one of the patches is clashing with something else (another mod or a game update).
  Turn the master switch off to confirm, then switch individual entries in
  **Patched methods** off until it stops, and please report which one.
- **Some gore still shows up:** report where it happened and what you were doing.
- **The mod does nothing after a game update:** the patches rely on internal class and
  method names, which can change. Check the log (`BepInEx/LogOutput.log`) for lines like
  `Type not found` or `Method not found`.

## Reporting problems

Open an issue at https://github.com/aahspaghetticode/sulfurnogore and include:
the game version, your `BepInEx/LogOutput.log`, and what you were doing.

## Credits

- Options page built with [SULFUR Native UI Lib](https://thunderstore.io/c/sulfur/p/ryuka_labs/SULFUR_Native_UI_Lib/) by ryuka.
- Patching with HarmonyX, included with BepInEx.

## License

MIT, see `LICENSE`.
