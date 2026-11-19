# Dot-source this script to configure a repository-local SDK environment.
$ErrorActionPreference = 'Stop'
$HarborRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$env:DOTNET_CLI_HOME = Join-Path $HarborRoot 'artifacts/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $HarborRoot 'artifacts/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$HarborDotNet = (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $HarborDotNet) {
    $HarborDotNet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
}
if (-not (Test-Path -LiteralPath $HarborDotNet -PathType Leaf)) {
    throw 'Install .NET 10 SDK or add its dotnet executable to PATH.'
}
