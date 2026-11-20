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
function Add-CleanupInstaller([string]$Run, [string]$Report, [string]$Area = 'installers', [switch]$NoExe, [switch]$EmptyExe) {
    $modified = [IO.Directory]::GetLastWriteTimeUtc($Run)
    $directory = Join-Path $Run $Area
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'package-report.json'), $Report)
    if (-not $NoExe) {
        [IO.File]::WriteAllText((Join-Path $directory 'HarborBackup-0.9.0-Setup-x64.exe'), $(if ($EmptyExe) { '' } else { 'synthetic executable' }))
    }
    [IO.Directory]::SetLastWriteTimeUtc($Run, $modified)
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

    # A nested workflow input protects its ancestor run, without keeping siblings or
    # treating the artifacts root itself as a request to keep every historical run.
    $nestedRoot = Join-Path $Scratch 'nested-preservation'
    $nestedFileRun = New-CleanupRun 'nested-preservation/publish' 'HarborBackup-0.9.0-20260101-000001' 1
    $nestedDirectoryRun = New-CleanupRun 'nested-preservation/publish' 'HarborBackup-0.9.0-20260101-000002' 2
    $nestedOther = New-CleanupRun 'nested-preservation/publish' 'HarborBackup-0.9.0-20260101-000003' 3
    $nestedLatest = New-CleanupRun 'nested-preservation/publish' 'HarborBackup-0.9.0-20260101-000004' 4 'release-manifest.json'
    Add-CleanupInstaller $nestedFileRun '{"validationBuild":true}'
    $nestedFile = Join-Path $nestedFileRun 'installers/HarborBackup-0.9.0-Setup-x64.exe'
    $nestedDirectory = Join-Path $nestedDirectoryRun 'payload/app'
    New-Item -ItemType Directory -Path $nestedDirectory -Force | Out-Null
    [IO.Directory]::SetLastWriteTimeUtc($nestedDirectoryRun, [datetime]::Parse('2026-01-01T00:02:00Z').ToUniversalTime())
    Remove-HarborArtifactHistory -ArtifactsRoot $nestedRoot -KeepCount 1 -PreservePaths @($nestedFile,$nestedDirectory) | Out-Null
    foreach ($path in @($nestedFileRun,$nestedDirectoryRun,$nestedLatest)) { Assert-Cleanup (Test-Path -LiteralPath $path) 'A nested workflow input/output lost its ancestor run.' }
    Assert-Cleanup (-not (Test-Path -LiteralPath $nestedOther)) 'Nested preservation kept an unrelated sibling run.'
    Remove-HarborArtifactHistory -ArtifactsRoot $nestedRoot -KeepCount 1 -PreservePaths $nestedRoot | Out-Null
    foreach ($path in @($nestedFileRun,$nestedDirectoryRun)) { Assert-Cleanup (-not (Test-Path -LiteralPath $path)) 'A broad root preservation path kept all historical runs.' }

    $prefixRun = New-CleanupRun 'nested-preservation/test-results' 'run-prefix' 1
    $prefixExtra = New-CleanupRun 'nested-preservation/test-results' 'run-prefix-extra' 0
    $prefixLatest = New-CleanupRun 'nested-preservation/test-results' 'run-latest' 2 'completed.json'
    Remove-HarborArtifactHistory -ArtifactsRoot $nestedRoot -KeepCount 1 -PreservePaths (Join-Path $prefixExtra 'synthetic.txt') | Out-Null
    Assert-Cleanup (-not (Test-Path -LiteralPath $prefixRun)) 'A sibling name prefix was mistaken for an ancestor boundary.'
    foreach ($path in @($prefixExtra,$prefixLatest)) { Assert-Cleanup (Test-Path -LiteralPath $path) 'A nested file or latest report was deleted.' }

    # Keep the latest production installer even after multiple newer payload-only
    # publishes. Validation packages and incomplete evidence cannot replace it.
    $formalRoot = Join-Path $Scratch 'formal-preservation'
    $formalOlder = New-CleanupRun 'formal-preservation/publish' 'HarborBackup-0.9.0-20260101-000001' 1 'release-manifest.json'
    Add-CleanupInstaller $formalOlder '{"validationBuild":false}'
    $formalLatest = New-CleanupRun 'formal-preservation/publish' 'HarborBackup-0.9.0-20260101-000002' 2 'release-manifest.json'
    Add-CleanupInstaller $formalLatest '{"validationBuild":false}'
    [IO.File]::SetLastWriteTimeUtc((Join-Path $formalOlder 'installers/package-report.json'), [datetime]::Parse('2026-01-01T00:01:00Z').ToUniversalTime())
    [IO.File]::SetLastWriteTimeUtc((Join-Path $formalLatest 'installers/package-report.json'), [datetime]::Parse('2026-01-01T00:02:00Z').ToUniversalTime())
    $invalidFormal = @()
    $invalidReports = @('{"validationBuild":true}','{}','{"validationBuild":"false"}','{"validationBuild":null}','{','[{"validationBuild":false}]')
    for ($i = 0; $i -lt $invalidReports.Count; $i++) {
        $invalidRun = New-CleanupRun 'formal-preservation/publish' ('HarborBackup-0.9.0-20260101-{0:D6}' -f (3 + $i)) (3 + $i) 'release-manifest.json'
        Add-CleanupInstaller $invalidRun $invalidReports[$i]
        $invalidFormal += $invalidRun
    }
    $validationOnly = New-CleanupRun 'formal-preservation/publish' 'HarborBackup-0.9.0-20260101-000009' 9 'release-manifest.json'
    Add-CleanupInstaller $validationOnly '{"validationBuild":false}' -Area 'validation-installers'
    $missingExe = New-CleanupRun 'formal-preservation/publish' 'HarborBackup-0.9.0-20260101-000010' 10 'release-manifest.json'
    Add-CleanupInstaller $missingExe '{"validationBuild":false}' -NoExe
    $emptyExe = New-CleanupRun 'formal-preservation/publish' 'HarborBackup-0.9.0-20260101-000011' 11 'release-manifest.json'
    Add-CleanupInstaller $emptyExe '{"validationBuild":false}' -EmptyExe
    $payloadOnly = @(12..13 | ForEach-Object { New-CleanupRun 'formal-preservation/publish' ('HarborBackup-0.9.0-20260101-{0:D6}' -f $_) $_ 'release-manifest.json' })
    $stableInstallers = New-CleanupRun 'formal-preservation/installers' 'latest' 0
    Remove-HarborArtifactHistory -ArtifactsRoot $formalRoot -KeepCount 2 -WhatIf | Out-Null
    foreach ($path in @($formalOlder,$formalLatest,$stableInstallers)) { Assert-Cleanup (Test-Path -LiteralPath $path) 'Installer discovery under WhatIf changed a synthetic artifact.' }
    Remove-HarborArtifactHistory -ArtifactsRoot $formalRoot -KeepCount 2 | Out-Null
    foreach ($path in @($formalLatest,$stableInstallers) + $payloadOnly) { Assert-Cleanup (Test-Path -LiteralPath $path) 'The latest formal installer, stable installer tree or latest payload was deleted.' }
    foreach ($path in @($formalOlder,$validationOnly,$missingExe,$emptyExe) + $invalidFormal) { Assert-Cleanup (-not (Test-Path -LiteralPath $path)) 'Invalid/validation packaging evidence displaced the latest formal installer protection.' }
    Assert-Cleanup (Test-Path -LiteralPath (Join-Path $stableInstallers 'synthetic.txt')) 'Cleanup touched the independent stable installers tree.'

    # Rebuilding an old payload can produce the newest installer without changing
    # the publish run root timestamp. New payload-only runs must not displace it.
    $repackedOlder = New-CleanupRun 'formal-preservation/publish' 'HarborBackup-0.9.0-20260101-000000' 0 'release-manifest.json'
    Add-CleanupInstaller $repackedOlder '{"validationBuild":false}'
    [IO.File]::SetLastWriteTimeUtc((Join-Path $repackedOlder 'installers/package-report.json'), [datetime]::Parse('2026-01-01T00:30:00Z').ToUniversalTime())
    Assert-Cleanup ([IO.Directory]::GetLastWriteTimeUtc($repackedOlder) -lt [IO.Directory]::GetLastWriteTimeUtc($formalLatest)) 'The repackaging fixture did not retain its older run-root timestamp.'
    $futurePayloads = @(14..15 | ForEach-Object { New-CleanupRun 'formal-preservation/publish' ('HarborBackup-0.9.0-20260101-{0:D6}' -f $_) $_ 'release-manifest.json' })
    Remove-HarborArtifactHistory -ArtifactsRoot $formalRoot -KeepCount 2 | Out-Null
    foreach ($path in @($repackedOlder,$stableInstallers) + $futurePayloads) { Assert-Cleanup (Test-Path -LiteralPath $path) 'Newer payload-only runs displaced the newest repackaged production installer.' }
    Assert-Cleanup (-not (Test-Path -LiteralPath $formalLatest)) 'Run-root ordering selected an older installer report instead of the latest package.'
    Write-Output 'PASS: artifact retention, production-installer preservation, nested inputs, scope, links and concurrency.'
} finally {
    if ($Junction -and (Test-Path -LiteralPath $Junction)) { [IO.Directory]::Delete($Junction) }
    Assert-HarborArtifactTree $Scratch
    Remove-Item -LiteralPath $Scratch -Recurse -Force
}
