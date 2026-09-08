# G HUB comparison and implementation priorities

The [design baseline](design/README.md), finalized 2026-09-08, decides which gaps belong
in LightHub and their delivery gates. [Product strategy](PRODUCT-STRATEGY.md) preserves
the research behind those decisions. This comparison is a historical inventory, not
a requirement to copy every G HUB workflow or the authoritative release roadmap.

Review date: 2026-09-07. This compares the current source, including unreleased
iterations, with G HUB workflows. G HUB capabilities vary by device and firmware;
software-mode features are not automatically available in a mouse's onboard memory.

## Current position

LightHub is presently an experimental gaming-mouse onboard configuration tool, not
a feature-complete replacement for the G HUB ecosystem. The working foundation is
identity/capability discovery, basic profiles, normal bindings, guarded writes,
complete backups and verified recovery of supported profile sectors.

| Area | LightHub today | Remaining work | Priority |
| --- | --- | --- | --- |
| DPI and report rate | Capability-based stages, default/shift stage and 125-1000 Hz where reported; temporary DPI tested on Windows GPW1 | More direct stage interaction; extended DPI/high-rate features for newer devices | P1 |
| Physical button assignment | Mouse/key/media actions, one modifier combination; GPW1 physical diagram | Better action search/categories, shortcut capture, more model diagrams, physical input verification | P1 |
| Macro editor | Not implemented; existing unknown bytes preserved | Named macros, step list, press/release, delays, validation, import/export, button assignment and execution semantics | P1, staged |
| G-Shift | DPI Shift exists, but this is not a G-Shift mapping layer | Second binding table, hold activation, clear layer UI, model-specific preservation/recovery | P1 |
| Profile library | Edit reported onboard slots and activate a slot | Named local presets, duplicate/reset, import/export, compare, device-compatible application; onboard rename/disable only with validated encoding | P1 |
| Per-app profiles | Not implemented | Process/focus monitoring, default fallback and conflict handling; requires an optional resident component | P2, product decision |
| LIGHTSYNC | Not implemented; lighting bytes preserved | GPW brightness, static/off first; zones, animations, synchronization later | P2 |
| Backup lifecycle | New full list, size/model/time/status, export selected, multi-delete and manual keep-10 cleanup | User labels, pinned milestones, optional automatic retention and duplicate-content policy | P1 follow-up |
| Recovery | Full snapshots; same-unit profile/directory recovery, journals and pending-backup protection | Guided recovery UI; macro/unknown-sector restoration; do not label full backup as universal restoration | P1 |
| Connection and telemetry | Discovery, hotplug handling and point-in-time battery/DPI readings | Continuous status, signal/charging behavior, reliable sleep/wake, multi-device integration tests and recovery after disconnect | P1 |
| Software actions | Basic device-encoded keyboard/media bindings | App launch, text actions, complex sequences, scripting/Lua-like workflows; often requires host execution | P2/P3 |
| Device settings | Limited to validated mouse features | Model-specific sleep/power/sensor controls, newer polling formats and other reported capabilities | P2 |
| Ecosystem hardware | Four identification entries; only one Windows GPW1 has write evidence | Keyboard, headset, microphone, webcam, wheel and other drivers; audio EQ/voice processing are separate subsystems | P3 |
| Sharing/community | Local backup export; no online runtime | Portable preset format first; optional sharing without raw unit identity or memory dumps | P2/P3 |
| Distribution | Source and Windows preview; cross-publish previously exercised | Native platform testing, installer/update/uninstall, signing, accessibility/HiDPI and actual GitHub CI/releases | P1 before broad release |

P1 is the next practical work for the existing mouse product; it is not a promise that
all these items are completed in this iteration. P3 expands the product substantially.

## Macro implementation must have two explicit targets

### Onboard macros

The mouse executes supported events without LightHub running. This fits the current
product contract, but the firmware's supported events, capacity and repeat behavior
must be established independently. A G HUB software macro is not necessarily portable.

The local MIT better-logihub reference has `Macro`/`MacroStep`, encoder/decoder,
macro-sector packing and references from profile bindings. It includes press/release,
key, delay, consumer, text and wait-for-release concepts. These are useful protocol
references, not evidence that every operation is safe on our GPW1 firmware.

Current LightHub limitations are concrete:

- No macro data model, event editor, library, recording or execution engine.
- `TransactionEngine.EnsureEditScope` rejects macro-sector changes.
- `TransactionEngine.Restore` refuses differing non-profile sectors.
- Macro references, shared ownership, allocation, sector overflow and interrupted
  updates are not handled. Changing a pointer before its data is durable could leave
  a broken binding. Shared macro sectors must not damage other profiles.
- A keyboard combination such as Ctrl+C encodes one chord. It is not a timed macro.

Implementation order:

1. Read-only macro discovery: parse existing pointers/steps with strict bounds,
   preserve unknown opcodes and display unsupported formats without rewriting them.
2. Local macro library/editor: named events with press/release/delay; validate balanced
   key state, event limits and total duration. Clearly identify local-only drafts until
   hardware application exists. No global input capture is required for manual entry.
3. Dedicated macro write/recovery driver: establish allocation, reference tracking,
   data-before-pointer ordering, copy-on-write where feasible and complete rollback.
   Test corrupt pointers, shared sectors, full capacity and interrupted transfers.
4. GPW1 trial using an inactive profile: backup, write, full read-back, restore, then
   controlled physical triggering and power-cycle checks. Never infer success solely
   from byte round-trips. Expose only verified execution modes.
5. Add broader event types, repeats and other models when supported by evidence.

### Software macros

LightHub would execute events while running. Hold/toggle repeats, app launching,
per-app actions and scripting can depend on a resident process, per-platform input
injection and event capture. Stop/cancel and releasing held keys on error are mandatory.
This requires a product/architecture decision because the current contract promises
onboard operation without a background service or global input hooks. Do not silently
add that dependency while implementing a macro editor.

## Backup changes in this iteration

- Removed the 100-entry display cap. Display model, time, size, checksum/readability
  and recovery protection, plus total count/size.
- Select and export an existing backup without a connected device. Restore selection
  explicitly or use the existing file picker; device identity checks remain in force.
- Select multiple backups and confirm permanent deletion. The confirmation lists
  the exact files. No existing user backups were deleted by the development task.
- Manual cleanup previews older backups while keeping the newest 10 per physical
  unit, all unfinished-transaction references and unreadable backups. It is not a
  scheduled automatic retention job.
- The core checks both `BackupPath` and `BeforePath` again at deletion time. A shared
  cross-process storage lock prevents deletion during transaction backup/journal
  creation and commit. Invalid journals fail closed for cleanup.
- Management refuses external paths and linked files/directories. Export cannot
  overwrite the application's managed backup/recovery storage.
- Completed transaction journals remain as history; this iteration does not prune
  journals, raw recovery captures or log files. File-system I/O errors can interrupt
  a deletion batch after some files were removed; the UI refreshes the remaining list.
- Ordinary flash saves still create a recovery backup. No-op applies and temporary
  current-DPI changes do not create a flash-save backup.

## Recommended sequence

1. Finish this backup-management iteration and exercise the new UI locally.
2. Build a portable named profile library and read-only macro inspection together;
   use the GPW1 as the reference device and keep all other models evidence-gated.
3. Implement the narrow onboard macro driver and its recovery before exposing Apply.
4. Add G-Shift and simple GPW lighting as independent capabilities.
5. Decide whether to introduce an optional resident software mode for per-app switching
   and software macros; continue the hardware validation and distribution work.

## References checked

- [G HUB overview](https://www.logitechg.com/en-us/software/ghub): supported ecosystem,
  community presets and automatic profile behavior.
- [G HUB basics](https://www.logitechg.com/en-us/software/guides/g-hub-basics): assignments,
  sensitivity, LIGHTSYNC, device and application profile workflows.
- [KEYCONTROL](https://www.logitechg.com/en-us/software/guides/keycontrol): layered keyboard
  assignments, macros and G SHIFT. Keyboard capabilities must not be attributed to GPW1.
- [LIGHTSYNC](https://www.logitechg.com/en-us/software/guides/lightsync-rgb): lighting workflows.
- [better-logihub onboard implementation](https://github.com/cUDGk/better-logihub/blob/71bf2b052ef6cd887c3be6d16144cb018edfe089/src/onboard.rs): macro representation/encoding reference.
- [Public device research](DEVICE-RESEARCH.md): libratbag, Piper, Solaar and model-specific limits.
