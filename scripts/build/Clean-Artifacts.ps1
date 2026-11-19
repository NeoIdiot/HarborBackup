#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess)]
param([ValidateRange(1, 100)][int]$KeepCount = 2)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ArtifactMaintenance.ps1')
$options = @{}
if ($PSBoundParameters.ContainsKey('KeepCount')) { $options.KeepCount = $KeepCount }
Remove-HarborArtifactHistory @options -WhatIf:$WhatIfPreference
