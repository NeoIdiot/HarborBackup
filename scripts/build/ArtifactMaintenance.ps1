#Requires -Version 5.1
# Shared by repository workflows; never operates on installed application data.
$script:HarborArtifactsBase = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../artifacts'))

function Assert-HarborArtifactPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -ne $script:HarborArtifactsBase -and -not $full.StartsWith($script:HarborArtifactsBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact path escapes repository artifacts: $full"
    }
    # Inspect ancestors as well as the target: a lexically safe path may traverse a junction.
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Artifact path contains a link: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}

function Enter-HarborArtifactOperation {
    $null = Assert-HarborArtifactPath $script:HarborArtifactsBase
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $id = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:HarborArtifactsBase.ToUpperInvariant())))).Replace('-', '') }
    finally { $sha.Dispose() }
    $mutex = [Threading.Mutex]::new($false, ('Local\HarborBackup.Artifacts.' + $id))
    try {
        try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'Another repository build, test or artifact cleanup is running.' }
        return $mutex
    } catch { $mutex.Dispose(); throw }
}

function Assert-HarborArtifactTree([string]$Path) {
    $null = Assert-HarborArtifactPath $Path
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Path)
    while ($queue.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $queue.Dequeue() -Force -ErrorAction Stop) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Artifact tree contains a link: $($item.FullName)" }
            if ($item.PSIsContainer) { $queue.Enqueue($item.FullName) }
        }
    }
}

function Remove-HarborArtifactHistory {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$ArtifactsRoot = $script:HarborArtifactsBase, [ValidateRange(1, 100)][int]$KeepCount = 2,
        [string[]]$PreservePaths = @())
    if (-not $PSBoundParameters.ContainsKey('KeepCount')) {
        $policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'artifact-retention.json') -Raw -ErrorAction Stop | ConvertFrom-Json
        if ($policy.keepCount -isnot [int] -and $policy.keepCount -isnot [long] -or $policy.keepCount -lt 1 -or $policy.keepCount -gt 100) { throw 'Invalid artifact retention keepCount (expected 1..100).' }
        $KeepCount = $policy.keepCount
    }
    $root = Assert-HarborArtifactPath $ArtifactsRoot
    $preserved = @($PreservePaths | ForEach-Object { Assert-HarborArtifactPath $_ })
    $lease = Enter-HarborArtifactOperation
    try {
        $activePaths = @(Get-Process | ForEach-Object { try { if ($_.Path) { $_.Path } } catch { } })
        $groups = @(
            @{ Name = 'publish'; Pattern = '^HarborBackup-\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?-\d{8}-\d{6}$'; Marker = 'release-manifest.json' },
            @{ Name = 'install-validation'; Pattern = '^[a-f0-9]{32}$'; Marker = 'validation.json' },
            @{ Name = 'test-results'; Pattern = '.+'; Marker = 'completed.json' }
        )
        foreach ($group in $groups) {
            $area = Assert-HarborArtifactPath (Join-Path $root $group.Name)
            if (-not (Test-Path -LiteralPath $area)) { continue }
            $runs = @(Get-ChildItem -LiteralPath $area -Directory -Force -ErrorAction Stop |
                Where-Object { $_.Name -match $group.Pattern } | Sort-Object LastWriteTimeUtc, Name -Descending)
            $keep = @($runs | Select-Object -First $KeepCount | ForEach-Object { $_.FullName })
            # Keep the latest successful result even when newer attempts failed.
            $successful = $runs | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $group.Marker) } | Select-Object -First 1
            if ($successful) { $keep += $successful.FullName }
            foreach ($run in $runs) {
                if ($run.FullName -in $keep -or $run.FullName -in $preserved) { continue }
                try {
                    if ($activePaths | Where-Object { $_.StartsWith($run.FullName + '\', [StringComparison]::OrdinalIgnoreCase) }) {
                        Write-Warning "Artifact cleanup skipped a running application: $($run.FullName)"
                        continue
                    }
                    Assert-HarborArtifactTree $run.FullName
                    if ($PSCmdlet.ShouldProcess($run.FullName, 'Remove old repository artifact run')) {
                        Remove-Item -LiteralPath $run.FullName -Recurse -Force -ErrorAction Stop
                        Write-Output "Removed: $($run.FullName)"
                    }
                } catch { Write-Warning "Artifact cleanup skipped $($run.FullName): $($_.Exception.Message)" }
            }
        }
    } finally { $lease.ReleaseMutex(); $lease.Dispose() }
}

function Invoke-HarborArtifactCleanup {
    param([string[]]$PreservePaths = @())
    # An optional cleanup failure must not invalidate a successful build/test.
    try { Remove-HarborArtifactHistory -PreservePaths $PreservePaths }
    catch { Write-Warning "Artifact cleanup skipped: $($_.Exception.Message)" }
}
