<#
.SYNOPSIS
  Build the shippable Windows64 standalone locally and (optionally) publish it to a
  GitHub Release -- the local-machine replacement for the deploy-gh-release.yml
  GitHub Actions workflow.

.DESCRIPTION
  Rationale: ADR-0014 already forces the CI build onto the owner's own machine
  (Unity 6 Personal license cannot activate on hosted runners). A GitHub Actions
  self-hosted runner is therefore just "run this on the owner's machine, with extra
  ceremony (runner registration, queueing, GITHUB_TOKEN seepage)". This script drops
  the ceremony and runs the same proven steps directly.

  Steps (mirror of the workflow, minus Sigstore attestation which is GitHub-Actions
  only -- the real reproducibility defense is the ADR-0014 HITL clean-machine verify):
    1. Resolve Unity + verify local install (no GameCI, no license dance)
    2. uv sync --frozen (materialize python/.venv)
    3. Unity build (BackcastShippableBuild.BuildWindows64)
    4. Verify build output (exe + runtime-manifest.json)
    5. Library size guard (8GB raw cap)
    6. Assemble dist/ + single-top-folder zip (tar.exe for long paths)
    7. Python SBOM (CycloneDX)
    8. Smoke stages 1-4 (zip integrity / manifest schema / venv import / Player GUI)
    9. SHA256SUMS
   10. Release: create/update a DRAFT GitHub Release and upload assets (gh)

  Unity path resolution: UNITY_EDITOR_PATH (process env, then <repo>/.env, then
  <repo>/python/.env -- same order as EnvConfig.cs / run-live-e2e.ps1), falling back
  to the Unity Hub path derived from ProjectSettings/ProjectVersion.txt.

  The GitHub Release is created as a DRAFT by default (the ADR-0014 HITL gate: owner
  extracts on a clean machine, runs the Replay AC, then clicks Publish).

.PARAMETER Version
  Artifact version string. Default: the exact git tag if HEAD is tagged, else
  "local-<shortsha>".

.PARAMETER GhAccount
  gh account whose token is used for Release operations (set GH_TOKEN for the gh
  subprocess only; does NOT change the global active account). Use the repo owner
  account that has write access, e.g. botterYosuke. Empty = use current gh auth.

.PARAMETER SkipRelease
  Build + smoke only; do not touch GitHub Releases (bit-rot / local verification).

.PARAMETER PublishNow
  Create the Release as published (non-draft) instead of draft. Skips the HITL gate.

.PARAMETER SkipSmoke
  Skip the 4 smoke stages (faster iteration; not recommended before a real release).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/build-and-release.ps1 -SkipRelease
  powershell -ExecutionPolicy Bypass -File scripts/build-and-release.ps1 -GhAccount botterYosuke
  powershell -ExecutionPolicy Bypass -File scripts/build-and-release.ps1 -Version v0.3.0 -GhAccount botterYosuke

.NOTES
  Exit codes: 0 = OK / 1 = build or smoke failure / 2 = config error / 3 = Editor running
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$GhAccount,
    [int]$BuildTimeoutMin = 40,
    [switch]$SkipRelease,
    [switch]$PublishNow,
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'

# This script lives in <repo>/scripts/.
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $RepoRoot

# Live progress: PS Write-Host buffers when stdout is redirected to a file (looks dead
# during the long build/zip). Mirror each phase line to a progress log via Add-Content
# (opens/writes/closes = immediate flush) so `Get-Content -Wait` shows live progress.
# Keep it at repo root (NOT under Temp/, which Unity batchmode wipes on launch).
$ProgressLog = Join-Path $RepoRoot 'build-and-release.progress.log'
Set-Content -LiteralPath $ProgressLog -Value '' -Encoding utf8
function Step([string]$msg) {
    $line = ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $msg)
    Write-Host $line
    try { Add-Content -LiteralPath $ProgressLog -Value $line -Encoding utf8 } catch {}
}

function Fail([string]$msg, [int]$code = 1) {
    Write-Error $msg
    exit $code
}

# Run a scriptblock with EAP=Continue so native-command stderr / stream redirects
# (2>$null on git/gh) do not raise a terminating NativeCommandError under PS 5.1's
# $ErrorActionPreference='Stop'. Returns the block's output; resets $LASTEXITCODE.
function Invoke-Quiet([scriptblock]$Block) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $prev }
}

# Resolve a key from process env, then <repo>/.env, then <repo>/python/.env.
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

# ---- Editor lock guard (one Unity instance per project) --------------------
$Lock = Join-Path $RepoRoot 'Temp/UnityLockfile'
if (Test-Path -LiteralPath $Lock) {
    try {
        Remove-Item -LiteralPath $Lock -Force -ErrorAction Stop
        Write-Warning 'Stale UnityLockfile removed (no live lock held). Continuing.'
    } catch {
        $procs = Get-Process -Name 'Unity' -ErrorAction SilentlyContinue
        Fail ("Unity Editor appears to hold this project's lock (cannot delete $Lock). " +
              'Close the Editor before building. Running Unity PIDs: ' +
              (($procs | ForEach-Object { $_.Id }) -join ', ')) 3
    }
}

# ---- Resolve Unity version + executable ------------------------------------
$pvLine = Get-Content ProjectSettings/ProjectVersion.txt |
    Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1
$UnityVersion = ($pvLine -split '\s+')[1]
if ([string]::IsNullOrEmpty($UnityVersion)) { Fail 'Could not read Unity version from ProjectSettings/ProjectVersion.txt' 2 }

# Pin Windows bsdtar (System32) explicitly. A bare `tar.exe` can resolve to Git's GNU
# tar on PATH, which misreads an absolute "C:\..." path as a remote host ("Cannot
# connect to C: resolve failed"). bsdtar also handles > MAX_PATH via the \\?\ API.
$Tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $Tar)) { Fail "Windows bsdtar not found at $Tar" 2 }

$Unity = Resolve-EnvValue 'UNITY_EDITOR_PATH'
if ([string]::IsNullOrEmpty($Unity)) {
    $Unity = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"
    Write-Host "UNITY_EDITOR_PATH not set; falling back to Hub path: $Unity"
}
if (-not (Test-Path -LiteralPath $Unity)) {
    Fail "Unity Editor $UnityVersion not found at: $Unity (set UNITY_EDITOR_PATH or install via Hub)" 2
}

# ---- Resolve artifact version ----------------------------------------------
# `git tag --points-at HEAD` exits 0 with empty output when HEAD is untagged (no
# stderr), unlike `git describe --exact-match` which fatals with "No names found".
if ([string]::IsNullOrEmpty($Version)) {
    $tagAtHead = Invoke-Quiet { & git tag --points-at HEAD } | Select-Object -First 1
    if (-not [string]::IsNullOrEmpty($tagAtHead)) {
        $Version = $tagAtHead.Trim()
    } else {
        $sha = (Invoke-Quiet { & git rev-parse --short HEAD }).Trim()
        $Version = "local-$sha"
    }
}
$global:LASTEXITCODE = 0

Write-Host "Repo     : $RepoRoot"
Write-Host "Unity    : $Unity ($UnityVersion)"
Write-Host "Version  : $Version"
Write-Host "Release  : $(if ($SkipRelease) { 'skip' } elseif ($PublishNow) { 'published' } else { 'draft' })"
Write-Host ''

# ---- Clean transient outputs (keep Library/ and python/.venv) --------------
Step 'Clean transient outputs'
foreach ($p in @('build', 'dist', 'smoke-extract')) {
    if (Test-Path -LiteralPath $p) {
        # cmd.exe rmdir handles paths > MAX_PATH (the bundled venv nests ~285 chars).
        & cmd.exe /c "rmdir /s /q `"$p`"" | Out-Null
    }
}
if (Test-Path -LiteralPath 'release-body-verify.md') { Remove-Item -Force 'release-body-verify.md' }
Remove-Item smoke-try*.log -Force -ErrorAction SilentlyContinue

# ---- uv sync (materialize python/.venv) ------------------------------------
Step 'uv sync --frozen'
$uv = (Get-Command uv -ErrorAction SilentlyContinue)
if (-not $uv) {
    $uvLocal = Join-Path $env:USERPROFILE '.local\bin\uv.exe'
    if (Test-Path -LiteralPath $uvLocal) { $uvExe = $uvLocal } else { Fail 'uv not found on PATH or %USERPROFILE%\.local\bin (install: https://astral.sh/uv)' 2 }
} else {
    $uvExe = $uv.Source
}
Push-Location (Join-Path $RepoRoot 'python')
try {
    & $uvExe sync --frozen
    if ($LASTEXITCODE -ne 0) { Fail "uv sync failed (exit=$LASTEXITCODE)" }
} finally { Pop-Location }

# ---- Unity build -----------------------------------------------------------
# BackcastShippableBuild runs as an IPostprocessBuildWithReport, so the post-process
# (cpython + engine + site-packages copy + compileall + manifest) lands in the same
# invocation; runtime-manifest.json is written LAST, so its presence == build done.
#
# We do NOT use Start-Process -Wait: Unity batchmode can finish the build but hang on
# shutdown (duckdb upstream segfault/hang, commit da794cf), which would block -Wait
# forever even though the artifacts are already on disk. Instead launch detached, poll
# the build artifacts + log, then force-kill Unity if it lingers after the build is done.
Step 'Unity build (BackcastShippableBuild.BuildWindows64); polling artifacts (no -Wait)'
$buildLog = Join-Path $RepoRoot 'build/unity-build.log'
New-Item -ItemType Directory -Force -Path (Join-Path $RepoRoot 'build') | Out-Null
$builtExe = Join-Path $RepoRoot 'build/windows64/backcast.exe'
$builtManifest = Join-Path $RepoRoot 'build/windows64/backcast_Data/StreamingAssets/PythonRuntime/runtime-manifest.json'
$unityArgs = @(
    '-batchmode', '-nographics', '-quit',
    '-projectPath', $RepoRoot,
    '-buildTarget', 'StandaloneWindows64',
    '-executeMethod', 'BackcastShippableBuild.BuildWindows64',
    '-logFile', $buildLog
)
$proc = Start-Process -FilePath $Unity -ArgumentList $unityArgs -PassThru -NoNewWindow
$deadline = (Get-Date).AddMinutes($BuildTimeoutMin)
$artifactsSeenAt = $null
while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
    if ((Test-Path -LiteralPath $builtExe) -and (Test-Path -LiteralPath $builtManifest)) {
        # Build + post-process complete. Allow a short window for a clean self-exit,
        # then stop polling (Unity is hung on shutdown).
        if ($null -eq $artifactsSeenAt) { $artifactsSeenAt = Get-Date; Step 'build artifacts present; waiting up to 30s for clean Unity exit' }
        elseif (((Get-Date) - $artifactsSeenAt).TotalSeconds -gt 30) { break }
    }
    Start-Sleep -Seconds 3
}
if (-not $proc.HasExited) {
    Step 'Unity still alive after build; terminating (known shutdown hang)'
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {}
    try { Wait-Process -Id $proc.Id -Timeout 15 -ErrorAction SilentlyContinue } catch {}
}
$exitNote = if ($proc.HasExited) { "exit=$($proc.ExitCode)" } else { 'killed' }

# Surface post-process log lines + compile errors.
if (Test-Path -LiteralPath $buildLog) {
    Get-Content -LiteralPath $buildLog | Where-Object { $_ -match '\[BackcastShippableBuild\]' } |
        ForEach-Object { Write-Host $_ }
    $csErr = Select-String -LiteralPath $buildLog -Pattern 'error CS\d+' -ErrorAction SilentlyContinue
    if ($csErr) { Fail "Unity build had $($csErr.Count) C# error(s) (see $buildLog)" }
}

# ---- Verify build output (artifacts are the source of truth, not exit code) -
# Unity may have been force-killed on a shutdown hang, so judge success by the
# artifacts the build is supposed to produce, not by the process exit code.
Step "Verify build output ($exitNote)"
if (-not (Test-Path -LiteralPath $builtExe)) {
    if (Test-Path -LiteralPath $buildLog) { Get-Content -LiteralPath $buildLog -Tail 80 }
    Fail "BackcastShippableBuild did not produce $builtExe"
}
if (-not (Test-Path -LiteralPath $builtManifest)) { Fail "Post-process did not produce $builtManifest" }
Step "OK: $builtExe"

# ---- Library size guard (8GB raw cap) --------------------------------------
$libSize = (Get-ChildItem (Join-Path $RepoRoot 'Library') -Recurse -Force -ErrorAction SilentlyContinue |
    Measure-Object Length -Sum).Sum / 1GB
Write-Host ("Library size: {0:N2} GB" -f $libSize)
if ($libSize -gt 8) { Fail ("Library bloat: {0:N2} GB exceeds 8GB raw cap" -f $libSize) }

# ---- Assemble dist + zip ---------------------------------------------------
Step 'Assemble dist + zip'
$stageName = "backcast-windows64-$Version"
$stage = Join-Path $RepoRoot "build/$stageName"
New-Item -ItemType Directory -Force -Path (Join-Path $RepoRoot 'dist') | Out-Null
# Rename in place within build/ (atomic metadata op, same parent dir) rather than a
# cross-directory Move-Item: the bundled venv tree has lazily-created .pyc files and
# > MAX_PATH (260) paths that force Move-Item into a copy-fallback which fails
# ("cannot create a file that already exists"). An atomic rename does no traversal.
if (Test-Path -LiteralPath $stage) { & cmd.exe /c "rmdir /s /q `"$stage`"" | Out-Null }
# Retry: right after the build, the freshly-written 34k-file tree can be transiently
# locked (Windows Defender real-time scan, lingering compileall python), causing
# Rename access-denied. The locks release within seconds, so back off and retry.
$src = Join-Path $RepoRoot 'build/windows64'
$renamed = $false
for ($i = 1; $i -le 12 -and -not $renamed; $i++) {
    try { Rename-Item -LiteralPath $src -NewName $stageName -ErrorAction Stop; $renamed = $true }
    catch {
        if ($i -eq 1) { Step "build output transiently locked; retrying rename (Defender scan settling)" }
        Start-Sleep -Seconds 5
    }
}
if (-not $renamed) { Fail "Could not rename build output after retries (still locked): $src" }
$zip = Join-Path $RepoRoot "dist/$stageName.zip"
& $Tar -a -c -f $zip -C (Join-Path $RepoRoot 'build') $stageName
if ($LASTEXITCODE -ne 0) { Fail "tar create failed (exit=$LASTEXITCODE)" }
$zipMB = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
Write-Host "Assembled: $zip ($zipMB MB)"

# ---- Python SBOM (CycloneDX) -----------------------------------------------
Step 'Python SBOM'
$sbom = Join-Path $RepoRoot "dist/sbom-python-$Version.cdx.json"
& $uvExe tool run --from cyclonedx-bom cyclonedx-py environment (Join-Path $RepoRoot 'python/.venv') -o $sbom
if ($LASTEXITCODE -ne 0) { Write-Warning "SBOM generation failed (exit=$LASTEXITCODE); continuing without SBOM" }

# ---- Smoke stages ----------------------------------------------------------
$tree = $null
if (-not $SkipSmoke) {
    Step 'Smoke Stage 1: zip integrity (re-extract)'
    $out = Join-Path $RepoRoot 'smoke-extract'
    if (Test-Path -LiteralPath $out) { & cmd.exe /c "rmdir /s /q `"$out`"" | Out-Null }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    & $Tar -x -f $zip -C $out
    if ($LASTEXITCODE -ne 0) { Fail "tar extract failed (exit=$LASTEXITCODE)" }
    $tree = Join-Path $out "backcast-windows64-$Version"
    if (-not (Test-Path -LiteralPath (Join-Path $tree 'backcast.exe'))) { Fail 'Stage 1 fail: backcast.exe missing from extracted zip' }
    Write-Host 'Stage 1 PASS'

    Step 'Smoke Stage 2: manifest schema'
    $manifestPath = Join-Path $tree 'backcast_Data/StreamingAssets/PythonRuntime/runtime-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { Fail 'Stage 2 fail: runtime-manifest.json missing' }
    $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($m.schema -ne 1) { Fail "Stage 2 fail: schema drift ($($m.schema))" }
    if ($m.target -ne 'StandaloneWindows64') { Fail "Stage 2 fail: target drift ($($m.target))" }
    $runtimeRoot = Split-Path $manifestPath -Parent
    foreach ($p in @($m.paths.cpython, $m.paths.project_root, $m.paths.venv_site_relative)) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot $p))) { Fail "Stage 2 fail: manifest path missing: $p" }
    }
    Write-Host "Stage 2 PASS (cpython=$($m.cpython_version), built_at=$($m.built_at))"

    Step 'Smoke Stage 3: bundled venv import'
    $runtimeRoot = Join-Path $tree 'backcast_Data/StreamingAssets/PythonRuntime'
    $py = Join-Path $runtimeRoot 'cpython/python.exe'
    if (-not (Test-Path -LiteralPath $py)) { Fail 'Stage 3 fail: bundled python.exe missing' }
    $venvSite = Join-Path $runtimeRoot 'python/.venv/Lib/site-packages'
    if (-not (Test-Path -LiteralPath $venvSite)) { Fail "Stage 3 fail: venv site-packages missing at $venvSite" }
    # Mirror the production Locator: prepend venv site-packages to PYTHONPATH (pyvenv.cfg
    # is deleted in deploy, so direct python.exe can't auto-discover the venv).
    $env:PYTHONPATH = $venvSite
    & $py -c "import duckdb, marimo, pyarrow, sklearn, pandas, numpy, joblib, orjson, httpx, websockets, pydantic; print('venv-import OK')"
    $importCode = $LASTEXITCODE
    Remove-Item Env:\PYTHONPATH -ErrorAction SilentlyContinue
    if ($importCode -ne 0) { Fail "Stage 3 fail: import smoke (exit=$importCode)" }
    Write-Host 'Stage 3 PASS'

    Step 'Smoke Stage 4: Player GUI smoke (retry once)'
    $playerExe = Join-Path $tree 'backcast.exe'
    $contract = '\[WorkspaceEngineHost\] live-configured server built; main GIL-free; lanes polling\.'
    function Invoke-Smoke([string]$Tag) {
        $log = Join-Path $RepoRoot "smoke-$Tag.log"
        Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
        $p = Start-Process -FilePath $playerExe -ArgumentList '-logFile', $log -PassThru
        $deadline = (Get-Date).AddSeconds(90)
        $hit = $false
        while ((Get-Date) -lt $deadline) {
            if (Test-Path -LiteralPath $log) {
                $content = Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue
                if ($content -and ($content -match $contract)) { $hit = $true; break }
            }
            Start-Sleep -Milliseconds 500
        }
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {}
        try { Wait-Process -Id $p.Id -Timeout 10 -ErrorAction Stop } catch {}
        if ($hit) { Write-Host "smoke OK ($Tag)" }
        else {
            Write-Warning "smoke fail ($Tag) -- log dump:"
            if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log }
        }
        return $hit
    }
    if (-not (Invoke-Smoke 'try1')) {
        Write-Warning 'Player GUI smoke try1 failed; retrying once'
        if (-not (Invoke-Smoke 'try2')) { Fail 'Player GUI smoke failed twice' }
    }
    Write-Host 'Stage 4 PASS'
} else {
    Step 'Smoke stages skipped (-SkipSmoke)'
    # Still need an extracted tree for the manifest copy below if releasing.
    $tree = $stage
}

if ($SkipRelease) {
    Write-Host ''
    Write-Host "DONE (build + smoke only). Artifacts in dist/:"
    Get-ChildItem (Join-Path $RepoRoot 'dist') | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize
    exit 0
}

# ---- Supplementary release assets ------------------------------------------
Step 'Release assets (SHA256SUMS + manifest copy)'
$manifestSrc = Join-Path $tree 'backcast_Data/StreamingAssets/PythonRuntime/runtime-manifest.json'
Copy-Item -LiteralPath $manifestSrc (Join-Path $RepoRoot 'dist/runtime-manifest.json') -Force
$assetFiles = @(
    (Join-Path $RepoRoot "dist/backcast-windows64-$Version.zip"),
    (Join-Path $RepoRoot 'dist/runtime-manifest.json')
)
if (Test-Path -LiteralPath $sbom) { $assetFiles += $sbom }
$sumLines = foreach ($f in $assetFiles) {
    $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
    "$($h.ToLower())  $(Split-Path $f -Leaf)"
}
$sumsPath = Join-Path $RepoRoot 'dist/SHA256SUMS.txt'
$sumLines | Set-Content -LiteralPath $sumsPath -Encoding utf8
Get-Content -LiteralPath $sumsPath
$assetFiles += $sumsPath

# ---- Release body (verify procedure) ---------------------------------------
$fence = [string][char]0x60 * 3
$repo = Invoke-Quiet { & gh repo view --json nameWithOwner -q .nameWithOwner 2>$null }
if ([string]::IsNullOrEmpty($repo)) { $repo = 'botterYosuke/backcast' }
$bodyLines = @(
    '## Verification (run before extracting)',
    '',
    "$fence" + 'pwsh',
    "gh release download $Version -p '*.zip' -p 'SHA256SUMS.txt'",
    "Get-FileHash backcast-windows64-$Version.zip -Algorithm SHA256",
    '# compare against SHA256SUMS.txt',
    $fence,
    '',
    'Built locally via scripts/build-and-release.ps1 (ADR-0014 owner machine).',
    '',
    '## Runtime requirements',
    '',
    '- Windows 10 64-bit',
    '- DuckDB data root via env BACKCAST_JQUANTS_DUCKDB_ROOT (ADR-0006)',
    '- No system Python install required (cpython bundled)',
    '- No VC++ Redistributable required (vcruntime DLLs bundled)'
)
$bodyPath = Join-Path $RepoRoot 'release-body-verify.md'
$bodyLines | Set-Content -LiteralPath $bodyPath -Encoding utf8

# ---- Create / update the GitHub Release ------------------------------------
Step 'GitHub Release'
# Use the owner account token for the gh subprocess only (does not change active account).
$savedToken = $env:GH_TOKEN
if (-not [string]::IsNullOrEmpty($GhAccount)) {
    $tok = Invoke-Quiet { & gh auth token -u $GhAccount 2>$null }
    if ([string]::IsNullOrEmpty($tok)) { Fail "gh auth token for account '$GhAccount' not available (gh auth login first)" 2 }
    $env:GH_TOKEN = $tok
}
try {
    $exists = $false
    Invoke-Quiet { & gh release view $Version 2>$null 1>$null }
    if ($LASTEXITCODE -eq 0) { $exists = $true }
    $global:LASTEXITCODE = 0

    if ($exists) {
        Write-Host "Release $Version exists; uploading assets with --clobber"
        & gh release upload $Version @assetFiles --clobber
        if ($LASTEXITCODE -ne 0) { Fail "gh release upload failed (exit=$LASTEXITCODE)" }
    } else {
        $createArgs = @($Version, '--title', "backcast $Version", '--notes-file', $bodyPath)
        if (-not $PublishNow) { $createArgs += '--draft' }
        # Mark common pre-release suffixes as prerelease.
        if ($Version -match '-(rc|beta|alpha)\.') { $createArgs += '--prerelease' }
        $createArgs += $assetFiles
        & gh release create @createArgs
        if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (exit=$LASTEXITCODE)" }
    }
} finally {
    if ([string]::IsNullOrEmpty($savedToken)) { Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue }
    else { $env:GH_TOKEN = $savedToken }
}

$state = if ($PublishNow) { 'published' } else { 'DRAFT' }
Write-Host ''
Write-Host "DONE. $state Release '$Version' is ready: https://github.com/$repo/releases"
if (-not $PublishNow) { Write-Host 'HITL: extract on a clean machine, run the Replay AC, then click Publish.' }
exit 0
