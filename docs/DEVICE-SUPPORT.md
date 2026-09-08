# Device support contributions

The support catalog is embedded at build time in `src/LightHub.Core/devices.json`.
It contains identification facts and evidence-based write rules, not executable plugins.

To add a model:

1. Run the CLI `list` and `inspect`. Record model/product IDs, firmware, OS, direct vs
   receiver connection, memory geometry, features and actual capability lists.
2. Submit redacted diagnostics with the device support issue template. Do not submit
   a private backup containing macros/keystrokes or unit identity by default.
3. Add synthetic or explicitly sanitized fixtures covering the observed layout.
   A fixture needs a recorded source and expected decoder output, not just self-roundtrip.
4. Review parsing, directory semantics, active profile, DPI and rate encodings against
   protocol references. Product names are never the authority for device identity.
5. Build an identification-only catalog entry with no write platforms. A maintainer
   may create a local test branch enabling a driver after protocol review.
6. Run backup, inactive-profile edit, read-back and restoration on consenting test
   hardware. Record physical button behavior, power cycle, sleep/wake and firmware.
7. Submit evidence and tests for the exact platform. A reviewer decides whether to add
   that platform to the write rule. Do not carry Windows evidence into Linux/macOS.

Existing standard profile code supports bounded variable geometry. This is not an
assertion that all layouts sharing a format byte have identical behavior. The write
rule must match memory model, profile/macro format, profile/button counts and sector
geometry. The format-5 high-rate driver is not implemented.
Each mutation additionally requires the observed firmware, transport key and operation
in `firmwareWithEvidence`, `connectionsWithEvidence`, and `verifiedOperations`.
An activation or macro capability does not inherit standard profile evidence.

Read-only means no configuration-changing requests. HID++ reads still send USB reports
and may wake hardware. No automatic configuration is applied after detection.

Catalog knowledge references: libratbag device definitions (identity facts), HID++
reverse-engineered protocol implementations in better-logihub. See third-party notices.
