#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ApplicationDirectory, [ValidateRange(30,1800)][int]$SampleSeconds = 120, [ValidateRange(5,120)][int]$WarmupSeconds = 20)
$ErrorActionPreference = 'Stop'
$Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
if (Get-Process -Name HarborBackup.Desktop -ErrorAction SilentlyContinue) { throw 'Exit the existing tray before an isolated measurement. No process was stopped.' }
$Fixture = Join-Path $Repo ('artifacts/measurements/' + [guid]::NewGuid().ToString('N'))
$Cloud = Join-Path $Fixture 'cloud'
New-Item -ItemType Directory -Path $Cloud -Force | Out-Null
$Project = Join-Path $Fixture 'data/config/projects.json'
$System = Join-Path $Fixture 'data/config/backup-settings.json'
$Preferences = Join-Path $Fixture 'data/config/desktop-settings.json'
[IO.Directory]::CreateDirectory((Split-Path $Project)) | Out-Null
[IO.File]::WriteAllText($Project, '{"schemaVersion":1,"folders":[]}')
[IO.File]::WriteAllText($System, '{"schemaVersion":1,"sync":{"enabled":true},"schedule":{"intervalMinutes":30,"startDelayMinutes":2}}')
$Exe = Join-Path $ApplicationDirectory 'app/HarborBackup.Desktop.exe'
$Arguments = '--background --config "' + $Project + '" --system-config "' + $System + '" --preferences "' + $Preferences + '"'
$Process = Start-Process -FilePath $Exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
$Samples = @()
try {
    Start-Sleep -Seconds $WarmupSeconds
    $Process.Refresh()
    if ($Process.HasExited) { throw "Tray exited before measurement: $($Process.ExitCode)" }
    $Runtime = @($Process.Modules | Where-Object ModuleName -eq 'coreclr.dll' | ForEach-Object FileName)
    if ($Runtime.Count -ne 1 -or -not $Runtime[0].StartsWith($ApplicationDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The tray did not load its private runtime.' }
    $CpuStart = $Process.TotalProcessorTime.TotalSeconds
    $Watch = [Diagnostics.Stopwatch]::StartNew()
    while ($Watch.Elapsed.TotalSeconds -lt $SampleSeconds) {
        Start-Sleep -Seconds 5
        $Process.Refresh()
        if ($Process.HasExited) { throw 'Tray unexpectedly exited during measurement.' }
        $Samples += [pscustomobject][ordered]@{ seconds = [Math]::Round($Watch.Elapsed.TotalSeconds, 3); privateBytes = $Process.PrivateMemorySize64; workingSetBytes = $Process.WorkingSet64; cpuSeconds = $Process.TotalProcessorTime.TotalSeconds }
    }
    $CpuSeconds = $Process.TotalProcessorTime.TotalSeconds - $CpuStart
    $Report = [ordered]@{
        schemaVersion = 1; scenario = 'Hidden tray awaiting authorization, empty project list, no backup, no forced GC'; timeUtc = [DateTime]::UtcNow.ToString('o')
        os = [Environment]::OSVersion.VersionString; logicalProcessors = [Environment]::ProcessorCount; runtimePath = $Runtime[0]
        warmupSeconds = $WarmupSeconds; sampleSeconds = $Watch.Elapsed.TotalSeconds; sampleCount = $Samples.Count
        privateBytesMean = ($Samples | Measure-Object privateBytes -Average).Average; privateBytesMax = ($Samples | Measure-Object privateBytes -Maximum).Maximum
        workingSetBytesMean = ($Samples | Measure-Object workingSetBytes -Average).Average; workingSetBytesMax = ($Samples | Measure-Object workingSetBytes -Maximum).Maximum
        cpuPercentOneCore = 100 * $CpuSeconds / $Watch.Elapsed.TotalSeconds
        cpuPercentMachine = 100 * $CpuSeconds / $Watch.Elapsed.TotalSeconds / [Environment]::ProcessorCount
        installedLogicalBytes = (Get-ChildItem -LiteralPath $ApplicationDirectory -File -Recurse | Measure-Object Length -Sum).Sum
        samples = $Samples
    }
    $Report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Fixture 'measurement.json') -Encoding UTF8
    Write-Output "Measurement: $(Join-Path $Fixture 'measurement.json')"
    $Report.Remove('samples')
    Write-Output ($Report | ConvertTo-Json)
}
catch {
    $Process.Refresh()
    [ordered]@{ schemaVersion = 1; status = 'FailedBeforeCompletion'; timeUtc = [DateTime]::UtcNow.ToString('o'); error = $_.Exception.Message; exitCode = $(if ($Process.HasExited) { $Process.ExitCode } else { $null }); privateBytesMean = $null; workingSetBytesMean = $null; samples = $Samples } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Fixture 'measurement.json') -Encoding UTF8
    throw
}
finally {
    $Process.Refresh()
    if (-not $Process.HasExited) {
        $ExitRequest = Start-Process -FilePath $Exe -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru -Wait
        if (-not $Process.WaitForExit(30000)) { Write-Warning 'Safe shutdown is still pending; no process was killed.' }
    }
    $Process.Dispose()
}
