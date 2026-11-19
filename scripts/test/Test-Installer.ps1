#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
$ArtifactLease=Enter-HarborArtifactOperation
try {
$Repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Installer=(Resolve-Path -LiteralPath $Installer).Path
$Report=Get-Content -LiteralPath (Join-Path (Split-Path $Installer) 'package-report.json') -Raw | ConvertFrom-Json
if(-not $Report.validationBuild -or (Get-FileHash -LiteralPath $Installer).Hash -ne $Report.sha256){throw 'A manifest-verified isolated validation installer is required.'}
if(Get-Process -Name 'HarborBackup.Desktop' -ErrorAction SilentlyContinue){throw 'Exit the desktop app before isolated installation validation.'}
$Root=Join-Path $Repo ('artifacts/install-validation/'+[guid]::NewGuid().ToString('N'))
$Install=Join-Path $Root 'Installed HarborBackup 测试'
$App=Join-Path $Install 'app'
$Cloud=Join-Path $Root 'cloud';$Source=Join-Path $Root 'source';$Config=Join-Path $Install 'data/config'
foreach($Directory in @($Root,$Cloud,$Source)){New-Item -ItemType Directory -Path $Directory -Force|Out-Null}
function Invoke-Setup([string]$Label){$p=Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="'+$Install+'"'),('/LOG="'+(Join-Path $Root ($Label+'.log'))+'"')) -WindowStyle Hidden -PassThru -Wait;return $p.ExitCode}
function Invoke-Cli([string[]]$Arguments){$output=& (Join-Path $App 'HarborBackup.Cli.exe') @Arguments;if($LASTEXITCODE -ne 0){throw "CLI failed ($LASTEXITCODE): $output"};return $output}
# Expected failures write to stderr. Windows PowerShell 5.1 turns redirected native stderr into a
# terminating error under ErrorActionPreference=Stop, so relax it locally and inspect the exit code.
function Invoke-CliFailure([string[]]$Arguments){$previous=$ErrorActionPreference;$ErrorActionPreference='Continue';try{$output=& (Join-Path $App 'HarborBackup.Cli.exe') @Arguments 2>&1;$code=$LASTEXITCODE}finally{$ErrorActionPreference=$previous};return [pscustomobject]@{Code=$code;Output=$output}}
# Retained state paths can exceed 260 characters. Windows PowerShell 5.1 (unlike 7) cannot hash them
# without the extended-length prefix, so open the stream through \\?\ and hash it.
function Get-DataHash([string]$Path){$stream=[IO.File]::OpenRead(([string][char]92)*2+'?'+[char]92+$Path);try{$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$sha.Dispose()}}finally{$stream.Dispose()}}
if((Invoke-Setup 'install') -ne 0){throw 'Installation failed.'}
$InitialProjects=Get-Content -LiteralPath (Join-Path $Config 'projects.json') -Raw|ConvertFrom-Json
if($InitialProjects.schemaVersion -ne 2 -or $InitialProjects.folders.Count -ne 0){throw 'Installer did not create current empty project settings.'}
if(Test-Path -LiteralPath (Join-Path $Install 'runtime')){throw 'Fresh install contains an obsolete runtime directory.'}
# A self-contained app must not rely on the system dotnet host or DOTNET_ROOT.
$SavedDotnetRoot=$env:DOTNET_ROOT
$SavedDotnetRootX64=$env:DOTNET_ROOT_X64
$SavedPath=$env:PATH
try {
    $env:DOTNET_ROOT=Join-Path $Root 'missing-dotnet'
    $env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
    $env:PATH=$env:SystemRoot+'\System32;'+$env:SystemRoot
    Invoke-Cli @('--help')|Out-Null
} finally {$env:DOTNET_ROOT=$SavedDotnetRoot;$env:DOTNET_ROOT_X64=$SavedDotnetRootX64;$env:PATH=$SavedPath}
# Load the packaged MSAL dependencies without a network request or production credentials.
$AccountConfig=Join-Path $Root 'account-smoke/config'
New-Item -ItemType Directory -Path $AccountConfig -Force|Out-Null
@{schemaVersion=2;enabled=$false;clientId=[guid]::NewGuid().ToString();accountId='isolated-test'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $AccountConfig 'onedrive-account.json') -Encoding UTF8
$Disconnected=Invoke-Cli @('cloud-disconnect','--config',(Join-Path $AccountConfig 'projects.json'))|ConvertFrom-Json
if($Disconnected.status -ne 'AuthorizationRemoved'){throw 'Packaged authorization dependencies failed.'}
New-Item -ItemType Directory -Path $Config -Force|Out-Null
[ordered]@{schemaVersion=2;folders=@(@{id=([guid]::NewGuid().ToString('N')+[guid]::NewGuid().ToString('N'));source=$Source;destination='Projects/Demo'})}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Config 'projects.json') -Encoding UTF8
[ordered]@{schemaVersion=1;sync=@{enabled=$true};schedule=@{intervalMinutes=1440;startDelayMinutes=1440}}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $Config 'backup-settings.json') -Encoding UTF8
@{schemaVersion=1;oneDriveRoot=$Cloud}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Config 'storage-settings.json') -Encoding UTF8
$Original=Join-Path $Source 'sample.txt';$Target=Join-Path $Cloud 'Projects/Demo/sample.txt'
[IO.File]::WriteAllText($Original,'preserve source')
# Old directory configuration is evidence, never a fallback transport.
$LegacyBefore=(Get-FileHash -LiteralPath (Join-Path $Config 'storage-settings.json')).Hash
$Unbound=Invoke-CliFailure @('status','--json')
if($Unbound.Code -eq 0){throw 'Removed directory transport was unexpectedly enabled.'}
@{schemaVersion=2;enabled=$true;clientId=[guid]::NewGuid().ToString();accountId='isolated-test';driveId='synthetic-drive';rootId='synthetic-root';repositoryId=[guid]::NewGuid().ToString('N')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $Config 'onedrive-account.json') -Encoding UTF8
Push-Location ([IO.Path]::GetTempPath())
try {$Status=Invoke-Cli @('status','--json')|ConvertFrom-Json} finally {Pop-Location}
if($Status.cloudUploadStatus -ne 'AccountTransport'){throw 'Installed CLI is not account-only.'}
$Paused=Invoke-Cli @('pause','--json')|ConvertFrom-Json
if(-not $Paused.paused){throw 'Pause did not persist.'}
$Resumed=Invoke-Cli @('resume','--json')|ConvertFrom-Json
if($Resumed.paused){throw 'Resume did not persist.'}
# A synthetic binding without a token must not send a request or copy to the old local target.
$Unauthorized=Invoke-CliFailure @('backup','--json')
if($Unauthorized.Code -eq 0){throw 'Backup ran without authorization.'}
if((Test-Path -LiteralPath $Target) -or [IO.File]::ReadAllText($Original) -ne 'preserve source'){throw 'Unauthorized operation changed business files.'}
if((Get-FileHash -LiteralPath (Join-Path $Config 'storage-settings.json')).Hash -ne $LegacyBefore){throw 'Old data was changed.'}
$Desktop=Join-Path $App 'HarborBackup.Desktop.exe'
$Process=Start-Process -FilePath $Desktop -ArgumentList '--background' -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 4
    $Process.Refresh();if($Process.HasExited){throw 'Installed desktop exited unexpectedly.'}
    $Modules=@($Process.Modules|Where-Object ModuleName -eq 'coreclr.dll'|ForEach-Object FileName)
    $RuntimeRoot=[IO.Path]::GetFullPath($App)+'\'
    if($Modules.Count -ne 1 -or -not $Modules[0].StartsWith($RuntimeRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Desktop did not load the self-contained runtime from app.'}
    $WorkingSet=$Process.WorkingSet64
    if((Invoke-Setup 'busy-upgrade') -eq 0){throw 'Installer replaced a running application.'}
} finally {
    if(-not $Process.HasExited){$Stop=Start-Process -FilePath $Desktop -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru -Wait;if(-not $Process.WaitForExit(15000)){throw 'Desktop did not exit safely.'}}
}
if(-not(Test-Path -LiteralPath (Join-Path $Config 'desktop-settings.json'))){throw 'Desktop preferences were not saved under data/config.'}
$Sentinel=Join-Path $Install 'data/keep.txt';[IO.File]::WriteAllText($Sentinel,'retain user data')
$Before=@{};Get-ChildItem -LiteralPath (Join-Path $Install 'data') -Recurse -File|ForEach-Object{$Before[$_.FullName]=(Get-DataHash $_.FullName)}
[IO.File]::WriteAllText((Join-Path $App 'obsolete-test.dll'),'obsolete')
# Simulate the old managed runtime directory without touching user data.
$OldRuntime=Join-Path $Install 'runtime/shared/obsolete'
New-Item -ItemType Directory -Path $OldRuntime -Force|Out-Null
[IO.File]::WriteAllText((Join-Path $OldRuntime 'old-runtime.dll'),'obsolete')
if((Invoke-Setup 'upgrade') -ne 0){throw 'Upgrade failed.'}
if(Test-Path -LiteralPath (Join-Path $Install 'runtime')){throw 'Obsolete runtime directory retained.'}
if(Test-Path -LiteralPath (Join-Path $App 'obsolete-test.dll')){throw 'Obsolete application file retained.'}
foreach($Path in $Before.Keys){if((Get-DataHash $Path) -ne $Before[$Path]){throw 'Upgrade changed data.'}}
Invoke-Cli @('status','--json')|Out-Null
[string]$FreshRoot=Join-Path $Root 'fresh-start/data'
$FreshConfig=Join-Path $FreshRoot 'config'
New-Item -ItemType Directory -Path $FreshConfig -Force|Out-Null
$FreshOriginal=Join-Path $FreshConfig 'projects.json'
[IO.File]::WriteAllText($FreshOriginal,'{"schemaVersion":1,"folders":[]}')
$FreshHash=(Get-FileHash -LiteralPath $FreshOriginal).Hash
$FreshResult=Invoke-Cli @('initialize','--fresh','--config',$FreshOriginal)|ConvertFrom-Json
if((Get-FileHash -LiteralPath (Join-Path $FreshResult.retainedData 'config/projects.json')).Hash -ne $FreshHash){throw 'Fresh initialization changed retained old data.'}
$FreshSettings=Get-Content -LiteralPath $FreshOriginal -Raw|ConvertFrom-Json
if($FreshSettings.schemaVersion -ne 2 -or $FreshSettings.folders.Count -ne 0){throw 'Fresh initialization did not generate empty ID-based settings.'}
$Uninstall=Start-Process -FilePath (Join-Path $Install 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $Root 'uninstall.log')+'"')) -WindowStyle Hidden -PassThru -Wait
if($Uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $Desktop)){throw 'Uninstall failed.'}
foreach($Path in $Before.Keys){if((Get-DataHash $Path) -ne $Before[$Path]){throw 'Uninstall changed data.'}}
if((Invoke-Setup 'reinstall') -ne 0){throw 'Reinstall failed.'}
Invoke-Cli @('status','--json')|Out-Null
$LockedBefore=@{};Get-ChildItem -LiteralPath (Join-Path $Install 'data') -Recurse -File|ForEach-Object{$LockedBefore[$_.FullName]=(Get-DataHash $_.FullName)}
$ConfigLock=[IO.File]::Open((Join-Path $Config 'projects.json'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try { if((Invoke-Setup 'locked-data') -eq 0){throw 'Installer silently completed despite unreadable data.'} }
finally { $ConfigLock.Dispose() }
foreach($Path in $LockedBefore.Keys){if((Get-DataHash $Path) -ne $LockedBefore[$Path]){throw 'Failed data check changed data.'}}
[IO.File]::WriteAllText((Join-Path $Config 'projects.json'),'{"schemaVersion":1,"folders":[]}')
$OldBefore=@{};Get-ChildItem -LiteralPath (Join-Path $Install 'data') -Recurse -File|ForEach-Object{$OldBefore[$_.FullName.Substring((Join-Path $Install 'data').Length+1)]=(Get-DataHash $_.FullName)}
if((Invoke-Setup 'old-format-upgrade') -ne 0){throw 'Old-format automatic rebuild failed.'}
$Retained=@(Get-ChildItem -LiteralPath $Install -Directory -Filter 'data-retained-*')
if($Retained.Count -ne 1){throw 'Expected one retained original data directory.'}
foreach($Relative in $OldBefore.Keys){if((Get-DataHash (Join-Path $Retained[0].FullName $Relative)) -ne $OldBefore[$Relative]){throw 'Automatic rebuild changed original data.'}}
$Rebuilt=Get-Content -LiteralPath (Join-Path $Config 'projects.json') -Raw|ConvertFrom-Json
if($Rebuilt.schemaVersion -ne 2 -or $Rebuilt.folders.Count -ne 0){throw 'Automatic rebuild did not create empty current settings.'}
if([IO.File]::ReadAllText($Original) -ne 'preserve source' -or (Test-Path -LiteralPath $Target)){throw 'Automatic rebuild changed business files.'}
$RebuiltBefore=@{};Get-ChildItem -LiteralPath (Join-Path $Install 'data') -Recurse -File|ForEach-Object{$RebuiltBefore[$_.FullName]=(Get-DataHash $_.FullName)}
if((Invoke-Setup 'repeat-install') -ne 0){throw 'Repeat install failed.'}
foreach($Path in $RebuiltBefore.Keys){if((Get-DataHash $Path) -ne $RebuiltBefore[$Path]){throw 'Repeat install reset current data.'}}
if(@(Get-ChildItem -LiteralPath $Install -Directory -Filter 'data-retained-*').Count -ne 1){throw 'Repeat install rebuilt current data again.'}
[ordered]@{passed=$true;version=$Report.version;applicationDirectory=$Install;selfContainedRuntime=$Modules;desktopWorkingSetBytes=$WorkingSet;checks=@('fresh install automatically creates current empty configuration','packaged MSAL offline authorization smoke','default CLI config shared with desktop','unrelated working directory','Chinese and space paths','legacy directory transport unavailable','account status and pause/resume','unauthorized backup preserves source and old storage','cloud transfers covered by isolated integration suite','self-contained runtime loaded from app','obsolete runtime directory removed','busy upgrade blocked','upgrade preserves all data hashes','obsolete program files removed','uninstall preserves all data hashes','reinstall reads retained data','fresh initialization retains old data without migration','installer automatically rebuilds old format with all bytes retained','repeat install preserves rebuilt data','unreadable data aborts installation without resetting data')}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Root 'validation.json') -Encoding UTF8
Write-Output "PASS: $Root"
Invoke-HarborArtifactCleanup -PreservePaths @($Root, (Split-Path (Split-Path $Installer)))
} finally {$ArtifactLease.ReleaseMutex();$ArtifactLease.Dispose()}
