<#
.SYNOPSIS
  Run pytest and (optionally) a Unity E2E runner, then print ONE merged E2E Action-ID rollup.

.DESCRIPTION
  Bridges the Python and Unity halves of the Action-ID ledger into a single verdict for an
  agent's autonomous loop. pytest emits "[E2E <id> PASS/FAIL/SKIP]" via the scenario conftest
  hook; the Unity runner emits the same tags. Both logs feed the shared Write-E2ERollup.

  Verdict (exit 1 if any holds):
    - pytest exit code != 0  (floor: catches collection errors and untagged-test failures)
    - any FAIL in the merged rollup
    - the Unity sub-run reported failure

.PARAMETER PytestArgs
  Extra args passed to pytest (default: full suite). e.g. -PytestArgs 'tests/test_subscribe_market_data_batch.py'

.PARAMETER Venue
  Also run a Unity live-login runner (kabu|tachibana) via run-live-e2e.ps1. HITL prerequisites apply.

.PARAMETER Method
  Also run a specific Unity -executeMethod runner via run-live-e2e.ps1.

.PARAMETER IncludeFraming
  Also run FramingProbe.Run as an independent Unity leg (logs to Temp/Unity_E2E_Framing.log). Default OFF
  so the heavy Unity legs stay opt-in. Composes with -Venue / -Method (each leg runs in series and its
  Action-ID tags merge into the single rollup).

.EXAMPLE
  pwsh scripts/run-all-tests.ps1
  pwsh scripts/run-all-tests.ps1 -PytestArgs 'tests/test_subscribe_market_data_batch.py'
  pwsh scripts/run-all-tests.ps1 -Venue kabu
  pwsh scripts/run-all-tests.ps1 -IncludeFraming
#>
[CmdletBinding()]
param(
    [string]$PytestArgs,
    [ValidateSet('kabu', 'tachibana')]
    [string]$Venue,
    [string]$Method,
    [switch]$IncludeFraming
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'E2ERollup.ps1')

$tempDir = Join-Path $RepoRoot 'Temp'
if (-not (Test-Path -LiteralPath $tempDir)) { New-Item -ItemType Directory -Path $tempDir -Force | Out-Null }
$pytestLog        = Join-Path $tempDir 'pytest_e2e.log'
$unityLog         = Join-Path $tempDir 'Unity_E2E.log'
$unityFramingLog  = Join-Path $tempDir 'Unity_E2E_Framing.log'

# 1) pytest (streamed via Tee; $LASTEXITCODE keeps uv/pytest's code -- Tee-Object is a cmdlet
#    and does not overwrite it).
Write-Host '=== pytest ==='
# NB: assign in two steps. An `if`-expression assignment collapses a single-element
# array back to a scalar, which would then splat char-by-char.
$pyArgList = @()
if (-not [string]::IsNullOrEmpty($PytestArgs)) { $pyArgList = @($PytestArgs -split '\s+') }
Push-Location (Join-Path $RepoRoot 'python')
try {
    uv run pytest @pyArgList 2>&1 | Tee-Object -FilePath $pytestLog
    $pytestExit = $LASTEXITCODE
} finally {
    Pop-Location
}
Write-Host "pytest exit = $pytestExit"

# 2) Unity runner (optional). run-live-e2e.ps1 prints its own single-log rollup; the merged
#    rollup below is authoritative.
$logs = @($pytestLog)
$unityFail = $false
if ($Venue -or $Method) {
    Write-Host ''
    Write-Host '=== Unity E2E ==='
    $launcher = Join-Path $PSScriptRoot 'run-live-e2e.ps1'
    # run-live-e2e.ps1 reports config errors via Write-Error + exit 2/3. Under the inherited
    # $ErrorActionPreference='Stop' that Write-Error would terminate this script before the
    # merged rollup prints, so catch it and fold the failure into the verdict instead.
    try {
        if ($Venue) { & $launcher -Venue $Venue -LogFile $unityLog }
        else        { & $launcher -Method $Method -LogFile $unityLog }
        if ($LASTEXITCODE -ne 0) { $unityFail = $true }
    } catch {
        Write-Warning "Unity E2E leg did not run: $_"
        $unityFail = $true
    }
    if (Test-Path -LiteralPath $unityLog) { $logs += $unityLog }
}

# 2b) Framing probe (optional, opt-in via -IncludeFraming). Runs in series after the Venue/Method leg so
# Unity project lock is released. Separate log keeps the Action-ID tags grep-able per leg.
if ($IncludeFraming) {
    Write-Host ''
    Write-Host '=== Unity E2E (Framing) ==='
    $launcher = Join-Path $PSScriptRoot 'run-live-e2e.ps1'
    try {
        & $launcher -Method 'FramingProbe.Run' -LogFile $unityFramingLog
        if ($LASTEXITCODE -ne 0) { $unityFail = $true }
    } catch {
        Write-Warning "Framing leg did not run: $_"
        $unityFail = $true
    }
    if (Test-Path -LiteralPath $unityFramingLog) { $logs += $unityFramingLog }
}

# 3) Merged rollup across both logs.
Write-Host ''
Write-Host '=== MERGED ROLLUP (pytest + Unity) ==='
$fails = Write-E2ERollup $logs

# Verdict.
$overallFail = ($pytestExit -ne 0) -or ($fails -gt 0) -or $unityFail
if ($overallFail) {
    Write-Host "[ALL TESTS FAIL] pytestExit=$pytestExit rollupFails=$fails unityFail=$unityFail"
    exit 1
}
Write-Host '[ALL TESTS PASS]'
exit 0
