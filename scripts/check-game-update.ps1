param(
 [string]$ValheimDir = 'D:/Steam/steamapps/common/Valheim',
 [string]$ServerDir = 'D:/Steam/steamapps/common/Valheim dedicated server',
 [switch]$SkipServer
)
# One-command check after a Valheim update: rebuild against the installed game, run the offline
# suites, then run the game-contract harness against each installation and summarize which probes
# and optimizations still find their native contracts. Read-only on the game; nothing is launched.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot
$failures = [System.Collections.Generic.List[string]]::new()
function Step([string]$name, [scriptblock]$block) {
 Write-Host "== $name"
 & $block
 if ($LASTEXITCODE -ne 0) { $failures.Add("$name (exit $LASTEXITCODE)") }
}
$managed = if (Test-Path -LiteralPath (Join-Path $ValheimDir 'valheim_Data/Managed')) { Join-Path $ValheimDir 'valheim_Data/Managed' } else { Join-Path $ValheimDir 'valheim_server_Data/Managed' }
$assembly = Join-Path $managed 'assembly_valheim.dll'
if (!(Test-Path -LiteralPath $assembly)) { throw "assembly_valheim.dll not found under $managed" }
Write-Host ("Installed assembly_valheim.dll SHA256: " + (Get-FileHash -LiteralPath $assembly).Hash)
Write-Host ("Installed UnityPlayer: " + (Get-Item -LiteralPath (Join-Path $ValheimDir 'UnityPlayer.dll')).VersionInfo.FileVersion)

Step 'Rebuild plugin against the installed game' { dotnet build src/BetterPerformance/BetterPerformance.csproj -c Release "-p:ValheimDir=$ValheimDir" -t:Rebuild -warnaserror }
Step 'Offline core suite' { dotnet run --project tests/BetterPerformance.Tests -c Release }
Step 'Python report suite' { python -m unittest discover -s tests -p 'test_*.py' }
$dll = Join-Path $repoRoot 'src/BetterPerformance/bin/Release/net472/BetterPerformance.dll'
$targets = @(@{Name='client'; Dir=$ValheimDir})
if (!$SkipServer) { $targets += @{Name='dedicated server'; Dir=$ServerDir} }
foreach ($target in $targets) {
 $log = Join-Path $repoRoot ('.qa/game-update-check-' + ($target.Name -replace ' ','-') + '.log')
 New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot '.qa') | Out-Null
 Step ("Game-contract harness, " + $target.Name) {
  dotnet run --project tests/BetterPerformance.GameTests -c Release "-p:ValheimDir=$ValheimDir" -- $target.Dir $dll 2>&1 | Tee-Object -FilePath $log | Out-Null
 }
 Write-Host ("Harness log: " + $log)
 $lines = Get-Content -LiteralPath $log
 $unavailable = $lines | Where-Object { $_ -match 'unavailable|unsupported_layout|type_unavailable|patch_failed|rejected' -and $_ -notmatch 'STATIC ONLY|offline|standalone' }
 if ($unavailable) { Write-Host "  Contract warnings (verify each against docs/game-update-guide.md):"; $unavailable | ForEach-Object { Write-Host ('   ' + $_) } }
 $lines | Where-Object { $_ -match 'checks|^PASS|^FAIL|^Installed game version' } | ForEach-Object { Write-Host ('   ' + $_) }
}
Write-Host ''
if ($failures.Count) { Write-Host ('FAILED: ' + ($failures -join '; ')); exit 1 }
Write-Host 'All gates passed. Next: run one isolated disposable-world session (docs/game-update-guide.md, step 4) before deploying.'
exit 0
