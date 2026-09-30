# Dismode Memory Optimizer 0.3.0

Dismode Memory Optimizer is an optional, independent GPL component for Windows
11 23H2 build 22631 or newer on x64. Closing Dismode's gaming interface does not
stop this component.

## Projects

- `Dismode.MemoryOptimizer.Core` contains models, automation policy, neutral
  protocol contracts, SQLite storage, game-safety guards, and the memory port.
- `Dismode.MemoryService` is the only privileged executor and the only database
  writer. The installer registers it as LocalSystem with Automatic Delayed Start.
- `Dismode.MemoryOptimizer` is the non-elevated WinUI 3 settings window and tray.

The three projects have no reference to `Dismode.Core`, `Dismode.Data`,
`Dismode.Windows`, or `Dismode.Contracts`. Integration with the gaming program
uses process launch/status and the neutral files documented in
[`protocol/memory-optimizer-v1.md`](protocol/memory-optimizer-v1.md).

## Safe defaults

The installed default profile trims user process working sets and purges only the
low-priority standby list. Automation is enabled, but runs only when available RAM
is at or below 20%, system CPU stays at or below 5% for five minutes, the
30-minute cooldown has expired, and no game-safety guard blocks the request.
The schedule and advanced mode are disabled.

Advanced mode exposes all eight operations inherited from Windows Memory Cleaner.
Those operations can make Windows reload data and are not an FPS feature. Both
manual and automatic requests pass through the same game-safety guard.

## Interface

The WinUI interface has separate Overview, Automation, Processes, History, and
Advanced pages. Settings remain a local draft until the user explicitly saves or
discards them. The compact panel shows RAM and the essential actions without
shrinking the complete settings form. Its always-on-top preference is optional and
disabled by default.

The commit metric is derived from Windows page-file/commit values and is labelled
as RAM plus page file. The process virtual address space is intentionally not shown
as available memory.

## Build and test

```powershell
dotnet restore .\Dismode.MemoryOptimizer.sln
dotnet build .\Dismode.MemoryOptimizer.sln --configuration Release
dotnet test .\Dismode.MemoryOptimizer.sln --configuration Release --no-build
dotnet format .\Dismode.MemoryOptimizer.sln --verify-no-changes
```

Aggressive native tests are never enabled by the ordinary test command. They may
run only on a disposable Windows 11 VM after setting
`DISMODE_ALLOW_AGGRESSIVE_MEMORY_TESTS=1`.

The application icon can be regenerated from source with:

```powershell
.\tools\New-MemoryOptimizerIcon.ps1
```

## License and source

This component is licensed under GNU GPL version 3 only. It is derived from
Windows Memory Cleaner 3.0.8 © Igor Mundstein. Read [NOTICE.md](NOTICE.md),
[MODIFICATIONS.md](MODIFICATIONS.md), and [LICENSE](LICENSE).
