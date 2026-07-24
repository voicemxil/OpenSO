# OpenSO v0.4.3

Everything below is new since v0.4.2, for both the game client/server and the launcher.

---

# Game

## Edit A Sim
- **You can now rename your sim** from the Edit A Sim screen — no more support requests. The name field is editable when re-entering CAS for an existing sim, and the server enforces the same rules as creation: 3–24 letters/spaces, must start with a letter, and the name can't already be taken on the shard.
- Keeping your current name is always allowed, even if it predates the naming rules — only a *changed* name is re-validated, so older sims can still edit their appearance freely.
- **A new look now costs §1,000.** The charge applies only when you actually change something about your sim's appearance — gender, skin tone, head or body. If you can't afford it nothing is applied and nothing is deducted.
- **Renaming is free**, and so is editing your bio. To stop names being churned, a sim can only be renamed **once per day** — you can still change their look as often as you can pay for it.
- Renames and appearance edits now show up immediately on person pages and searches — the server no longer serves the old cached name until the nightly restart.

## macOS: Native Retina Rendering
- The Mac client now renders at **full Retina resolution** — crisp UI, avatars, 3D, and 2D at native pixels instead of the old scaled-up point resolution.
- Mouse input is precisely mapped at Retina scale: clicks land everywhere in the window, drags, pie menus, and the catalog all behave.
- Window resizing and borderless fullscreen (Alt+Enter) track correctly at any size.

## 2D Mode Fixed on Mac
- Fixed the long-standing **blocky, nearest-neighbor sprite scaling** in 2D mode on the OpenGL backend — a half-pixel projection error introduced by the shader toolchain upgrade. Sprites are pixel-crisp again.
- Fixed 2D **depth sorting**: object pieces no longer show through each other (mailbox flags, shower walls), whether the client was launched in 2D or 3D mode.
- Fixed **thin seams and overlap slivers** between the parts of multi-tile objects (hot tubs, large furniture).
- **Sharper 2D zoom on high-DPI displays**: each zoom level now uses the densest sprite art available — middle zoom shows closest-zoom art at true 1:1 Retina detail, and closest zoom is a clean integer double.
- Switching from 3D to 2D no longer leaves the sprite scene subtly stretched by a carried-over fractional zoom (all platforms), and the zoom buttons track your actual zoom level.
- Windows high-DPI: free-cam mouse look no longer drifts off-center at UI scales above 100%.

## Graphics
- **Ground-up SSAO** (Scalable Ambient Obscurance) replaces the old disabled ambient occlusion path — proper contact shadows in 3D mode, correct under render scaling, with an ambient/direct lighting split. Further refined with motion-gated temporal validity and a plane-aware blur, so it stays stable while the camera moves.
- **Temporal anti-aliasing rebuilt along FSR2/FSR3 lines.** Thin features — railings, wires, fence posts — lock in a single frame instead of shimmering; moving sims leave far less ghosting; rain is treated as its own reactive case rather than smearing; and disocclusion behind slow-moving silhouettes is handled properly.
- **OpenGL now matches DirectX** for temporal quality, including on older `ps_3_0` hardware.
- **Motion blur** no longer shows tile seams, and reactive surfaces are excluded from it.
- **New Sharp Bilinear upscaling option**, plus reduced terrain grain and fixes to disabled items in the graphics dropdowns.
- The **sky dome returns to the classic upstream look** (brightness and day/night colors restored) with velocity-buffer support for TAA and motion blur as the only addition.
- Fixed the sky/city backdrop taking a **circular bite** out of the horizon during 2D↔3D camera transitions on the OpenGL backend.
- 3D picking: small objects made of tiny mesh pieces (roaches, firefly lights, wall phones) are reliably clickable — clicks within a small buffer of the geometry now count.
- Fixed a **crash to desktop** when the game re-created its render targets (switching to 2D with supersampling on, and similar): a target could be disposed while still bound, and the next frame tried to resolve it.

## Build Mode
- The **eyedropper and sledgehammer** now hide themselves when they can't be used: in the buy-mode inventory tab, and for anyone without build/buy permission (a plain roommate or visitor). If you switch to inventory or lose permission while a tool is active, it's cancelled automatically.

## Polish
- The remaining **FreeSO branding in game text** is now OpenSO: the welcome/hint system, money and property guides, staff mail sender names, and error dialogs.
- **The OpenGL client now shows the OpenSO icon.** The Windows client carries both graphics backends and they read different icon resources — DirectX had the OpenSO mark while OpenGL still showed the old FreeSO one. The Linux/macOS executable and the Linux desktop icon were also still FreeSO-branded, and are fixed too.
- **The version string on the login screen stays inside the window.** It was positioned against the background image, which is larger than the default window, so it could sit off-screen entirely; it is now pinned to the bottom-left corner and follows the window when resized.

## Server
- **Moving out no longer bricks the plot.** Previously, if the cleanup after a move-out didn't complete, the empty plot stayed unbuyable forever ("lot taken" on a plot showing only its coordinates as the title). Dead lots are now recycled automatically: buying the location just works, the nightly maintenance sweeps up any that linger, and they no longer reappear as phantom lots after a server restart. Objects left on a recycled lot return to their owners' inventories.
- **The AFK timeout works again in first person / direct control.** These camera modes stream a control packet every tick, which counted as activity and kept idle players on lots forever. Only real movement input counts now, restoring the FreeSO behavior: idle warning after 15 minutes, disconnect at 20.
- Archive-only in-game moderation (the self-host user list actions) is now fully disabled on the live server, as are its user-list broadcasts.
- **Admin API hardening**: admin authentication now defaults to deny, and the localhost login bypass has been removed.
- Admin mail now delivers to **every avatar** on the target account (previously it could fail outright against the database's constraints), and reports cleanly when an account has no avatars.
- The admin API returns readable JSON errors to the admin webapp instead of opaque "failed to fetch" responses, and supports paginated listings.
- **New public API: daily money-object payout rates** (`GET /userapi/payouts`). The nightly rebalance already decided which money objects pay more today and which one gets the bonus, but that was only visible in the in-game newspaper. It's now readable by launchers and community dashboards: per object, the current multiplier, the day-over-day change, and the bonus object of the day. No login required — these are global rates, identical for everyone. Player balances remain private and are exposed by no endpoint.

---

# Launcher

## Linux
- **Ships as an AppImage.** Self-update understands AppImage installs and replaces the single file atomically, rather than trying to swap a directory that isn't there.
- The embedded AppImage runtime is **pinned and hash-verified**, and is the static build — no `libfuse2` required on the host.
- Hardened Linux archive extraction, including preserving the executable bit through a self-update.

## The Sims Online: detection, repair and reinstall
- **Detects an existing TSO install** — launcher-managed, registry, or a legacy retail path — and validates that it is actually complete rather than trusting a bare folder.
- A partial or truncated install is reported as **Incomplete** with what's missing, and offers a repair.
- **Reinstall now always downloads a fresh, verified copy.** It previously reused another install found on your machine without saying so, which — because the source comes from the Maxis registry — could be an unrelated server's game files.
- Reusing a local copy is still supported, but as a deliberate **"Use existing copy"** action that names the folder it would copy from, offered only when nothing is installed yet. It saves the 1.27 GB download on a fresh setup.

## Self-update
- **Fixed: the launcher could not update on accounts with non-Latin usernames.** Greek, Cyrillic, CJK and similar profile names were mangled into garbage in the update script, and the update failed with "the launcher cannot be found at the path specified".
- **No more console window during an update.** A command window used to flash — and could be left stranded on screen showing an error.

## Interface
- **The city map is legible.** Genesis' map thumbnail never appeared at all, because the shard runs one of the original TSO maps and the launcher only looked among the OpenSO-bundled ones. It's now shown at its natural size instead of being blown up sevenfold into a blur, and sits beside the live server stats under the shard name.
- The 3D Mesh Pack row on the Installer page showed the literal text "True" instead of its install state, and its button now reads Reinstall once the pack is present.
