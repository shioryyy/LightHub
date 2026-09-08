# Platform setup

## Windows

Target: supported Windows 10/11 x64; ARM64 is a build target awaiting physical tests.
The self-contained package contains .NET and native rendering dependencies. Do not copy
only the executable. Standard USB HID drivers are used. No administrator privileges
are requested. Exit G HUB before writing. Unsigned downloads may trigger SmartScreen.

## Linux

Target: mainstream glibc desktop distributions with X11 or supported Avalonia Wayland
environment, fontconfig, libX11, libICE, libSM and libudev. Validate the exact package
on the target distro; the .NET runtime support matrix still applies.

`packaging/linux/70-lighthub.rules` grants access to active local desktop users through
udev `uaccess`, limited to Logitech hidraw devices. Inspect the rule before installation:

```sh
sudo install -m 0644 packaging/linux/70-lighthub.rules /etc/udev/rules.d/70-lighthub.rules
sudo udevadm control --reload-rules
```

Unplug and reconnect the receiver. Do not run the application as root and do not use
world-writable `MODE=0666` rules. `uaccess` requires the distribution's session/logind
setup; remote/headless sessions may need an administrator-managed group rule instead.

## macOS

HidSharp uses macOS HID APIs. USB access may require user approval on recent systems.
Architecture-specific self-contained outputs can be generated, but a signed/notarized
.app bundle and actual hardware validation remain release gates. A raw publish folder
is a developer artifact, not a polished macOS installer.

No platform receives write support just because its application launches. The current
catalog gates writes separately by platform. CI without USB hardware verifies logic,
rendering and packaging only.
