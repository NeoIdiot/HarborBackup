#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Tools = Join-Path $Repo 'artifacts/tools'
$Installer = Join-Path $Tools 'innosetup-7.1.0-x64.exe'
$Destination = Join-Path $Tools 'inno-7.1.0'
New-Item -ItemType Directory -Path $Tools -Force | Out-Null
if (-not (Test-Path -LiteralPath $Installer)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile $Installer
}
if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ne '0362A383ED217D4C4239B5933866DD96D3EB2102737DA92F80F6057A4B40DF2F') { throw 'Compiler download hash mismatch.' }
$Signature = Get-AuthenticodeSignature -LiteralPath $Installer
if ($Signature.Status -ne 'Valid' -or $Signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B\.V\.') { throw 'Compiler publisher verification failed.' }
$Process = Start-Process -FilePath $Installer -ArgumentList @('/PORTABLE=1','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',('/DIR="' + $Destination + '"')) -WindowStyle Hidden -PassThru -Wait
if ($Process.ExitCode -ne 0) { throw "Compiler extraction failed: $($Process.ExitCode)" }
Write-Output "Portable compiler: $(Join-Path $Destination 'ISCC.exe')"
