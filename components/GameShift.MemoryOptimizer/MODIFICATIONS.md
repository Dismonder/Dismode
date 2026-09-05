# Modifications relative to Windows Memory Cleaner 3.0.8

The GameShift port was prepared in 2026 for GameShift Memory Optimizer 0.3.0.

The 0.4.0 tray-panel revision (2026-09-05) adds a native dark, DPI-aware
quick panel with physical RAM metrics, service-backed states and separate
interface-exit/component-disable actions. Optimization and pause keep the
panel open. Missing service data disables actions instead of displaying an
online state. A hidden startup probe measures the panel without activation.
Release signing continues to require a valid Authenticode signature, including
when explicitly using a test certificate.

- Removed the upstream WPF interface, updater, themes, donation integration,
  signing material, and Windows XP–10 compatibility branches.
- Raised the platform floor to Windows 11 23H2 build 22631, x64.
- Ported the memory code to .NET 10 and source-generated `LibraryImport` calls.
- Replaced raw token and process handles with `SafeHandle` types.
- Added scoped privilege enable/restore for `SeDebugPrivilege`,
  `SeIncreaseQuotaPrivilege`, and `SeProfileSingleProcessPrivilege`.
- Added explicit `ERROR_NOT_ALL_ASSIGNED` handling.
- Replaced `GetLastWin32Error` use for NT calls with
  `RtlNtStatusToDosError` mapping.
- Added cancellation boundaries, a single-flight engine, stable operation order,
  and a result for each selected memory area.
- Preserved the upstream eight area flags with their original numeric values.
- Added separate manual and automatic profiles, mutual exclusion for standby-list
  variants, and explicit consent for advanced and global working-set modes.
- Added conservative defaults: user working sets plus low-priority standby list;
  automatic trigger at less than 20% available RAM after five minutes below 5%
  CPU; 30-minute cooldown; schedule disabled.
- Added blocking for active GameShift sessions, known games, protected
  launchers/anti-cheat processes, and unknown foreground full-screen processes.
- Added an isolated LocalSystem Windows service, per-user authenticated named
  pipes, replay/idempotency checks, a 1 MiB limit, and per-user SQLite history.
- Added an unprivileged WinUI 3 interface and tray that remain independent of the
  GameShift gaming processes.
- Replaced the original single-form interface with a dark WinUI navigation shell,
  a custom title bar, responsive pages, an explicit settings draft, and a separate
  compact panel with optional always-on-top behavior.
- Replaced the process virtual-address-space figure with Windows commit usage
  (RAM plus page file), added a one-minute in-memory RAM trend, and limited process
  and history refreshes to their active views.
- Added a locally generated multi-resolution application icon; its reproducible
  generator is distributed with the corresponding source.
- Added source-archive generation, SHA-256 manifests, GPL release checks, and
  tests that keep aggressive native operations behind an explicit disposable-VM
  flag.
