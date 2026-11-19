# Public interface examples

The PNGs and GIFs in this directory are rendered from the actual WinForms controls using `PublicShowcaseTests`. Projects, local paths, device identities, cloud observations and success results are synthetic fixtures. They are UI examples, not evidence of real OneDrive authorization, transfers or recovery acceptance. The GIF advances between four example screens every 3.5 seconds; it is not a recording of a live backup.

All displayed local paths use the fictional `C:\Example` tree. The generator has no account session, production configuration read, run-lock acquisition or transfer call. A footer labels every frame as synthetic. The test checks visible control and grid text for unexpected absolute Windows paths.

Regenerate from the repository root on Windows:

```powershell
./scripts/test/Export-PublicShowcase.ps1
```

After an existing Release build, `-NoBuild` exports without rebuilding. This entry runs only `PublicShowcaseTests`; the general test suite and its production-process preconditions are separate. It does not close an installed HarborBackup instance. Normal regression tests render and check the fixture in memory and do not rewrite public assets unless `HARBORBACKUP_PUBLIC_MEDIA_DIR` is explicitly set.

Expected assets for each of `zh-CN` and `en-US`:

- `overview.<language>.png`: two projects, complete-success information and a known backlog.
- `preview.<language>.png`: selected uploads, protection before replacement, and an unresolved choice.
- `restore.<language>.png`: current-file candidates and synthetic content-verification results.
- `restore-archive.<language>.png`: previous-archive candidates and synthetic verification results.
- `showcase.<language>.gif`: the same four frames in order, with a persistent synthetic-demo label.

Rendering uses native Windows controls and fonts, so pixel output can differ with the rendering environment. Do not substitute screenshots from an actual account or unreviewed files under `artifacts`.
