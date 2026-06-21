---
name: behavior-to-e2e
description: >-
  backcast（Unity C# + 埋め込み Python の取引アプリ・このリポジトリ）で、ユーザーが挙動を言葉で語ったとき
  ——「こう動いてほしい / こうなったら困る / この挙動を保証して / 目視せず自動で確認したい / 台本にしたい /
  リリース前に壊れてないか担保したい」——それを **E2E 回帰ゲート**に変換するスキル。backcast の E2E 正本は
  **AFK Unity batchmode probe（`-executeMethod <…>Probe.Run` / `<Surface>E2ERunner.cs`）＋ `docs/findings/NNNN-*.md`
  の RED→GREEN・再走手順**、純 Python は **pytest / golden gate**。**Bevy の `tests/e2e/FLOWS.md` /
  `tests/e2e_replay.rs` は backcast に無い**（あれは移植元 TTWR 専用＝`references/ttwr-bevy-legacy.md`）。
  必ず起動する場面: 「この挙動をテスト/ゲートにして」「ユーザーストーリ（台本）をテストに」「台本を書いて」
  「E2E で通したい」「N ステップを通しで確認」「目視の代替」「全行動を網羅したい / 網羅台帳」「テストでカバー
  されているか確認（カバレッジ照会）」「リプレイ/発注/ポートフォリオ/venue ログインの挙動を保証」。
  **不具合修正（「issue #N を修正」「bug を直す」「配線漏れを実装」）・レビュー指摘修正（「review findings を
  fix」「Medium をつぶして」）・stub 実装（「NotImplementedError を解消」）・検証/クローズスライス（「発火する
  test で証明」）・spike の gate 判定**も、挙動が変わる/AC が AFK probe・characterization・HITL gate を要求するなら
  起動命令。**enum バリアント（VenueState / ExecutionMode 等）が AC に列挙されていたら全バリアント網羅**。
  ⚠️ **最重要の不変条件**: ユーザーが他スラッシュコマンド（`/grill-with-docs` `/parallel-agent-dev`
  `/nautilus-trader` `/tdd` `/simplify` `/plan` 等）を**明示タイプ**していても、台本・E2E・網羅・挙動保証・
  RED の語が出たら **設計インタビュー/実装に入る前に最初に本スキルを formal invoke** する。過去 12+ 回、
  タイプ済み他スキルや grill の設計に注意が奪われて invoke を飛ばし、成果物だけ後付けで揃える miss を繰り返した
  ——**成果物の品質・設計済み度・新規性と formal invoke は独立**（詳細と masking phrase の一覧は本体「過去の
  invoke 漏れ」節）。TTWR(Bevy) リポで E2E を触るときだけ `references/ttwr-bevy-legacy.md` を読む。
---

# behavior-to-e2e — 挙動の言葉を backcast の E2E ゲートに変える

ユーザーが日本語で語った「こうあってほしい挙動」を、backcast の回帰ゲートに落とす。**取りうる操作は原則すべて
カタログ化し、可能な限り自動テストにする**。自動化できないもの（実ピクセル・実 venue・OS ダイアログ）も除外
せず、HITL として理由付きで台帳に載せる。

> **backcast ≠ TTWR。** 本体は backcast（Unity C# + 埋め込み Python）専用。Bevy の `tests/e2e/FLOWS.md` /
> `tests/e2e_replay.rs` / Rust `Harness` / `BackendStatusUpdate`→resource seam は backcast に**存在しない**ので、
> それらの機構が要るとき（＝TTWR リポでの作業）だけ [`references/ttwr-bevy-legacy.md`](./references/ttwr-bevy-legacy.md) を読む。

## backcast の E2E 正本（gate 早見表）

| 挙動の出所 | gate（backcast 正本） |
|---|---|
| C# / Unity 挙動 | **AFK batchmode probe**（`-executeMethod <…>.Run`）＝回帰ゲート。探索用 `*Probe.cs` を `<Surface>E2ERunner.cs` へ昇格（ADR-0015）。実ピクセル・実ウィンドウは **owner HITL** |
| Python seam ロジック（engine/*・handler・poll） | `python/tests/test_*.py`（pytest。fake/stub で seam を直接駆動）。RED→GREEN を findings に記録 |
| 純 Python engine（Unity 未配線・oracle あり） | golden gate スクリプト（capture/verify）＋ `docs/findings/NNNN` の AC 対応表 |
| spike probe の卒業（throwaway→本実装） | spike probe を production pytest gate へ卒業。spike-only dep は `pytest.importorskip` で gate |
| ユーザーストーリ（台本） | `Assets/Tests/E2E/Editor/<Name>E2ERunner.md`（台本＝合否の正本）＋ `<Name>E2ERunner.cs`（自動判定） |

**backcast での本スキルの実体＝「probe/pytest を回帰ゲートとして著したか／findings に RED→GREEN・再走手順を
記録したか」**。FLOWS.md 採番に拘泥しない。

> **挙動が C#→Python を跨ぐとき＝「Python-FREE fake で C# 配線、pytest で engine 正しさ」の 2 ゲート分割**（#95 Phase 2
> per-cell RUN 実例 2026-06-20）。AFK probe から実埋め込みインタプリタを起こそうとせず、**host-bridge を interface
> 化してその AFK section に Python-FREE な fake executor を注入**し、C# 側（ボタン presence・index→窓 routing・stale
> result の drop 等）だけを assert する。engine 側の実挙動（例: marimo reactive で下流が再計算され独立 cell は再計算
> されない）は **pytest が正本**。こうすると AFK runner が Python-FREE のまま速く・隔離されたゲートになり（既存
> `StrategyEditorNotebookE2ERunner` が FakeMarimoSynthesizer で同型）、RED→GREEN litmus も両ゲートで別々に立つ
> （C#: routing を 1 窓に collapse させると RED ／ Python: `compute_cells_to_run` の autorun を縮める/広げると RED）。
> orchestration brain は MonoBehaviour-free な plain C# クラス（`NotebookCellCoordinator` と同型）にして、AFK が
> root 無しで RunCell/Drain を直駆動できる形にするのが配線テストの肝。
>
> **跨ぎが「DATA 経路」（C# press → Python RPC → 実 engine → 実データ → 状態更新）のときは、engine 側ゲートを純 pytest 単体ではなく「実バックエンド RPC を端から端まで叩く Python e2e」にするのが最短で最強**（#95 Phase 4 実例 2026-06-20）。Unity AFK runner を新規に起こして実 backtest を駆動すると 1 走 2–3 分 ＋ 反復デバッグで重いが、**`DataEngineBackend.run_cell(source, index, scenario_json)` を合成 DuckDB ＋ 実 marimo で直接呼ぶ Python e2e**（`test_notebook_replay_afk.py` 同型）なら、bt 構築→注入→`bt.replay()` 駆動→observer→`last_portfolio`→summary の **DATA 半分を決定論的に・秒で**固定できる（pacing は実 wallclock 差、cross-thread stop は worker thread ＋ `force_stop` で実証可能）。Unity AFK は **Python-FREE な control-logic fake**（scenario hand-off・running guard・▶↔■ トグルだけ）に絞る＝「Unity でしか証明できないこと」だけを Unity に残す。**marimo thread-local RuntimeContext は per-test で同一スレッド close が要る**（複数 backend を立てると "RuntimeContext already initialized"）。
>
> **AFK の AC 文言そのものが後発 finding に supersede されていないか、probe を書く前に binding と突き合わせる**（#95 Phase 4 実例: issue 本文 AC「速度変更が効く（mid-run 変更）」は F6 で「速度は開始時キャプチャ・走行中変更不可」に確定済み＝実現不能なので、AFK は「`bars_per_second` 違いで wallclock が変わる」へ**読み替えて** pin した）。issue 本文の AFK AC を額面で probe 化すると、設計が却下した挙動をテストしようとして詰む。[[grill-with-docs]] の「ADR×findings 突合」を AFK AC にも適用する。

## 二層 E2E と台本規約（ADR-0015）

E2E は `Assets/Tests/E2E/Editor/*E2ERunner.{md,cs}` に置く。`.md`＝台本（仕様・観測点・合格条件の正本）、`.cs`＝
自動判定。共通規約は [`Assets/Tests/E2E/Editor/E2E-CONVENTIONS.md`](../../../Assets/Tests/E2E/Editor/E2E-CONVENTIONS.md)、
全台本のロールアップ網羅台帳は `E2E-INDEX.md`。

- **Surface E2E** … 1 画面部品でユーザーができる操作を網羅（入力・状態遷移・host 呼び出しまで）。
- **Journey E2E** … 複数サーフェスをまたぐ実ユーザーストーリー（横断データ伝播）。例 `ReplayToHakoniwaE2ERunner`。
- 各台本は**操作一覧表**（`Action ID(<Surface>-<NN>) / 行動 / 入口(file:line) / 観測点 / 自動判定 / カバー状態 / 既存Probe`）を持つ。
- **カバー状態 5値**: `自動(E2E済)` / `自動(E2E済・<別Runner>)` / `自動(Probe有・要昇格)` / `要新規自動化` / `HITL専用`（理由併記） / `対象外`（理由併記）。
- `Probe`（`Assets/Editor/*Probe.cs`）＝探索・使い捨て。回帰ゲート化したら `E2ERunner` へ昇格。

## ワークフロー（backcast）

1. **挙動を 1 文の不変条件に言い換える**。「何を観測すれば『動いた』と言えるか」を resource/フィールド/ファイル/
   ログ行まで落とす。曖昧なら owner に確認。
2. **既存カバレッジを棚卸し**。`Assets/Tests/E2E/Editor/*.md` の操作一覧表と `Assets/Editor/*Probe.cs`・
   `python/tests/` を当たり、未カバーだけを新規にする。「未カバー」と断ずる前に既存 Probe の section を読む。
3. **台本（.md）に Action 行を足す/更新**。カバー状態を 5値で付け、HITL/対象外 は理由併記。
4. **gate を著す**: C#/Unity は AFK probe（`<Surface>E2ERunner.cs`、`Probe`→`E2ERunner` 昇格）、Python seam は
   pytest、純 engine は golden。**RED→GREEN**（壊れた状態で RED を確認 → fix → GREEN）を `docs/findings/NNNN` に記録。
5. **AFK で実走確認**（下記の罠に注意）。GREEN・exit 0・`error CS\d+` 0 件を確かめる。
6. **`E2E-INDEX.md` のロールアップに登録/更新**。新規 runner は対応する表（Surface / Journey / **Issue release-gate slice
   runners**）へ 1 行足し、件数（行数・自動(E2E済)・要新規自動化・HITL）を台本と整合させる。⚠️ **これは台本(.md) 更新とは別作業で漏れやすい**
   （実例 #89 2026-06-20: `QuitConfirmE2ERunner` を著して AFK GREEN まで通したのに INDEX 登録が抜け、後続スライスで初めて発覚）。
   slice runner（特定 issue の release-gate を細く gate するもの）は Surface/Journey 表でなく「Issue release-gate slice runners」表に置く。
7. 完了したら CLAUDE.md 規約に従い `simplify` と `post-impl-skill-update` を併発。

## AFK probe の実走確認（罠 4 点・memory `unity-afk-probe-run` が正本）

- **recompile-skip**: `.cs` 編集直後の初回 `-executeMethod` はコンパイルで終わり実行されない → **2 回目**で走る。
- **flush race**: 通知直後の grep は 0 件に見える → shutdown sentinel（`Found no leaked weakptrs` 等）を待ってから再 grep。
- **lock-abort**: 次の Unity 起動前に GUI Editor が起動していないか確認。**起動中（`Temp/UnityLockfile` 有 ＝ macOS `pgrep -lf "Unity.app/Contents/MacOS/Unity"` が hit）は batchmode が lock-abort で衝突**（`Aborting batchmode: another Unity instance`）。owner の Editor は勝手に kill せず閉じてもらう（AskUserQuestion）。
- **logFile 相対パス無視（macOS 実例 2026-06-21 / #100）**: `-logFile Temp/foo.log` のような**相対パスは macOS Unity batchmode で honor されず**、既定 `~/Library/Logs/Unity/Editor.log` に出て「No such file」で空振りする → **必ず絶対パス**（`-logFile /tmp/foo.log` 等）を渡す。`>/dev/null 2>&1` リダイレクトは log 取得に影響しない（log は logFile 側）。
- 実走確認は **Bash `grep -a "<TAG>"`**（ripgrep の Grep ツールも `Select-String` も `→` 入り PASS 行を取りこぼす）。
- compile-only ゲート: `-batchmode -quit -projectPath <abs> -logFile <abs>`（`-executeMethod` 無し）で `error CS\d+` 0 件。**warning CS も 0 件**を目標（例: `FindFirstObjectByType` は CS0618 obsolete → `FindAnyObjectByType` を使う）。
- 実行: `<Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod <Name>.Run -logFile <abs>`。
  Unity は **macOS: `/Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity`** ／ Windows: `C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe`。

## 直列制約（memory `e2e-wave2-runner-promotion` が正本）

**runner 昇格は authoring も検証も「直列」**。`parallel-agent-dev` での並行は壊れる:
- 全 runner が `Assets/Tests/E2E/Editor/` の同一 compile 単位＝同時 .cs Write でレース。
- AFK は **Unity プロジェクトロックで同時 1 本のみ**（並走＝batchmode abort）。
- owner の並行 git 操作とも干渉。

→ `.md` 台本の authoring は並行可（前回 wave-1 で実証）。**`.cs` runner 昇格と AFK 検証は 1 本ずつ直列**。
primary probe のみ移送し secondary probe は据え置く。新規 section の負 assert は presence/liveness guard を先に置く（vacuous 回避）。

## carve-out — 「formal invoke の有無」ではなく「findings に RED→GREEN を記録したか」が判定軸

backcast の shippable slice / engine slice では、release-gate の正本は **AFK `*Probe.cs` の section 一覧 ＋ HITL
checklist ＋ findings の RED→GREEN**。これらは通常 `grill-with-docs` が設計段階で findings に確定するので、
**grill が findings に AFK probe section ＋ RED→GREEN を確定済みなら、別途 formal invoke は不要**（実例 #13 infinite
canvas・#24/#25 kernel・#23 Live demo の混在スライス＝bug-class fix の RED→GREEN を findings 0014 に記録）。
判定軸は「findings に RED→GREEN を記録したか」＝line 上の「本スキルの実体」を満たしているか。

ship しない throwaway spike（auto-bootstrap を検証後に戻す探索 spike）は**非適用**＝findings doc / owner playmode
目視が gate（実例 #8 viz-spike）。判定基準＝「ship する App 挙動が変わるか」。

## 過去の invoke 漏れ — 同型 miss と教訓（12+ 回の蓄積を圧縮）

**症状**: 成果物（RED→GREEN・findings・台本）は規約どおり揃うのに、**本スキルの formal invoke 自体を飛ばす**。
タイプ済み他スキルや grill の設計インタビューに注意が奪われるのが共通の根。**成果物の品質・設計済み度・新規性と
formal invoke は独立**。

**注意を奪う誘因（masking phrase）の実例**:
- `/grill-with-docs` 併発 → 設計インタビューに没入（#22/#35/#41/#59/#60/#66/#76/#81）。
- `/nautilus-trader` 等 domain skill 併発 → domain reference に注力（#25 round3/5/6）。修正対象が Nautilus-free でも独立に必須。
- `/parallel-agent-dev` 等を**明示タイプ** → タイプ済み skill が「やること全部」に見え、未タイプの gate-skill が落ちる（#「全行動を E2E 台本に」2026-06-19）。
- 「配線漏れを実装」＝defect なのに『実装』で feature に見える（#59）。
- 「issue #5(エピック) に着手」＝実作業は sub-issue、スコープ判定に気を取られる（#41）。
- 「stub 実装 / NotImplementedError 解消」系 enhancement（#35）。
- 純 UI-geometry/presence 修正で既存 probe が presence を assert していない（正当に HITL-only）とき（#81）。
- 純 Python parity pytest が `/tdd` の自然な成果物で「ただの unit test」に見える（#76 S6b-α）。
- feature でも AC が `behavior-to-e2e` 名指し / AFK probe / characterization / HITL を要求していれば起動命令（#26/#44/#60）。
- spec-only の波（台本 `.md` だけ・runner は次波）で「まだ実装前だから早い」に見える（射程は台本 authoring そのもの）。

**remedy（不変）**: 台本・E2E・網羅・挙動保証・RED の語が出たら、**他に何がタイプされていても設計/実装に入る前に
最初に formal invoke** し、「gate を著したか／findings に RED→GREEN・再走手順を記録したか／台本のカバー状態・
ID 採番」を checklist 確認する。✅ 実証済み成功例: #25 r5・#76 S1・#76 T6 follow-up・#76→hakoniwa 台本
（記録直後の次セッションで grill 前 formal invoke）＝remedy は機能する。以後この順序を既定とする。

## 完了基準（backcast）

- C#/Unity: 対応 AFK probe / `<Name>E2ERunner` が GREEN・exit 0・`error CS\d+` 0 件。台本の操作一覧表とカバー状態を更新。**新規 runner は `E2E-INDEX.md` のロールアップにも 1 行登録**（台本更新と別作業・漏れやすい）。
- Python: `uv run pytest`（または fake/stub 単体スクリプト）が GREEN。RED→GREEN を `docs/findings/NNNN` に記録。
- 観測が「ユーザーが語った挙動」の十分条件になっている（delete-the-production-logic litmus を通る＝production
  ロジックを消すと必ず落ちる。vacuous な負 assert を避ける）。
- 回帰の肝（既存不変条件）を新規テストで壊していない。
- HITL/対象外 は理由付きで台帳に残っている。

## 関連 memory / reference

- memory `unity-afk-probe-run` — AFK 実走の罠（recompile-skip / flush-race / lock-abort / grep -a）と実 Unity パス。
- memory `e2e-wave2-runner-promotion` — Probe→E2ERunner 昇格の型・直列制約・findings 記録・rename 規律。
- `Assets/Tests/E2E/Editor/E2E-CONVENTIONS.md` / `E2E-INDEX.md` — 台本規約と網羅台帳。
- [`references/ttwr-bevy-legacy.md`](./references/ttwr-bevy-legacy.md) — **TTWR(Bevy) 専用**メカニクス（FLOWS.md /
  `e2e_replay.rs` / Rust Harness / state seam → resource 早見表）。backcast では使わない。
