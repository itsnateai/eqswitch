# Contributing to EQSwitch

Thanks for your interest in contributing to EQSwitch!

## Reporting Bugs

1. Check existing [issues](https://github.com/itsnateai/eqswitch/issues) first
2. Include your Windows version, .NET version (if building from source), and EQ server
3. Describe what you expected vs what happened
4. Include steps to reproduce

## Building from Source

```bash
# Clone
git clone https://github.com/itsnateai/eqswitch.git
cd eqswitch

# Debug build
dotnet build

# Release build (single-file portable exe)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

## Pull Requests

1. Fork and create a feature branch
2. Keep changes focused — one feature or fix per PR
3. Test with at least one EQ client running (or mock the process detection)
4. Run `dotnet build` to verify no errors or warnings
5. Update CHANGELOG.md under `[Unreleased]`

## Code Style

- All Win32 P/Invoke goes in `Core/NativeMethods.cs` — never scatter `DllImport`
- Use `FileLogger.Info/Warn/Error()` for diagnostic logging (1 MB rotation, thread-safe). Reserve `Debug.WriteLine` for debugger-only spam
- Dispose `Process` objects with `using var`
- Graceful degradation: log and continue on Win32 failures, don't crash
- All colors and control factories live in `UI/DarkTheme.cs` — never use `Color.FromArgb()` outside that file
- Follow existing patterns in the codebase

## Architecture Notes

The `## Project Structure` table in the [README](README.md#project-structure) is the entry point. From there, the most useful files to skim first:

- `Program.cs` — entry point, single-instance mutex, first-run setup
- `UI/TrayManager.cs` — orchestration hub that owns every manager
- `Core/NativeMethods.cs` — single source for all P/Invoke
- `Config/AppConfig.cs` — strongly-typed JSON config model
- `Native/eqswitch-di8.cpp` — DirectInput proxy and background-input hooks

`CHANGELOG.md` has per-version notes on design decisions and rationale.

## License

By contributing, you agree that your contributions will be licensed under the [GPL-2.0-or-later License](LICENSE).
