# OpenSO v0.4.2

## New: Build Mode Tools
- **Eyedropper** — pick up any object, wall pattern, or floor (including diagonal
  floor triangles) straight from the lot. Picking an object jumps the catalog to its
  exact category and page with the item selected, ready to place another.
- **Sledgehammer** — quickly delete objects, walls, and floors, with drag support.
- Both tools are based on SegerEnd's upstream FreeSO PR, with our fixes on top:
  exact-depth picking of whatever is under the cursor, and full support for both
  diagonal wall orientations and diagonal floor triangles.

## Lot & Building Fixes
- Wall tools now reliably target **diagonal walls** from every camera angle
  (including the snapped rotate-button views), pick the **correct face** of
  diagonals and straight walls, and register clicks on the **top half** of walls.
- Painting diagonal walls applies to the face you're pointing at.
- Flood-filling floors now fills the triangles on **both sides** of a diagonal wall
  when the fill reaches both (e.g. around a free-standing wall line) — one triangle
  no longer stays bare.

## Create-A-Sim (CAS V2)
- Arrow keys no longer jump focus between tiles/buttons and change your selections —
  they now stay in the text field you're typing in. (Tab/Shift+Tab still cycle fields.)
- Clicking past the end of a line places the caret at that line's end instead of the
  start of the next line; clicking empty space below the text goes to the end.
- Cleaned up the default bio template (stray spaces around "Quote:"/"Fav. music:").
- Naming your sim the same as your username no longer falsely reports "a sim with
  that name already exists", and name validation errors show the real reason.

## Graphics
- Fixed the city backdrop around the lot **vanishing into the void** at steep,
  straight-down camera angles after lowering the Surrounding Lots setting while in
  a lot.
- The surroundings cut-out now resizes correctly when changing the Surrounding
  Lots setting mid-lot.

## Server
- **Lot permissions are now enforced in free roam**: walking across a lot boundary or
  routing into a neighbouring lot respects admit lists, ban lists, and ban-all — the
  same rules as entering from the map, with the same "not admitted" message. Closed
  lot borders act as walls; a routing sim cancels gracefully instead of retrying.
- **Lot loading is now crash-proof against invalid items**: corrupted or missing
  objects are removed (and logged) instead of making the lot fail to open. Corrupt
  interaction states reset instead of taking the object down.
- The server status API now advertises each shard's city map (fixes the launcher
  showing the wrong city thumbnail).

## Also
- Camera rotate/zoom buttons work in 3D mode.
- Fixed a fatal crash placing stairs then switching build/buy modes.
- Fixed a SimAntics error when joining job lots.

*Launcher v0.4.2 ships separately: correct city thumbnail per shard, working disk
space check on Bazzite/immutable Linux, and a progress bar that actually reaches 100%.*
