#Requires -Version 5.1
[CmdletBinding()]
param([string]$PublishDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
$installerExportRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))
$installerExportLease = Enter-HarborArtifactOperation
$installerExportStage = $null
$installerExportPrevious = $null
$installerExportOldMoved = $false
$installerExportCommitted = $false

function Test-InstallerExportWithin([string]$Child, [string]$Parent) {
    return $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Child.StartsWith($Parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Remove-InstallerExportTree([string]$Path) {
    $checked = Assert-HarborArtifactPath $Path
    if (Test-Path -LiteralPath $checked) {
        Assert-HarborArtifactTree $checked
        Remove-Item -LiteralPath $checked -Recurse -Force -ErrorAction Stop
    }
}
function Assert-InstallerExportCatalog([string]$Path) {
    Assert-HarborArtifactTree $Path
    $items = @(Get-ChildItem -LiteralPath $Path -Force)
    if ($items.Count -eq 0) { return }
    $latestPath = Join-Path $Path 'latest.json'
    if (-not (Test-Path -LiteralPath $latestPath -PathType Leaf)) { throw 'Refusing to replace an output directory not owned by installer export.' }
    $catalog = Get-Content -LiteralPath $latestPath -Raw | ConvertFrom-Json
    if ($catalog.schemaVersion -ne 1 -or $catalog.validationBuild -isnot [bool] -or $catalog.validationBuild -or
        [string]$catalog.version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$' -or
        $catalog.installer -cne ('HarborBackup-' + $catalog.version + '-Setup-x64.exe') -or
        [string]$catalog.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $null -eq $catalog.source -or
        $catalog.signed -isnot [bool] -or ($catalog.installerBytes -isnot [int] -and $catalog.installerBytes -isnot [long]) -or
        $catalog.installerBytes -le 0) { throw 'Refusing to replace an unverifiable installer export catalog.' }
    $allowed = @($catalog.installer, 'package-report.json', 'SHA256SUMS.txt', 'latest.json', 'README.md')
    if ($items.Count -ne $allowed.Count -or @($items | Where-Object { $_.PSIsContainer -or $_.Name -cnotin $allowed }).Count -ne 0) {
        throw 'Refusing to replace an installer output containing unknown files or directories.'
    }
    $existingReport = Get-Content -LiteralPath (Join-Path $Path 'package-report.json') -Raw | ConvertFrom-Json
    $existingInstaller = Get-Item -LiteralPath (Join-Path $Path $catalog.installer)
    if ($existingReport.validationBuild -isnot [bool] -or $existingReport.validationBuild -or
        $existingReport.version -cne $catalog.version -or $existingReport.sha256 -ne $catalog.sha256 -or
        $existingReport.installerBytes -ne $catalog.installerBytes -or $existingReport.signed -ne $catalog.signed -or
        ($existingReport.source | ConvertTo-Json -Depth 20 -Compress) -cne ($catalog.source | ConvertTo-Json -Depth 20 -Compress) -or
        $existingInstaller.Length -ne $catalog.installerBytes -or
        (Get-FileHash -LiteralPath $existingInstaller.FullName -Algorithm SHA256).Hash -ne $catalog.sha256 -or
        (Get-Content -LiteralPath (Join-Path $Path 'SHA256SUMS.txt') -Raw).TrimEnd([char[]]"`r`n") -cne ($catalog.sha256 + ' *' + $catalog.installer)) {
        throw 'Refusing to replace an installer export with inconsistent evidence.'
    }
}
try {
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $installerExportRoot 'installers' }
    $OutputDirectory = Assert-HarborArtifactPath $OutputDirectory
    if (-not $PublishDirectory) {
        $publishRoot = Assert-HarborArtifactPath (Join-Path $installerExportRoot 'publish')
        $reports = @()
        if (Test-Path -LiteralPath $publishRoot -PathType Container) {
            foreach ($run in Get-ChildItem -LiteralPath $publishRoot -Directory -Force) {
                if ($run.Name -notmatch '^HarborBackup-\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?-\d{8}-\d{6}$' -or
                    ($run.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
                $candidate = Assert-HarborArtifactPath (Join-Path $run.FullName 'installers/package-report.json')
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $reports += Get-Item -LiteralPath $candidate }
            }
        }
        $newest = $reports | Sort-Object LastWriteTimeUtc, FullName -Descending | Select-Object -First 1
        if (-not $newest) { throw 'No normal installer package report was found under artifacts/publish.' }
        $PublishDirectory = Split-Path -Parent (Split-Path -Parent $newest.FullName)
    }
    $PublishDirectory = Assert-HarborArtifactPath $PublishDirectory
    if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) { throw 'PublishDirectory does not exist.' }
    if ((Test-InstallerExportWithin $OutputDirectory $PublishDirectory) -or
        (Test-InstallerExportWithin $PublishDirectory $OutputDirectory)) { throw 'Installer source and output must not contain each other.' }
    $sourceDirectory = Assert-HarborArtifactPath (Join-Path $PublishDirectory 'installers')
    Assert-HarborArtifactTree $sourceDirectory
    $reportPath = Assert-HarborArtifactPath (Join-Path $sourceDirectory 'package-report.json')
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.validationBuild -isnot [bool] -or $report.validationBuild) { throw 'Only a normal installer (validationBuild=false) can be exported.' }
    if ([string]$report.version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$' -or
        [string]$report.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $null -eq $report.source -or
        $report.signed -isnot [bool] -or ($report.installerBytes -isnot [int] -and $report.installerBytes -isnot [long]) -or
        $report.installerBytes -le 0) { throw 'Installer report is missing version, checksum, source identity, signing status or byte length.' }
    $expectedName = 'HarborBackup-' + $report.version + '-Setup-x64.exe'
    $installers = @(Get-ChildItem -LiteralPath $sourceDirectory -File -Filter '*Setup-x64.exe')
    if ($installers.Count -ne 1 -or $installers[0].Name -cne $expectedName) { throw 'The report must identify exactly one versioned normal installer.' }
    $installer = $installers[0]
    if ($installer.Length -ne [long]$report.installerBytes -or
        (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash -ne $report.sha256) { throw 'Installer size or SHA-256 differs from its package report.' }
    $reportHash = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $OutputDirectory) {
        if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) { throw 'Installer output is not a directory.' }
        Assert-InstallerExportCatalog $OutputDirectory
    }
    $parent = Assert-HarborArtifactPath (Split-Path -Parent $OutputDirectory)
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $installerExportStage = Assert-HarborArtifactPath (Join-Path $parent ('.installer-export-stage-' + [guid]::NewGuid().ToString('N')))
    $installerExportPrevious = Assert-HarborArtifactPath (Join-Path $parent ('.installer-export-previous-' + [guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $installerExportStage | Out-Null
    $stagedInstaller = Join-Path $installerExportStage $expectedName
    Copy-Item -LiteralPath $installer.FullName -Destination $stagedInstaller
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $installerExportStage 'package-report.json')
    if ((Get-Item -LiteralPath $stagedInstaller).Length -ne [long]$report.installerBytes -or
        (Get-FileHash -LiteralPath $stagedInstaller -Algorithm SHA256).Hash -ne $report.sha256 -or
        (Get-FileHash -LiteralPath (Join-Path $installerExportStage 'package-report.json') -Algorithm SHA256).Hash -ne $reportHash) {
        throw 'Staged installer or report verification failed; the previous entry is unchanged.'
    }
    $utf8 = New-Object Text.UTF8Encoding($false)
    $relativeSource = $sourceDirectory.Substring($installerExportRoot.TrimEnd('\').Length + 1).Replace('\', '/')
    $latest = [ordered]@{ schemaVersion = 1; installer = $expectedName; version = $report.version;
        source = $report.source; sourceDirectory = $relativeSource; sha256 = $report.sha256;
        installerBytes = $report.installerBytes; signed = $report.signed; validationBuild = $false }
    [IO.File]::WriteAllText((Join-Path $installerExportStage 'latest.json'), ($latest | ConvertTo-Json -Depth 20), $utf8)
    [IO.File]::WriteAllText((Join-Path $installerExportStage 'SHA256SUMS.txt'), ($report.sha256 + ' *' + $expectedName + [Environment]::NewLine), $utf8)
    $readme = "# Latest built installer`r`n`r`nRun $expectedName to install.`r`n`r`nThis folder contains the most recently exported normal build. It does not imply installation acceptance, real-account validation, or a build of the current HEAD.`r`n`r`nSee latest.json and the unchanged package-report.json for the original source identity, version, signing status and SHA-256. The sourceDirectory in latest.json is relative to repository artifacts. Validation installers are never exported here.`r`n"
    [IO.File]::WriteAllText((Join-Path $installerExportStage 'README.md'), $readme, $utf8)
    Assert-HarborArtifactTree $installerExportStage
    $null = Assert-HarborArtifactPath $OutputDirectory
    # Same-volume directory renames publish only the completed staging tree. If the
    # second rename fails, restore the old entry before reporting failure.
    if (Test-Path -LiteralPath $OutputDirectory) {
        Assert-InstallerExportCatalog $OutputDirectory
        $null = Assert-HarborArtifactPath $installerExportPrevious
        [IO.Directory]::Move($OutputDirectory, $installerExportPrevious)
        $installerExportOldMoved = $true
    }
    $null = Assert-HarborArtifactPath $installerExportStage
    $null = Assert-HarborArtifactPath $OutputDirectory
    [IO.Directory]::Move($installerExportStage, $OutputDirectory)
    $installerExportCommitted = $true
    if ($installerExportOldMoved) {
        try { Assert-InstallerExportCatalog $installerExportPrevious; Remove-InstallerExportTree $installerExportPrevious }
        catch { Write-Warning ('Installer exported; an old entry remains at ' + $installerExportPrevious + ': ' + $_.Exception.Message) }
    }
    Write-Output ('Installer: ' + (Join-Path $OutputDirectory $expectedName))
    Write-Output ('Build information: ' + (Join-Path $OutputDirectory 'latest.json'))
} catch {
    if (-not $installerExportCommitted -and $installerExportOldMoved) {
        $null = Assert-HarborArtifactPath $OutputDirectory
        Assert-HarborArtifactTree $installerExportPrevious
        if (Test-Path -LiteralPath $OutputDirectory) { throw ('Cannot restore the old entry over an unexpected path; preserved at ' + $installerExportPrevious) }
        [IO.Directory]::Move($installerExportPrevious, $OutputDirectory)
    }
    throw
} finally {
    if ($installerExportStage -and (Test-Path -LiteralPath $installerExportStage)) {
        try { Remove-InstallerExportTree $installerExportStage }
        catch { Write-Warning ('Staging cleanup skipped: ' + $_.Exception.Message) }
    }
    $installerExportLease.ReleaseMutex(); $installerExportLease.Dispose()
}
