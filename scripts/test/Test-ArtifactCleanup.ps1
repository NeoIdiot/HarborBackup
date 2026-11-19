#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Helper = Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1'
. $Helper
$Scratch = Join-Path $script:HarborArtifactsBase ('cleanup-tests/' + [guid]::NewGuid().ToString('N'))
$null = Assert-HarborArtifactPath $Scratch
New-Item -ItemType Directory -Path $Scratch -Force | Out-Null
$Junction = $null
function Assert-Cleanup([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-CleanupRun([string]$Area, [string]$Name, [int]$Order, [string]$Marker = '') {
    $path = Join-Path (Join-Path $Scratch $Area) $Name
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $path 'synthetic.txt'), 'test material')
    if ($Marker) { [IO.File]::WriteAllText((Join-Path $path $Marker), '{}') }
    [IO.Directory]::SetCreationTimeUtc($path, [datetime]::Parse('2026-01-01T00:00:00Z').ToUniversalTime().AddMinutes($Order))
    [IO.Directory]::SetLastWriteTimeUtc($path, [datetime]::Parse('2026-01-01T00:00:00Z').ToUniversalTime().AddMinutes($Order))
    return $path
}
try {
    $runs = @(1..5 | ForEach-Object {
        $marker = if ($_ -in @(1,3)) { 'release-manifest.json' } else { '' }
        New-CleanupRun 'publish' ('HarborBackup-0.9.0-20260101-{0:D6}' -f $_) $_ $marker
    })
    $unknown = New-CleanupRun 'publish' 'manual-material' 0
    $cache = New-CleanupRun 'nuget' 'keep' 0
    $state = New-CleanupRun 'state' 'keep' 0
    Remove-HarborArtifactHistory -ArtifactsRoot $Scratch -KeepCount 2 -WhatIf | Out-Null
    Assert-Cleanup (Test-Path -LiteralPath $runs[0]) 'WhatIf deleted a run.'
    Remove-HarborArtifactHistory -ArtifactsRoot $Scratch -KeepCount 2 | Out-Null
    foreach ($path in $runs[0..1]) { Assert-Cleanup (-not (Test-Path -LiteralPath $path)) 'Old run was not removed.' }
    foreach ($path in @($runs[2..4]) + @($unknown,$cache,$state)) { Assert-Cleanup (Test-Path -LiteralPath $path) 'A retained or unmanaged directory was removed.' }

    $old = New-CleanupRun 'install-validation' ([guid]::NewGuid().ToString('N')) 0 'validation.json'
    $latest = New-CleanupRun 'install-validation' ([guid]::NewGuid().ToString('N')) 1 'validation.json'
    $oldReport = New-CleanupRun 'test-results' 'old' 0 'completed.json'
    $latestReport = New-CleanupRun 'test-results' 'run-new' 1 'completed.json'
    $pinned = New-CleanupRun 'publish' 'HarborBackup-0.9.0-20260101-000006' -2
    Remove-HarborArtifactHistory -ArtifactsRoot $Scratch -KeepCount 1 -PreservePaths $pinned | Out-Null
    Assert-Cleanup (Test-Path -LiteralPath $pinned) 'A workflow input/output explicitly preserved by its caller was removed.'
    foreach ($path in @($old,$oldReport)) { Assert-Cleanup (-not (Test-Path -LiteralPath $path)) 'Old validation/report was retained.' }
    foreach ($path in @($latest,$latestReport)) { Assert-Cleanup (Test-Path -LiteralPath $path) 'Latest validation/report was deleted.' }

    $rejected = $false
    try { Remove-HarborArtifactHistory -ArtifactsRoot (Join-Path $Scratch '../../../data') -KeepCount 1 | Out-Null }
    catch { $rejected = $true }
    Assert-Cleanup $rejected 'A path outside repository artifacts was accepted.'

    # Junction descendants and junction ancestors must both prevent deletion.
    $linkedRun = New-CleanupRun 'publish' 'HarborBackup-0.9.0-20260101-000000' -1
    $Junction = Join-Path $linkedRun 'linked-cache'
    New-Item -ItemType Junction -Path $Junction -Target $cache | Out-Null
    [IO.Directory]::SetLastWriteTimeUtc($linkedRun, [datetime]::Parse('2025-01-01T00:00:00Z').ToUniversalTime())
    Remove-HarborArtifactHistory -ArtifactsRoot $Scratch -KeepCount 1 -WarningVariable linkWarnings -WarningAction SilentlyContinue | Out-Null
    Assert-Cleanup (Test-Path -LiteralPath $linkedRun) 'A tree containing a junction was deleted.'
    Assert-Cleanup (Test-Path -LiteralPath (Join-Path $cache 'synthetic.txt')) 'Cleanup followed a junction.'
    $rejected = $false
    try { $null = Assert-HarborArtifactPath (Join-Path $Junction 'child') } catch { $rejected = $true }
    Assert-Cleanup $rejected 'A path through a junction was accepted.'
    [IO.Directory]::Delete($Junction); $Junction = $null

    # A different PowerShell thread cannot clean while a workflow owns the mutex.
    $lease = Enter-HarborArtifactOperation
    $worker = [PowerShell]::Create()
    try {
        $null = $worker.AddScript('param($helper); . $helper; try { $m = Enter-HarborArtifactOperation; $m.ReleaseMutex(); $m.Dispose(); "unlocked" } catch { "busy" }').AddArgument($Helper)
        $answer = @($worker.Invoke())
        Assert-Cleanup ($answer.Count -eq 1 -and $answer[0] -eq 'busy') 'Concurrent artifact operations were allowed.'
    } finally { $worker.Dispose(); $lease.ReleaseMutex(); $lease.Dispose() }
    Write-Output 'PASS: artifact retention, latest-success protection, scope, links and concurrency.'
} finally {
    if ($Junction -and (Test-Path -LiteralPath $Junction)) { [IO.Directory]::Delete($Junction) }
    Assert-HarborArtifactTree $Scratch
    Remove-Item -LiteralPath $Scratch -Recurse -Force
}
