---
name: Device support report
about: Report hardware evidence for a new mouse, platform or firmware
labels: device-support
---

Reports in this category are the input for extending the device catalog. Writes are
enabled only per model + firmware + connection + platform with recorded evidence;
a feature request alone never enables a driver.

**Device** (exact model, e.g. G PRO Wireless):

**Firmware** (from LightHub `inspect` if readable, or G HUB):

**Connection** (receiver model + slot, wired):

**Platform** (OS version, architecture):

**What works** (identification / read / backup / ...):

**What fails** (with exact error text):

**Evidence** (see CONTRIBUTING.md):
- Redacted diagnostics export
- Before/after backups of any experiment (kept local unless asked; share only after
  removing personal data — backups contain unit identity and mappings)
- Protocol traces or read-back comparisons

**Willingness to run bounded hardware tests** (yes/no, what hardware you can spend):
