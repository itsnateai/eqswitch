# EQSwitch — Roadmap

> Post-release feature roadmap. Items marked `[x]` are complete; `[ ]` are planned.

---

## v2.3.0 — Performance & Fullscreen `[x]`

- [x] P2-04: Background FPS throttling (NtSuspendProcess/NtResumeProcess duty cycle)
- [x] P2-03: Borderless fullscreen mode (WinEQ Y+1 offset trick, rcMonitor bounds)

## v2.4.0 — Tray UX Overhaul `[x]`

- [x] Grouped tray submenus (Video Settings, Settings, Launcher)
- [x] Emoji/icon prefixes on all menu items (medieval theme)
- [x] Dark themed context menus (DarkMenuRenderer)
- [x] Configurable tray click actions with delayed resolution
- [x] Simplified CPU Affinity submenu
- [x] Custom video presets (up to 3)
- [x] Process Manager dark retro restyle
- [x] FloatingTooltip for "already running"
- [x] First-run auto-opens Settings

## v2.5.0 — Quality of Life (In Progress)

- [x] Reset Defaults button in Video Settings form
- [ ] Reset Defaults button in Process Manager form
- [ ] Per-client CPU affinity — assign different core sets to each EQ client
- [ ] Per-client process priority — e.g. main=High, alt=Normal
- [ ] VirusTotal clean scan on published exe

---

## Removed Items

The following items have been evaluated and intentionally removed:

- ~~Interactive PiP (click to focus)~~ — PiP is intentionally click-through; opacity already configurable
- ~~Saveable layout presets~~ — Removed, not needed for target use case
- ~~Focus-follow-mouse mode~~ — Removed
- ~~PiP zoom-on-hover~~ — Removed
