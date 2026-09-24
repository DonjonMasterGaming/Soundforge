param(
    [string]$Dotnet = "dotnet",
    [string]$OutputRoot = (Join-Path $PSScriptRoot '.validation'),
    [switch]$AllowCDriveOutput
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputRoot)
if ([IO.Path]::GetPathRoot($output) -eq 'C:\' -and !$AllowCDriveOutput) {
    throw 'C: output requires explicit -AllowCDriveOutput (approved for Phase 1 on this machine).'
}
if ($output -eq [IO.Path]::GetPathRoot($output) -or $output -eq [IO.Path]::GetFullPath($PSScriptRoot)) {
    throw 'Choose a dedicated validation output directory.'
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$run = Join-Path $output ('run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run | Out-Null
$artifacts = Join-Path $run 'artifacts'
$env:DOTNET_CLI_HOME = Join-Path $output 'cli-home'
$env:NUGET_PACKAGES = Join-Path $output 'nuget'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Invoke-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Invoke-Dotnet @('build', (Join-Path $PSScriptRoot 'Soundforge.sln'), '-c', 'Release', '--artifacts-path', $artifacts)
$tests = Join-Path $PSScriptRoot 'tests\Soundforge.ProjectStoreRegression\Soundforge.ProjectStoreRegression.csproj'
Invoke-Dotnet @('build', $tests, '-c', 'Release', '--artifacts-path', $artifacts)
$harness = Join-Path $artifacts 'bin\Soundforge.ProjectStoreRegression\release\Soundforge.ProjectStoreRegression.dll'
Invoke-Dotnet @($harness)
$publish = Join-Path $run 'Soundforge-0.9a.4-win-x64'
Invoke-Dotnet @('publish', (Join-Path $PSScriptRoot 'Soundforge\Soundforge.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--artifacts-path', $artifacts, '-o', $publish)
$hashes = Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
    [PSCustomObject]@{ File = [IO.Path]::GetRelativePath($publish, $_.FullName); SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'published-sha256.json') -Encoding utf8
Write-Host "Validation passed. Self-contained application: $publish"
Write-Host 'Copy the whole published directory for testing. OAuth client configuration and credentials are not included.'
