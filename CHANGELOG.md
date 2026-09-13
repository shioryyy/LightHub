# Changelog

## Unreleased

- Candidate 0.3.0-alpha.2: focused physical-button editor with action search, explicit
  save targets and compact DPI-stage cards. Device control metadata lives in the catalog.
- Preserve current sensitivity when disabled DPI stages are compacted; reject disabled
  default/Shift references and keep sparse or invalid edits intact across language changes.
- Prepare separately gated slot enable/activation with directory-only updates and
  recovery tests. Production activation permissions remain closed pending hardware evidence.
- Handle HID++ 2.0 INVALID_FEATURE_INDEX during optional feature discovery without
  hiding other protocol errors.
- Fix headless dispatcher initialization and macOS parent-path aliases; exercise
  Windows reparse boundaries with junctions when symbolic-link privileges are absent.
- Include source commit/dirty provenance, verify package contents and reconstruct
  the source archive in CI. A source archive's commit remains a declaration.
- Add an offline macro library and bilingual manual editor: bounded keyboard steps,
  reordering, duplication, import/export, retained invalid drafts and recoverable deletion.
- Protect macro saves with revision checks and a library lease; preserve unknown
  formats and isolate corrupt files. Local saves never write to the device or execute input.
- Add read-only `trigger-inspect` CLI diagnostics; no diversion, remapping or input capture.
- Keep onboard macro writing, binding and the software runner disabled pending evidence.
- Exclude personal macro/draft files from source archives and build manifests.
- Use a shared headless test dispatcher for Skia's render loop; retain isolated test data/windows.
- Fix nullable battery-voltage formatting; unavailable/zero voltage is shown as unknown.
- Runtime values refresh after explicit actions; the idle UI does not poll HID.
- Bind the status progress bar to the busy state so the idle window runs no animation.
- Show what a macro-pointer button binding refers to in the device editor, read
  only and in both languages: decoded steps for complete macros, and verbatim
  state reporting for blank, unsupported, truncated, looping or damaged content.
- Add issue templates, including a structured device-support report that feeds the
  evidence-gated catalog process.
- Guide recovery when an unfinished transaction is detected: the backups page now
  lists each pending transaction with its recorded backup file, error and device,
  with a direct restore action that keeps identity checks. Writing stays locked
  until the transaction is resolved.
- Compile validated local keyboard macros into onboard macro-format-1 sector
  images with reference packing rules (alignment, jump reserve, terminator) and
  strict capacity refusal. Pure local functions for a future, separately gated
  write driver; verified by round-tripping through the read-only parser. Nothing
  writes to a device.
- Read-only onboard profile names (memory model 1 layout A): the editor shows a
  stored name next to each profile slot and `inspect` lists it; undecodable content
  is reported as absent. Renaming still requires a validated write driver.
- Redacted diagnostics now summarize onboard macro coverage as counts and states;
  step content, profile names and unit identity stay excluded. The app version line
  no longer names a stale release.
- Add read-only onboard macro inspection (M4 step one): strict-bounds parsing of
  macro-format-1 sectors and binding pointers, with unknown opcodes, truncated
  streams, loops and CRC damage reported as states instead of guesses. New
  `macros <endpoint-id>` CLI listing; no byte is ever rewritten.
- Harden the engineering probe and release verification after independent review:
  corrupt-state restore entry, explicit warm-dpi execute flag with durable recovery
  markers, and full archive-content checks in verify-package.

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
