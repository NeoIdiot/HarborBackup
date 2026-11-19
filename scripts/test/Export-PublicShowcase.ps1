#Requires -Version 5.1
[CmdletBinding()]
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Use-DotNet.ps1')
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
$showcaseLease = Enter-HarborArtifactOperation
$showcasePreviousMedia = $env:HARBORBACKUP_PUBLIC_MEDIA_DIR
$showcasePreviousDpi = $env:HARBORBACKUP_UI_DPI
$showcaseProject = Join-Path $HarborRoot 'tests/HarborBackup.Desktop.Tests/HarborBackup.Desktop.Tests.csproj'
$showcaseResults = Join-Path $HarborRoot ('artifacts/public-showcase/' + [guid]::NewGuid().ToString('N'))
try {
    # This targeted fixture creates only synthetic in-memory UI. It never opens an account,
    # reads production configuration, uses the production mutex, or closes an installed app.
    if (-not $NoBuild) {
        & $HarborDotNet restore $showcaseProject --configfile (Join-Path $HarborRoot 'NuGet.Config') --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Showcase restore failed.' }
        & $HarborDotNet build $showcaseProject -c Release --no-restore --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Showcase build failed.' }
    }
    $env:HARBORBACKUP_PUBLIC_MEDIA_DIR = Join-Path $HarborRoot 'docs/media'
    $env:HARBORBACKUP_UI_DPI = '96'
    & $HarborDotNet test $showcaseProject -c Release --no-restore --no-build --filter 'FullyQualifiedName~PublicShowcaseTests' --logger trx --results-directory $showcaseResults
    if ($LASTEXITCODE -ne 0) { throw 'Showcase export failed.' }
    Write-Output ('Synthetic public media: ' + $env:HARBORBACKUP_PUBLIC_MEDIA_DIR)
} finally {
    $env:HARBORBACKUP_PUBLIC_MEDIA_DIR = $showcasePreviousMedia
    $env:HARBORBACKUP_UI_DPI = $showcasePreviousDpi
    $showcaseLease.ReleaseMutex(); $showcaseLease.Dispose()
}
