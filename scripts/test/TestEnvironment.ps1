#Requires -Version 5.1
function Assert-HarborTestEnvironment {
    param(
        [scriptblock]$DesktopProbe = { @(Get-Process -Name 'HarborBackup.Desktop' -ErrorAction SilentlyContinue) },
        [string]$RunMutexName = 'Local\HarborBackup.Run',
        [string]$RunningMarkerName = ('Global\HarborBackup.' + [Environment]::UserName.ToUpperInvariant() + '.Running')
    )
    if (@(& $DesktopProbe).Count -gt 0) {
        throw 'Test precondition failed: exit HarborBackup from the tray first. Tests share production mutexes; -IsolatedProfile isolates directories, not locks. The application has not been stopped.'
    }
    $marker = $null
    if ([Threading.Mutex]::TryOpenExisting($RunningMarkerName, [ref]$marker)) {
        $marker.Dispose()
        throw 'Test precondition failed: a HarborBackup running marker exists. Exit the application before testing.'
    }
    $mutex = $null
    if ([Threading.Mutex]::TryOpenExisting($RunMutexName, [ref]$mutex)) {
        $held = $false
        try {
            try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
            if (-not $held) { throw 'Test precondition failed: the HarborBackup run lock is held (possibly by CLI). Wait for the operation to finish; do not start the application during testing.' }
        } finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
    }
}
