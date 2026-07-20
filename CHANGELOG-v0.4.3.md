# OpenSO v0.4.3

## Sim Name Changes (Edit A Sim)
- **You can now rename your sim** from the Edit A Sim screen — no more support requests. The name field is editable when re-entering CAS for an existing sim, and the server enforces the same rules as creation: 3–24 letters/spaces, must start with a letter, and the name can't already be taken on the shard.
- Keeping your current name is always allowed, even if it predates the naming rules — only a *changed* name is re-validated, so older sims can still edit their appearance freely.
- Renames (and appearance edits) now show up immediately on person pages and searches — the server no longer serves the old cached name until the nightly restart.

## Build Mode
- The **eyedropper and sledgehammer** now hide themselves when they can't be used: in the buy-mode inventory tab, and for anyone without build/buy permission (a plain roommate or visitor). If you switch to inventory or lose permission while a tool is active, it's cancelled automatically.

## Server
- **Moving out no longer bricks the plot.** Previously, if the cleanup after a move-out didn't complete, the empty plot stayed unbuyable forever ("lot taken" on a plot showing only its coordinates as the title). Dead lots are now recycled automatically: buying the location just works, the nightly maintenance sweeps up any that linger, and they no longer reappear as phantom lots after a server restart. Objects left on a recycled lot return to their owners' inventories.
- **The AFK timeout works again in first person / direct control.** These camera modes stream a control packet every tick, which counted as activity and kept idle players on lots forever. Only real movement input counts now, restoring the FreeSO behavior: idle warning after 15 minutes, disconnect at 20.
- Archive-only in-game moderation (the self-host user list actions) is now fully disabled on the live server, as are its user-list broadcasts.
