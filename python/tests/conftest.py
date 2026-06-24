"""Gap 3 — bridge pytest outcomes into the E2E Action-ID rollup.

A test tagged ``@pytest.mark.scenario("KABU-LIVE-03")`` emits the canonical
``[E2E KABU-LIVE-03 PASS|FAIL|SKIP]`` tag derived from its *actual* outcome, so
``run-live-e2e.ps1`` / ``run-all-tests.ps1`` roll it up alongside the Unity runners
in the same Action-ID ledger.

The tag is computed from the pytest report (never printed by the test body), so a
test cannot emit a green tag without actually passing — this preserves the project
rule "do not make tests always-pass" (CLAUDE.md). One test may carry several
scenario marks; the rollup's FAIL-wins dedup folds duplicate IDs across tests.
"""
from __future__ import annotations

import pytest


_TAG = {"passed": "PASS", "failed": "FAIL", "skipped": "SKIP"}


def _scenario_ids(item) -> list[str]:
    return [m.args[0] for m in item.iter_markers(name="scenario") if m.args]


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item, call):
    outcome = yield
    report = outcome.get_result()

    ids = _scenario_ids(item)
    if not ids:
        return

    # report.outcome is already "passed"/"failed"/"skipped". Tag the call phase (the
    # verdict) and a setup skip/error; teardown failures are out of scope for scenario
    # tags (rare, and a sibling test with the same id would surface FAIL via the rollup).
    if report.when == "call":
        status = _TAG[report.outcome]
    elif report.when == "setup" and report.outcome != "passed":
        status = _TAG[report.outcome]
    else:
        return

    tr = item.config.pluginmanager.get_plugin("terminalreporter")
    for sid in ids:
        line = f"[E2E {sid} {status}]"
        if tr is not None:
            tr.write_line(line)
        else:  # pragma: no cover - fallback when no terminal reporter (e.g. xdist worker)
            print(line)
