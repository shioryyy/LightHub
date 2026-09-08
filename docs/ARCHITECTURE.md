# Architecture and invariants

This file describes the existing implementation. The finalized target design is in
[technology and architecture](design/02-architecture.md) and [operation contracts](design/03-behavior.md).
The Application library now owns transactions, backup management, local assets and
DeviceSession orchestration. Desktop and CLI use the same save/activate/preview/restore
services through IDeviceAccess; Hid supplies the native adapter. Remaining design gates
are recorded in [the refactor report](validation/refactor-20260908.md).

The Core library references only the BCL. Native USB access and UI controls are outside
its dependency boundary. Core code is tested with a fake report transport and synthetic
devices with different profile, button and sector counts. No test requires this user's
files, HID receiver, account, development directory or machine serial number.

## Discovery

HidSharp reconstructs/parses report descriptors to find HID++ output reports 0x10/0x11.
Windows collections are grouped by physical path with collection suffixes removed;
VID/PID alone never groups two physical receivers. On Linux/macOS a shared-report HID
endpoint is used directly. Known receivers probe slots 1..6; direct devices use 255.
The device's feature 0x0003 returns the unit and transport product IDs used by the catalog.
Only devices exposing onboard profile feature 0x8100 enter the profile manager.

## Transaction boundaries

A receiver-scoped file lease excludes other LightHub UI/CLI access under the same user.
Known G HUB processes block writes. Arbitrary vendor software is not globally locked.
The UI runs a single device operation at a time and disables window close during it.
Discovery and full reads are cancelable between bounded HID calls; flash commits are not.

The UI owns an immutable baseline and an editable draft. Before writing, the backend
reopens the endpoint, rechecks physical identity and layout, validates every changed
profile against live DPI/rate capabilities, and compares the entire memory AND active
state with the baseline. Unknown bytes cannot be changed through the edit API.

Backup is durably flushed and reloaded before the pending journal is created. Profile
sectors precede the directory. Each sector is re-read immediately before writing,
written once and compared byte-for-byte afterward. No timeout triggers a flash retry.
Only runtime state differing from the desired state is restored. Complete memory,
active state and expected sensor DPI must match the request.

Failure retains a journal referencing the pre-write backup. Explicit restore can repair
bad-CRC profile sectors but requires a valid matching backup and saves current raw data
first. It refuses unknown profile regions, changed macro pointers and other-sector
differences pending a validated recovery driver.
Complete declared memory is backed up; full memory backup does not imply full memory
write permission. Factory-erased all-FF sectors are preserved without inventing CRCs.

## Format evolution

The backup envelope is `LightHub/2`: UTF-8 JSON payload plus SHA-256 of that payload.
The payload contains schema version, physical identity, raw and decoded geometry,
active state and all declared sectors. CRC-CCITT-FALSE applies to written sectors.
This is integrity checking, not cryptographic authentication of an author.

Unknown geometry is bounded before allocation/read. Unverified device/platform pairs
remain read-only. The standard mouse codec handles its known profile layout; extended
formats must get a separate codec and tests. Old WinForms backups are explicitly rejected.

## Runtime behavior

No network clients, services, autostart entries, global input hooks or auto-update loop.
Device handles are opened for an operation and closed afterward, except an active
temporary DPI preview holds a short-lived session and receiver lease. Connected idle UI
does not poll HID. HID topology notifications are debounced on the UI thread. When idle
the selected endpoint disappears, changes are retained and writes are blocked until
an explicit refresh. Unrelated HID additions do not discard drafts or invalidate it.
Wireless sleep behind an unchanged receiver may require manual refresh. Actual
platform hotplug behavior remains a hardware release gate. Drafts are never auto-applied.

Diagnostics are generated from a fixed allowlist of model/firmware/capability fields.
Raw memory, unit ID, OS endpoint path and bindings are not exported in diagnostics.
Private backup data is excluded by the source/package scripts and gitignore.
