# Contributing

Use .NET 10 SDK and the committed lock files. Run `dotnet restore --locked-mode`,
`dotnet build --no-restore` and `dotnet test --no-restore` before opening a pull request.
`dotnet format whitespace --verify-no-changes` checks C# whitespace.

Keep protocol code in Core, OS/HID handling in Hid and presentation in Desktop.
Do not add UI or native dependencies to Core. Every device write requires validation,
pre-write backup, a pending journal and full read-back. Never retry a timed-out flash
write. Preserve raw bytes outside the feature being edited.

New devices use the process in `docs/DEVICE-SUPPORT.md`. Include platform and firmware
evidence. Default to read-only until write behavior is reviewed. Unit/CI tests must
never enumerate real hardware. Hardware tests require the explicit CLI argument.

English is the documentation baseline; user-visible labels also have Simplified
Chinese strings. Add corresponding translations when adding new controls. Technical
protocol errors may remain English; do not silently swallow them.

Dependency changes must update package locks and notices. Review license compatibility,
known vulnerabilities and platform/native asset behavior. Avoid network calls at runtime.

Pull requests should state the concrete failure or behavior change, affected models,
tests performed and tests not performed. A "works on my mouse" report is useful evidence,
but does not justify unrelated model/platform write support.
