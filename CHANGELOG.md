# Changelog

## 1.0.0

- First release.
- Removes blood effects, gibs, gib explosions, melee slash marks and bullet-hole decals.
- In-game options page (via SULFUR Native UI Lib) with a master switch and one toggle per patch.
- Toggles apply live, no restart needed.

## 1.0.1

- Fix version mismatch for UI lib requirement

## 1.0.2

- Options menu now has five plainly named switches instead of one per game method: Blood splatters, Blood stains, Gibs and body chunks, Hit effects and slash marks, and Bullet holes.
- New "Brown blood" option: any blood that is still shown is tinted brown, like dirt.
- The master switch is now called "Enable mod". Settings moved to new config entries, so they reset to their defaults (everything on); the old `[Targets]` entries are no longer used and can be deleted.
- README: noted that pre-placed dead bodies in levels are not fixed yet.
