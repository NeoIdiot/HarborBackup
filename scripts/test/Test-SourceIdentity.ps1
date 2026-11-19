#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/SourceIdentity.ps1')
$repo = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) ('artifacts/source-identity-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $repo -Force | Out-Null
$hooks = Join-Path $repo 'empty-hooks'; New-Item -ItemType Directory -Path $hooks | Out-Null
function Invoke-TestGit([string[]]$Arguments) {
    & git -C $repo -c user.name=HarborBackup-Test -c user.email=test@example.invalid -c "core.hooksPath=$hooks" @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Git operation failed.' }
}
function Assert-DirtyRejected {
    try { $null = Get-HarborSourceIdentity $repo }
    catch { if ($_.Exception.Message -notlike '*clean committed working tree*') { throw }; return }
    throw 'Dirty source was accepted.'
}
Invoke-TestGit @('init','--quiet')
# Hooks are empty and Git identity is passed per command; no global/user configuration is changed.
Set-Content -LiteralPath (Join-Path $repo '.gitignore') -Value 'empty-hooks/' -Encoding UTF8
Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value 'original' -Encoding UTF8
Invoke-TestGit @('add','.gitignore','source.txt'); Invoke-TestGit @('commit','--quiet','-m','Synthetic source')
$identity = Get-HarborSourceIdentity $repo
Assert-HarborSourceIdentity $repo $identity
Add-Content -LiteralPath (Join-Path $repo 'source.txt') -Value 'changed'
Assert-DirtyRejected
if ((Get-HarborSourceIdentity $repo -AllowDirty).clean) { throw 'Development test context did not identify dirty source.' }
Invoke-TestGit @('add','source.txt'); Assert-DirtyRejected
Invoke-TestGit @('commit','--quiet','-m','Synthetic changed source')
try { Assert-HarborSourceIdentity $repo $identity; throw 'Stale revision was accepted.' }
catch { if ($_.Exception.Message -notlike '*differs from the published payload*') { throw } }
Set-Content -LiteralPath (Join-Path $repo 'new.cs') -Value '// required untracked source'
Assert-DirtyRejected
Write-Output 'PASS: source identity binds clean commit/tree and rejects tracked, staged, untracked and stale-revision changes.'
