# Changelog

This file summarizes user-visible changes. Detailed validation records, including untested areas, are in [docs/development/validation-index.md](docs/development/validation-index.md). A local build record does not imply a GitHub Release has been published.

## Unreleased

- Add the MIT project license, public-facing Chinese/English documentation, privacy and security reporting guidance.
- Prepare Windows synthetic-test CI, dependency-update configuration and contribution templates. Hosted CI has not yet been exercised in a public repository.
- Require future release payloads to include the exact project license.

## 0.9.19 — 2026-11-19

- Replace the project automatic two-way checkbox with Upload only, Two-way backup and Download only modes; retain common baselines, initialization, conflict protection and automatic budgets when switching modes.
- Make Run now follow each project's mode, while explicit upload/download actions retain their direction. Keep reading existing Boolean mode settings.
- Keep project protection status on one line with full evidence in tooltips and project details; remove duplicate resolved cloud-target displays and retain paths relative to OneDrive root.

Development regression: 666 passing tests and four process-interruption recovery checks. See the [development validation record](docs/development/validation-project-modes-2026-11-19.md); installer validation is recorded separately after packaging.

## 0.9.18 — 2026-11-19

- Close the main window with Esc while keeping the tray icon and background tasks running, matching the window's close button.
- Add an optional automatic two-way backup setting for each local project, disabled by default. Automatic runs select upload or download per file using the common baseline, retain conflicts for explicit resolution and never propagate deletions.
- Share persisted pause control and transfer/time budgets across automatic uploads and downloads, including CLI automatic calls; show each transfer's direction in run details.

See the [0.9.18 validation record](docs/development/validation-0.9.18.md) for the exact source and package identity, 641 passing regression tests and 20 isolated installation checks.

## 0.9.17 — 2026-11-19

- Restore current files or the previous archive into a separate directory, with content verification and bounded, rotating restore tests.
- Reduce historical metadata body reads with validated checkpoints and conservative fallback.
- Show complete-success age, restore-verification scope and known backlog for the current mapping.
- Add shared upload/download speed limits and clearer conflict details with independent export.
- Add process-interruption recovery tests. Development regression: 606 tests; isolated installation validation: 20 checks.

See the [0.9.17 validation record](docs/development/validation-0.9.17.md) for the exact source and package identity.

## 0.9.16 — 2026-11-18

- Send problem notifications through Windows notification center and route activation to pending issues.
- Refresh offline indicators after network recovery and retry an interrupted cloud-list read.

Earlier changes remain available through the [validation index](docs/development/validation-index.md) and tagged source history where retained.
