# 0054 — E2E 第二波 1本目: ScenarioStartupE2ERunner 昇格

**日付**: 2026-06-19 / **ブランチ**: `e2e/replay-to-hakoniwa-runner`
**関連**: [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md)（runner 配置・命名） /
[台本](../../Assets/Tests/E2E/Editor/ScenarioStartupE2ERunner.md) / [findings 0015](0015-scenario-startup-panel.md)（#29 パネル本体） /
[E2E-CONVENTIONS](../../Assets/Tests/E2E/Editor/E2E-CONVENTIONS.md)（section ↔ Action ID 方針）

## 文脈

第二波（台本の `自動(Probe有・要昇格)` / `要新規自動化` 行を `*E2ERunner.cs` 回帰ゲートへ昇格）の 1 本目。
ScenarioStartup タイル（Replay 実行設定パネル, #29）を選定。理由: 検証ロジックが純 C# で Python-FREE、
既存 throwaway `ScenarioStartupProbe`（S1〜S10）が台本 SCENARIO-01〜11 をほぼ被覆済みで delete-the-logic litmus が明快。
FooterMode は base 取り込み直後の #84（footer 可視性）churn を避けて後回しにした。

## 決定（型・後続11本に適用）

- **昇格モデル**: throwaway `Assets/Editor/ScenarioStartupProbe.cs` を `git mv` で
  `Assets/Tests/E2E/Editor/ScenarioStartupE2ERunner.cs` へ移動・改名（.meta も移して GUID 保全）。旧 Probe は削除。
  先例: `ReplayToHakoniwaProbe` → `ReplayToHakoniwaE2ERunner`（昇格時に旧 Probe 削除済み）。ADR-0015 準拠。
- **section ↔ Action ID 方針 = (B)「自然な検証単位」**: 台本の「1 Action ID = 1 section」を厳密適用せず、
  共有 pure validation（`Section2_Validation` = `ScenarioStartupValidation.Validate`/`TryBuildForWrite`）を
  Action ID ごとに人工分割しない。各 section header に `Covers: <Action IDs>` を明記し台本から追跡可能にする。
  E2E-CONVENTIONS.md「runner の section ↔ Action ID 対応方針」へ規約化（owner 承認）。
- 実証済み Probe の Section1〜10 の assert は **1 行も削らず移送**（回帰網を落とさない）。PASS/FAIL タグを
  `[E2E SCENARIO STARTUP PASS/FAIL]` に統一、`EditorApplication.Exit` を（batchmode 限定でなく）無条件化＝self-failing gate
  （ReplayToHakoniwaE2ERunner と同形）。
- **SCENARIO-12（File→New Clear）= 新規 Section11**。台本で唯一の `要新規自動化` 行。`ScenarioStartupController.Clear()`
  は実在（in-memory のみ・on-disk 不触）なので production コード追加は無し。

## RED→GREEN（delete-the-logic litmus）

- **RED**: `ScenarioStartupController.Clear()` の `Universe.ReplaceAll(Array.Empty<string>())` を一時コメントアウト →
  AFK 実走で `[E2E SCENARIO STARTUP FAIL] clear: universe not emptied`（Run の exit-1 分岐）。ゲートが非空虚であることを実証。
- **GREEN**: 復元 → `[E2E SCENARIO STARTUP PASS] merge-preserve + validation + registry + File→New clear verified`、全 11 section PASS。
- compile-only ゲート（`-executeMethod` 無し）で `error CS` 0 件を先に確認済み。

## 再走手順

```pwsh
$unity = "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
$log = "$env:TEMP\sse2e.log"
& $unity -batchmode -nographics -quit -projectPath "C:\Users\sasai\Documents\backcast" `
         -executeMethod ScenarioStartupE2ERunner.Run -logFile $log
# 期待: ログに [E2E SCENARIO STARTUP PASS] / UNITY exit 0
```

- **Unity ログは UTF-8**。PowerShell `Select-String` と本セッションでは Grep ツール(ripgrep)も PASS 行（`File→New` の
  `→` を含む行）を取りこぼした。**`grep -a "E2E SCENARIO STARTUP" <log>` で確認**すること（memory `unity-afk-probe-run` 参照）。
- compile-only: `-executeMethod` を外した同コマンドで `grep -aE "error CS[0-9]+"` が 0 件。

## 改名の波及

- **active 参照を現行化**: 台本（`ScenarioStartupE2ERunner.md` カバー状態 `要昇格`→`E2E済`・既存Probe 列）、
  `E2E-INDEX.md`（ロールアップ: E2E済 12 / 要昇格 0 / 要新規 0 / HITL 2）、`RunButtonE2ERunner.md`（RUN-05/06 の oracle 名）、
  `python/tests/test_scenario_inline_golden.py` / `capture_scenario_inline_golden.py`（Leg B コメント名）、
  `docs/findings/0015`（dangling パス `Assets/Editor/ScenarioStartupProbe.cs` → 新パス）。
- **historical findings は旧名を履歴として保持**（falsify しない）: `0023 / 0025 / 0027 / 0042 / 0043 / 0046 / 0051` は
  当時の RED→GREEN ログとして `ScenarioStartupProbe.SectionN` を参照したまま残す。旧名は本 findings #54 で改名済みと記録。

## base 同期（着手前）

`origin/main` が 2 コミット先行（`a0a0101 #84 footer 可視性` / `3abc8ca Mac File 保存`）していたため、footer/menu/layout を触る
後続 runner を stale base に書かないよう **着手前に rebase**（conflict 無し）。base には sibling `TachibanaLiveE2ERunner`（#53）も在る。

## grill で判明した前提修正（後続スライス用・本 runner 範囲外）

第二波の `要確認` 14 件をコード裏取りした際、handoff の前提が 2 点誤っていた。後続 Journey/Sidebar runner で必ず反映する:

1. **mock fill は `get_state_json` に載らない**。`get_state_json` はチャート/execution_mode のみ。fill→建玉は別チャネル
   `engine.last_portfolio`（`replay_kernel_observer.push_portfolio`）→ `get_portfolio_json()`/`get_portfolio()` で読む
   （TTWR 2 チャネル設計, findings 0044）。oracle: `python/tests/test_replay_portfolio_positions.py` /
   `test_get_portfolio_json.py`。→ LiveManualTrade / AuthorToRun Journey の「fill 確認」観測点は portfolio チャネルに差し替える。
2. **`IAvailableInstrumentsProvider` の実 DuckDB/venue 配線は未実装**。production は `MockAvailableInstrumentsProvider`
   （6 銘柄ハードコード）のみ（#31 が mock、実ソースは別 issue）。→ UniverseSidebar runner は mock 挙動の固定までで、
   「実 DuckDB を assert」は対象外に落とす。
3. （参考・確認済み）`set_execution_mode`（`live_orchestrator.py:857`・Live 系は venue ログイン必須）/
   `stop_live_strategy`（`live_orchestrator.py:1766`・モード不問）は実体ありで配線済み。FooterMode の純ロジック
   `FooterModeViewModel.RequestMode` は headless 反射切替可（Replay=即時・RPC 無し）。
</content>
</invoke>
