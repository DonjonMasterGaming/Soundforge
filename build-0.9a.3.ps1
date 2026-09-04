param(
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$buildsRoot = [IO.Path]::GetFullPath('F:\Folders\Useful Stuff\Visual Studio\Soundforge Builds')
$versionRoot = [IO.Path]::GetFullPath((Join-Path $buildsRoot '0.9a.3'))
if ($sourceRoot -ne 'F:\Folders\Useful Stuff\Visual Studio\Soundforge 0.9a.3') { throw "Run this script from the canonical 0.9a.3 workspace." }
if ($versionRoot -ne 'F:\Folders\Useful Stuff\Visual Studio\Soundforge Builds\0.9a.3') { throw "Unexpected output path." }
if (Test-Path -LiteralPath $versionRoot) { Remove-Item -LiteralPath $versionRoot -Recurse -Force }
$packageRoot = Join-Path $versionRoot 'Soundforge-0.9a.3-Portable-Update'
$payloadRoot = Join-Path $packageRoot 'payload'
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

dotnet publish (Join-Path $sourceRoot 'Soundforge\Soundforge.csproj') -c $Configuration -r win-x64 --self-contained true -o $payloadRoot
if ($LASTEXITCODE -ne 0) { throw 'Soundforge publish failed.' }
dotnet publish (Join-Path $sourceRoot 'Soundforge.Updater\Soundforge.Updater.csproj') -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $packageRoot
if ($LASTEXITCODE -ne 0) { throw 'Soundforge updater publish failed.' }
Copy-Item -LiteralPath (Join-Path $sourceRoot 'packaging\update.json') -Destination (Join-Path $packageRoot 'update.json')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'RELEASE-0.9a.3.md') -Destination (Join-Path $packageRoot 'README-0.9a.3.md')

$pluginContainer = Join-Path $packageRoot 'Optional Stream Deck Plugin'
$pluginBundle = Join-Path $pluginContainer 'com.soundforge.control.sdPlugin'
New-Item -ItemType Directory -Path $pluginContainer -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'StreamDeckPlugin\com.soundforge.control.sdPlugin') -Destination $pluginBundle -Recurse
dotnet publish (Join-Path $sourceRoot 'StreamDeckPlugin\Soundforge.StreamDeckPlugin.csproj') -c $Configuration -r win-x64 --self-contained true -o $pluginBundle
if ($LASTEXITCODE -ne 0) { throw 'Stream Deck plugin publish failed.' }
$pluginZip = Join-Path $pluginContainer 'Soundforge-StreamDeck-0.9a.3.zip'
Compress-Archive -Path $pluginBundle -DestinationPath $pluginZip -CompressionLevel Optimal
Remove-Item -LiteralPath $pluginBundle -Recurse -Force
Move-Item -LiteralPath $pluginZip -Destination (Join-Path $pluginContainer 'Soundforge-StreamDeck-0.9a.3.streamDeckPlugin')

$zipPath = Join-Path $versionRoot 'Soundforge-0.9a.3-Portable-Update.zip'
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath "$zipPath.sha256.txt" -Value "$hash  Soundforge-0.9a.3-Portable-Update.zip" -Encoding ascii
Write-Host "Created $zipPath"
Write-Host "SHA-256 $hash"
