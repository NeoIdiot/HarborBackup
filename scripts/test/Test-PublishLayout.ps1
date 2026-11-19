#Requires -Version 5.1
# Synthetic files exercise packaging guards only; installed executables are tested separately.
$ErrorActionPreference='Stop'
$Repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Root=Join-Path $Repo ('artifacts/package-layout-tests/'+[guid]::NewGuid().ToString('N'))
$App=Join-Path $Root 'app'
New-Item -ItemType Directory -Path $App -Force|Out-Null
$Guard=Join-Path $Repo 'scripts/release/Assert-PublishLayout.ps1'
$ProjectLicense=Join-Path $Repo 'LICENSE'
$PackagedLicense=Join-Path $Root 'licenses/HarborBackup-LICENSE.txt'
New-Item -ItemType Directory -Path (Split-Path $PackagedLicense) -Force|Out-Null
Copy-Item -LiteralPath $ProjectLicense -Destination $PackagedLicense
$Version=(Get-Content (Join-Path $Repo 'packaging/windows/release.json') -Raw|ConvertFrom-Json).runtimeVersion
foreach($Name in @('HarborBackup.Desktop.exe','HarborBackup.Cli.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll','System.Security.Cryptography.ProtectedData.dll','Microsoft.Identity.Client.dll')){
    [IO.File]::WriteAllText((Join-Path $App $Name),'fixture')
}
$Config=@{runtimeOptions=@{includedFrameworks=@(@{name='Microsoft.NETCore.App';version=$Version},@{name='Microsoft.WindowsDesktop.App';version=$Version})}}|ConvertTo-Json -Depth 5
foreach($Entry in @('HarborBackup.Desktop','HarborBackup.Cli')){
    [IO.File]::WriteAllText((Join-Path $App "$Entry.runtimeconfig.json"),$Config)
}
function Assert-Rejected([string]$Name){
    $Rejected=$false
    try { & $Guard -Payload $Root -RuntimeVersion $Version -ProjectLicense $ProjectLicense } catch {$Rejected=$true}
    if(-not $Rejected){throw "Packaging guard accepted: $Name"}
    Write-Output "PASS: $Name"
}
& $Guard -Payload $Root -RuntimeVersion $Version -ProjectLicense $ProjectLicense
Write-Output 'PASS: self-contained WinForms layout'
Remove-Item -LiteralPath $PackagedLicense
Assert-Rejected 'missing project license'
[IO.File]::WriteAllText($PackagedLicense,'different license')
Assert-Rejected 'changed project license'
Copy-Item -LiteralPath $ProjectLicense -Destination $PackagedLicense -Force
$Native=Join-Path $App 'coreclr.dll'
Remove-Item -LiteralPath $Native
Assert-Rejected 'missing runtime binary'
[IO.File]::WriteAllText($Native,'fixture')
$ConfigPath=Join-Path $App 'HarborBackup.Cli.runtimeconfig.json'
[IO.File]::WriteAllText($ConfigPath,'{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.14"}}}')
Assert-Rejected 'framework-dependent entry'
[IO.File]::WriteAllText($ConfigPath,$Config.Replace($Version,'0.0.0'))
Assert-Rejected 'wrong runtime version'
[IO.File]::WriteAllText($ConfigPath,$Config)
$Wpf=Join-Path $App 'PresentationFramework.dll'
[IO.File]::WriteAllText($Wpf,'fixture')
Assert-Rejected 'unneeded WPF profile'
Remove-Item -LiteralPath $Wpf
New-Item -ItemType Directory -Path (Join-Path $Root 'runtime')|Out-Null
Assert-Rejected 'duplicate runtime directory'
