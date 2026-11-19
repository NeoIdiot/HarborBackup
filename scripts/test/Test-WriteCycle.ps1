#Requires -Version 5.1
[CmdletBinding()]
param([switch]$IsolatedProfile)
. (Join-Path $PSScriptRoot '../build/Use-DotNet.ps1')
$PreviousAppData=$env:APPDATA
try {
    if($IsolatedProfile){$env:APPDATA=Join-Path $HarborRoot 'artifacts/appdata'}
    & $HarborDotNet test (Join-Path $HarborRoot 'tests/HarborBackup.Integration.Tests/HarborBackup.Integration.Tests.csproj') --no-restore --filter FullyQualifiedName~CloudTransportTests
    if($LASTEXITCODE -ne 0){throw 'Cloud write/recovery cycle tests failed.'}
} finally {$env:APPDATA=$PreviousAppData}