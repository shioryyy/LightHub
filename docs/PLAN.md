# Community Product Plan

The authoritative design baseline is now [LightHub product and engineering plan](design/README.md),
version 1.0, decided 2026-09-08. It defines scope, technology, commands, data, recovery,
milestones and acceptance gates. Those decisions are not claims of implemented features.

The remainder of this file is the earlier architecture/product snapshot. Where its
scope or release gates differ, the design baseline takes precedence, including the
per-platform stability gates. [Product strategy](PRODUCT-STRATEGY.md) is retained as
the research/proposal history that informed the decision.

The [G HUB gap analysis](GHUB-GAP-ANALYSIS.md) records current workflow gaps and the
staged macro/profile/lighting plan, including backup management added in the latest iteration.

## Product contract

LightHub is an independent, offline desktop configuration manager for Logitech
HID++ gaming mice. A configured mouse must work without a LightHub process.
The UI, protocol library and diagnostics CLI serve multiple devices and operating
systems. No hard-coded device ordinal, fixed profile count, or name-only write gate.

G HUB also manages keyboards, headsets and wheels. Those require different drivers.
This project does not describe those product categories as supported until their
drivers, recovery paths and hardware evidence exist.

## Architecture decision

Choose .NET 10 LTS, Avalonia 12 and HidSharp 2.6.4. See ADR-001 for alternatives.
The previous .NET Framework/WinForms prototype remains a separate local reference.
This is a new public architecture and a new backup schema, not a cosmetic reskin.

Core boundaries:

1. `LightHub.Core`: pure HID++ framing, feature lookup, dynamic memory geometry,
   profile codecs, device support policy, checksummed snapshots, transaction journal.
2. `LightHub.Hid`: Windows/macOS/Linux HID access, descriptor-based report routing,
   receiver-slot enumeration, physical receiver leases and G HUB conflict detection.
3. `LightHub.Desktop`: Avalonia controls, editable draft, localizations, confirmation,
   backup/recovery and redacted diagnostics. No protocol byte offsets in the UI.
4. `LightHub.Cli`: reproducible read-only diagnostics and explicit hardware smoke tools.
5. Tests: fake transport for faults, variable synthetic devices, headless UI tests,
   separate hardware evidence records. Unit tests never open a real device.

## Support policy

Device discovery is broad, while write access is evidence-gated. Product IDs come
from the mouse's identity feature, not the receiver's product ID or device name.

- Validated driver: matching identity, device type, platform and memory-layout record.
- Experimental read-only: HID++ profile capability exists but no matching write evidence.
- Unsupported: cannot safely interpret the protocol/geometry; diagnostic error only.

Adding more IDs is not evidence of compatibility. Initial identification entries:
GPW1, G305/G304, G502 HERO and G PRO X Superlight. Only GPW1 Windows has local hardware
write evidence. These entries are neither complete device coverage nor firmware-wide
certification. A new model/platform requires a contributor report and review.

## Implemented in this iteration

- Cross-platform solution and locked dependency graph.
- Dynamic profile/button/sector counts, actual DPI capability and report-rate masks.
- Windows split collections and Linux/macOS shared-report transport paths.
- Multiple receiver discovery and wireless slots 1..6; direct devices at index 255.
- All-declared-sector backups, including factory-erased sectors.
- Protected primary click, normal mouse/key/media bindings, keyboard combinations.
- Conflict detection, same-unit recovery, pre-write backup, durable pending journal,
  data before directory, byte-for-byte verification, no write retries.
- Cancellation for discovery/reads; commits cannot be canceled mid-flash.
- Receiver-scoped interprocess leases across UI/CLI operations.
- Device selection, dynamic profile editor, English/Chinese, diagnostics, recovery UI.
- CI definitions for three desktop OSes; source and self-contained package scripts.
- License inventory, security/bug/device-report templates and release checklist.

## Before a stable 1.0

These are release gates, not promises that more code alone can satisfy:

1. Validate at least three mouse families on Windows, with multiple simultaneous
   receivers, multiple firmware versions, sleep/wake and physical remapping tests.
2. Obtain Linux and macOS hardware evidence. Run actual CI and installed packages
   there; merely cross-publishing from Windows does not verify those systems.
3. Exercise recovery on dedicated test hardware under power interruption. Do not
   deliberately interrupt writes on a contributor's only mouse.
4. Add backend-independent hotplug lifecycle tests and reliable selected-device
   reconnect behavior, preserving drafts without silently applying them.
5. Validate HiDPI, accessibility, keyboard navigation, macOS bundle/signing/notarization,
   Windows signing/installer, Linux packaging and upgrade/uninstall behavior.
6. Independent protocol/security review and contribution-based support-table review.

Versioning restarts at `0.2.0-alpha.1` for the community line. The old prototype's
`0.9.0 RC1` was not a meaningful public maturity signal. This release deliberately
does not claim stable 1.0 or universal G HUB replacement.

## Next independent drivers

After the standard profile driver is validated on additional mice, add extended DPI
and high-polling-rate profile formats, RGB, G-Shift and macros as separate feature
modules with raw-byte preservation tests. Pairing and firmware update remain out of
scope until there is a separate recovery design. No downloadable executable plugins.
