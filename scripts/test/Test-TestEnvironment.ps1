#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestEnvironment.ps1')
$prefix = 'Local\HarborBackup.EnvironmentTest.' + [guid]::NewGuid().ToString('N')
$runName = $prefix + '.Run'; $markerName = $prefix + '.Running'
function Assert-Rejected([scriptblock]$Action, [string]$Expected) {
    try { & $Action } catch { if ($_.Exception.Message -notlike ('*' + $Expected + '*')) { throw }; return }
    throw "Expected rejection: $Expected"
}
Assert-HarborTestEnvironment -DesktopProbe { @() } -RunMutexName $runName -RunningMarkerName $markerName
Assert-Rejected { Assert-HarborTestEnvironment -DesktopProbe { @([pscustomobject]@{ Id = 123 }) } -RunMutexName $runName -RunningMarkerName $markerName } 'exit HarborBackup'
$marker = [Threading.Mutex]::new($false, $markerName)
try { Assert-Rejected { Assert-HarborTestEnvironment -DesktopProbe { @() } -RunMutexName $runName -RunningMarkerName $markerName } 'running marker' }
finally { $marker.Dispose() }
$readyName = $prefix + '.Ready'; $releaseName = $prefix + '.Release'
$ready = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $readyName)
$release = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $releaseName)
$job = Start-Job -ArgumentList $runName,$readyName,$releaseName -ScriptBlock {
    param($LockName,$ReadyName,$ReleaseName)
    $mutex = [Threading.Mutex]::new($false,$LockName)
    $signal = [Threading.EventWaitHandle]::OpenExisting($ReadyName)
    $finish = [Threading.EventWaitHandle]::OpenExisting($ReleaseName)
    $held = $false
    try { $held = $mutex.WaitOne(0); if (-not $held) { throw 'Synthetic mutex collision.' }; $null = $signal.Set(); $null = $finish.WaitOne(20000) }
    finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose(); $signal.Dispose(); $finish.Dispose() }
}
try {
    if (-not $ready.WaitOne(20000)) { throw 'Synthetic lock holder did not start.' }
    Assert-Rejected { Assert-HarborTestEnvironment -DesktopProbe { @() } -RunMutexName $runName -RunningMarkerName $markerName } 'run lock is held'
} finally {
    $null = $release.Set()
    $null = Wait-Job -Job $job -Timeout 10
    Receive-Job -Job $job -ErrorAction Stop | Out-Null
    Remove-Job -Job $job -Force
    $ready.Dispose(); $release.Dispose()
}
Assert-HarborTestEnvironment -DesktopProbe { @() } -RunMutexName $runName -RunningMarkerName $markerName
Write-Output 'PASS: test preflight rejects desktop, running marker and active mutex without stopping any application.'
