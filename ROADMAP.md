# EQSwitch — Roadmap

> Post-release feature roadmap. All planned features complete as of v2.6.0.

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

## v2.5.0 — Quality of Life `[x]`

- [x] Reset Defaults button in Video Settings form
- [x] VirusTotal clean scan on published exe (0/70 detections)

## v2.6.0 — Per-Character Overrides `[x]`

- [x] Per-client CPU affinity — assign different core sets to each EQ client
- [x] Per-client process priority — e.g. main=High, alt=Normal
- [x] Character Edit dialog (double-click in Settings → Characters)
- [x] Process Manager "Source" column (Custom vs Global indicator)

---

## Removed Items

The following items have been evaluated and intentionally removed:

- ~~Interactive PiP (click to focus)~~ — PiP is intentionally click-through; opacity already configurable
- ~~Saveable layout presets~~ — Removed, not needed for target use case
- ~~Focus-follow-mouse mode~~ — Removed
- ~~PiP zoom-on-hover~~ — Removed
