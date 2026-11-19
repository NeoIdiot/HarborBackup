#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
$exportScript = Join-Path $PSScriptRoot '../release/Export-Installer.ps1'
$exportTestRoot = Assert-HarborArtifactPath (Join-Path $PSScriptRoot ('../../artifacts/diagnostics/installer-export-' + [guid]::NewGuid().ToString('N')))
$exportTestLease = Enter-HarborArtifactOperation
$exportTestLink = $null
function Assert-ExportTest([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function New-ExportFixture([string]$Name, [string]$Version, [string]$Identity, [bool]$Validation = $false) {
    $run = Assert-HarborArtifactPath (Join-Path $exportTestRoot $Name)
    $directory = Join-Path $run 'installers'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $exe = Join-Path $directory ('HarborBackup-' + $Version + '-Setup-x64.exe')
    [IO.File]::WriteAllBytes($exe, [Text.Encoding]::ASCII.GetBytes('Synthetic installer ' + $Identity))
    $report = [ordered]@{ version = $Version; source = [ordered]@{ revision = ($Identity * 40); tree = ('f' * 40); clean = $true; sdk = '10.0.115' };
        installerBytes = (Get-Item -LiteralPath $exe).Length; sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;
        signed = $false; validationBuild = $Validation }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $directory 'package-report.json') -Encoding UTF8
    return $run
}
function Assert-ExportRejected([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-ExportTest $rejected $Message
}
try {
    New-Item -ItemType Directory -Path $exportTestRoot -Force | Out-Null
    $output = Join-Path $exportTestRoot 'entry'
    $first = New-ExportFixture 'first' '9.1.1' 'a'
    & $exportScript -PublishDirectory $first -OutputDirectory $output | Out-Null
    $firstName = 'HarborBackup-9.1.1-Setup-x64.exe'
    Assert-ExportTest (Test-Path -LiteralPath (Join-Path $output $firstName)) 'The normal installer was not copied.'
    $originalReport = Join-Path $first 'installers/package-report.json'
    Assert-ExportTest ((Get-FileHash -LiteralPath $originalReport).Hash -eq (Get-FileHash -LiteralPath (Join-Path $output 'package-report.json')).Hash) 'The original package report was changed.'
    $latest = Get-Content -LiteralPath (Join-Path $output 'latest.json') -Raw | ConvertFrom-Json
    Assert-ExportTest ($latest.source.revision -eq ('a' * 40) -and $latest.version -eq '9.1.1' -and -not $latest.validationBuild) 'Original source identity/version was not retained.'
    Assert-ExportTest (-not [IO.Path]::IsPathRooted($latest.sourceDirectory)) 'Source directory must be artifact-relative.'
    Assert-ExportTest ((Get-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Raw).Contains($latest.sha256 + ' *' + $firstName)) 'Checksum entry does not match the installer.'
    $entryHash = (Get-FileHash -LiteralPath (Join-Path $output $firstName)).Hash
    $latestHash = (Get-FileHash -LiteralPath (Join-Path $output 'latest.json')).Hash

    $validation = New-ExportFixture 'validation' '9.1.2' 'b' $true
    Assert-ExportRejected { & $exportScript -PublishDirectory $validation -OutputDirectory $output } 'A validation installer was accepted.'
    $missingFlag = New-ExportFixture 'missing-flag' '9.1.2' 'b'
    $flagReportPath = Join-Path $missingFlag 'installers/package-report.json'
    $flagReport = Get-Content -LiteralPath $flagReportPath -Raw | ConvertFrom-Json
    $flagReport.PSObject.Properties.Remove('validationBuild')
    $flagReport | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $flagReportPath -Encoding UTF8
    Assert-ExportRejected { & $exportScript -PublishDirectory $missingFlag -OutputDirectory $output } 'A missing validation flag was accepted.'
    $badFlag = New-ExportFixture 'string-flag' '9.1.2' 'b'
    $flagReportPath = Join-Path $badFlag 'installers/package-report.json'
    $flagReport = Get-Content -LiteralPath $flagReportPath -Raw | ConvertFrom-Json
    $flagReport.validationBuild = 'false'
    $flagReport | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $flagReportPath -Encoding UTF8
    Assert-ExportRejected { & $exportScript -PublishDirectory $badFlag -OutputDirectory $output } 'A nonboolean validation flag was accepted.'
    $missingReport = Join-Path $exportTestRoot 'missing-report'
    New-Item -ItemType Directory -Path (Join-Path $missingReport 'installers') -Force | Out-Null
    Assert-ExportRejected { & $exportScript -PublishDirectory $missingReport -OutputDirectory $output } 'A missing package report was accepted.'
    $damaged = New-ExportFixture 'damaged' '9.1.2' 'b'
    [IO.File]::WriteAllBytes((Join-Path $damaged 'installers/HarborBackup-9.1.2-Setup-x64.exe'), [Text.Encoding]::ASCII.GetBytes('Synthetic installer x'))
    Assert-ExportRejected { & $exportScript -PublishDirectory $damaged -OutputDirectory $output } 'Damaged installer bytes were accepted.'
    Assert-ExportRejected { & $exportScript -PublishDirectory $first -OutputDirectory (Join-Path $first 'entry') } 'Overlapping source/output was accepted.'
    Assert-ExportRejected { & $exportScript -PublishDirectory $first -OutputDirectory (Split-Path -Parent $first) } 'An output containing the source was accepted.'
    $unknown = Join-Path $exportTestRoot 'unknown-output'
    New-Item -ItemType Directory -Path $unknown | Out-Null
    $sentinel = Join-Path $unknown 'keep.txt'; [IO.File]::WriteAllText($sentinel, 'unrelated user material')
    Assert-ExportRejected { & $exportScript -PublishDirectory $first -OutputDirectory $unknown } 'An unknown output directory was replaced.'
    Assert-ExportTest ((Get-Content -LiteralPath $sentinel -Raw) -ceq 'unrelated user material' -and @(Get-ChildItem -LiteralPath $unknown -Force).Count -eq 1) 'Unknown output material was modified.'
    $extraMaterial = Assert-HarborArtifactPath (Join-Path $output 'keep.txt')
    [IO.File]::WriteAllText($extraMaterial, 'extra material inside an otherwise valid catalog')
    Assert-ExportRejected { & $exportScript -PublishDirectory $first -OutputDirectory $output } 'Extra files in an existing catalog were deleted.'
    Assert-ExportTest ((Get-Content -LiteralPath $extraMaterial -Raw) -ceq 'extra material inside an otherwise valid catalog') 'Extra catalog material changed.'
    Remove-Item -LiteralPath $extraMaterial -Force
    $linked = Join-Path $exportTestRoot 'linked'
    New-Item -ItemType Directory -Path $linked | Out-Null
    $exportTestLink = Assert-HarborArtifactPath (Join-Path $linked 'installers')
    New-Item -ItemType Junction -Path $exportTestLink -Target (Join-Path $first 'installers') | Out-Null
    Assert-ExportRejected { & $exportScript -PublishDirectory $linked -OutputDirectory $output } 'A linked source directory was accepted.'
    # Delete only the junction itself, without recursively traversing its target.
    [IO.Directory]::Delete($exportTestLink); $exportTestLink = $null
    Assert-ExportTest ($entryHash -eq (Get-FileHash -LiteralPath (Join-Path $output $firstName)).Hash -and
        $latestHash -eq (Get-FileHash -LiteralPath (Join-Path $output 'latest.json')).Hash) 'A rejected export changed the previous stable entry.'

    $second = New-ExportFixture 'second' '9.1.2' 'c'
    & $exportScript -PublishDirectory $second -OutputDirectory $output | Out-Null
    $entries = @(Get-ChildItem -LiteralPath $output -Filter '*Setup-x64.exe' -File)
    Assert-ExportTest ($entries.Count -eq 1 -and $entries[0].Name -eq 'HarborBackup-9.1.2-Setup-x64.exe') 'Successful replacement retained an old installer.'
    $latest = Get-Content -LiteralPath (Join-Path $output 'latest.json') -Raw | ConvertFrom-Json
    Assert-ExportTest ($latest.source.revision -eq ('c' * 40)) 'Replacement source identity is incorrect.'
    Assert-ExportTest (@(Get-ChildItem -LiteralPath $exportTestRoot -Directory -Filter '.installer-export-*').Count -eq 0) 'Successful export left staging/previous entries.'
    Write-Output 'PASS: normal installer export, original provenance, validation/hash/link/overlap rejection, failure preservation and replacement.'
} finally {
    try {
        if ($exportTestLink -and (Test-Path -LiteralPath $exportTestLink)) {
            # This path was created under the independently verified synthetic root above.
            if (-not ([IO.Path]::GetFullPath($exportTestLink)).StartsWith($exportTestRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing junction cleanup outside the fixture.' }
            [IO.Directory]::Delete($exportTestLink)
        }
        $null = Assert-HarborArtifactPath $exportTestRoot
        if (Test-Path -LiteralPath $exportTestRoot) { Assert-HarborArtifactTree $exportTestRoot; Remove-Item -LiteralPath $exportTestRoot -Recurse -Force }
    } finally { $exportTestLease.ReleaseMutex(); $exportTestLease.Dispose() }
}
