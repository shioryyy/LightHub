# ADR-001: Avalonia and modern .NET

Status: accepted for the community architecture, 2026-09-07.

Reconfirmed by D05 of the [design baseline](design/README.md), 2026-09-08. The target
module boundaries, operation-specific capabilities and acceptance rules are specified
in [technology and architecture](design/02-architecture.md). Module changes remain planned.

The difficult part is device I/O, validation and recovery, not rendering HTML. The
project already has useful C# protocol knowledge and hardware observations. Using
modern .NET keeps that expertise while removing the Windows-only Framework/UI layer.

| Candidate | Advantages | Costs relevant here |
| --- | --- | --- |
| Avalonia + .NET 10 | One language, consistent renderer, headless tests, Windows/Linux/macOS, mature tooling | Larger self-contained package, Skia/native dependencies, more memory than old WinForms |
| Tauri + Rust + web UI | Strong native backend, capable web ecosystem, reasonable app packaging | Two language/tooling stacks, different system WebViews, WebKit dependencies on Linux; no automatic HID portability |
| Qt + C++ | Long desktop history, strong native I/O, platform coverage | C++ ownership/toolchain cost and LGPL/commercial deployment considerations |
| PySide + Python | Rapid iteration, Qt ecosystem | Python packaging/updates and larger runtime surface; no advantage for this existing protocol work |
| Electron | Consistent Chromium UI | Bundled browser and higher baseline overhead do not fit the product priority |
| WinForms/.NET Framework | Small local executable, quick Windows deployment | Windows-only UI and older high-DPI architecture |

Avalonia is MIT; HidSharp is Apache-2.0. HidSharp uses platform HID APIs without a
separately installed device driver or bundled proprietary SDK. The protocol library
does not reference Avalonia or HidSharp and can be reused by another UI later.

Distribution uses self-contained .NET binaries so end users do not install an SDK
or runtime. Trimming/AOT are not enabled until reflection and all native dependency
paths are validated. Runtime networking, auto-update and telemetry are absent.

The decision is not that C# is universally better than Rust. It minimizes migration
risk and keeps the protocol, CLI, tests and UI accessible to contributors through one
toolchain. Revisit if measurable deployment or platform limitations outweigh that benefit.
