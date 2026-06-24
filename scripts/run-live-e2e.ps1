<#
.SYNOPSIS
  Launch Unity in batchmode (headless) and run a real-venue login E2E runner in one command.

.DESCRIPTION
  Resolves the Unity editor path from UNITY_EDITOR_PATH (process env wins, then <repo>/.env,
  then <repo>/python/.env -- same order as EnvConfig.cs). Always writes the log to
  <repo>/Temp/Unity_E2E.log (the default AppData Editor.log is shared across projects and
  pollutes log parsing).

  This exercises the *env* login path (KabuLiveE2ERunner / TachibanaLiveE2ERunner). The #122
  prompt (tkinter dialog) path needs a display and does NOT run headless (owner HITL only).

  Prerequisites (HITL):
    - kabu     : kabuStation app running + logged in + API enabled (verify 18081); DEV_KABU_API_PASSWORD in .env
    - tachibana: DEV_TACHIBANA_AUTH_ID_DEMO / DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO / DEV_TACHIBANA_SECOND in .env
    - Unity Editor must NOT have this project open (one instance per project)

  Exit codes: 0=PASS (runner's EditorApplication.Exit(0)) / 1=FAIL or compile error /
              2=config error / 3=Editor already running

.EXAMPLE
  pwsh scripts/run-live-e2e.ps1 -Venue kabu
  pwsh scripts/run-live-e2e.ps1 -Venue tachibana
  pwsh scripts/run-live-e2e.ps1 -CompileOnly
  pwsh scripts/run-live-e2e.ps1 -Method VenueLoginSecretProbe.Run
#>
[CmdletBinding(DefaultParameterSetName = 'Compile')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Venue')]
    [ValidateSet('kabu', 'tachibana')]
    [string]$Venue,

    [Parameter(Mandatory, ParameterSetName = 'Compile')]
    [switch]$CompileOnly,

    [Parameter(Mandatory, ParameterSetName = 'CustomMethod')]
    [string]$Method,

    [string]$LogFile
)

$ErrorActionPreference = 'Stop'

# This script lives in <repo>/scripts/.
$RepoRoot = Split-Path -Parent $PSScriptRoot

# (#3) Map -Venue to the existing per-venue runner method. We do NOT invent a unified runner
# that parses GetCommandLineArgs(); the repo already ships KabuLiveE2ERunner / TachibanaLiveE2ERunner.
$pset = $PSCmdlet.ParameterSetName
switch ($pset) {
    'Venue'        { $Method = if ($Venue -eq 'kabu') { 'KabuLiveE2ERunner.Run' } else { 'TachibanaLiveE2ERunner.Run' } }
    'Compile'      { $Method = $null }   # compile gate: no -executeMethod
    'CustomMethod' { }                   # use -Method as given
}

# (#6) Resolve a key from process env, then <repo>/.env, then <repo>/python/.env.
function Resolve-EnvValue([string]$key) {
    $fromProc = [Environment]::GetEnvironmentVariable($key)
    if (-not [string]::IsNullOrEmpty($fromProc)) { return $fromProc }
    foreach ($candidate in @((Join-Path $RepoRoot '.env'), (Join-Path $RepoRoot 'python/.env'))) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $line = Get-Content -LiteralPath $candidate |
            Where-Object { $_ -match "^\s*$([regex]::Escape($key))\s*=" } |
            Select-Object -First 1
        if ($line -and ($line -match "^\s*$([regex]::Escape($key))\s*=\s*(.*)$")) {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $null
}

# (Gap 1) Shared per-scenario rollup (also used by run-all-tests.ps1).
. (Join-Path $PSScriptRoot 'E2ERollup.ps1')

# (#2) Zombie lockfile guard. Try to delete it: success means it was stale (continue);
# failure means a live Editor still holds it (abort).
$Lock = Join-Path $RepoRoot 'Temp/UnityLockfile'
if (Test-Path -LiteralPath $Lock) {
    try {
        Remove-Item -LiteralPath $Lock -Force -ErrorAction Stop
        Write-Warning 'Stale UnityLockfile removed (no live lock held). Continuing.'
    } catch {
        $procs = Get-Process -Name 'Unity' -ErrorAction SilentlyContinue
        Write-Error ("Unity Editor appears to hold this project's lock (cannot delete $Lock). " +
                     'Close the Editor before running batchmode. Running Unity PIDs: ' +
                     (($procs | ForEach-Object { $_.Id }) -join ', '))
        exit 3
    }
}

# (#1) Unity editor path.
$Unity = Resolve-EnvValue 'UNITY_EDITOR_PATH'
if ([string]::IsNullOrEmpty($Unity)) {
    Write-Error 'UNITY_EDITOR_PATH is not set (process env / <repo>/.env / <repo>/python/.env).'
    exit 2
}
if (-not (Test-Path -LiteralPath $Unity)) {
    Write-Error "UNITY_EDITOR_PATH does not point to an existing file: $Unity"
    exit 2
}

# (#1) Pin the log under the project and start fresh each run.
if ([string]::IsNullOrEmpty($LogFile)) {
    $LogFile = Join-Path $RepoRoot 'Temp/Unity_E2E.log'
}
$logDir = Split-Path -Parent $LogFile
if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
if (Test-Path -LiteralPath $LogFile) { Remove-Item -LiteralPath $LogFile -Force }

# Build args (quote paths that may contain spaces).
$argList = @(
    '-batchmode', '-nographics', '-quit',
    '-projectPath', "`"$RepoRoot`"",
    '-logFile', "`"$LogFile`""
)
if ($Method) { $argList += @('-executeMethod', $Method) }
$argString = $argList -join ' '

Write-Host "Unity   : $Unity"
Write-Host "Project : $RepoRoot"
if ($pset -eq 'Compile') { Write-Host 'Mode    : compile-only gate' } else { Write-Host "Method  : $Method" }
Write-Host "Log     : $LogFile"
Write-Host 'Launching Unity (streaming log; Ctrl-C to abort)...'

$proc = Start-Process -FilePath $Unity -ArgumentList $argString -PassThru -NoNewWindow

# (#4) Stream the log live and stop when the process exits (a single tail-at-end read
# looks like a hang to an agent during a long compile/test).
$retry = 0
while (-not (Test-Path -LiteralPath $LogFile) -and -not $proc.HasExited -and $retry -lt 40) {
    Start-Sleep -Milliseconds 250; $retry++
}
if (Test-Path -LiteralPath $LogFile) {
    $fs = [System.IO.File]::Open($LogFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $reader = New-Object System.IO.StreamReader($fs)
    try {
        while (-not $proc.HasExited) {
            $line = $reader.ReadLine()
            if ($null -ne $line) { Write-Host $line } else { Start-Sleep -Milliseconds 150 }
        }
        while ($null -ne ($line = $reader.ReadLine())) { Write-Host $line }   # drain remainder
    } finally {
        $reader.Dispose(); $fs.Dispose()
    }
}
$proc.WaitForExit()
$code = $proc.ExitCode

# Compile-only verdict comes from the log (Unity can exit 0 even with C# errors).
if ($pset -eq 'Compile') {
    $csErrors = @()
    if (Test-Path -LiteralPath $LogFile) {
        $csErrors = Select-String -LiteralPath $LogFile -Pattern 'error CS\d+' -ErrorAction SilentlyContinue
    }
    if ($csErrors.Count -gt 0) {
        Write-Host "[COMPILE FAIL] $($csErrors.Count) C# error(s):"
        $csErrors | ForEach-Object { $_.Line } | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    Write-Host "[COMPILE PASS] no 'error CS' in log; Unity exit=$code"
    exit $code
}

# (Gap 1) Per-scenario rollup from the log tags.
$fails = Write-E2ERollup @($LogFile)
Write-Host "Unity process exit = $code"

# Verdict: when E2E tags are present they are authoritative (E2E-INDEX records that some runners
# exit 139 on a shutdown segfault yet PASS by tag). With no tags, fall back to the process exit.
if ($fails -ge 0) {
    if ($fails -eq 0) { Write-Host '[E2E PASS] all tagged scenarios passed'; exit 0 }
    Write-Host "[E2E FAIL] $fails scenario(s) failed"; exit 1
}
exit $code
