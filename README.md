# LightHub

The finalized [product and engineering design](docs/design/README.md) defines scope,
technology, behavior, recovery and delivery gates. Design version 1.1 is not the app
version or a claim that planned capabilities are already implemented.

The LightHub HID backend passed a Windows GPW1 inactive-profile write/restore test
on one physical unit. Power-cycle and physical input tests remain pending. See
[hardware validation](docs/validation/gpw1-windows.md) before testing writes.

An offline, open-source desktop configuration manager for Logitech HID++ gaming mice.
Built with .NET 10, Avalonia and HidSharp. No G HUB installation, background service,
input hook, account or runtime network access required.

The [design baseline 1.1](docs/design/README.md) retains macro editing as a core
goal and plans an optional software macro runner. Fixed software profiles will not
require application switching; both runtime and automatic switching are opt-in.
The local macro editor is now implemented; onboard macro writing and the software
runner remain unavailable. See the [macro guide](docs/MACROS.md) and
[batch validation report](docs/validation/macros-20260912.md).

**Development candidate: 0.3.0-alpha.2.** Release readiness is tracked in the
[candidate checklist](docs/releases/0.3.0-alpha.2-checklist.md); the earlier
[refactor acceptance report](docs/validation/refactor-20260908.md) remains historical evidence.
This iteration passed GPW1 inactive-save/restore and runtime-DPI regression, but activation,
physical input and lifecycle gates remain open. Cross-platform architecture does not mean all
platform/device combinations have been validated. Read the support matrix below.
This repository is suitable for public development, not yet a stable universal release.

[中文说明](README.zh-CN.md) · [Product plan](docs/PLAN.md) · [Stack decision](docs/ADR-001-stack.md) · [Contributing](CONTRIBUTING.md)

## What works

- Multiple HID++ devices and receiver slots; select the intended device explicitly.
- Device-reported profile slots, button count, memory geometry, DPI values and rate mask.
- DPI stages/default/shift, polling rate, normal mouse/keyboard/media bindings.
- GPW1 physical button diagram and location labels; temporary current-DPI control
  with read-back, separate from persistent onboard writes (unreleased iteration).
- Separate save and activation commands. Activation remains disabled without its own evidence.
- Complete declared-memory backups, CRC validation and checksummed backup envelopes.
- Automatic pre-write backup, same-unit validation, stale-state refusal, write journal,
  full read-back, profile recovery, redacted diagnostics.
- English and Simplified Chinese UI; CLI for diagnostics and reproducible testing.
- Local backup management: full history, size totals, selected export/delete and manual
  cleanup retaining 10 per device plus protected/unreadable backups (unreleased).
- Named semantic presets, import/export, draft preservation, backup naming/pins/milestones,
  and a recoverable local recycle area. Cleanup is manual; expiry never silently deletes files.
- Shared Application services for desktop and CLI; preview checks ownership before restoring DPI.

For the remaining work beyond basic profiles, see the [G HUB gap analysis](docs/GHUB-GAP-ANALYSIS.md).
Multi-step keyboard macros can be edited and saved locally. They cannot yet be
bound to the device or executed. G-Shift editing remains unavailable.

## Compatibility

Public protocol/device sources and the next validation steps are recorded in
[device research](docs/DEVICE-RESEARCH.md). Public model metadata alone does not enable writes.

| Device | Windows | Linux / macOS |
| --- | --- | --- |
| G PRO Wireless (GPW1) | Experimental standard profile/runtime DPI/restore; BOT 74.02.0026, C539 receiver only | Read-only until hardware validation |
| G305 / G304 | Identification / experimental read-only | Experimental read-only |
| G502 HERO | Identification / experimental read-only | Experimental read-only |
| G PRO X Superlight | Identification / experimental read-only | Experimental read-only |
| Other HID++ 2 devices exposing onboard profiles | Discovery / read-only when format is understood | Same policy |
| Keyboards, headsets, wheels, Bluetooth-only devices | No supported write driver | No supported write driver |

Unknown models are not writable. No "force write" switch. Catalog entries are reviewed
with protocol traces, sanitized fixtures and hardware reports; see [device support](docs/DEVICE-SUPPORT.md).
RGB, G-Shift editing, macro execution, receiver pairing and firmware update are outside this version.
Existing unknown/profile bytes are preserved. Restoring changed macro sectors is refused.

## Run

Use the self-contained archive for your OS/architecture, extract the entire directory,
and run `LightHub.Desktop` (`.exe` on Windows). A .NET SDK/runtime is not required for
that package. Use `--demo` to inspect the UI without writing or opening HID devices.

Packages are currently local build artifacts, unsigned and not automatically uploaded.
macOS .app/notarization and native installers are release gates, not implemented claims.
Linux needs HID access: see [platform setup](docs/PLATFORMS.md).

Select a device, read its profile, edit and confirm **Save to selected slot**. The write
confirmation lists the target, DPI/rate and changed bindings. Closing the window exits
the process. Do not run another hardware utility during a transaction.

Backups and transaction records live under the OS local application-data directory,
inside `LightHub`. Existing installations continue using `LightHubCommunity` so absolute
recovery journal paths remain valid. Neither directory is silently deleted or merged.
Export diagnostics omits unit IDs, raw memory, paths and user mappings. Backups contain
physical identity and mappings; do not attach them publicly without reviewing them.

The new `LightHub/2` backup format is intentionally separate from the old local
WinForms prototype. Old backups are rejected, not silently converted or applied.

## Build and test

Install the .NET 10 SDK, then from the repository root:

```sh
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test --no-restore
dotnet run --project src/LightHub.Desktop -- --demo
dotnet run --project src/LightHub.Cli -- list
```

Use `tools/publish.ps1 -Runtime win-x64` (PowerShell 7 on non-Windows) for self-contained
packages and source archives. `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` and
`win-arm64` are also publish targets, not claims of hardware validation.

Dependency locks are committed. GitHub Actions builds/tests on Windows, Linux and macOS
and uploads CI artifacts. It does not publish public releases automatically. See
[release gates](docs/RELEASING.md) before tagging a release.

## CLI

```sh
dotnet run --project src/LightHub.Cli -- list
dotnet run --project src/LightHub.Cli -- inspect ENDPOINT_ID
dotnet run --project src/LightHub.Cli -- backup ENDPOINT_ID backup.lhbackup
dotnet run --project src/LightHub.Cli -- restore ENDPOINT_ID backup.lhbackup --yes
```

Endpoint IDs identify an OS endpoint, not a permanent mouse ID. Re-enumerate after
replugging. Restore verifies physical identity again. The separate `smoke` command
temporarily edits an inactive profile and restores it; it requires the explicit
`--write-inactive-and-restore` argument. It is for hardware contributors, not CI.

## License

MIT. Dependencies and protocol references are credited in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Not affiliated with, endorsed by or supported by Logitech.
