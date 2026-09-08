# Security

This application can modify device flash configuration. It does not flash firmware,
pair receivers, install services, elevate privileges or connect to a server.

Checksums detect accidental corruption; they do not authenticate backup authors.
Backups must match physical device identity and memory layout. Restore refuses changed
macro/unknown sectors without a validated recovery driver. Treat backup files as private:
they may encode keystrokes, macros and user mappings.

Report security issues through the repository's private vulnerability reporting feature
when the GitHub repository is configured. Do not include secrets in a public issue.
Before public launch the maintainer must enable private reporting and set an actual
contact; this local source tree does not invent a support email or organization.

Supported security updates currently target the latest development version only.
There is no stable release support commitment yet. Third-party HID tools are not
controlled by LightHub's process lock; avoid concurrent device-management software.
