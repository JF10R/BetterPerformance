[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ValheimDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$resolvedGame = (Resolve-Path -LiteralPath $ValheimDir).Path
$projectPath = Join-Path $repoRoot 'src/BetterPerformance/BetterPerformance.csproj'

& dotnet build $projectPath -c Release "-p:ValheimDir=$resolvedGame"
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed.' }

[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$artifactRoot = Join-Path $repoRoot 'artifacts'
$stagingPath = Join-Path $artifactRoot ('package-' + [Guid]::NewGuid().ToString('N'))
$pluginDirectory = Join-Path $stagingPath 'BepInEx/plugins/BetterPerformance'
New-Item -Path $pluginDirectory -ItemType Directory -Force | Out-Null

# Whitelist our output. Never copy an entire build directory or game references.
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/BetterPerformance/bin/Release/net472/BetterPerformance.dll') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $stagingPath
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stagingPath
Copy-Item -LiteralPath (Join-Path $repoRoot 'AGENTS.md') -Destination $stagingPath
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $stagingPath -Recurse
$scriptDirectory = Join-Path $stagingPath 'scripts'
New-Item -Path $scriptDirectory -ItemType Directory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/summarize_capture.py') -Destination $scriptDirectory
$zipPath = Join-Path $artifactRoot ("BetterPerformance-$version.zip")
Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $zipPath -Force
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
Set-Content -LiteralPath ($zipPath + '.sha256') -Value ($hash + '  ' + (Split-Path $zipPath -Leaf)) -Encoding ASCII
Write-Output "Package created: $zipPath"
Write-Output 'No deployment or game/server launch was performed.'
