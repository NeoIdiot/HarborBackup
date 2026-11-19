param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Label='manual', [switch]$CacheOnly, [switch]$TransportCompare)
. (Join-Path $PSScriptRoot '../build/Use-DotNet.ps1')
. (Join-Path $PSScriptRoot '../build/ArtifactMaintenance.ps1')
. (Join-Path $PSScriptRoot 'TestEnvironment.ps1')
. (Join-Path $PSScriptRoot '../build/SourceIdentity.ps1')
Assert-HarborTestEnvironment
$flowLease=Enter-HarborArtifactOperation
Push-Location $HarborRoot
try {
    $flowOutput=Join-Path $HarborRoot ('artifacts/diagnostics/flow-'+$Label+'-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $flowOutput | Out-Null
    Get-HarborSourceIdentity $HarborRoot -AllowDirty | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $flowOutput 'source.json') -Encoding UTF8
    & $HarborDotNet restore scripts/test/OneDriveFlow/Probe.csproj --ignore-failed-sources -p:NuGetAudit=false
    if($LASTEXITCODE -ne 0){throw 'Probe restore failed.'}
    & $HarborDotNet build scripts/test/OneDriveFlow/Probe.csproj -c Release --no-restore --verbosity minimal
    if($LASTEXITCODE -ne 0){throw 'Probe build failed.'}
    Copy-Item -LiteralPath scripts/test/OneDriveFlow/bin/Release/net10.0-windows -Destination (Join-Path $flowOutput 'probe-bin') -Recurse
    $flowHashes=@(Get-ChildItem src -Recurse -File -Filter *.cs | Where-Object {$_.FullName -notmatch '\\(bin|obj)\\'} | ForEach-Object { [pscustomobject]@{path=$_.FullName.Substring($HarborRoot.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
    $flowHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $flowOutput 'source-hashes.json') -Encoding UTF8
    $flowArguments=@($flowOutput)
    if($CacheOnly){$flowArguments+='--cache-only'}
    if($TransportCompare){$flowArguments+='--transport-compare'}
    & $HarborDotNet (Join-Path $flowOutput 'probe-bin/Probe.dll') @flowArguments
    if($LASTEXITCODE -ne 0){throw "OneDrive flow failed; evidence: $flowOutput"}
    Write-Output "Completed: $flowOutput"
} finally {Pop-Location;$flowLease.ReleaseMutex();$flowLease.Dispose()}
