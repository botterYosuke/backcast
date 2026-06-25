# UniversePersistE2ERunner — 台本（ADR-0031 S3 / #143）

方針: [ADR-0031 D4](../../../../docs/adr/0031-cell-driven-dynamic-universe-bt-universe-api.md) ＋
[findings 0115 §S3](../../../../docs/findings/0115-issue141-145-cell-driven-dynamic-universe.md)。

## 何を gate するか

`bt.universe.*` 編集の永続化を **既存の Save タイミングに限定**（ADR-0031 D4・独自トリガを引かない）。編集は registry を
dirty にし chart に反映するが **sidecar を書かない**。既存の full-registry Save 経路（**Run-commit / Save As → `Commit` →
`SetStartupParamsAndInstruments(Universe.Ids)`**）でのみ scenario.instruments に co-write（Newtonsoft merge＝layout キー・
unknown scenario キー保持）。**File→Save は universe を touch しない**既存不変条件（JOURNEY-LAYOUT-07）は維持＝production コード変更ゼロ。

Python-FREE：bt.universe 編集を `UniverseBridge.ApplyJson`（BTUNIV-09 と同 seam）で模擬し、永続化経路（Commit→sidecar）は全 C#。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod UniversePersistE2ERunner.Run -logFile <abs>
# expect: [E2E UNIVERSE PERSIST PASS] / exit=0（確認は Bash `grep -a "UNIVERSE PERSIST"`）
pwsh scripts/run-live-e2e.ps1 -Method UniversePersistE2ERunner.Run
```

## 操作一覧表

| Action ID | 行動 | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|
| `PERSIST-01` | bt.universe.add(B)（Save せず） | registry [A,B]＋chart B・disk sidecar は [A] のまま | reflected ∧ sidecar 不変（dirty・no-write） | 自動(E2E済) |
| `PERSIST-02` | dirty 状態で `Commit`（Run-commit/Save As） | scenario.instruments=[A,B]・layout キー＋unknown scenario キー保持 | merge-write co-write | 自動(E2E済) |
| `PERSIST-03` | edit→Commit→restart（fresh root 再 open） | registry が disk から [A,B] を seed | saved survives restart | 自動(E2E済) |
| `PERSIST-04` | edit→**Commit せず**→restart | registry [A]・disk に C 漏れなし | unsaved reverts | 自動(E2E済) |
| `BTUNIV-05/08` | Python 半（編集は dirty・自動永続化しない） | pytest | `@pytest.mark.scenario` | 自動(E2E済・pytest) |
| `PERSIST-05` | 実機目視：cell で add → RUN(commit) → 再起動で残る／RUN せず再起動で消える | owner 手元 | — | HITL専用 |

## RED→GREEN litmus（findings 0115 §S3）

- `DriveUniverseBridge` が edit を registry に apply しない → Commit が旧 universe を書き PERSIST-02/03 RED。
- bt.universe.add を Flush-on-edit（却下案）にする → add 単体が sidecar を書き PERSIST-01 RED。
- 回帰: `LayoutPersistenceJourneyE2ERunner` JOURNEY-LAYOUT-07（File→Save は universe を書かない）が GREEN のまま。
