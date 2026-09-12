# G PRO Wireless / Windows evidence

The 2026-09-08 Application-layer refactor passed a new inactive save/restore and runtime
DPI regression. See [the new acceptance record](refactor-20260908.md) for exact scope
and timings. Slot activation, physical input and power-cycle gates remain open.

Date: 2026-09-07. This document separates earlier protocol evidence from tests of the
new community implementation. It is not a certification for every GPW1 firmware.

## Earlier local prototype

The separate .NET Framework + native HIDAPI prototype read this physical GPW1 on a
C539 LIGHTSPEED receiver. Mouse IDs include 4079/C088; memory model 1, profile format 3,
macro format 1, five profiles, eight buttons, 16 sectors of 255 bytes. Current firmware
reported maximum DPI 25600.

It temporarily changed inactive profile 5's DPI, polling rate and a keyboard binding,
read back exact bytes, restored the original configuration and verified it again with
new HID handles. After a user-confirmed off/on power cycle, the backup and active state
matched the original baseline exactly. Those results establish protocol/geometry
evidence and underpin the narrow Windows GPW1 write rule.

## Community implementation

The new implementation uses HidSharp, feature-derived identity, capability decoding,
full-declared-memory snapshots and a new transaction engine. It is a different backend.

- Native Windows receiver and HID report descriptor discovery runs successfully.
- Non-HID++ keyboard/mouse collections are filtered before parsing.
- Earlier in this development run the receiver repeatedly reported resource error 0x09
  for wireless slot 1. Both the earlier prototype and independent better-logihub
  reference reported the same unreachable mouse state.
- A later run on 2026-09-07 successfully discovered the awake mouse and completed
  inspect, backup, smoke and a second backup using separate CLI processes.
- Firmware BOT 74.02.0026, mouse IDs 4079/C088 and receiver C539 reported the geometry
  described above. The smoke command changed inactive sector 5's DPI, polling rate
  and keyboard binding, verified the modified profile, restored the baseline and
  reported `PASS: modified inactive profile and restored complete baseline.`
- Before/after backup payloads and their SHA256 values matched exactly, including
  all 16 sectors, mode 1, active sector 1 and DPI index 2. The final backup used a
  fresh device connection. Personal backups remain local and are excluded from Git.
- Synthetic transport tests cover the GPW1 geometry, capabilities, write ordering,
  all-FF unused memory, stale active state, CRC corruption and interrupted writes.

The Windows GPW1 write driver now has new-backend write/restore evidence on this unit.
The backend remains **experimental**: physical input, power-cycle persistence and the
remaining scenarios below have not been validated with this implementation. This
result does not extend write support to other models or platforms.

## Regression procedure and remaining checks

### Slot activation and lifecycle evidence, 2026-09-12

Same unit (BOT 74.02.0026, C539 receiver slot 1, mode 1, active sector 1, 1600 DPI,
battery 4122 mV), alpha.2 candidate source 701802e, Windows 10.0.26200, tester present.

- Bounded activation via the engineering probe: preflight `plan` then
  `activation --execute-and-restore --hold-seconds 60`. The probe wrote only the
  directory enable flag and CRC for slot 2 (default 800 DPI), switched the active
  slot, verified by full read-back, held 60 seconds, then restored sector 1 /
  1600 DPI and the original directory. Read-back evidence:
  `artifacts/hardware-validation/activation-838f63e91d2f4c48a9baaee966e58094.json`
  (private recovery backup path recorded there; `physicalInputVerified` stays false
  in the file because the tester's observation is recorded here instead).
- Tester observation during the hold: movement speed was clearly slower (1600 → 800),
  left/right/side buttons all behaved normally, no other anomaly. This is the physical
  input evidence for this bounded activation path.
- Lifecycle persistence: a semantic preset (derived from the baseline backup with the
  production codec, scratch tool under `artifacts/lifecycle/`) was saved to disabled
  slot 5 through the ordinary CLI save path — DPI stage 1 400 → 450, rate 1000 → 500,
  bindings unchanged. The tester then performed a normal power off/on. A fresh-process
  backup confirmed the written config persisted (sector 5 decoded to 450/500) while
  every other sector, mode, active slot and sensor DPI matched the baseline.
- The baseline was then restored through the ordinary CLI restore path with fresh HID
  handles; a further fresh-process backup verified sector 5 back at 400/1000, no other
  sector differences and identical active state.
- `dpi-smoke` on a cold connection: current DPI changed, verified and restored in
  465 ms including session open; onboard memory and active state unchanged.

Remaining for this unit: sleep/wake and receiver-reconnect behavior with the desktop
app open (read-only reconnect, drafts retained, stale writes refused), USB wired mode,
multiple simultaneous receivers, and UI hotplug. Production `Activate`/`EnableProfile`
permissions stay closed pending the independent review gate; the probe's temporary
permission never leaves its own process.

### Unreleased interaction/performance iteration, 2026-09-07

- The updated transport and transaction engine passed another inactive-sector smoke:
  apply including full preflight and final verification took 6116 ms; restore including
  full verification took 6089 ms. All memory and active state matched the baseline.
- The `dpi-smoke` command changed current DPI through feature 0x2201, function 3,
  and read it back in 120 ms on an already-open connection. It restored the original
  current DPI and verified all 16 onboard sectors and active state were unchanged.
  This is a temporary sensor setting, not a persistence test. The GUI also spends
  time opening a connection and reading identity/capabilities; it displays total time.
- A complete CLI backup including startup and discovery took 6.76 seconds before
  transport changes and 3.81 seconds in an initial Release sample afterward. These
  individual observations are not a controlled performance benchmark or a latency SLA.
  A subsequent Debug sample took 4.70 seconds while tests were running; its complete
  payload still matched the pre-iteration backup after both hardware smoke tests.
- The editor now uses the transaction's actual final read-back snapshot and retains
  the selected profile. It no longer adds a third full snapshot after a successful save.
- Physical button positions are based on public Piper metadata. Actual input and
  removable side-button behavior still need user testing.

With an awake GPW1 and other management tools closed:

```sh
LightHub.Cli list
LightHub.Cli inspect ENDPOINT_ID
LightHub.Cli backup ENDPOINT_ID before.lhbackup
LightHub.Cli smoke ENDPOINT_ID --write-inactive-and-restore
LightHub.Cli dpi-smoke ENDPOINT_ID
LightHub.Cli backup ENDPOINT_ID after.lhbackup
```

The smoke operation changes an unused profile and restores it. Compare all sectors and
active state. Then test physical buttons, a changed configuration across power-off,
sleep/wake, USB wired mode, multiple simultaneous receivers and UI hotplug behavior.
Keep backup files local; do not publish unit identity or mappings in the evidence report.
