# Predictable device configuration: product and refactoring proposal

Date: 2026-09-08 (Asia/Shanghai).
Status: historical research and proposal. Product decisions are now finalized in the
[design baseline](design/README.md), version 1.0, 2026-09-08. This document remains as
evidence and reasoning history; its proposed defaults are superseded where they differ.
Neither document claims that the planned behavior has already been implemented.

## Why revisit the plan now

The community implementation proved important protocol and recovery mechanisms, but
the recent feature comparison encouraged a list of missing G HUB features before
fully specifying the user workflows. An alternative should be judged by predictable
configuration and reliability, not feature-count parity with G HUB.

The existing offline/onboard product contract is a useful foundation. The remaining
problem is translating that contract into defaults, state transitions, storage models
and acceptance tests. Pause macro, G-Shift and RGB expansion until those boundaries
are agreed and the first targeted refactor is complete. A full rewrite is not justified
by the evidence currently available.

## Research: what the complaints actually establish

This was a qualitative review of three accessible Reddit discussions and current
Logitech documentation. The threads date from 2022-2023, not a representative survey
of current users or the current release. Search results were used for discovery;
the conclusions below use the original posts/comments read through public RSS.
Some additional threads were rate-limited and are not treated as reviewed evidence.
No numerical popularity ranking, failure rate or majority preference is claimed.

| Source | Evidence read | Design implication |
| --- | --- | --- |
| [Disable automatic profile switching?](https://www.reddit.com/r/logitech/comments/xh1i14/disable_automatic_profile_switching/) (2022-09-17) | G502 user wants the same three custom profiles across games; unwanted switching and difficulty finding the control. Onboard mode did not run their macros. Replies explain persistent profiles, while another explicitly wants reliable game switching. | Separate manual selection, fixed software execution and app-based switching. Do not assume every user wants the same mode or that all macros fit onboard. |
| [Is G HUB really that bad?](https://www.reddit.com/r/LogitechG/comments/wp23z8/is_g_hub_really_that_bad/) (2022-08-15) | OP and some replies report satisfactory operation. Other replies describe confusing profile ownership, unreliable switching after sleep, alleged update-related profile loss, resource overhead and oversized UI. One recommends storing settings onboard and removing the app. | Preserve configuration, make ownership visible, keep normal editing compact and test sleep/update paths. Complaints are self-reports, not independently reproduced vendor defects. |
| [G Hub is literally the worst software I have ever used](https://www.reddit.com/r/LogitechG/comments/15fao3w/g_hub_is_literally_the_worst_software_i_have_ever/) (2023-08-01) | G915 user reports difficulty performing the simple task of saving onboard lighting. A reply prefers software that works over lower RAM use alone. | A simple durable configuration workflow matters more than cosmetic lightness. Keyboard-specific evidence does not establish GPW feature support. |
| [Current G HUB FAQ](https://www.logitechg.com/en-us/software/ghub) (read 2026-09-08) | Describes the profile system as "off by default" and explains that an unconfigured automatic game profile starts from defaults, which can appear to reset settings. | Do not repeat the claim that all current G HUB installations enable switching by default. Distinguish actual data loss from activation of a different profile. |

Observed complaint themes are loss of control over active settings, unclear software
versus onboard ownership, difficult simple tasks, unreliable lifecycle behavior,
reported configuration loss and disproportionate background/UI overhead. There are
also satisfied users and users who value automatic switching. These observations
support an optional, explicit design; they do not establish that most owners only
want onboard mode.

Before claiming broad user validation, recruit participants across three groups:
set-once onboard users, manual multi-profile users and software-macro/app-switching
users. Test the same tasks, record completion/failures and revise the defaults based
on observed behavior. No participants have been contacted in this task.

## Proposed product contract

LightHub configures supported hardware with predictable, explicit effects. The
primary experience is onboard-first and offline. A user should be able to configure
the mouse, verify it and close LightHub.

Onboard-first is an application workflow preference. It does NOT mean silently
changing a device from host mode to onboard mode when the app starts or reconnects.

| Decision | Proposed default |
| --- | --- |
| Startup / discovery / reconnect | Read only; no configuration write, mode switch or activation |
| Profile ownership | Local presets belong to the user; onboard slots belong to the physical device |
| Saving an inactive slot | Save that slot; preserve the currently active slot and DPI state |
| Activation | Separate explicit command; do not preselect "activate on save" for every draft |
| Active-profile edits | Preserve the current DPI index when valid. If it becomes invalid, require an explicit replacement. Explain any unavoidable hardware reload effect and validate it. |
| Temporary adjustment | Explicit temporary operation with read-back; no flash write for every slider movement |
| App detection / automatic switching | Absent from the initial onboard product; off unless explicitly enabled in any later software mode |
| Fixed software profile | A separate future option; software macros must not force automatic app switching |
| Background startup | Off; no hidden resident process after an ordinary close |
| Cloud / account / telemetry | None required; local presets and backups remain usable offline |
| Updates | User-controlled, configuration-preserving and reversible before broad distribution; no automatic device writes as part of migration |
| Unsupported functionality | Clear capability status; never silently drop actions or pretend that an unsupported macro was applied |

Advanced software execution would be a distinct optional component, with documented
permissions, visible running state and an explicit stop action. Stopping it must release
held inputs and restore its defined fallback. It is deferred, not silently added to
the existing no-service/no-hook contract.

## Four operations that must not be conflated

1. Edit: modify a draft. No HID output and no device-state change.
2. Save: validate a draft, create the recovery material, write the intended persistent
   region and verify. Does not implicitly choose a different active profile.
3. Activate: select a known existing configuration deliberately. The UI identifies
   which physical slot and operating mode will become active.
4. Try temporarily: update a supported current setting without claiming persistence.
   Track its origin and provide Revert and Save to slot as distinct actions.

A temporary-preview session should capture its starting runtime state. Normal exit
from preview should revert after checking that this session still owns that state.
Disconnect, sleep, another writer or application termination can make restoration
impossible; never claim it occurred or queue a blind restoration for a replacement
device. This lifecycle is a proposed improvement over the current standalone temporary
DPI command, not a claim about its present behavior.

Saving an already active profile may necessarily affect runtime behavior on some
firmware. This must be tested and described accurately. The product rule is to avoid
unrequested changes, not promise effects that the hardware cannot support.

## State model and persistence

Keep four distinct data concepts:

- Physical snapshot: unit-bound, complete memory/state evidence used for conflicts
  and recovery. Preserve the current `LightHub/2` backup reader.
- Portable preset: named semantic settings with a version and capability requirements.
  It must not contain another mouse's raw memory or unit identity.
- Draft: edits based on a particular snapshot/preset, including validation and an
  explicit activation intention. Discarding it is not hardware rollback.
- Runtime state: active slot, mode, live DPI and any temporary-session ownership.
  A changed runtime state is not automatically persistent data corruption.

Connection/operation states should be explicit: disconnected, reading, ready,
editing, previewing, committing, verifying, conflict and recovery-required. Each
command has a defined set of allowed states. Reconnection invalidates runtime ownership
and requires a fresh identity check before mutation; drafts can remain available.

Continue using versioned structured files and atomic replacement where appropriate.
A database, event bus or plugin framework is not required just to name presets.
Backup retention and journal protection need one storage owner; separate viewmodels
must not invent independent deletion or recovery rules.

## Concrete refactoring findings

| Existing code | Why it matters | Proposed change |
| --- | --- | --- |
| `Workspace.LoadProfile`: resets `Activate = true` on every load | Editing an inactive configuration defaults to making it active when saved | Represent save and activation as separate user intentions |
| `OnboardDevice.Edit`: sets `DpiIndex = profile.DefaultIndex` even for a non-activating edit of the active sector | A button-only edit can reset the current DPI stage | Preserve the current index if valid; make changes explicit and test them |
| `Workspace.CanSetCurrentDpi`: `CanWrite && HasButtonMap` | A visual diagram and the full flash-write permission gate control a runtime feature | Define evidence/capability decisions per operation, independent of artwork |
| `Workspace` directly handles HID, drafts, backup lists, localization and diagnostics | New macros/presets would add more state to the same object and make failure tests difficult | Extract a small device-operation service and profile/backup viewmodels at concrete boundaries |
| `TransactionEngine` assumes standard profile/directory edits | Macro allocation and shared references do not fit safely into the current edit-scope checks | Keep the current driver; add a separately reviewed write plan/codec when macro recovery is designed |
| Every `Workspace.Run` calls synchronous `RefreshBackups`, which parses the full history | Even a quick runtime action can cause unrelated UI-thread disk work | Refresh affected storage views asynchronously on relevant changes; do not add arbitrary display caps |
| `MainWindow` responds to broad HID topology changes by invalidating the connection | Unrelated device changes can unnecessarily disrupt the editor | Compare the selected endpoint's identity/topology through a testable connection controller |

These are findings about code paths and design coupling, not evidence of a newly
observed destructive hardware failure. This review did not modify runtime code or
perform device writes.

Keep the tested C# protocol framing, geometry/CRC handling, raw-byte preservation,
identity checks, backup schema, write journal and fake-transport tests. The current
Core/Hid/Desktop/CLI split supports gradual change. Nothing found requires changing
the UI framework or rewriting everything in a different language.

## Migration sequence and stop conditions

### Phase 0: agree the behavior contract

Review this proposal, specify the initial release scope and prototype these flows:
change one DPI value, edit an inactive slot, save and close, restore a backup, reconnect
with a draft, and encounter an unsupported macro. Do not start by adding more tabs.

### Phase 1: capture current behavior and isolate commands

- Preserve an immutable source/build baseline and current backup fixtures before
  refactoring. An initial Git commit is still outstanding and is a separate action.
- Add focused tests for save/activate separation, current DPI preservation, no HID
  mutations during discovery, conflicts and pending recovery.
- Extract operation interfaces and viewmodel responsibilities without changing memory
  formats or relaxing checks. Keep both CLI and UI on the same transaction service.
- Make backup refresh asynchronous and operation-specific; use fault-injected tests
  for disconnect/reconnect, canceled reads and stale drafts.

### Phase 2: implement the revised onboard workflow

- Apply explicit save/activation semantics and preview lifecycle.
- Add named local presets with a separate versioned schema and capability-aware mapping.
- Preserve old backups and unknown bytes; do not migrate device memory on startup.
- Repeat GPW inactive-slot write/restore and physical active-state verification.

### Phase 3: add hardware features under the same rules

Read-only macro inspection first, then a narrow validated onboard macro write/recovery
driver. G-Shift and simple lighting follow only with evidence. Do not expose Apply for
an editor whose transaction/recovery path does not exist.

### Phase 4: optional software mode

Proceed only if user research justifies it. Specify manual fixed profile separately
from automatic switching, process lifetime, permissions, event injection, stop/release
behavior, conflicts and crash recovery. Keep the onboard application usable without it.

Stop and redesign an affected component if its intended operations cannot be expressed
without bypassing identity checks, overwriting unknown memory or weakening recovery.
A broader rewrite needs measurable evidence such as unfixable platform I/O limitations
or inability to isolate command/state ownership, not simply a long feature backlog.

## Acceptance gates

- Opening, selecting, scanning or reconnecting a device produces no mutating HID call.
- Saving an inactive profile leaves the active sector, mode and current DPI stage
  unchanged unless the user explicitly requested a transition.
- A button-only save preserves the current valid DPI index; invalidated stages require
  an explicit replacement and never silently select a different speed.
- A local preset is never treated as a raw cross-device recovery backup.
- A successful save means verified device state; temporary success is labeled separately.
- An interrupted write retains recoverable evidence and never enables silent retries.
- App updates preserve presets and backups and never reapply settings automatically.
- Unsupported macro opcodes/capabilities are reported without destructive normalization.
- Closing the onboard application leaves no process required for its saved mappings.
- UI immediately acknowledges commands; storage and HID work do not block rendering.
  Define numerical latency targets only alongside repeatable cold/warm measurements.
- Record read, preflight, backup, transfer, verification and UI-update timings separately.
  Existing samples (about 120 ms warm current-DPI command and 6.1 s full checked save)
  are observations, not promised future targets or whole-GUI benchmarks.

Automated checks must be followed by GPW1 physical button, power-cycle and sleep tests.
Other models and native Linux/macOS remain unverified until contributors or available
hardware provide evidence. Passing tests is necessary but not a complete product review.
