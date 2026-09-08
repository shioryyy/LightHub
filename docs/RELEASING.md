# Release checklist

For each preview or stable release:

- Review exact final source, dependency locks and license inventory.
- Build/test on Windows, Linux and macOS. Record actual CI URLs after execution.
- Run platform-native smoke tests of extracted self-contained packages.
- Run read/write/restore tests on supported hardware and record evidence.
- Review physical power cycle, sleep/wake, multiple receivers and button behavior.
- Review minimum window, real display scaling, keyboard access and screen-reader labels.
- Verify backups and transaction journals survive application failure.
- Confirm no application telemetry, background service or runtime network requests.
- Generate package file hashes, dependency manifest and full source archive.
- Ensure artifacts contain no local device IDs, backups, traces, credentials or test logs.
- Update version, release notes and precise support matrix; do not mark an alpha stable
  merely because the tests or build succeed.
- Sign Windows binaries and notarize macOS bundles for a broader stable distribution.
- Enable security reporting, branch protection and review requirements in GitHub.
- Upload draft release artifacts; publish only with maintainer authorization.

`tools/publish.ps1` produces artifacts but never contacts GitHub or publishes releases.
The checked-in workflow uploads CI artifacts, not public releases. Hardware changes are
not run in CI. Cross-publish success is not equivalent to native-platform validation.
