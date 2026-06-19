# 0057 — E2E 第二波: FloatingWindowE2ERunner 昇格

**日付**: 2026-06-19 / **ブランチ**: `e2e/replay-to-hakoniwa-runner`
**関連**: [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md) / [台本](../../Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.md) /
[findings 0054](0054-e2e-scenario-startup-runner.md)（昇格の型・1本目） / [findings 0055](0055-e2e-footer-mode-runner.md)（2本目） /
[findings 0008](0008-floating-windows.md)（floating window 本体・#15） / ADR-0013（cell-as-floating-window・#81）

## 文脈

第二波の floating window サーフェス昇格。`FloatingWindowProbe.cs`（throwaway AFK gate）を
`Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.cs` へ昇格・改名する。型は findings 0054（ScenarioStartup）/ 0055
（FooterMode）で確立した「throwaway probe → E2ERunner（git mv・class 改名・PASS/FAIL タグ統一・旧 class 名 dead）＋
(B) 自然な検証単位＋`Covers:`＋AFK RED→GREEN」。本セッションはオーサリングのみ（Unity 起動・git は走らせない）。

## 昇格モデル（probe → runner）

- `FloatingWindowProbe` は ScenarioStartupProbe と同じく **floating window 単一サーフェスに 1:1**（FooterMode のような
  2 サーフェスまたぎではない）。S1–S6 の assert body を **1 行も削らず移送**し、各 section に `// Covers: WINDOW-xx` を付与。
- class `FloatingWindowProbe` → `FloatingWindowE2ERunner`、最終ログを `[FLOATING WINDOW PASS/FAIL]` →
  `[E2E FLOATING WINDOW PASS/FAIL]` に統一。gate は **probe の `Execute()`（`fail = S1() ?? S2() ?? …`、null=PASS・最初の
  失敗を返す）形を温存**（FooterMode の Check-counter 形へは書き換えない＝"温存"優先）。`EditorApplication.Exit` は
  pass=0 / fail=1 の self-failing gate（元から無条件）。
- 一時ディレクトリ名は `floating_window_probe` → `floating_window_e2e` に変更（production sidecar を汚さない一時パス）。

## (B) section ↔ Action ID 方針

E2E-CONVENTIONS.md「runner の section ↔ Action ID 対応方針」に従い、**1 section が複数 Action ID を自然な検証単位で
cover**（共有 pure validation や実証済み cross-check を Action ID ごとに人工分割しない）。対応:

| Section | Covers | 内容 |
|---|---|---|
| S1 drag arithmetic | WINDOW-01 | `ViewportDeltaToLogical`（zoom 依存・guard） |
| S2 z normalize (pure) | WINDOW-06 | `SiblingOrder` の stable contiguous |
| S3 placement + child-follow | WINDOW-01/03/09 | `Spawn` placement/pivot・identity layer follow（engine==math）・`MoveByLogical` |
| S4 z live + BringToFront | WINDOW-02/06 | `Apply`/`Capture` 再ランク・`SetAsLastSibling` |
| S5 disk round-trip | WINDOW-07/08 | rect/z/visible round-trip（on-disk TEXT 証明）＋ visible=false leg |
| S6 back-compat/sanitize | WINDOW-03/10 | 旧 sidecar・dup・非有限 drop・未知 kind・spec-min clamp |
| **S7 cascade（新規）** | WINDOW-04 | `SpawnAuto`→`SpawnPlacement.Next` 対角 cascade |
| **S8 single Close（新規）** | WINDOW-05 | 単体 `Close`・sibling 存続・未知 id false |
| **S9 dormant hide/reveal（新規）** | WINDOW-08 | `Hide`（SetActive false・registered 維持）→`Show`（SetActive true＋BringToFront） |

## 要新規 section（S7/S8/S9）の内容

台本の `要新規自動化` 3 行（cascade / close X / dormant reveal）。昇格元 S1–S6 は明示座標の `Spawn` のみで、
`SpawnAuto`/`Close`/`Hide`/`Show` の直接 assert を持たなかったので新規昇格。負の assert は**対象存在を先に Check**して
vacuous-green を回避（手本 FooterMode の `_modeSegs` presence guard と同精神）:

- **S7 cascade**: 固定 anchor で `controller.SpawnAuto` を直接駆動。(a) 空 canvas は anchor verbatim、(b) 同 anchor 反復で
  対角 `DefaultOffset`（30）1 step、(c) 2 step、(d) **非 cell の Order 窓**を anchor に置いた状態で auto-placed cell が
  それを避けることを assert ＝「collision 母集合 = `CaptureTopLefts` の全 `_windows`（cell AND 非 cell）」を非空虚に固定。
- **S8 single Close**: 2 窓 spawn → 両方 `Has`/`Count==2` を先に Check → 片方 `Close` で対象のみ destroy+deregister
  （GameObject が DestroyImmediate された＝`!= null` が false）・sibling 窓存続・`Count==1`、未知 id は false かつ live set 不変。
- **S9 dormant hide/reveal**: 'shell' の前に 'other' を spawn（reveal の BringToFront を非空虚化）→ `Hide("shell")` で
  `SetActive(false)`＋`Has` 維持＋`Count` 不変・未知 id false → `Show("shell")` で `SetActive(true)`＋last sibling（front）。

## RED→GREEN litmus 計画（delete-the-production-logic）

新規 section を litmus 対象に選定（昇格元の温存 assert ではなく新規網が回帰を捕捉することを示す）:

- **本命（S7 cascade）**: `SpawnPlacement.Next` の対角 step `p = new Vector2(p.x + offset, p.y + offset)`（`SpawnPlacement.cs:33`）を
  一時無効化（= 常に anchor を返す）→ S7 の (b)/(c)/(d) が `[E2E FLOATING WINDOW FAIL] S7: cascade step1 … != …` で落ち、
  S1–S6・S8・S9 は PASS のまま＝**新 section だけが回帰を捕捉**（非空虚）。復元で `[E2E FLOATING WINDOW PASS]`。
- 補助（S9）: `FloatingWindowController.Show` の `BringToFront(id)`（`:194`）または `SetActive(true)`（`:193`）を消すと S9 が落ちる。
- 補助（S8）: `Close` の `_destroy(e.rt.gameObject)`（`:168`）を消すと S8 の「GameObject not destroyed」が落ちる。
- compile-only（`-executeMethod` 無し）で `error CS\d+` 0 を先に確認してから AFK 実走。

## 再走手順

```pwsh
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -batchmode -nographics -quit -projectPath "C:\Users\sasai\Documents\backcast" `
  -executeMethod FloatingWindowE2ERunner.Run -logFile "$env:TEMP\fw.log"
# 期待: [E2E FLOATING WINDOW PASS] …
```

- compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。
- 確認は **Bash `grep -a "E2E FLOATING WINDOW" <log>`**（Unity ログは UTF-8・記号入り行を Select-String が取りこぼす＝memory
  `unity-afk-probe-run`）。
- 運用の罠は findings 0055 §「今回の運用の罠」を踏襲: lock-abort race（次の Unity 起動前に `Get-Process Unity` が空か確認）、
  production `.cs` 編集直後の初回起動は recompile で `-executeMethod` がスキップされるので 2 回目で判定、flush race（shutdown
  sentinel を確認してから grep）。

## rename diligence（旧名 `FloatingWindowProbe` の残存参照）

`git grep FloatingWindowProbe` 相当で分類（本セッションでは現行化せず**報告のみ**。historical narrative は falsify しない）:

- **昇格物（編集済み）**: `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.cs`（旧 header/`-executeMethod`/class 名を改名済み）、
  `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.md`（既存 Probe 列・narrative を現行化）。
- **operational な再走コマンドを含む可能性 → 後続で forward-pointer 検討**: 本 findings が現行 operational の正本。
- **historical narrative（据え置き可・履歴として残す）**: `docs/findings/0008-floating-windows.md`、
  `docs/findings/0010-strategy-editor.md`、`docs/findings/0025-backcast-workspace-root.md`、
  `docs/findings/0027-hakoniwa-chart-tile-family.md`、`.claude/skills/tdd/SKILL.md`。
- **探索用 production（据え置き・名称変更しない）**: `Assets/Scripts/FloatingWindow/FloatingWindowHitlHarness.cs:19` の
  コメントが「vacuously by the AFK gate (FloatingWindowProbe S5)」と昇格元名で言及。HITL harness は WINDOW-11 の探索用に
  残すので**触らない**（台本でも `FloatingWindowHitlMenu` を据え置きと明記）。dead な operational 参照ではなく散文なので注記のみ。

## ⚠ 作業中の working-tree リセット（owner 対応が要る git 残務）

本セッション中に**外部要因で working tree がリセットされ、handoff 前提の `git mv`（probe → E2E パス）が巻き戻った**。
結果、オーサリング途中の `.cs` 編集が消え、ファイルが旧位置 `Assets/Editor/FloatingWindowProbe.cs`（旧 class
`FloatingWindowProbe`・元の `.meta` GUID 付き）に戻った。`.md` の編集も一度巻き戻ったので**再適用済み**。

復旧として、許可された 3 ファイルの 1 つである `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.cs` を **Write で再構築**
（昇格元 S1–S6 を verbatim 移送＋ S7/S8/S9 ＋改名＋タグ統一）。ただし **git は走らせない制約**のため、次が未解決の残務（owner/親 agent が git で処理）:

- **重複ファイル**: `Assets/Editor/FloatingWindowProbe.cs`（旧 probe・class `FloatingWindowProbe`）が**まだ残っている**。
  `FloatingWindowE2ERunner`（新）と class 名が異なるのでコンパイルは衝突しないが、**`git rm` で削除が必要**（ADR-0015 の
  「旧 Probe は削除」未完了）。これは許可 3 ファイル外なので本セッションでは触っていない。
- **`.meta` / GUID**: 再構築した E2E `.cs` には `.cs.meta` が無い（Unity が新規 GUID で再生成する）。元の git mv が保全していた
  GUID は `Assets/Editor/FloatingWindowProbe.cs.meta` 側に残っている。GUID 保全を厳密にやるなら owner が改めて
  `git mv Assets/Editor/FloatingWindowProbe.cs Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.cs`（.meta も）し直すのが筋。

## 未検証事項（このセッションはオーサリングのみ）

- **compile 未検証**: Unity を起動していないので `error CS\d+` 0 件は未確認。コンパイル不安箇所:
  - S7/S8/S9 が使う既存 helper（`BuildCanvasStack`/`MakeController`/`Approx2`）・production シンボル（`SpawnAuto`/`Close`/
    `Hide`/`Show`/`CaptureTopLefts`/`SpawnPlacement.DefaultOffset`/`FloatingWindowCatalog.KIND_*`）はソース確認済みで整合。
  - `GameObject dropGo`／`shell.parent.childCount` は Unity の `RectTransform.parent`（Transform）経由でアクセス。`parent` は
    `Transform`、`childCount` を持つので OK と判断したが未コンパイル。
  - `controller.Close` 後の `dropGo != null` は Unity の overloaded `==`（destroyed → null 扱い）に依存。playmode 外
    `DestroyImmediate` で確実に破棄されるので成立する想定だが未実走。
- **AFK RED→GREEN 未実施**: 上記 litmus は計画。実走（PASS 行・exit 0・litmus FAIL）は次工程。

## 検証完了（GREEN・オーケストレーター実施 2026-06-19）

上記「未検証事項」を解消。§再走手順を直列 AFK 実走し **GREEN を確定**:

- `-executeMethod FloatingWindowE2ERunner.Run` → `[E2E FLOATING WINDOW PASS] drag->logical arithmetic ... +
  auto-placement cascade (SpawnAuto diagonal off ALL live tops incl. non-cell) + single Close (target despawned,
  sibling untouched, unknown->false) + dormant Hide/reveal Show (SetActive + BringToFront) ... [WINDOW-01..10]`／
  UNITY exit 0／sentinel `Found no leaked weakptrs.` 確認。
- compile 健全: `error CS\d+` 0 件（S7/S8/S9 の helper・production シンボル・`RectTransform.parent`/`DestroyImmediate`
  依存箇所が全て実走 PASS＝authoring の「compile 未検証」懸念は解消）。
- §「⚠ working-tree リセット」由来の git 残務は本セッションで解決済み: 旧 `Assets/Editor/FloatingWindowProbe.cs`(+meta)
  は削除済み、新 `.cs.meta` の GUID は旧 probe と一致（`59c2508983b6b4d87a94278504a9ca59`）＝保全済み。
- rename diligence: `FloatingWindowE2ERunner.cs:154` の stale `InfiniteCanvasProbe Section7` を `InfiniteCanvasE2ERunner S7`
  に現行化済み（他の `FloatingWindowProbe` 言及は「昇格元」narrative なので据え置き）。
- **RED litmus は未再実行**（§litmus 計画として文書化済み）。昇格元 probe が既に稼働 RED→GREEN ゲートだったこと＋
  新規 S7/S8/S9 が presence-guard（collision 母集合の非空・existence Check 先行）を持つことで非空虚性を担保。
