# Build and test report

Environment: Windows 11 x64, .NET SDK 10.0.400, 2026-09-07.

Completed:

- New-backend Windows GPW1 inspect, backup and inactive-profile write/restore smoke
  passed on one unit. A fresh connection produced an identical complete backup.
  See gpw1-windows.md for firmware, scope and remaining hardware checks.
- Locked NuGet restore, Debug/Release build, warnings treated as errors.
- 61 xUnit v3 tests passing in the unreleased backup-management iteration, including
  Avalonia headless tests, current-DPI guards, untouched-sector verification, deletion
  protection, per-device retention, export and backup-selection behavior.
- Synthetic memory layouts: 1/3/5/8 profiles, 6/8/11/16 buttons, 255/256/512-byte
  sectors and 8/16/32 declared sectors; unknown models/platforms remain read-only.
- Fault tests for stale state, corrupted bytes, interrupted transfer, bad read-back,
  wrong physical identity, cancellation and recovery restrictions.
- English/Chinese headless renders at 960x680, 1120x800 and 1440x1000.
- Native Windows desktop screenshot inspection and launch.
- Self-contained Windows x64 package and CLI launch with DOTNET_ROOT deliberately
  pointing at a nonexistent runtime. No user SDK/runtime installation required.
- Linux x64 and macOS ARM64 self-contained cross-publish from Windows.
- NuGet vulnerability query including transitive packages reported no known vulnerable
  packages from the configured source at query time. This is not a security audit.

Not completed here:

- Linux/macOS native startup, display/input behavior or USB hardware tests.
- Remote GitHub Actions execution; workflow definitions are supplied but no CI run URL
  exists until this source is pushed to a GitHub repository.
- New-backend GPW1 physical button tests, power-cycle persistence, sleep/wake,
  wired mode and UI hotplug tests.
- Real high-DPI multi-monitor tests, screen-reader evaluation, multi-device physical
  tests, power-interruption recovery and independent code review.
- Signing, notarization, installer/updater, public GitHub release.

Observed early Windows self-contained ZIP size: approximately 71 MiB, including shared
runtime and CLI. Debug/demo working set was roughly 169 MiB on this workstation; final
release memory depends on rendering, fonts, hardware and OS. The old prototype's tiny
package/memory numbers do not apply to this architecture.
