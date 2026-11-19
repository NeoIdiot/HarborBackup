#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Payload, [Parameter(Mandatory=$true)][string]$RuntimeVersion, [string]$ProjectLicense)
$ErrorActionPreference = 'Stop'
$App = Join-Path $Payload 'app'
if ($ProjectLicense) {
    $PackagedLicense = Join-Path $Payload 'licenses/HarborBackup-LICENSE.txt'
    if (-not (Test-Path -LiteralPath $PackagedLicense -PathType Leaf) -or
        (Get-FileHash -LiteralPath $PackagedLicense -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $ProjectLicense -Algorithm SHA256).Hash) {
        throw 'The packaged HarborBackup license is missing or differs from the committed project LICENSE.'
    }
}
if (Test-Path -LiteralPath (Join-Path $Payload 'runtime')) { throw 'Self-contained payload must not include a second runtime directory.' }
foreach ($File in @('HarborBackup.Desktop.exe','HarborBackup.Cli.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll','System.Security.Cryptography.ProtectedData.dll','Microsoft.Identity.Client.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $App $File) -PathType Leaf)) { throw "Self-contained payload missing $File" }
}
if (Test-Path -LiteralPath (Join-Path $App 'PresentationFramework.dll')) { throw 'Unexpected WPF payload; publish the WinForms profile only.' }
foreach ($Entry in @('HarborBackup.Desktop','HarborBackup.Cli')) {
    $Options = (Get-Content -LiteralPath (Join-Path $App "$Entry.runtimeconfig.json") -Raw | ConvertFrom-Json).runtimeOptions
    if ($Options.framework -or $Options.frameworks) { throw "$Entry requires an external shared framework." }
    $Frameworks = @($Options.includedFrameworks)
    if ($Frameworks.Count -ne 2) { throw "$Entry has unexpected self-contained frameworks." }
    foreach ($Name in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $Matches = @($Frameworks | Where-Object { $_.name -eq $Name -and $_.version -eq $RuntimeVersion })
        if ($Matches.Count -ne 1) { throw "$Entry does not include the pinned $Name runtime." }
    }
}
