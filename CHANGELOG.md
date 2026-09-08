# Changelog

## Unreleased

- Fix nullable battery-voltage formatting; unavailable/zero voltage is shown as unknown.
- Refresh foreground runtime DPI/stage once per second and battery every 30 seconds;
  retain draft/conflict baseline and never write or read complete memory during polling.

### 0.3.0-alpha.1 engineering preview

- Rename the product to LightHub; keep legacy personal data paths for journal compatibility.
- Extract Application services shared by CLI and desktop. Save no longer implicitly activates.
- Preserve current DPI stage; require an explicit replacement when removing it.
- Scope mutation permissions to operation, firmware and transport evidence.
- Add guarded DPI preview lifecycle, local presets and retained drafts.
- Add backup names, pins, milestones and recoverable deletion; permanent removal is explicit.
- Reject restoration of unverified profile regions and changed macro pointers.
- Fix language-switch draft loss and add application, UI and cross-process regression tests.
- GPW1 inactive-save/restore and runtime-DPI regression passed after refactoring.
  Activation, physical input, native platform lifecycle and stable release gates remain outstanding.
- Generate Unix tar archives with explicit executable modes, including when built on Windows.

- Backup management: full list and storage totals, model/time/checksum/protection
  metadata, export/restore selection, multi-delete and manual keep-10-per-device cleanup.
  Recovery dependencies are protected and deletion is serialized against transactions.
- G HUB gap analysis covering macros, G-Shift, profile libraries, lighting, resident
  features and other device categories. Macro writing remains unsupported.
- Dedicated button page with GPW1 physical locations, selectable diagram and bilingual
  position names independent of the assigned actions. Other layouts retain numbered labels.
- Temporary current-DPI control for the validated Windows GPW1 path, with capability
  checks and immediate read-back; onboard profiles are saved separately.
- Prefer the last responding HID collection to avoid repeated idle-channel waits.
- Reuse the transaction's fully verified snapshot in the editor instead of reopening
  and reading all memory again. Preserve the selected profile after saving.
- Timed hardware smoke output, current-DPI smoke, and public device research notes.

## 0.2.0-alpha.1

New community architecture, separate from the local WinForms prototype:

- .NET 10 / Avalonia desktop application and HidSharp cross-platform transport.
- Independent Core, Hid, Desktop, CLI and test projects.
- Dynamic memory/capabilities, multi-device discovery, evidence-based write catalog.
- All-sector backups, same-device restore, transaction journals and full read-back.
- English/Chinese UI, diagnostics, profile editor and backup browser.
- Locked dependency graph, three-OS CI definition, source/self-contained packaging,
  license inventory and contribution/device-report workflows.

This is a development preview. Only documented hardware/platform combinations have
write evidence. No firmware updates, macro editing, RGB driver or universal-device claim.
