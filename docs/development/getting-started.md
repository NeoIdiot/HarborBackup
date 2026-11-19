# Getting started

This guide starts from a fresh Windows Git checkout. Run commands in PowerShell at the repository root. The standard build and tests use synthetic data and do not require a OneDrive account or an existing `artifacts` directory.

## Install the prerequisites

1. Install Git and obtain a Git checkout of this repository. The test and release scripts record Git source identity, so use a checkout with `.git` rather than a source ZIP for those workflows.
2. Use Windows x64 with Windows PowerShell 5.1 or PowerShell 7.
3. Install the Windows x64 **.NET SDK**, following [global.json](../../global.json). The current baseline is `10.0.100` with `latestPatch` roll-forward, so it accepts the `10.0.1xx` feature band. A runtime-only installation or only a different SDK feature band does not satisfy this selection. SDK installers are available from the [official .NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

An IDE is optional. Inno Setup is needed only for creating installers and is bootstrapped later in this guide.

Prepare the current shell:

```powershell
. ./scripts/build/Use-DotNet.ps1
& $HarborDotNet --list-sdks
& $HarborDotNet --version
```

`--version` must succeed from the repository root and resolve an SDK allowed by `global.json`. If `dotnet` is not found, install the SDK or add its executable to this shell's `PATH`, then open a fresh shell. The helper uses `dotnet` from `PATH`, falling back to the standard Program Files installation.

`Use-DotNet.ps1` does not install an SDK. It sets `DOTNET_CLI_HOME` to `artifacts/dotnet-home` and `NUGET_PACKAGES` to `artifacts/nuget`, and disables first-use extras for this shell. These directories are created as needed and remain ignored by Git.

## Restore and build

```powershell
& $HarborDotNet restore HarborBackup.slnx --configfile NuGet.Config --locked-mode
& $HarborDotNet build HarborBackup.slnx -c Release --no-restore
```

The first restore downloads packages from NuGet using [NuGet.Config](../../NuGet.Config); a warm cache is not assumed. Keep `--locked-mode` for ordinary development. If a dependency intentionally changes, update the affected lock files and include them with the change instead of bypassing a lock mismatch.

Outputs are under each project's `bin` / `obj`; package caches and workflow reports live under `artifacts`. Both are ignored. Warnings are treated as errors by [Directory.Build.props](../../Directory.Build.props).

## Run a development build

Exit an already-running HarborBackup instance before starting another build of the desktop application. Development and installed applications share instance/run coordination even when their data directories differ.

```powershell
& $HarborDotNet run --project src/HarborBackup.Desktop/HarborBackup.Desktop.csproj -c Release --no-build
```

The desktop remains in the tray when its window closes; use the tray's Exit command when finished. A development output uses its own `data` directory beside the binaries. Do not point a development build at installed or production data.

To inspect CLI syntax without connecting an account:

```powershell
& $HarborDotNet run --project src/HarborBackup.Cli/HarborBackup.Cli.csproj -c Release --no-build -- --help
```

Plain source builds do not embed the release application's public OneDrive Client ID. They can compile and run synthetic tests without it. Real login requires a valid public application ID via the advanced account settings or the `HarborBackupOneDriveClientId` build property; never use a client secret. See the [login configuration notes](default-onedrive-login.md). Use separate synthetic project files and a dedicated test cloud folder if explicitly testing a real account.

## Run the regression suite

Exit HarborBackup from the tray and wait for CLI previews/transfers to end. Keep it closed during the run:

```powershell
./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile
```

This command restores locked dependencies, builds the solution and runs Core, Windows, Desktop and Integration tests serially. It also checks artifact cleanup, source identity, test preconditions and process-interruption recovery using synthetic storage. It does not need real credentials and does not test real Graph transfers.

`-IsolatedProfile` redirects part of the user profile; it does **not** isolate the production mutex. If preconditions fail, exit the application yourself and rerun. The script does not close a production process for you. Do not run this command concurrently with publishing, packaging, installer validation or another test run.

After a successful restore, `-NoRestore` can reuse dependencies for later runs. Start without that switch on a fresh checkout. Results are stored in per-run directories under `artifacts/test-results`; successful complete runs include `completed.json`. Retention may remove older batches. Do not commit caches or reports.

For targeted tests, keep test projects serial and follow the same preconditions. Use the full entry point for final validation of changes that affect shared behavior. The [verification workflow](verification.md) explains the checks, limits and optional performance probes.

## Package and validate an installer

Packaging is optional for ordinary contributions. It needs the pinned runtime packages, a clean committed Git tree and the pinned Inno Setup compiler. Source changes during publishing or packaging invalidate source identity.

Download and extract the compiler into the repository's ignored tools directory:

```powershell
./scripts/release/Get-InstallerCompiler.ps1
```

This bootstrap downloads Inno Setup `7.1.0` from its upstream GitHub release, verifies the pinned SHA-256 and publisher signature, then installs the portable compiler under `artifacts/tools/inno-7.1.0`. It does not require a pre-existing tools cache. `Build-Installer.ps1` uses that compiler by default; a custom `-Compiler` path must report the same pinned version.

After committing the intended source, resources and documentation:

```powershell
./scripts/release/Publish.ps1 -IsolatedProfile
```

Publishing restores pinned self-contained `win-x64` runtime packs, verifies their hashes and produces a source-identified payload under `artifacts/publish`. It reads the public application ID and runtime pins from [packaging/windows/release.json](../../packaging/windows/release.json). A fork must review its own OneDrive application registration before distributing login-enabled builds. An intentionally unconfigured offline development payload needs the explicit `-AllowUnconfiguredOneDrive` switch and cannot provide default login.

Use the directory printed by `Publish.ps1` as `$publishDirectory` below, then use the exact validation-installer path printed by the first command as `$validationInstaller`:

```powershell
./scripts/release/Build-Installer.ps1 -PublishDirectory $publishDirectory -ValidationBuild
./scripts/test/Test-Installer.ps1 -Installer $validationInstaller
./scripts/release/Build-Installer.ps1 -PublishDirectory $publishDirectory
```

The two variables are paths to output from the preceding commands, not preinstalled tools or checked-in artifacts. Keep HarborBackup closed while running installer validation. The validation installer is a separate build without production registration or shortcuts; the script installs under `artifacts/install-validation` and checks lifecycle and data preservation using synthetic files. Do not give it a normal production installer.

Publish, packaging and test workflows manage ignored output through the repository retention policy. They do not publish a GitHub release automatically. Keep release claims tied to the exact source commit, payload, installer hash and validation evidence. Source tests, isolated installation checks, real-account checks and production acceptance are distinct results; report untested items explicitly.

## Where to read next

- [Contribution rules](../../CONTRIBUTING.md) and [AGENTS.md](../../AGENTS.md).
- [Current account transport](../architecture/onedrive-account-transport.md) and [installation layout](../architecture/storage-layout.md).
- [Current user guide](../user-guide/onedrive-account.en-US.md) and [configuration examples](../../config/examples/README.md).
- [Versioned validation records](validation-index.md), retained as audit history rather than instructions to reproduce old local paths.
