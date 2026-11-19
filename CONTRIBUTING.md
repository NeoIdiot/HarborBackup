# Contributing to HarborBackup

Start with [Getting started](docs/development/getting-started.md), [AGENTS.md](AGENTS.md), the [current transport design](docs/architecture/onedrive-account-transport.md), [storage layout](docs/architecture/storage-layout.md) and [verification workflow](docs/development/verification.md). The getting-started guide covers installing the SDK and bootstrapping packaging tools; no existing `artifacts` cache is required.

## Set up and check a change

Use a Git checkout on Windows, Git, PowerShell 5.1 or later, and the SDK selected by [global.json](global.json). From the repository root:

```powershell
. ./scripts/build/Use-DotNet.ps1
& $HarborDotNet --version
& $HarborDotNet restore HarborBackup.slnx --configfile NuGet.Config --locked-mode
& $HarborDotNet build HarborBackup.slnx -c Release --no-restore
./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile
```

Install the SDK before running these commands. `Use-DotNet.ps1` selects `dotnet` and places package/cache output under `artifacts`; it does not download an SDK. The first restore requires network access to NuGet. Building and normal synthetic tests require neither Inno Setup nor a real OneDrive account.

Before the test command, exit every HarborBackup desktop instance from the tray and wait for CLI operations to finish. Tests share the production run-lock name and run the four test projects serially. `-IsolatedProfile` does not isolate that mutex. Do not start the installed application during a test run, and do not run build/release/installer workflows concurrently.

Add tests for behavior changes and run the affected checks. Run the full test entry before proposing a change to transfer, persistence, recovery, scheduling or shared UI behavior. For documentation-only changes, check links and command accuracy. State exactly what you ran and what remains untested; passing source tests is not installation or real-account acceptance.

## Preserve the design contracts

- Keep the five product projects: Core, Windows, OneDrive, Desktop and Cli. Core has no platform/host dependency; Windows owns local file effects; OneDrive owns Graph and Windows-user-protected authorization. Desktop and CLI share use cases.
- Backup never writes the source; Pull never writes cloud business contents. Neither propagates deletion. Protect different contents before replacement and commit a common baseline only after verification.
- Use account transport only. Do not add a fallback to a locally synchronized OneDrive folder. Preserve ordinary cloud files and the current history/conflict rules.
- Treat `state` as recovery evidence and `cache` as rebuildable optimization. Cleanup must preserve state, archives and unresolved conflicts. Validate device, project, mapping, paths and format before recovery.
- Keep feature directories compact and introduce abstractions for actual use cases. A format, protocol, configuration or installation-layout change must update its current design document.

Current formats and sanitized examples are in [config/examples](config/examples/README.md). The 0.4 / PowerShell implementation is historical reference, not a compatibility or migration requirement.

## Keep development isolated

Use synthetic files and an independent temporary directory or this checkout's ignored `artifacts` tree. Do not change installed application data, real scheduled tasks, production backups or scripts outside the repository as part of development. Real OneDrive authorization and performance probes are separate, explicitly scoped checks; they are not required for the default regression suite.

Do not commit credentials, tokens, local configuration, machine identities, real business paths, logs, screenshots containing private data, NuGet caches or build output. Public screenshots should use synthetic project names and paths. Share a minimal sanitized reproduction when reporting a failure.

## Prepare a contribution

Keep commits focused on a task. Describe the concrete problem, resulting behavior, verification and remaining limits in the pull request. Update both UI language catalogs when adding user-facing strings; keep the Chinese and English user guides consistent when behavior changes.

Keep existing versioned validation records as audit history. Add a new record for new evidence instead of rewriting a past result to describe current code. Privacy redactions may replace private report-name prefixes with explicit wildcards while preserving the original directory, date, source identity, scope and result; do not invent replacement evidence.

The project uses the [MIT License](LICENSE). Preserve the source and license notices for third-party code or assets and document licenses when proposing dependencies. Do not remove existing third-party notices.

Use [SUPPORT.md](SUPPORT.md) for ordinary questions and [SECURITY.md](SECURITY.md) for vulnerability reporting. Review the [privacy notes](PRIVACY.md) before sharing diagnostic material and the [public-release checklist](docs/development/open-source-checklist.md) before preparing public assets.

Publishing and installer creation are separate maintainer steps. They require a clean committed source tree, locked dependencies/runtime versions and the pinned compiler. See [Getting started](docs/development/getting-started.md#package-and-validate-an-installer) and the [verification workflow](docs/development/verification.md). A local build does not authorize publishing a release or asserting that it passed installation acceptance.
