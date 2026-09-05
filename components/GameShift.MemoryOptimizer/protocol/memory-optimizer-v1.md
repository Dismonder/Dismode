# Memory Optimizer protocol v1

## Transport and authentication

The service exposes one pipe per interactive Windows SID:

`GameShift.MemoryOptimizer.v1.<SID>`

Each pipe is created with a protected ACL granting access only to LocalSystem,
built-in administrators, and that user SID. The server impersonates every
connected client and compares the token SID with `userSid` in the request.

Messages are UTF-8 JSON prefixed by a four-byte little-endian length. The maximum
message size is 1 MiB. Each request contains protocol version, request ID,
idempotency key, UTC timestamp, SID, command, JSON payload, and the payload
SHA-256. The accepted timestamp window is two minutes. Request IDs prevent replay;
an already completed idempotency key returns the cached response.

The checksum detects a changed payload after request creation. The authenticated
named-pipe token and ACL are the authorization boundary; the checksum is not a
replacement for authentication.

## Commands

| Value | Command | Purpose |
|---:|---|---|
| 1 | `GetStatus` | Current memory, service state, settings, and last result |
| 2 | `GetSettings` | Read normalized per-user settings |
| 3 | `SaveSettings` | Validate and persist settings |
| 4 | `Optimize` | Run the selected saved profile through all safety guards |
| 5 | `GetHistory` | Return bounded optimization history |
| 6 | `PauseAutomation` | Persistently pause automatic runs |
| 7 | `ResumeAutomation` | Resume automatic runs |
| 8 | `ShutdownTray` | Confirm that the requesting tray may close; service stays up |

## Neutral GameShift data

The service reads, but never writes, the following files from
`%ProgramData%\GameShift\Shared\<SID>`:

- `known-games-v1.json`: schema version, generation timestamp, and game records
  containing a neutral ID, display name, and executable file names.
- `active-game-v1.json`: schema version, game ID/display name, PID, start time, and
  a short expiration time.

The gaming program writes each file atomically. An active lease is accepted only
while unexpired and while its PID exists. Missing, corrupt, oversized, or expired
files never authorize an operation; independent process/full-screen guards still
apply.
