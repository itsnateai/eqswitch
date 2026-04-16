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

- All Win32 P/Invoke goes in `Core/NativeMethods.cs` — never scatter DllImport
- Use `Debug.WriteLine` for diagnostic logging
- Dispose Process objects with `using var`
- Graceful degradation: log and continue on Win32 failures, don't crash
- Follow existing patterns in the codebase

## Architecture Notes

See the project's `CLAUDE.md` for detailed architecture documentation, design decisions, and gotchas.

## License

By contributing, you agree that your contributions will be licensed under the [GPL-2.0-or-later License](LICENSE).
