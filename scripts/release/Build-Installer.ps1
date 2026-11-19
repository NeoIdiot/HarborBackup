#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PublishDirectory, [string]$Compiler, [switch]$ValidationBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
. (Join-Path $PSScriptRoot '../build/SourceIdentity.ps1')
$ArtifactLease = Enter-HarborArtifactOperation
try {
$Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$Manifest = Get-Content -LiteralPath (Join-Path $PublishDirectory 'release-manifest.json') -Raw | ConvertFrom-Json
$Release = Get-Content -LiteralPath (Join-Path $Repo 'packaging/windows/release.json') -Raw | ConvertFrom-Json
if ($Manifest.schemaVersion -ne 4 -or $Manifest.deployment -ne 'self-contained' -or $Manifest.trimmed -or $Manifest.rid -ne 'win-x64') { throw 'Installer requires the source-identified untrimmed self-contained x64 payload (manifest v4).' }
Assert-HarborSourceIdentity $Repo $Manifest.source
if ($Manifest.version -ne $Release.version -or $Manifest.runtime -ne $Release.runtimeVersion) { throw 'Release metadata and payload do not match.' }
$Payload = Join-Path $PublishDirectory 'payload'
& (Join-Path $PSScriptRoot 'Assert-PublishLayout.ps1') -Payload $Payload -RuntimeVersion $Release.runtimeVersion -ProjectLicense (Join-Path $Repo 'LICENSE')
$Expected = @{}
foreach ($File in $Manifest.files) {
    $Path = [IO.Path]::GetFullPath((Join-Path $Payload $File.path))
    if (-not $Path.StartsWith($Payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes payload.' }
    if ((Get-FileHash -LiteralPath $Path).Hash -ne $File.sha256) { throw "Payload modified: $($File.path)" }
    $Expected[$Path] = $true
}
foreach ($File in Get-ChildItem -LiteralPath $Payload -File -Recurse) {
    if (-not $Expected.ContainsKey($File.FullName)) { throw "Unlisted payload file: $($File.Name)" }
}
if (-not $Compiler) { $Compiler = Join-Path $Repo 'artifacts/tools/inno-7.1.0/ISCC.exe' }
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
$CompilerVersion = (& $Compiler --version | Out-String).Trim()
if ($CompilerVersion -notmatch '^7\.1\.0') { throw "Use the pinned Inno Setup 7.1.0 compiler: $CompilerVersion" }
$Folder = if ($ValidationBuild) { 'validation-installers' } else { 'installers' }
$Output = Join-Path $PublishDirectory $Folder
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$ProductId = if ($ValidationBuild) { 'HarborBackup.IsolatedValidation' } else { $Release.appId }
$Arguments = @("--define=PayloadDir=$Payload", "--define=ReleaseDir=$Output", "--define=AppVersion=$($Manifest.version)", "--define=NumericVersion=$((($Manifest.version -split '-')[0])+'.0')", "--define=ProductId=$ProductId")
if ($ValidationBuild) { $Arguments += '--define=ValidationBuild=1' }
& $Compiler --quiet @Arguments (Join-Path $Repo 'packaging/windows/HarborBackup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$Installers = @(Get-ChildItem -LiteralPath $Output -Filter '*Setup-x64.exe')
if ($Installers.Count -ne 1) { throw 'Expected one installer.' }
$Installer = $Installers[0]
Assert-HarborSourceIdentity $Repo $Manifest.source
$Report = [ordered]@{ version = $Manifest.version; source = $Manifest.source; runtime = $Manifest.runtime; deployment = $Manifest.deployment; trimmed = $Manifest.trimmed; installerBytes = $Installer.Length; payloadBytes = $Manifest.payloadBytes; sha256 = (Get-FileHash -LiteralPath $Installer.FullName).Hash; signed = (Get-AuthenticodeSignature -LiteralPath $Installer.FullName).Status -eq 'Valid'; validationBuild = [bool]$ValidationBuild }
$Report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'package-report.json') -Encoding UTF8
Write-Output "Installer: $($Installer.FullName)"
Write-Output ($Report | ConvertTo-Json -Compress)
Invoke-HarborArtifactCleanup -PreservePaths $PublishDirectory
} finally { $ArtifactLease.ReleaseMutex(); $ArtifactLease.Dispose() }
