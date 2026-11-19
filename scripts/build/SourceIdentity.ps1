#Requires -Version 5.1
function Get-HarborSourceIdentity([string]$Repository, [switch]$AllowDirty) {
    $revision = (& git -C $Repository rev-parse --verify HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40,64}$') { throw 'Publishing requires a committed Git source revision.' }
    $tree = (& git -C $Repository rev-parse 'HEAD^{tree}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $tree -notmatch '^[a-f0-9]{40,64}$') { throw 'Cannot verify the Git source tree.' }
    $changes = @(& git -C $Repository status --porcelain=v1 --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify source cleanliness.' }
    if ($changes.Count -ne 0 -and -not $AllowDirty) { throw 'Publishing requires a clean committed working tree, including required new files. Commit source changes and exclude local data before publishing; no files were committed automatically.' }
    return [pscustomobject][ordered]@{ revision = $revision; tree = $tree; clean = ($changes.Count -eq 0) }
}
function Assert-HarborSourceIdentity([string]$Repository, $Expected) {
    $actual = Get-HarborSourceIdentity $Repository
    if ($null -eq $Expected -or $Expected.clean -ne $true -or $actual.revision -ne $Expected.revision -or $actual.tree -ne $Expected.tree) {
        throw 'Source revision/tree differs from the published payload. Use the exact committed release source.'
    }
}
