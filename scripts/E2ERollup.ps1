<#
  Shared E2E Action-ID rollup. Dot-sourced by run-live-e2e.ps1 (Unity log) and
  run-all-tests.ps1 (Unity + pytest logs) so the rollup logic lives in one place.

  Scans one or more logs for the canonical tag "[E2E <id> PASS|FAIL|SKIP] <msg>"
  (E2E-CONVENTIONS.md section 5; the pytest conftest emits the same form). Dedup
  precedence across duplicate ids: FAIL > PASS > SKIP (a scenario that fails in any
  test is FAIL; PASS beats SKIP so a covered-and-passed id is not hidden by a skip).

  Returns the number of FAIL entries, or -1 when no tag is present (caller then falls
  back to the process exit code). SKIP is neutral (not counted as a failure).
#>

function Write-E2ERollup {
    param([string[]]$LogPaths)

    $rx = '\[E2E ([A-Z0-9][A-Z0-9-]*) (PASS|FAIL|SKIP)\]\s*(.*)'
    $rank = @{ 'SKIP' = 1; 'PASS' = 2; 'FAIL' = 3 }
    $entries = [ordered]@{}

    foreach ($lp in $LogPaths) {
        if ([string]::IsNullOrEmpty($lp) -or -not (Test-Path -LiteralPath $lp)) { continue }
        $hits = Select-String -LiteralPath $lp -Pattern $rx -AllMatches -ErrorAction SilentlyContinue
        foreach ($h in $hits) {
            foreach ($m in $h.Matches) {
                $id = $m.Groups[1].Value; $st = $m.Groups[2].Value; $msg = $m.Groups[3].Value.Trim()
                if (-not $entries.Contains($id)) {
                    $entries[$id] = @{ Status = $st; Msg = $msg }
                } elseif ($rank[$st] -gt $rank[$entries[$id].Status]) {
                    $entries[$id] = @{ Status = $st; Msg = $msg }
                }
            }
        }
    }

    Write-Host ''
    Write-Host '=================================================='
    Write-Host 'E2E Action-ID Rollup Report'
    Write-Host '=================================================='
    if ($entries.Count -eq 0) {
        Write-Host '(no [E2E <NAME> PASS/FAIL/SKIP] tags found)'
        Write-Host '=================================================='
        return -1
    }
    $fails = 0; $skips = 0
    foreach ($id in $entries.Keys) {
        $e = $entries[$id]
        switch ($e.Status) {
            'FAIL'  { $fails++; $sx = if ($e.Msg) { " -> $($e.Msg)" } else { '' }; Write-Host "[FAIL] $id$sx" }
            'SKIP'  { $skips++; Write-Host "[SKIP] $id" }
            default { Write-Host "[PASS] $id" }
        }
    }
    Write-Host '=================================================='
    Write-Host ("Summary: {0} PASS / {1} FAIL / {2} SKIP / {3} total" -f `
        ($entries.Count - $fails - $skips), $fails, $skips, $entries.Count)
    Write-Host '=================================================='
    return $fails
}
