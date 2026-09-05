# GameShift

GameShift is a native Windows 11 x64 application for desktop gaming PCs. It
prepares a transparent, reversible optimization plan for a game session and
restores the previous system state afterwards.

The clickable release automatically builds a local library from Steam, Epic,
GOG, Roblox, and directly accessible Xbox installations. For an approved game
session it can gracefully close and restore selected user applications, apply
BelowNormal plus EcoQoS to unrelated background processes, raise the game
priority through the real Windows process API, attach to an already-running
verified game instead of starting a duplicate, follow child processes after a
launcher exits, and request a graceful game close.
Known launchers, anti-cheat components, Windows processes, and helpers from
the selected game's installation directory are excluded from background
optimization.
GameShift bundles the standalone open-source PresentMon 2.5.1 collector and
starts it automatically only for the tracked game PIDs. FPS and frame time are
calculated from real ETW present events. Missing or stale data is shown as
unavailable and is never replaced with an estimate. The binary is pinned by
size and SHA-256, and its MIT license and third-party notices ship beside it.
Every production process action is journaled and reversible. GameShift 0.4.0
also ships a separate System Optimizer UI backed by the delayed-auto
`GameShiftSystemAgent` Windows service. Its A/B catalog is fail-closed: only
process priority, per-process Power Throttling and the fixed, independently
verified hibernation adapter are executable; entries without a verified
adapter remain visibly `Unsupported`, while security, anti-cheat, WHEA and BCD
changes are permanently blocked.

The library also exposes a per-game OptiScaler manager. In a few clicks it
lists exact versions from the recommended official Stable channel, the
community Beta channel, or the official daily Nightly channel. It downloads
the selected 7z release from a pinned GitHub repository, verifies the GitHub
SHA-256 digest, selects the actual Unreal shipping executable when applicable,
and installs one of four fixed proxy DLL names. Beta and Nightly require a
separate experimental-build confirmation; Beta is clearly marked as an
unofficial community source. Existing files are backed up and restored on
removal. Installation is blocked while the game is running, for detected
anti-cheat/online-only targets, and until the user explicitly confirms offline
or single-player use.

## Requirements

- Windows 11 23H2 or newer, x64
- desktop PC; laptop battery profiles are outside the current target

The installer build is self-contained and does not require a separate .NET or
Windows App Runtime installation. Building GameShift from source requires:

- .NET SDK 10.0.302
- Windows SDK 10.0.26100
- Visual Studio 2026 with WinUI application development
- Inno Setup 6 for producing the installer

## Build and test

```powershell
dotnet restore GameShift.sln
dotnet build GameShift.sln --configuration Debug
dotnet test GameShift.sln --configuration Debug --no-build
dotnet format GameShift.sln --verify-no-changes
```

## Run the application

The verified local release is assembled in:

```text
artifacts\GameShift-App\GameShift.exe
```

On this workstation the desktop shortcut `GameShift` points to that launcher.
`GameShift.exe` is the user-facing launcher. It requests UAC for the fixed
`GameShift.SessionHost.exe` backend when Windows requires it, then opens the
unprivileged `GameShift.UI.exe` maximized. `GameShift.SystemAgent.exe` is
installed and started separately as a LocalSystem Windows service; closing the
gaming UI does not stop recovery supervision. A second launch reuses the
already running interactive components and maximizes the existing window.

To rebuild the clickable release after all sessions have completed:

```powershell
.\tools\Build-LocalRelease.ps1 -CreateDesktopShortcut
```

The release script refuses to replace running components or an unfinished
journal, verifies the pinned PresentMon binary, runs build/tests/format
verification, publishes UI last, validates the loose XAML resources, and keeps
the prior directory as a rollback copy.

This local release is framework-dependent and requires the .NET 10 desktop
runtime and Windows App Runtime 2.3.1. For distribution, build the self-contained
installer instead.

## Build the EXE installer

```powershell
.\tools\Build-Installer.ps1
```

The resulting files are:

```text
artifacts\installer\GameShift-Setup-0.4.0-win-x64.exe
artifacts\installer\GameShift-Setup-0.4.0-win-x64.exe.sha256
```

The installer requires UAC once to write into `Program Files\GameShift`, adds a
Start menu shortcut and an optional desktop shortcut, and registers the normal
Windows uninstaller. It refuses to update or uninstall while GameShift or its
FPS collector is running. Profiles, history, settings, and the recovery journal
under `%LocalAppData%\GameShift` are intentionally preserved by uninstall.

The 0.4.0 release pipeline fails closed unless a valid production Authenticode
certificate is supplied through `GAMESHIFT_RELEASE_SIGNING_THUMBPRINT` (or the
matching command parameter). It signs all owned mutation clients and the final
installer, verifies the signer allow-list, creates a SHA-256 checksum beside
the installer and writes a per-file payload manifest inside the installed
directory.

## Architecture

- `GameShift.Launcher`: one-click, unprivileged launcher for the fixed local components.
- `GameShift.UI`: unprivileged WinUI 3 user interface.
- `GameShift.SessionHost`: UAC-elevated process in the interactive user session;
  owns game-session actions and the PresentMon child process.
- `GameShift.SystemAgent`: read-only service classifier and future privileged
  recovery owner.
- `GameShift.Core`: platform-independent domain and safety rules.
- `GameShift.Contracts`: versioned IPC contracts.
- `GameShift.Data`: persistence and recovery journal.
- `GameShift.Windows`: Windows observation and native adapters.

See `docs/CODEX_CONTEXT.md`, `docs/MVP_ACCEPTANCE.md`,
`docs/OPERATIONS.md`, and `docs/exec-plans/active/gameshift-mvp.md`
before making changes.
