# EQSwitch TODO

## Pending

### Tray menu "Enter World" per running client
- All native + SHM wiring exists: `CharSelectReader.RequestEnterWorld(pid)` → `shm->enterWorldReq` → DLL fires `XWM_LCLICK` on `CLW_EnterWorldButton`.
- Currently only called from `AutoLoginManager`. Add a per-client tray submenu item that calls `RequestEnterWorld(pid)` for manual sessions.
- Estimated: ~10 lines in `UI/TrayManager.cs` (the per-client submenu that already enumerates EQ clients).
- Touch points: locate where per-client items get added (search for `_processManager.Clients` or similar enumeration), append a `MakeMenuItem("Enter World", ...)` that opens a `CharSelectReader` for that PID and calls `RequestEnterWorld`.
