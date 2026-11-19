> Historical 0.7 directory-mode guide. For the current account-only application see [account setup](onedrive-account.en-US.md).

# HarborBackup 0.7 user guide

The install root contains app (application and SDK-selected self-contained runtime) and local data. Upgrades and uninstall preserve data. Closing the window keeps the tray running. No system .NET installation is required.

## Connect computers

Select each computer's own OneDrive root. Map projects to the same destination relative path (for example Projects/Demo); local source folders may differ. Metadata defaults to OneDrive/HarborBackup/metadata. Settings → Shared metadata folder can choose another shared OneDrive subfolder.

On a new computer, wait for OneDrive, preview, and explicitly retrieve target updates before editing. Device records may be stale. Local processing success does not confirm cloud upload. Always keep on this device keeps metadata locally available, but does not prove the latest peer records have arrived.

## Conflicts and file status

Issues → Device file status displays per-file source/target/common hashes and observation times. View selected details shows full hashes. The CLI command `devices --json` provides the same read-only information.

With a common baseline, one-sided changes are handled by direction, independent of clock order. Divergent edits on both sides preserve both versions and require Resolve selected file. Choose Use local version or Use target version after inspecting paths and hashes. The displaced file is protected. Changes after confirmation reject the operation.

Initial comparisons without a baseline retain the protected newer-version policy; equal-time different content remains unresolved. Preview before running automatic backup on a new computer.

If a reported peer version has not arrived locally, wait for OneDrive content synchronization and retry. Corrupt metadata, sync conflict copies and owner revision conflicts preserve evidence and block writes. Do not delete state to bypass checks. Failed publication retains a local outbox; backup or recovery retries it.

## Local data

Desktop and CLI share data/config/projects.json, backup-settings.json and storage-settings.json. UI preferences use desktop-settings.json. State, baselines, metadata-outbox and transactions are recovery evidence. Only hash cache and ordinary temporary output may be cleared. History and diagnostic logs are separate. Protection copies remain local by default; metadata contains hashes, not recoverable file content.

Enable login startup in Settings. Runtime updates ship with application releases. PowerShell/0.4 state is not imported. sharedMetadataDirectory=null selects the default location; syncMetadata=false disables sharing but retains local baseline conflict handling.

## Shared storage and local availability

New archives, conflicts and metadata default to OneDrive/HarborBackup/archives, conflicts and metadata. Settings can change the shared root or override each folder independently. Use default location clears that override. Saved paths display even before any backup project exists.

Archived versions and conflict copies independently support Always keep on this device (default) or Online-only. Preferences are local to each computer; both modes still sync through OneDrive. Online-only never deletes files: after transactions complete and the provider reports content in sync, the app requests release. Placeholders and temporary local content may remain; offline restoration is not guaranteed. Metadata and copy indexes request local retention.

File availability → Status details distinguishes waiting for registration/sync, download/release requests, local retention, on-demand content and retries. The tray retries a batch every minute and backup/recovery also processes pending items. Exit stops retries but preserves the catalog. Apply to existing copies discovers only this device's managed copies in the current folders.

Location changes do not move or delete existing files. View previous locations lists known original folders. Pending transactions block storage edits; interrupted transactions using the old default local protection location recover there before the new default takes effect. Do not clear state or delete synced files to free space.
