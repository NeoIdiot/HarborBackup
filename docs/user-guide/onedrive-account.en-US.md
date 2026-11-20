# HarborBackup 0.9: ordinary directory backup

## Automatic installation checks

The installer checks local data before launching the app. Fresh installations automatically receive empty current configuration. Unsupported project/account/run-state formats and missing or malformed required configuration are rebuilt automatically. The complete previous local data tree is retained under a sibling data-retained-* directory. Old projects and state are not migrated; source and cloud files are retained. No manual rebuild command is required.

Current-format data is preserved, including project IDs, credentials and recovery materials. Reinstalling does not reset it. Checks do not require a source directory to exist or a cloud connection. Read failures such as locked configuration files abort installation without resetting data; close the program holding the files and retry.

After a rebuild, open the app, reconnect the account, create a new management folder, and add projects. Existing ordinary cloud target folders can be selected and reviewed before the first upload or pull.

A new project receives one fixed 64-character hexadecimal ID. The editor displays a copyable, read-only ID. Renaming or changing source/destination preserves it. Other devices join the cloud project using the same ID. Different IDs identify different projects even when their names or folders match.

## Choose locations

Editing a project's source or destination retains its identity and suppresses its previous mapping in this device's list. Cloud catalog refresh resolves current folder names and groups location revisions by project ID. Changing a destination does not move existing files; subsequent uploads and pulls use the new configuration.

More actions > Remove project works for both local and cloud-only entries. It updates this device's configuration only, retaining all local files, cloud files, history, conflicts, and recovery materials. Other devices are unaffected. Removed entries stay removed after refresh or restart. To add one again, choose its folder through Add project > Browse cloud.

Connect your personal OneDrive account under Settings > Account and management folder. Select a HarborBackup management folder for metadata, archives and conflicts.

Add a project, choose its Local folder, then use Browse cloud or enter a Cloud folder relative to OneDrive root, such as Projects/DROP_CODE. OneDrive root is the fixed starting point, so this example means OneDrive/Projects/DROP_CODE. Original files and subdirectories appear directly there. New paths are created on the first upload, without an extra backups, computer, project-ID or version-ID prefix. Preview before saving. The editor and project list show the relative cloud folder without a duplicate resolved-target field or column.

Browse cloud opens the project's recorded cloud folder when its ID is known. It briefly caches folder listings and preloads some child-folder listings after displaying the current folder. Going up or reopening the picker can reuse unexpired listings, reducing repeated waits. Use Refresh to see folders recently created, renamed or moved on another device. A failed refresh preserves the previous listing and current location. Closing the picker cancels unfinished reads. Browsing and selecting folders do not upload or download project files.

Another device can connect the same management folder and Join cloud project with its own local source. Project targets must not overlap management storage or other targets. Existing nonempty project folders are allowed; files without trusted metadata are preserved for review.

## Comparison and transfer

Draft preview now includes a run-plan summary and Configuration impact: transfer sizes, protection, attention items, newly excluded and reintroduced entries. Counts are estimates from two read-only observations; folder changes or failed observations do not produce definite scope counts. Saving remains offline and independent of preview.

Read-only verification and CLI `verify` classify matches, differences, missing local/cloud entries, insufficient evidence and type conflicts. Evidence is local SHA-256 plus version-checked cloud metadata, without downloading cloud file bodies. JSON includes `verification`, `verificationEvidence` and `summary`. Verification differences or uncertain evidence return code 2; read failures return 1.

Automatic transfer and time budgets default to zero (unlimited). They apply only to automatic backup, across all projects in that run. Reaching a budget stops starting new files; active transfers finish and verify, and remaining files continue incrementally next run. Content size is reserved when work starts, so the last file can exceed the limit. Protocol overhead and cloud-internal copies are excluded. Manual backup, pull and recovery keep their existing behavior; the hard execution deadline and user cancellation still interrupt active work. Budget stopping is reported separately as `BudgetLimited`, while other file errors remain visible.

File-local read failures before writes defer only that file. Authorization, network and identity errors stop project dispatch; failures during writes retain recovery evidence and stop dispatch when their scope cannot be safely established. Structured `failureKind` distinguishes unavailable files, network, authorization, recovery, budget and cancellation.

Network and authorization failures return code 1. Unavailable individual files, unresolved choices and budget cutoffs return code 2; cancellation returns 130. Other failures take precedence over budget-only completion, while BudgetStopReason remains available.

Changing global or project exclusions takes effect on the next scan without resetting an initialized project or requiring another upload/pull. Excluded local and cloud files, baselines, history and recovery evidence are retained. Removing a rule refreshes observations and resumes per-file comparison with the retained common baseline; changes on both sides, or unequal files without a baseline, require an explicit choice for those files. Projects reset by earlier rule changes can continue from validated evidence for the same device, account and physical mapping. Actual folder changes still require an initial explicit operation.

Transfers process up to eight files concurrently by default, and load up to four file-metadata records concurrently. Directory dependencies and throttling can reduce actual concurrency. Normal uploads avoid a redundant journal rewrite, and downloads compute both checksums in one local read. Files keep their ordinary cloud names and folders; they are not packaged into archives. Protection, cancellation recovery and final verification remain in place. Actual improvement depends on file count, network latency, disk and service response.

Settings allow separate upload and download limits in KiB/s; zero means unlimited. Concurrent files share the limit for each direction. These limits pace local content transfers, including metadata bodies, rather than cloud-internal copies. Automatic duration/content budgets and transfer speed limits may be used together.

## Independent restore and verification

The Issues page can browse current cloud files and their previous archived copies, filter paths and restore selected files to an independent output directory. Output must not overlap project sources, application, data or protection directories. Existing paths are never overwritten. The operation checks cloud versions before and after downloading and verifies SHA-256; missing trusted evidence is reported rather than counted as success. It does not update common baselines or initialize automatic backup.

Restore tests create a new directory under the selected output root and retain recovered files and a JSON report. Optional byte budgets and rotating groups bound each run. A file larger than the budget is skipped; increase the budget to check that file. The project list shows a short protection status in a single-height row. Hover over the status or choose More actions > View project status to inspect the last complete successful run, the scope/count of the latest content restore verification, and known backlog and its first recorded time. The overview also retains these details. Counts are lower bounds when selection, errors or budgets leave unobserved work. A status showing no known backlog does not certify unobserved files. A reminder is raised when known backlog has persisted for at least 24 hours or the last complete success was at least 24 hours ago. A changed physical mapping does not inherit the old mapping's health evidence.

```powershell
HarborBackup.Cli.exe restore-list --destination "Projects/Demo" --scope all --json
HarborBackup.Cli.exe restore --destination "Projects/Demo" --scope archive --path "docs" --output "C:\RestoreReview" --json
HarborBackup.Cli.exe restore-test --destination "Projects/Demo" --scope all --output "C:\RestoreReview" --max-bytes 104857600 --groups 7 --json
```

`--scope` accepts `current`, `archive` or `all`; `--path` is an optional relative path prefix. A zero byte budget means unlimited; group counts range from 1 to 256. Rotation cursors are rebuildable cache. Verification timestamps describe only the reported sample, not the entire repository. `recover` remains the interrupted-transaction recovery command.

The window and tray show Waiting for network while offline, and scheduled backups skip their offline triggers. The indicator clears when Windows detects a network again. If the Backup projects page is open, a cloud list interrupted by disconnection retries once. Automatic backups follow their configured interval. You can retry manual operations after reconnecting. Run records and recovery evidence remain available. OneDrive service, proxy and secure connection failures retain their own messages.

An installed release reports actionable issues in Windows Notification Center. Select a notification to open Issues, or find the run details in Run history. Repeated reports of the same problem are grouped. Windows settings control notification delivery.

Saving a new project checks the selected cloud folder first. A registered folder offers to continue the existing project, preserving its identity and your chosen local folder and exclusions. If that project is already on this PC, a message asks you to edit it rather than adding it again. An unregistered nonempty folder opens Initial content review; confirm the review before saving settings. Cancellation or a failed check saves nothing. Review preserves both sides and transfers no business files. After saving, explicitly upload or retrieve to establish the mapping before automatic backup resumes. New projects require a connected account for this check; existing projects can still be edited offline.

Add project / Edit project offers an Automatic backup mode selector:

| Mode | Business-file direction |
| --- | --- |
| Upload only (default) | Local folder → OneDrive |
| Two-way backup | Upload local updates and download cloud updates |
| Download only | OneDrive → Local folder |

The project list shows the selected mode. This setting is local to each device; choose it separately on other PCs. Scheduled runs, Run now in the window or tray, and CLI `backup --automatic` follow each project's mode and use the global pause control, speed limits and automatic budgets. Run now retains the initial manual-operation requirement and is unavailable while automatic backup is paused. Explicit Upload selected and Download updates keep their stated directions and remain available during that pause; automatic budgets do not limit these manual operations.

Download only does not upload local business files to the project's cloud folder. It may still upload management metadata and copies that protect different local contents before replacement. All modes retain both sides' extra files and never propagate deletions.

Two-way backup chooses a direction from the common baseline rather than modification times. A file missing on one side is copied from the other when the selected mode permits that direction and trusted content evidence permits the transfer. Different changes on both sides, or unequal contents without a common baseline, retain conflict protection and require an explicit choice. Cloud files without sufficient evidence are not downloaded automatically. Comparison never downloads business contents; a selected download follows Pull's cloud-version checks, fresh local-content checks, protection and verified baseline commit. Deletions are not propagated, so a file deleted on one side may be restored by a later automatic run.

Changing the automatic mode retains existing mappings, baselines and initialization. New projects and actual folder changes still require one completed manual upload or pull first. Paths involved in unfinished transactions remain deferred for manual recovery using their original mapping. Manual upload leaves local source files unchanged; manual download leaves cloud business contents unchanged. In projects.json, `automaticBackupMode` accepts `upload`, `bidirectional` or `download`; see the [configuration examples](../../config/examples/README.md) for existing Boolean settings and validation rules.

Cloud comparisons use metadata, item IDs, version tags, sizes and checksums without downloading business file contents. Local SHA-256 caching follows size, UTC modification time and the local full-check interval. Full checks hash local contents only.

Upload selected and Download updates show an operation preview. Selecting differences and confirming uses the chosen direction's version, protects the replaced contents, and records a common version after verification. There is no separate baseline setup step. CLI users can save a preview with --prepare-upload or --direction Pull, then execute the reviewed report with backup/pull --selection. Unconfirmed differences are not forcibly overwritten.

Changing the local folder only saves settings; it does not move or synchronize files and the folder need not exist yet. Complete one manual upload or pull to resume the existing automatic backup policy. Pull creates the local destination during execution when needed.

Unfinished operations do not prevent saving settings or viewing a preview. Only overlapping file paths are deferred; other files in the same project can proceed. Use Recover unfinished transactions to recover using the original folder. Do not delete recovery evidence to dismiss the warning.

Backup projects > Retrieve cloud project lets you choose a cloud project and this PC's folder, review and select files, then retrieve and add the mapping. Cancelling preview does not save a new mapping. An already joined project uses its existing local folder. Join cloud project only saves the mapping.

Preview shows source, local destination, new/update counts and estimated business-content download size. Execution processes only checked items; later additions are excluded, and changed files or mappings are skipped for review. Local-only edits are preserved and deletions are not propagated. Missing local files with trusted cloud versions are restored. Configuration and recovery evidence remain after an interrupted retrieval.

Draft preview shows cloud reading, local scanning, counts and elapsed time. Stop preview preserves the draft; closing the editor requests cancellation before closing. You can edit the rules and retry after it stops.

Exclusion rules… in the preview shows the effective global and project rules and local/cloud skip counts. A directory-name rule excludes matching directories at any depth, including their subtrees; no trailing `*` is needed. A pruned directory counts once; its children are not scanned for counting. Draft previews use unsaved rules.

## History and recovery

The tray always keeps the blue anchor logo, with a bottom-right status badge: circular arrows for activity, an amber exclamation mark for attention, a red cross for failure, pause bars for paused automatic backups, and a dash while waiting for connection or network. No badge means idle and does not certify that every file is backed up. Hover for status and pending-file counts; open the main window for full details.

Pressing ESC in the main window works like its top-right ×: the main window closes, the tray icon remains, and active tasks and scheduled backups continue. Reopen the main window from the tray. An open dialog handles ESC first. Use Exit application to end background operation.

Drag the sidebar's right edge to change its width. A narrow sidebar shows navigation icons only, and its width is remembered when you exit.

Only the previous ordinary version is retained after a successful commit. Unresolved conflicts and unfinished recovery evidence are excluded from cleanup. Online-only protection does not download copies; Keep local does. An independently running OneDrive desktop client may still download cloud folders under its own settings.

Different cloud content is protected before the current local file is uploaded and verified. The live path can briefly be absent between protection and upload. Interrupted operations retain protected files and journals; use Recover unfinished transactions. Do not discard state or active protection folders.

## Upgrade

File results and device-status publication are handled separately: completed files remain completed if status publication needs a retry. Slow cloud project lists do not prevent local configuration edits. State read errors appear as diagnostics while configuration remains editable; unverifiable scheduler controls pause automatic work. Retention preferences for the saved repository can be changed offline without recovering unrelated operations first.

Old 0.8 numbered-version directories are preserved and are not automatically migrated or deleted. Destination paths now resolve directly under OneDrive root: DROP_CODE means OneDrive/DROP_CODE. Edit and preview if Projects/DROP_CODE is intended. Transactions from the 0.8 storage layout cannot be replayed as ordinary-directory operations.

When the local file is missing, an interrupted pull that has not written or protected content can be selected for a new download directly in the pull preview. No separate recovery step is needed. Preview only shows the plan; execution rechecks current file evidence and the recovery record, retains the original record, and starts a fresh download. Changed files are skipped so you can preview their new state.

Use Recover unfinished transactions to manually retry an interrupted backup for the current project mapping. The application retains the exact original journal, reconciles uploads and archives, restores a cloud gap if needed, and preserves recovery materials. It then uploads the current local file, protecting different cloud contents before replacement and committing the common baseline only after verification. You can edit the local file after interruption; reverting to its old version is unnecessary. Another failure leaves a new recoverable journal. Scheduled backups, Run now and readonly previews do not perform this explicit retry.

Retired mappings and other old-format transactions keep their existing recovery checks and are not interpreted as new uploads from the current mapping. Identifiable pending operations limit only affected files and do not block saving configuration. For unknown formats or missing original mappings, open item details; do not delete state to bypass checks.

The Project column lists the projects involved in a run, including the count and names for a batch. View selected details or double-click a run to open a separate read-only window with a project selector, search, an attention filter, a short error summary and a copyable log path; full original diagnostics stay in stored run records and logs. Closing it preserves the history list position. Pending issues shows attention items from the latest result, without importing successful actions from historical reports.

The installer contains a public application Client ID and the SDK-selected self-contained WinForms runtime (untrimmed, shared by Desktop and CLI in app; settings and state stay in data). Optional custom public IDs belong in Advanced settings; never enter a client secret. Credentials stay encrypted for the current Windows user and are not synchronized.

CLI supports cloud-connect, cloud-bind, preview, backup, pull --destination <path>, recover, verify and devices. devices reads shared file metadata; verify hashes local files only. cloud-inspect / cloud-resolve support explicit conflict selection.

Real-service isolated-folder tests have passed. Physical two-device and production-upgrade acceptance should still start with a small project.
