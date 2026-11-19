#Requires -Version 5.1
param([switch]$NoRestore,[switch]$IsolatedProfile,[ValidateSet('Debug','Release')][string]$Configuration='Release')
. (Join-Path $PSScriptRoot '../build/Use-DotNet.ps1')
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
. (Join-Path $PSScriptRoot 'TestEnvironment.ps1')
. (Join-Path $PSScriptRoot '../build/SourceIdentity.ps1')
Assert-HarborTestEnvironment
$TestSource=Get-HarborSourceIdentity $HarborRoot -AllowDirty
$TestSdk=(& $HarborDotNet --version | Out-String).Trim()
if($LASTEXITCODE -ne 0){throw 'Cannot identify the test SDK.'}
$PreviousAppData=$env:APPDATA
$ArtifactLease=Enter-HarborArtifactOperation
$Results=Join-Path $HarborRoot ('artifacts/test-results/run-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
Push-Location $HarborRoot
try {
    New-Item -ItemType Directory -Path $Results -Force | Out-Null
    @{source=$TestSource;sdk=$TestSdk;configuration=$Configuration;isolatedProfile=[bool]$IsolatedProfile;startedUtc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Results 'run-context.json') -Encoding UTF8
    if($IsolatedProfile){$env:APPDATA=Join-Path $HarborRoot 'artifacts/appdata'}
    & (Join-Path $PSScriptRoot 'Test-ArtifactCleanup.ps1')
    & (Join-Path $PSScriptRoot 'Test-TestEnvironment.ps1')
    & (Join-Path $PSScriptRoot 'Test-SourceIdentity.ps1')
    & (Join-Path $PSScriptRoot 'Test-PublishLayout.ps1')
    if(-not $NoRestore){ & $HarborDotNet restore HarborBackup.slnx --configfile NuGet.Config --locked-mode; if($LASTEXITCODE -ne 0){throw 'Restore failed.'} }
    & $HarborDotNet build HarborBackup.slnx -c $Configuration --no-restore --verbosity minimal
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    # Assemblies intentionally exercise the same real process-wide run lock. Run them serially.
    foreach($Name in @('Core','Windows','Desktop','Integration')) {
        Assert-HarborTestEnvironment
        & $HarborDotNet test "tests/HarborBackup.$Name.Tests/HarborBackup.$Name.Tests.csproj" -c $Configuration --no-build --no-restore --logger trx --results-directory $Results
        if($LASTEXITCODE -ne 0){throw "$Name tests failed."}
    }
    Assert-HarborTestEnvironment
    & (Join-Path $PSScriptRoot 'Test-CrashRecovery.ps1') -Configuration $Configuration -ResultsDirectory $Results
    $FinalSource=Get-HarborSourceIdentity $HarborRoot -AllowDirty
    if($FinalSource.revision -ne $TestSource.revision -or $FinalSource.tree -ne $TestSource.tree -or $FinalSource.clean -ne $TestSource.clean){throw 'Source identity changed during verification; this run is not marked complete.'}
    @{completedUtc=[DateTimeOffset]::UtcNow.ToString('o');source=$TestSource;sdk=$TestSdk;configuration=$Configuration}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Results 'completed.json') -Encoding UTF8
    Invoke-HarborArtifactCleanup -PreservePaths $Results
} finally {Pop-Location;$env:APPDATA=$PreviousAppData;$ArtifactLease.ReleaseMutex();$ArtifactLease.Dispose()}
