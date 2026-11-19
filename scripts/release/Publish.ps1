#Requires -Version 5.1
[CmdletBinding()]
param([switch]$NoRestore, [switch]$IsolatedProfile, [string]$OneDriveClientId, [switch]$AllowUnconfiguredOneDrive)
. (Join-Path $PSScriptRoot '../build/Use-DotNet.ps1')
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
. (Join-Path $PSScriptRoot '../build/SourceIdentity.ps1')
$Source = Get-HarborSourceIdentity $HarborRoot
$SdkVersion = (& $HarborDotNet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the build SDK.' }
$Source | Add-Member -NotePropertyName sdk -NotePropertyValue $SdkVersion
$Release = Get-Content (Join-Path $HarborRoot 'packaging/windows/release.json') -Raw | ConvertFrom-Json
$ProjectLicense = Join-Path $HarborRoot 'LICENSE'
if (-not (Test-Path -LiteralPath $ProjectLicense -PathType Leaf)) { throw 'The project LICENSE must be present in the committed release source.' }
if (-not $OneDriveClientId) { $OneDriveClientId = $Release.oneDriveClientId }
$ParsedClientId = [guid]::Empty
if (-not [string]::IsNullOrWhiteSpace($OneDriveClientId)) {
    if (-not [guid]::TryParse($OneDriveClientId, [ref]$ParsedClientId) -or $ParsedClientId -eq [guid]::Empty) {
        throw 'OneDriveClientId must be a non-empty application GUID, not a client secret.'
    }
    $OneDriveClientId = $ParsedClientId.ToString()
} elseif (-not $AllowUnconfiguredOneDrive) {
    throw 'Configure the HarborBackup OneDrive application ID before publishing. Use -AllowUnconfiguredOneDrive only for offline development validation.'
} else {
    $OneDriveClientId = ''
    Write-Warning 'Development build: default OneDrive login is unavailable.'
}
$PreviousAppData = $env:APPDATA
$Output = Join-Path $HarborRoot ('artifacts/publish/HarborBackup-' + $Release.version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$Payload = Join-Path $Output 'payload'
$App = Join-Path $Payload 'app'

$ArtifactLease = Enter-HarborArtifactOperation
Push-Location $HarborRoot
try {
    if ($IsolatedProfile) { $env:APPDATA = Join-Path $HarborRoot 'artifacts/appdata' }
    foreach ($Directory in @($App,(Join-Path $Payload 'docs'),(Join-Path $Payload 'licenses'))) { New-Item -ItemType Directory -Path $Directory -Force | Out-Null }
    foreach ($Project in @('HarborBackup.Cli','HarborBackup.Desktop')) {
        $ProjectPath = "src/$Project/$Project.csproj"
        if (-not $NoRestore) {
            & $HarborDotNet restore $ProjectPath -r $Release.runtimeIdentifier --configfile NuGet.Config --locked-mode "-p:RuntimeFrameworkVersion=$($Release.runtimeVersion)" -p:SelfContained=true
            if ($LASTEXITCODE -ne 0) { throw "Restore failed: $Project" }
        }
        foreach ($Pack in $Release.runtimePacks) {
            $Package = Join-Path $env:NUGET_PACKAGES "$($Pack.id)/$($Release.runtimeVersion)/$($Pack.id).$($Release.runtimeVersion).nupkg"
            if ((Get-FileHash -LiteralPath $Package -Algorithm SHA512).Hash -ne $Pack.sha512) { throw "Runtime pack checksum mismatch: $($Pack.id)" }
        }
        $Stage = Join-Path $Output $Project
        & $HarborDotNet publish $ProjectPath -c Release -r $Release.runtimeIdentifier --no-restore --self-contained true -o $Stage --verbosity minimal "-p:Version=$($Release.version)" "-p:SourceRevisionId=$($Source.revision)" "-p:HarborBackupOneDriveClientId=$OneDriveClientId" "-p:RuntimeFrameworkVersion=$($Release.runtimeVersion)" -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $Project" }
        foreach ($File in Get-ChildItem -LiteralPath $Stage -Recurse -File) {
            $Target = Join-Path $App $File.FullName.Substring($Stage.Length + 1)
            if (Test-Path -LiteralPath $Target) {
                if ((Get-FileHash -LiteralPath $Target).Hash -ne (Get-FileHash -LiteralPath $File.FullName).Hash) { throw "Conflicting application dependency: $Target" }
            } else { New-Item -ItemType Directory -Path (Split-Path $Target) -Force | Out-Null; Copy-Item -LiteralPath $File.FullName -Destination $Target }
        }
        if (-not ([IO.Path]::GetFullPath($Stage)).StartsWith([IO.Path]::GetFullPath($Output) + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stage path.' }
        Remove-Item -LiteralPath $Stage -Recurse -Force
    }
    $Release | Add-Member -NotePropertyName oneDriveClientId -NotePropertyValue $OneDriveClientId -Force
    $Release | Add-Member -NotePropertyName source -NotePropertyValue $Source -Force
    $Release | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Payload 'release.json') -Encoding UTF8
    Copy-Item -Path 'packaging/licenses/*' -Destination (Join-Path $Payload 'licenses')
    Copy-Item -LiteralPath $ProjectLicense -Destination (Join-Path $Payload 'licenses/HarborBackup-LICENSE.txt')
    Copy-Item -Path 'docs/user-guide/onedrive-account.*.md' -Destination (Join-Path $Payload 'docs')
    foreach ($Pack in $Release.runtimePacks) {
        $PackRoot = Join-Path $env:NUGET_PACKAGES "$($Pack.id)/$($Release.runtimeVersion)"
        foreach ($License in Get-ChildItem -LiteralPath $PackRoot -File | Where-Object { $_.Name -match '^LICENSE|^THIRD-PARTY-NOTICES' }) {
            Copy-Item -LiteralPath $License.FullName -Destination (Join-Path $Payload "licenses/$($Pack.id)-$($License.Name)")
        }
    }
    & (Join-Path $PSScriptRoot 'Assert-PublishLayout.ps1') -Payload $Payload -RuntimeVersion $Release.runtimeVersion -ProjectLicense $ProjectLicense
    $Files = @(Get-ChildItem -LiteralPath $Payload -Recurse -File | Sort-Object FullName | ForEach-Object { [pscustomobject][ordered]@{path=$_.FullName.Substring($Payload.Length+1);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
    Assert-HarborSourceIdentity $HarborRoot $Source
    [ordered]@{schemaVersion=4;version=$Release.version;source=$Source;runtime=$Release.runtimeVersion;rid=$Release.runtimeIdentifier;deployment='self-contained';trimmed=$false;payloadBytes=($Files|Measure-Object bytes -Sum).Sum;files=$Files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Output 'release-manifest.json') -Encoding UTF8
    Write-Output "Published: $Output"
    Invoke-HarborArtifactCleanup -PreservePaths $Output
} finally { Pop-Location; $env:APPDATA=$PreviousAppData; $ArtifactLease.ReleaseMutex(); $ArtifactLease.Dispose() }
