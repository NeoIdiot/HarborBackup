# HarborBackup

[简体中文](README.zh-CN.md)

Back up Windows folders to personal OneDrive while preserving file names and directory structure. HarborBackup provides a desktop window, scheduled tray backups and a CLI. It connects directly through Microsoft Graph and does not require the OneDrive sync client.

## Features

- Choose a local folder and a separate cloud destination for each project; preview exclusions and confirm individual uploads or downloads.
- Backup leaves source files unchanged; Pull leaves cloud contents unchanged. Neither operation propagates deletions.
- Compare changes against a common baseline. Changes on both sides require an explicit choice; different contents are protected before replacement, and the baseline advances after verification.
- Keep the previous ordinary version, with separate conflict copies and recovery materials. Restore current files or the previous archive into an independent folder and verify downloaded contents.
- Configure backup intervals, pause, transfer/time budgets and upload/download speed limits. Inspect project health, run history and JSON reports.

## UI walkthrough

<a href="docs/user-guide/demo.en-US.md"><img src="docs/media/overview.en-US.png" alt="HarborBackup's actual overview controls with synthetic project protection information" width="960"></a>

Actual controls are populated with synthetic data; no real OneDrive account transfer was performed. See the [five-step UI walkthrough](docs/user-guide/demo.en-US.md) for project preview, protected upload and independent restore.

## Platform and scope

The current application targets Windows x64 and personal Microsoft OneDrive accounts. It uses C#, .NET 10 and WinForms.

Cloud backups remain ordinary files. This is a file backup tool, without system images, NTFS permissions, alternate data streams or link semantics. Normal comparison and full checks use local hashes and cloud version metadata without downloading cloud file bodies. Independent restores and restore tests download and verify contents. Ordinary history retains one previous copy, rather than arbitrary point-in-time snapshots.

The old 0.4 / PowerShell configuration and data are not imported automatically. See the [user guide](docs/user-guide/onedrive-account.en-US.md) for initialization, upgrades and recovery.

## Install and connect

If this repository's **Releases** page provides an installer, download `HarborBackup-<version>-Setup-x64.exe` and check its SHA-256, signing status and validation scope against that release's notes. Source archives are not installers. If no release assets are available, build from source below.

The installer uses the current Windows user and defaults to `%LOCALAPPDATA%/Programs/HarborBackup`. It includes the required .NET runtime. Application files live in `app`; local configuration, credentials and recovery state live in `data`. Upgrades and uninstall retain `data`.

1. Open Settings > Account and management folder, connect OneDrive, and choose a management folder for metadata, history and conflicts.
2. Add a project with its local folder and a separate ordinary cloud destination.
3. Review the preview and complete an initial manual upload or pull before enabling automatic work for that mapping.

The authorization cache is encrypted for the current Windows user and stays local. The [English user guide](docs/user-guide/onedrive-account.en-US.md) covers the UI and CLI.

## Develop

Use Windows, Git, PowerShell and a .NET 10 SDK accepted by [global.json](global.json). Install the SDK first: `Use-DotNet.ps1` configures local caches but does not install it. The first restore needs access to NuGet.

Run these commands at the repository root. Before testing, exit HarborBackup from the tray and wait for CLI operations to finish:

```powershell
. ./scripts/build/Use-DotNet.ps1
& $HarborDotNet restore HarborBackup.slnx --configfile NuGet.Config --locked-mode
& $HarborDotNet build HarborBackup.slnx -c Release --no-restore
./scripts/test/Test.ps1 -Configuration Release -IsolatedProfile
```

See [Getting started](docs/development/getting-started.md) for a fresh setup, development launch, Inno Setup bootstrap and isolated installation validation. Building and synthetic tests do not need a real OneDrive account.

Completed production installers are collected in `artifacts/installers`, with their SHA-256 and original build report. For an existing build, run `./scripts/release/Export-Installer.ps1` to populate this folder. Preview cleanup of old build and test runs with `./scripts/build/Clean-Artifacts.ps1 -WhatIf`.

## Documentation and contributions

- [Documentation index](docs/README.md) and [contributing guide](CONTRIBUTING.md).
- [Privacy](PRIVACY.md), [security reporting](SECURITY.md) and [support](SUPPORT.md).
- [Current transport design](docs/architecture/onedrive-account-transport.md) and [installation layout](docs/architecture/storage-layout.md).
- [Validation records](docs/development/validation-index.md), including each version's tested scope and remaining limitations. Past results do not certify the current checkout or a different installer.
- [Public-release checklist](docs/development/open-source-checklist.md).

The source version is defined in [Directory.Build.props](Directory.Build.props). Keep local configuration, credentials, real business paths, logs and build output out of commits.

HarborBackup is licensed under the [MIT License](LICENSE). Third-party components retain their own licenses; see [packaging/licenses](packaging/licenses). See [CHANGELOG](CHANGELOG.md) for release summaries.
