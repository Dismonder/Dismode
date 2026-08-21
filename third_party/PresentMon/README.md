# PresentMon component

GameShift bundles only the official standalone PresentMon console collector.
The Intel GUI, service, installer, and Chromium Embedded Framework are not
included.

- Project: <https://github.com/GameTechDev/PresentMon>
- Release: `v2.5.1`
- Asset: `PresentMon-2.5.1-x64.exe`
- SHA-256:
  `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`
- License: MIT; see `LICENSE.txt`
- Additional notices: see `THIRD_PARTY.txt`

The release script verifies the exact size and SHA-256 before copying this
component into `Tools/PresentMon`. Runtime verification repeats the SHA-256
check before every new GameShift host lifetime can start the collector.

To update the component, pin a reviewed upstream release, replace the binary
and both notice files, then update the version, size, and SHA-256 constants in
`PresentMonComponent.cs`. Never download or execute an unpinned binary at
runtime.
