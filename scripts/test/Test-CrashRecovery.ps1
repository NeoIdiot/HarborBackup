#Requires -Version 5.1
param([ValidateSet('Debug','Release')][string]$Configuration='Release', [string]$ResultsDirectory)
. (Join-Path $PSScriptRoot '../build/Use-DotNet.ps1')
. (Join-Path $PSScriptRoot 'TestEnvironment.ps1')
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
Assert-HarborTestEnvironment
$crashArtifactLease = Enter-HarborArtifactOperation
try {
$probeProject = Join-Path $PSScriptRoot 'CrashRecovery/Probe.csproj'
# The helper adds no packages; reuse the repository's pinned, cached dependencies offline.
# Online vulnerability auditing belongs to dependency/release validation, not this synthetic fault test.
& $HarborDotNet restore $probeProject --configfile (Join-Path $HarborRoot 'NuGet.Config') --locked-mode --ignore-failed-sources -p:NuGetAudit=false --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Crash probe restore failed.' }
& $HarborDotNet build $probeProject -c $Configuration --no-restore --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Crash probe build failed.' }
$probeDll = Join-Path $PSScriptRoot "CrashRecovery/bin/$Configuration/net10.0-windows/Probe.dll"
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $HarborRoot ('artifacts/diagnostics/crash-recovery-' + [guid]::NewGuid().ToString('N')) }
$probeResults = [IO.Path]::GetFullPath($ResultsDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $HarborRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $probeResults.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Crash verification must remain under repository artifacts.' }
New-Item -ItemType Directory -Path $probeResults -Force | Out-Null
$crashResults = @()
foreach ($phase in @('Prepared','Archived','Uploaded','Metadata')) {
    $caseRoot = Join-Path $probeResults ('crash-' + $phase + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $caseRoot 'synthetic-test.marker') -Value 'synthetic-only'
    & $HarborDotNet $probeDll $caseRoot seed $phase
    if ($LASTEXITCODE -ne 0) { throw "Crash seed failed: $phase" }
    $probeStart = New-Object Diagnostics.ProcessStartInfo
    $probeStart.FileName = $HarborDotNet
    $probeStart.Arguments = '"' + $probeDll + '" "' + $caseRoot + '" interrupt ' + $phase
    $probeStart.UseShellExecute = $false
    $probeStart.CreateNoWindow = $true
    $probeStart.RedirectStandardOutput = $true
    $probeStart.RedirectStandardError = $true
    $probeProcess = New-Object Diagnostics.Process
    $probeProcess.StartInfo = $probeStart
    [void]$probeProcess.Start()
    try {
        $probeTimeout = [Diagnostics.Stopwatch]::StartNew()
        $signal = Join-Path $caseRoot 'interrupt-ready.txt'
        while (-not (Test-Path -LiteralPath $signal)) {
            if ($probeProcess.HasExited) { throw "Crash boundary not reached: $phase $($probeProcess.StandardError.ReadToEnd())" }
            if ($probeTimeout.Elapsed.TotalSeconds -ge 30) { throw "Crash probe timed out: $phase" }
            Start-Sleep -Milliseconds 50
        }
        # This is the PID we just created for the marked synthetic workspace, never an installed app.
        $probeProcess.Kill()
        if (-not $probeProcess.WaitForExit(10000)) { throw 'Synthetic process did not terminate.' }
    } finally {
        if (-not $probeProcess.HasExited) { $probeProcess.Kill(); [void]$probeProcess.WaitForExit(10000) }
        $probeProcess.Dispose()
    }
    & $HarborDotNet $probeDll $caseRoot recover $phase
    if ($LASTEXITCODE -ne 0) { throw "Crash recovery failed: $phase" }
    $crashResults += Get-Content -LiteralPath (Join-Path $caseRoot 'verified.json') -Raw | ConvertFrom-Json
    Write-Output "Crash recovery passed: $phase"
}
$crashResults | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $probeResults 'crash-recovery.json') -Encoding UTF8
} finally { $crashArtifactLease.ReleaseMutex(); $crashArtifactLease.Dispose() }
