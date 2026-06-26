---
name: behavior-to-e2e
description: >-
  backcast（Unity C# + 埋め込み Python の取引アプリ・このリポジトリ）で、ユーザーが挙動を言葉で語ったとき
  ——「こう動いてほしい / こうなったら困る / この挙動を保証して / 目視せず自動で確認したい / 台本にしたい /
  リリース前に壊れてないか担保したい」——それを **E2E 回帰ゲート**に変換するスキル。backcast の E2E 正本は
  **AFK Unity batchmode probe（`-executeMethod <…>Probe.Run` / `<Surface>E2ERunner.cs`）＋ `docs/findings/NNNN-*.md`
  の RED→GREEN・再走手順**、純 Python は **pytest / golden gate**。著したテストは **Action-ID タグ**で
  `scripts/run-all-tests.ps1` の rollup に載せて検証する（runner は `[E2E <id> PASS]`・pytest は
  `@pytest.mark.scenario`・conftest が実 outcome を翻訳）——「rollup に載せて」「scenario タグ」「run-all-tests」
  「Action-ID で検証」も起動命令。**Bevy の `tests/e2e/FLOWS.md` /
  `tests/e2e_replay.rs` は backcast に無い**（あれは移植元 TTWR 専用＝`references/ttwr-bevy-legacy.md`）。
  必ず起動する場面: 「この挙動をテスト/ゲートにして」「ユーザーストーリ（台本）をテストに」「台本を書いて」
  「E2E で通したい」「N ステップを通しで確認」「目視の代替」「全行動を網羅したい / 網羅台帳」「テストでカバー
  されているか確認（カバレッジ照会）」「リプレイ/発注/ポートフォリオ/venue ログインの挙動を保証」「本番ログインテストを作成して」「ログインテストを作成して」「prod/verify ログインを E2E に」。
  **「issue #N の問題が本当に出るか確認して」「バグが（まだ）再現するか確認」「再現する回帰ガード（テスト）を作って／著して」「回帰テストで再現を確認」**（＝報告済みバグを *fix する前に* RED 回帰ゲートで再現実証する依頼）も起動命令——これは「issue がまだ残ってるか確認」（本文ワークフロー Step 4 の xfail-strict / AFK RED→GREEN）と同型で、本スキルの中核（#147 実例 2026-06-26: `/behavior-to-e2e` を明示タイプしていたから発火したが、スラッシュ無しの「問題が本当に出るか回帰ガードで確認」だけでも発火させる）。
  **E2E 監査ハンドオフ（「audit existing E2E coverage」「find at least one bug」「bug hunt」「RED bug
  discovery」「gap-test」「網羅 gap を埋める」「STRATEGY-XX を追加して」「Section21 を作って」**）も同じ
  起動命令——既存 AFK runner に未網羅の AC や潜在バグを足す依頼は本スキルの中核。
  **不具合修正（「issue #N を修正」「bug を直す」「配線漏れを実装」）・レビュー指摘修正（「review findings を
  fix」「Medium をつぶして」、**`/code-review`・`/simplify` フローの中で Medium bug を直し AFK probe を新規作成
  **または既存 section へ assertion を足す/probe を硬化する（premise・faithfulness guard・vacuous 潰し・no-collateral 追加）**
  する場合も含む——「新規 probe ではなく既存 RRT/STRATEGY 行に assert を 1 つ足すだけ」と矮小化して invoke を飛ばすのが
  最頻の死角（#138 slice2 review 実例 2026-06-25: RRT-08 へ CaptureLayout premise assert を追加しながら formal invoke を落とした）。レビュー文脈に注意が向き、本スキルの invoke と Action-ID タグ付けを最も忘れやすい**）・stub 実装（「NotImplementedError を解消」）・検証/クローズスライス（「発火する
  test で証明」）・spike の gate 判定**も、挙動が変わる/AC が AFK probe・characterization・HITL gate を要求するなら
  起動命令。**enum バリアント（VenueState / ExecutionMode 等）が AC に列挙されていたら全バリアント網羅**。
  **実装フロー中に自分で AFK probe / E2E gate を新規作成するときも本スキルを invoke する**——hand-written probe は
  **Action-ID タグ（`[E2E <id> PASS]`）/ `scripts/run-all-tests.ps1` rollup 統合を忘れがち**で、`[XXX PASS]` だけ
  出して rollup に載らない gate を量産しやすい（#ADR-0027 Miro theme 実例 2026-06-25: ThemeProbe/WindowChromeProbe/
  SettingsAppearanceProbe を findings に「behavior-to-e2e で固定」と書きながら手書きし、Action-ID タグ無しで
  rollup 非搭載になった）。**さらに、値や構造を変える前に「その値/窓/window を assert している既存 AFK probe」を grep し、
  *それが sibling slice で既に stale=RED になっていないか*を疑う**——broad probe を回帰で回すと、自分の変更ではなく
  過去スライス（例 #103 plane 分割・#126 window 退役・space 再スキン）由来の旧 assert で fail することがある（同実例:
  ThemeProbe が退役済み farm パレットを、BackcastWorkspaceProbe が `_windows`/`startup` を assert したまま RED だった）。
  ⚠️ **最重要の不変条件**: ユーザーが他スラッシュコマンド（`/grill-with-docs` `/parallel-agent-dev`
  `/nautilus-trader` `/tdd` `/simplify` `/plan` 等）を**明示タイプ**していても、台本・E2E・網羅・挙動保証・
  RED の語が出たら **設計インタビュー/実装に入る前に最初に本スキルを formal invoke** する。過去 13+ 回、
  タイプ済み他スキルや grill の設計に注意が奪われて invoke を飛ばし、成果物だけ後付けで揃える miss を繰り返した
  ——**成果物の品質・設計済み度・新規性と formal invoke は独立**（詳細と masking phrase の一覧は本体「過去の
  invoke 漏れ」節）。**オーケストレーター役（「あなたがオーケストレーターになって専門 Agent を spawn して
  レビュー」「test-coverage agent に E2E を担当させる」）でも同じ——専門 Agent に E2E/カバレッジ分析を spawn
  委譲しても orchestrator 自身の formal invoke 義務は消えない**（#133-135 review 実例 2026-06-25: ユーザーが
  `/behavior-to-e2e` を明示タイプ＋「テストはユーザーの行動をカバ―してるか」をレビュー基準に列挙したのに、
  test-coverage agent を spawn して TKTEARDOWN-03〔Action-ID + rollup + RED native crash 実証〕を正しく著しながら
  本スキルの formal invoke を飛ばした＝miss #13）。TTWR(Bevy) リポで E2E を触るときだけ `references/ttwr-bevy-legacy.md` を読む。
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

### Action-ID rollup レールへ載せる（機能＝行動＝テストの連結）

著した gate は、その挙動の **Action-ID（`<Surface>-<NN>` / 台帳行）を名乗るタグで rollup に合流させる**ことで初めて
「行動＝テスト」が機械可読に閉じる。レール（`scripts/E2ERollup.ps1` が集計）:

| gate の種類 | タグの出し方 | 検証コマンド |
|---|---|---|
| C# / Unity runner | 成功マイルストンで `Debug.Log("[E2E <id> PASS]")`（E2E-CONVENTIONS §5。失敗は手前で抜け surface の `[E2E <NAME> FAIL]` が立つ＝C# は PASS マイルストン） | `pwsh scripts/run-live-e2e.ps1 -Venue <kabu/tachibana>` / `-Method <X>.Run` |
| pytest（台帳の Action-ID を正本に持つもの） | `@pytest.mark.scenario("<id>")` を付与 → `python/tests/conftest.py` が**実 outcome から** `[E2E <id> PASS/FAIL/SKIP]` を自動出力（手動 print ではないのでバイパス不可・`--strict-markers` 登録） | `cd python && uv run pytest` |
| Unity + Python を 1 verdict に | 上記両タグを merged rollup へ | `pwsh scripts/run-all-tests.ps1`（`-Venue`/`-Method` で Unity も併走・`pytest exit≠0` を floor） |

**タグが verdict の正本**（一部 live runner は shutdown segfault `exit=139` でも `[E2E … PASS]` タグで判定）。
大多数の unit/contract pytest は従来どおり **findings ゲート**のままでよい——`scenario` を付けるのは**台帳の
Action-ID 行を正本として持つテストだけ**（台帳を薄めない）。

> ⚠️ **rollup の正規表現は Action-ID を *単一トークン*（`[A-Z0-9][A-Z0-9-]*` ＝空白なし）でしか拾わない**（`scripts/E2ERollup.ps1`）。
> ＝Journey/Surface の**人間向け verdict 行**（`[E2E ADD CHART LADDER JOURNEY PASS]` のように**空白入りの NAME**）は rollup に
> **載らない**（"no tags found" になる）。完了基準「Action-ID が rollup に PASS で現れる」を満たすには、その NAME タグ*とは別に*
> **per-Action-ID の単一トークンタグ**（`[E2E ADDLADDER-04 PASS]` 等）を到達マイルストンで吐くこと（E2E-CONVENTIONS §5）。
> 失敗時は手前で抜けるので当該 id タグは出ず、rollup は「id 不在＝そのステップ未到達」と読む（実例 2026-06-24 / findings 0094：
> NAME タグだけ吐いて rollup が空になり、per-id タグを足して `5 PASS` に解消）。

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
> **「live/実 venue のデータ系バグ（UI に X が出ない・更新しない）」は、ゲートを著す前に *throwaway 実 venue spike で「どの runtime 仮説が真か」を実機実測で確定し*、確定した実 backend payload を *committed fixture* に固めて、最後に「純 decode/render 半分」を AFK で gate する**（#129 kabu live chart 実例 2026-06-25 / findings 0108）。live データ系の「板は出るがチャートが空」型は、コード読解だけでは「env A vs B」「board vs trades 給餌」「sparse vs dead」「render vs decode」のどの層が原因か*決められない*——agent も往々に逆の結論を出す（実例: 1 体目「live は給餌される」vs 2 体目「trades 依存で空」）。手順: (1) **既存 production adapter を直叩きする `python/spike/<venue>_*_probe.py` を書いて実 venue へ繋ぎ**、受信フレーム数・必須フィールドの有無/変化・downstream 充填（`per_id_ohlc_points` 等）を実測して仮説を一意に潰す（verify 18081 は market-data 無送信＝[[kabu-verify-no-marketdata-push]]、prod 18080 は board live＋約定が来れば充填、を実測で確定）。spike は本番配線（`LiveRunner`＋`LiveReducerBridge`＋`DataEngine`）をミラーし `get_state_json` の該当分岐まで忠実再現すると「C# が読む JSON」まで端から端で取れる。(2) **その実 JSON を `Assets/Tests/E2E/Editor/Fixtures/*.json` に committed fixture 化**（実データは合成より強い——例: 実 capture には top-level と per_instrument の両方に `ohlc_points` があり「locator が正しい方を拾うか」を試せる＝合成では作り込めない非空虚性）。(3) **AFK は「state JSON → decoder → view → 描画」の純 C# 半分だけ**を gate（実 venue 購読＝DATA 半分は HITL/KABU-LIVE 系に残す）。非空虚 floor は「不在 id → 0」「空 series（板あり）→ 0」で「描画が出る ⇔ 当該系列が非空」を両側 pin＝報告症状（板満杯・チャート空）が *空 trade series* であることを決定論で固定できる。spike は使い捨て（regression gate ではない・回帰の正本は fixture+AFK と spike の実証記述を findings に）。**market-time 依存の実測は [[git-bash-tz-asia-tokyo-double-shift]] に注意**（Git Bash の `TZ='Asia/Tokyo'` は二重変換で時刻を誤らせる→ザラ場判定を誤る）。
>
> **2 ゲート分割の盲点＝「Python-FREE C# ゲートも C#-FREE pytest も両方 GREEN なのに実機だけ落ちる」とき、バグは両ゲートの *継ぎ目そのもの*（pythonnet のスレッド境界・GIL・marimo thread-local context）にいる**（#102 console 実例 2026-06-21）。分割した瞬間、**どちらのゲートも跨がない seam が生まれる**——C# 側は fake executor で marimo を踏まず、pytest 側は純 CPython で「build と run が必ず同一 OS スレッド」だから、**marimo の `RuntimeContext`（`_ThreadLocalContext(threading.local)`）が build スレッドと run スレッドで食い違う**条件を**どちらも再現しない**。実機（pythonnet+`asyncio.run` レーン）だけが build≠run スレッドを踏んで `cell_runner.run_all()` 冒頭の `get_context()` が `ContextNotInitializedError`＝press 全体が無言で失敗（console も rich も出ない・フッターに error 文字列だけ）。**診断の入口は Unity ログの実トレースバック**（`logging.exception` 出力を **bash `grep -a "Traceback"`** で拾う）——「両ゲート緑」を真に受けず実機ログを最初に見る。**この種のバグのゲートは Unity AFK 不要**: seam の条件（build-on-thread-A → run-on-thread-B）を**純 Python で決定論的に再現**できる（`s._ensure_host()` を thread A で join → `s.run_pressed(...)` を thread B で）＝既存 pytest が「同一スレッドだから緑」になる罠を、テスト側でスレッドを割って破る。RED→GREEN litmus はその cross-thread テストで立つ（fix の context 再アサートを消すと即 `ContextNotInitializedError`）。直し方は marimo 公式の `RuntimeContext.install()`（save/install/restore の re-entrant ガード・AppKernelRunner が使用）で run スコープに context を pin＝手書き initialize/teardown より堅牢で bandaid でない（findings 0080）。**さらに肝＝seam を触る操作を *全列挙* してゲートしろ**: #102 は context を要する箇所が 2 つ（`_run` の `run_all` ＋ `_restage` のセル再登録）あり、最初の fix は `_run` だけ包んで「初回 press は GREEN・*編集して再 press* すると `_restage` で RED」を取りこぼした（実機 owner が「1 度目成功・2 度目失敗」で再発見）。**「happy path 1 回」のゲートでは足りない**——状態を変える 2 周目（編集→再実行・別セル・reactive 下流）まで回す。しかもこの 2nd-site 例外は `run_pressed` が内部 try で捕捉して error dict 化＝`logging.exception` に届かず Unity ログに traceback すら出ない（フッターの error 文字列だけが手掛かり）ので、**「ログに traceback が無い ＝ 例外が内部捕捉された別経路」と読む**。**さらに cross-thread ゲートは *単一スレッド* で書け**: 「build を別スレッド・run を別スレッド」で再現する版は、ephemeral スレッドに marimo の per-thread RuntimeContext が**残って leak**し（context を建てたスレッドで `teardown_context`/`close` しない）、スレッドスケジューリングの非決定性 ＋ 後続の marimo テスト（v19 等）の**間欠失敗（"RuntimeContext already initialized" / flaky）**を招く。代わりに **`_ensure_host()` で建てた直後に `teardown_context()` を呼んで「run スレッドに context が無い」状態を*同一スレッドで*作る**——決定的・leak 無しで同じ RED→GREEN が立つ（#102 で leak 版を書いて full-suite を間欠 RED にし、単一スレッド版へ書き換えて解消）。
>
> **marimo cell を *worker thread で走らせる* live/orchestrator 系 pytest は「run を stop しないと孤児 marimo session が pytest の FdCapture を壊す」**（#112 LiveCellBridge 実例 2026-06-23）。marimo cell を live 戦略として駆動する bridge（worker thread で `IncrementalNotebookSession.run_pressed`）を `LiveLoopManager.register_live_strategy`/`start_live_strategy` 経由で起こす pytest は、テスト末尾で **run を明示 stop（`stop_live_strategy` → detach → worker join → `session.close()`＝`teardown_context()` を worker スレッドで）しないと、worker が cell loop に居座ったまま session が孤児化**し、その per-thread RuntimeContext / stream redirect が pytest の **fd-level capture を破壊**＝**session teardown で `OSError: [Errno 9] Bad file descriptor`**（`_pytest/capture.py` の `readouterr`→`tmpfile.read`）。症状の罠: **各テスト単体では PASS、複数テストを同 process で回すと崩れる**（孤児 fd が session-global capture を汚すので、別テストの teardown に化けて出る＝FE… の連鎖）。診断: 単体 GREEN×複数 RED＋`Bad file descriptor` を見たら「marimo を起こす live テストが run を畳んでいない」を最初に疑う。rail 系（post-trade loss / strategy-error）は AccountSync/`fail_run` が detach するので idle cell でも自然に畳まれるが、**bar を流して発注させる「正常完了しない」テストは finally で `stop_live_strategy(run_id)` を必ず呼ぶ**（idle cell ＝ bt.replay でブロックは D5 の設計どおりで、stop の番兵だけが worker を抜けさせる）。**subprocess 隔離されたゲート（purity spike を `run_python([...])` で起こす型）はこの罠を踏まない**（fd が別 process）ので、in-process pytest と subprocess gate で同じ cell-live roundtrip を二重に持つと切り分けが速い。
>
> **「走行中スナップショットが stale / 再実行で前回値が残る」系バグは、既に「正しい」と確認済みの sibling poll フィールドを鏡映して直し、その鏡映先と同じ 2 段ゲートで担保する**（#100 slice① 実例 2026-06-21）。run_result tile が再実行中ずっと前回 full-stats を出す原因は **C# 側の set-at-return（同期 run が戻ってから set・run 開始時クリア無し）**だった。直し方の理想形は「新機構を足す」ではなく **engine の running-snapshot 兄弟（`last_portfolio`＝監査で『run 開始時クリア・atomic swap』が正しいと確認済み）を鏡映**＝`engine.last_run_summary` を①`on_run_begin` で `last_portfolio` と**同時にクリア**②`_finalize_run` で**同時にセット**③`get_run_summary_json`（honest-empty `""`）を **`get_portfolio_json` と同 GIL hold・同 Replay gate** で poll → `LatestRunSummary` → `RunSummaryJson`（`LatestPortfolioJson` と同型）。**ゲート分担も鏡映先と揃える**: engine lifecycle は **Python e2e で engine state を直接観測**（`next(bt.replay())` で `on_run_begin` を 1 step 発火させ `last_run_summary is None` を assert＝threads 不要・決定論的に RED→GREEN）、C# poll glue は **compile-gate（`error CS` 0）＋既存の `Test*JsonOverride` seam 経由の Surface/Journey runner（NBHAKO）**で tile rendering を担保（poll は実サーバ無しに AFK 化できないが override が同一プロパティを満たす）。**pure-compute は触らない契約は自動的に保たれる**（`on_run_begin` は bt 駆動 press でしか発火しない）。**poll モデル固有の ≤1 tick(50ms) 遅延は鏡映先と同一なので許容**＝それを 0 にする run-begin の Python→C# コールバックは非対称な特別扱い（altitude 上不可）。「set-at-return を poll-symmetric に」は stale-snapshot bug の既定リフレクション。
>
> **mode 連動の可視性トグル（窓を mode に応じて隠す/出す）を gate するとき、「live の toggle」だけでなく「その mode で *layout を save→再 boot* したときの self-heal」まで回せ**（#138 slice2 run_result 実例 2026-06-25 / findings 0110 §7.3a）。窓が `CaptureLayout` に乗る（＝永続化される）なら、隠れている mode で layout を save すると `visible=false` が焼かれ、次 boot で `ApplyGeometry`(`SetActive(w.visible)`) が非表示で復元する。**hide 方式の正否はここで決まる**: (a) **capture に乗る窓**（order ticket / run_result）＝`DriveOrderTicket` 型の**絶対トグル**（毎フレ `SetActive(mode 状態)`）で boot self-heal する／(b) **capture 除外**かつ独立 hide 状態あり（strategy_editor の deletable cell＝dormant 殻）＝remembered-set（自分が隠した id だけ戻す）。取り違えると——run_result を slice1 の remembered-set で実装したら **HIGH brick**（LiveManual 中 save→boot で空の remembered-set が `ShowHidden` 無効化＝**永久非表示・復旧不能**・`code-review(simplify) high` が検出）。**鏡映する sibling は「同じ persistence 区分」のものを選ぶ**（front-plane の DriveStrategyEditor を安易に鏡映せず、CaptureLayout 区分が一致する DriveOrderTicket を鏡映）。gate は **「隠す mode で SetActive(false)（= ApplyGeometry の persisted-hidden 復元を模擬）→ 表示 mode で drive → 可視へ self-heal」を 1 section に**（RRT-08 同型・remembered-set へ戻すと RED）。「happy path 1 回」＝live の toggle だけでは、この persisted-hidden の死角を取りこぼす。
>
> **AFK の AC 文言そのものが後発 finding に supersede されていないか、probe を書く前に binding と突き合わせる**（#95 Phase 4 実例: issue 本文 AC「速度変更が効く（mid-run 変更）」は F6 で「速度は開始時キャプチャ・走行中変更不可」に確定済み＝実現不能なので、AFK は「`bars_per_second` 違いで wallclock が変わる」へ**読み替えて** pin した）。issue 本文の AFK AC を額面で probe 化すると、設計が却下した挙動をテストしようとして詰む。[[grill-with-docs]] の「ADR×findings 突合」を AFK AC にも適用する。
>
> **「もう close 済みの issue に E2E を足して」依頼 = まず棚卸し → 退役した縦串ゲートの再縫合を疑え**（#95 E2E 仕上げ実例 2026-06-21）。全 Phase 出荷済みの issue でも、中心命題の **縦串 Journey ゲートが並行 refactor に巻き込まれて退役**していることがある（#95 の `ReplayToHakoniwa` が #99/ADR-0017 の Hakoniwa 作り直しで削除）。サインは **Surface 台本 `.md` に残る `RETIRED` runner への dangling 参照**。このとき真の穴は「Surface(fake)＋Python(engine) の 2 半分に分解されたまま **実 root 上で縫い合わせる C# Journey が空席**」＝それを新モデルで再縫合するのが理想形。**実 Python で end-to-end し直すな**（退役 runner の合成 DuckDB＋実 kernel 方式は 2–3 分／flaky）——engine 半分は既存 Python e2e が正本、新規価値は「実 wiring が両半分を縫う」縦串だけなので **Python-FREE で速く決定論的に** gate する。
>
> **実 root を Python-FREE で「press→制御」「走行スナップショット→tile」両半分とも駆動する 2 つの seam**（#95 縦串の実装レシピ）: ① press 側は root の本物の private callback（`BuildNotebookScenarioJson`/`SetCellRunButtonState`/`host.ForceStop`/`ViewFor`/`SetCellStaleRegions`）を **`Delegate.CreateDelegate` で bind し直した `NotebookRunController` を、executor だけ fake な同期レーン**（`startWorker:false`）で駆動＝wiring identity は production と同一・**rename は `GetMethod` null-check で "renamed?" として検出**（silent drift しない・Surface S13/14/15 が controller を自前で組むのと同型だが「root の実 callback に bind」する分より忠実）。② tile 側は **`WorkspaceEngineHost.TestPortfolioJsonOverride`/`TestRunSummaryJsonOverride`（#65 seam）に合成スナップショットを注入 → `_lastLiveShape=false`（Replay shape）で実 `RefreshLiveTiles()`→`PushReplayTiles()` を pump**＝override は実 poll lane と同一プロパティを満たすので `DecodePortfolio→FormatReplay*→ShowText` の鎖は 100% production。**逐次更新の非空虚化は連続スナップショットを「異なる string」で**（payload-change gate を通す・tile text が前 snapshot と変わることまで assert）。binding 前に `coordinator.New()` で cell0→region_001 を bind（`ResumeLastDocumentOrDefault` を呼ばない代替・`_cellRunButtons` の実ボタンは `BuildWorkspace` の `WireCellRunButton` 由来を reuse）。

> **edit-mode の `UnityEngine.Object.Destroy` は遅延（次フレーム）するので、「SetX→子を破棄して再構築」系の
> vacuity 逆コントロールを *破棄済み GameObject の数* で assert すると同フレームでは消えず偽 RED になる**（#34
> 実例 2026-06-24）。production が `Destroy`（runtime で正しい）を使う以上、test 側で `DestroyImmediate` に
> すり替えるのは production を変えるので不可。代わりに **同期更新されるフィールド**（件数ヘッダ・controller の
> List.Count・SoT 値）を assert する。実例: `OrderTicketView.SetRestingOrders([])` 後に行の [訂正] ボタン数 0 を
> 期待したら Destroy 遅延で `ModifyOrderE2ERunner` SectionB が RED → 同期更新の見出し `resting: 0` で GREEN。
> 併せて uGUI overlay の `GetComponent<T>() ?? AddComponent<T>()` は Unity の overloaded `==`（fake-null）を
> `??` が無視して AddComponent を飛ばし、後段の `.enabled` 等で `MissingComponentException`（"no T attached but
> a script is trying to access it"）になる——必ず明示 `== null` で分岐する（`SecretModalOverlay` 同型・#34 SectionC RED）。

> **実キーストローク挙動（InputField で Return=改行・Esc=取消 等）は headless 不可だが、EventSystem の handler 入口を
> *production の field に直呼び* すれば C# 決定を決定論で gate できる**（#148 Strategy Editor「Return で改行できず編集が
> 終了」実例 2026-06-26 / findings 0116）。`-batchmode -nographics` には IMGUI key pump も実フォーカスも無いので
> 「実 Return を押して改行が見える」は HITL。だが多くの key バグの真因は **新 Input System（`InputSystemUIInputModule`）が
> `ISubmitHandler.OnSubmit` / `ICancelHandler.OnCancel` / `IPointer*Handler` を dispatch する経路** にあり、これは
> `((ISubmitHandler)field).OnSubmit(new BaseEventData(es))` のように**実 builder 産 component の handler を直接 invoke** して
> assert できる（real keyboard 不要）。実例の真因＝`TMP_InputField.OnSubmit`（com.unity.ugui 2.0.0:4501）が
> **MultiLineNewline でも無条件 `DeactivateInputField()`**＝Enter で multiline code editor が blur（改行自体は
> `OnUpdateSelected` の key pump が別途挿入するが focus を奪われ編集終了）。fix は subclass の `OnSubmit` override で
> multiline のとき submit を消費。**gate の作り方**: (1) production builder の config 不変条件（`lineType==MultiLineNewline`）を
> assert（flip→RED）、(2) `field.OnSubmit(evt)` を実呼びして**消費したか**を test-observable counter（`SubmitConsumedCount`・
> `StrategyEditorView.CurrentOutput` と同型の "probe observability" field）で assert（override 撤去→base 無条件 deactivate→
> count 0→RED）、(3) **負コントロール**で single-line は消費 0（消費が lineType-gated・blanket swallow でないことを pin＝
> `if(true)` 化すると single-line が消費して RED）。real keystroke→可視改行は既存 HITL 行（"… 直接タイプ/IME"）へ
> cross-ref。**症状で真因 class を当てるな**——owner に「Return 以外は打てるか／Return で何が起きるか（無反応 vs blur vs
> 不可視改行）」を AskUserQuestion で 1 問聞けば focus-leave＝Submit-deactivate・無反応＝key pump 死・不可視＝wrap/layout に
> 一意に切れる（静的調査で config が正しく見えても実機ランタイム seam が原因のことがある）。
>
> **そして一つの EventSystem handler seam を直したら、*同じ component の兄弟 handler を全部 audit しろ*——同型バグが
> 必ず隣にいる**（#148 検証中に Escape を発見した実例 2026-06-26 / findings 0117）。`TMP_InputField` は
> `ISubmitHandler.OnSubmit`（Enter＝#148）だけでなく `ICancelHandler.OnCancel`（Escape）・`IMoveHandler.OnMove`
> （Tab/矢印）も実装し、InputSystemUIInputModule の既定 actions は Submit=Enter・**Cancel=Escape**・Move=Tab/矢印を
> 全部 dispatch する。Submit を override で直しても **Cancel は別 handler なので無傷で残る**——`OnCancel` は
> `m_WasCanceled`＋`DeactivateInputField`→`text=m_OriginalText`（restoreOriginalTextOnEscape 既定 true）で
> **Escape が focus 後の編集を全部 revert＋blur**（Enter blur より重いサイレントなデータ消失）。owner に
> 「ソースコードエディタとして致命的な不具合が他にないか」と聞かれたら、まず *修正済み seam の兄弟 handler*
> （`OnCancel`/`OnMove`）を package source で読んで blur/revert/navigate-away を確認する。fix とゲートは #148 の
> OnSubmit を**完全に鏡映**（`CancelConsumedCount` 可観測 field・multiline 限定 consume・single-line 負コントロール・
> Section27 を写した Section28・実 keystroke は HITL）。`OnMove` は base TMP が `if(!m_AllowInput) base.OnMove`
> で編集中は既に navigate を抑止しているので Tab は blur しない（＝兄弟 audit で「どれが穴でどれが既に塞がっているか」
> を見分ける）。memory [[eventsystem-handler-siblings-blur-tmp-inputfield]] が正本。
>
> **さらに #150（2026-06-26）: 一つのキーに *revert 経路が 2 つ* あり、handler interface を塞いでも足りないことがある——
> しかも残る経路が「headless で駆動できない seam」だと、gate は『直接叩ける述語 ＋ base 経路の忠実モデル』で非 vacuous 化する。**
> `InputSystemUIInputModule` は Escape を **(1) `ICancelHandler.OnCancel`（Cancel action）** と **(2) `OnUpdateSelected`→
> `KeyPressed`→`case KeyCode.Escape`（IMGUI key pump・`TMP_InputField.cs:2276`）** の**両方**に流す。`OnCancel` を override で
> 消費しても経路 2 が残り **HITL で「まだ消える」**（findings 0117 §HITL 続報）。経路 2 の fix は `OnUpdateSelected` を override して
> multiline 時に pump を自前所有し Escape を握り潰す（他キーは `protected` な `KeyPressed`/`SendOnSubmit`/`SelectAll`/
> `ForceLabelUpdate` で忠実に再 pump・private `m_IsCompositionActive`/`compositionLength` は触れないので OSX/IME 微枝は
> HITL に逃がす）。**ゲートの肝＝経路 1（OnCancel）は handler を直呼びできたが、経路 2（OnUpdateSelected）は global Event queue ＋
> focus を要し headless で駆動不能**。そこで (a) swallow 判定を `public bool TryConsumeKeyPumpEscape(Event)` に括り出して gate が
> 直接叩き、(b) **「swallow されなかった経路でだけ base の Escape 処理 `ProcessEvent(esc)`+`DeactivateInputField()` を実際に走らせて
> revert を起こす」非 vacuous モデル**で判定する（multiline=swallow→text 保持／single-line=非 swallow→base が実 revert＝既定維持
> ＋「base が本当に revert する」証明／非 Escape キー=非 swallow＝escape-gated）。focus 済み編集状態は reflection で TMP の
> activate 最小再現（`m_AllowInput`/`m_OriginalText`/`m_WasCanceled`）。delete-the-production-logic litmus は「swallow 枝撤去 → base
> モデルが revert → `edit was lost` で RED」（実機 AFK で RED→GREEN 実証済・STRATEGY-61/Section29）。**教訓: 修正 seam が
> headless 駆動不能なら、その seam の決定を直接叩ける述語に括り出し、その述語が *止めるはずの実 base 経路* をテスト内で
> モデル実行して「述語が止める ⟺ 損失が起きない」を両側 pin する**（さもないと「述語が true を返す」だけの vacuous gate になる）。

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
   - **REOPENED bug で reopen コメントに「実データ repro スニペット（実 symbol・実数値・観測出力）」が貼られているときは、合成 unit test を書く前に *その literal スニペットを実データで before/after 実行して再現*する**（#129 実例 2026-06-25：owner が `populate_replay_preview("8035.TSE","2025-01-06","2025-01-10","Minute") -> bars=1632 ; get_current_state().per_instrument keys: []` を貼った。projection ロジックを合成 DuckDB の pytest で RED→GREEN 実証して「直った」と報告しかけたら、owner に「そもそも #129 にコメントした問題を再現してみたのか？」と差し戻された）。この issue の教訓そのものが「proxy gate 緑＝実画面緑ではない」なので、**owner の正確な経路（実 root `S:\jp` 等・実 symbol・実 granularity）で `keys: []`→`keys: ['8035.TSE']` を自分の目で出す**のが第一級の検証。合成 pytest は決定論ゲートとして必須だが「再現したか」の答えにはならない。Windows で実データを直叩きするときは `BACKCAST_JQUANTS_DUCKDB_ROOT` を env で渡し、`PYTHONIOENCODING=utf-8`（print の `—`/`→` が cp932 で `UnicodeEncodeError` になるのを回避）。before は旧ロジックをインライン再現（file を壊さず手組み projection）して literal `[]` を出すと before/after が閉じる。
2. **既存カバレッジを棚卸し**。`Assets/Tests/E2E/Editor/*.md` の操作一覧表と `Assets/Editor/*Probe.cs`・
   `python/tests/` を当たり、未カバーだけを新規にする。「未カバー」と断ずる前に既存 Probe の section を読む。
3. **台本（.md）に Action 行を足す/更新**。カバー状態を 5値で付け、HITL/対象外 は理由併記。
   - **決定を反転/退役する slice では、その挙動を *参照する他 runner の .md 台本* と *PASS タグの要約文言* も棚卸しして flip する**。`.cs` の assert を反転しても、(a) 同じ挙動を spec正本として記述する別 runner の `.md`、(b) runner の最終 `Debug.Log("[E2E … PASS] …")` 要約文字列、(c) `E2E-INDEX.md` の issue/findings 参照——の 3 つは別ファイルで漏れやすく、放置すると **spec正本が gate と逆の旧挙動を語り続ける**（maintainer が .md を信じて反転を巻き戻すリスク）。実例 #113 2026-06-24: Open 層の自動 wrap 退役で `FileOpenNonMarimoE2ERunner.cs` を error 期待へ flip したが、`FileNavGuardE2ERunner.md`（FILEGUARD-06 行＋discardDirty seam 節）が #86 wrap/`WrapMode`/`discardDirty:true` を spec正本として記述したまま残り、`StrategyEditorNotebookE2ERunner` の PASS 要約も "non-marimo Open wraps as 1 cell" のままだった——いずれも code-review(simplify) が検出。grep 起点: 退役する型名/フラグ名（`WrapMode`/`discardDirty` 等）を **`.md` 込みで** 全 grep し、コメント・台本・要約も同時に直す。
     **さらに、退役するのが *共有 catalog の kind / enum value*（`FloatingWindowCatalog.KIND_*` / `OpenMenu.Venue` 等）なら、それを *他 runner の `.cs` フィクスチャ* が generic な stand-in に使っていないか必ず全 grep する**——「assert を反転」では済まず、**フィクスチャを生き残る kind へ移行して契約を保つ**のが正（削除すると回帰網が穴あく）。同時に **「catalog が全 kind を resolve する」系の assertion は『この kind を forward-compat で skip する』AC と *直接衝突* する**ので、退役 kind をその列挙から外す。実例 #126 2026-06-24（ADR-0026）: `KIND_STARTUP` を catalog `Default()` から外す（＝saved layout の `"startup"` を `TryGet=false` で skip させる唯一の手）と、`FloatingWindowE2ERunner` が startup を **core/dock の汎用 fixture** に使う 11 箇所が壊れ、`Section12a`（catalog が全 dock kind を resolve）が forward-compat-skip の AC と矛盾した。fixture を生き残る core（`KIND_RUN_RESULT`）/ plain dock（`KIND_BUYING_POWER`）へ移行し、`Section12a` から startup を除去、`Section32`（factory base group）を 5→4 に書換えて GROUP-14 を再固定。ADR-0019 §2「core kinds=FIXED」は *配置 supersession の帰結* として {run_result} に縮小（ADR は無改変・findings に帰結記録）。
4. **gate を著す**: C#/Unity は AFK probe（`<Surface>E2ERunner.cs`、`Probe`→`E2ERunner` 昇格）、Python seam は
   pytest、純 engine は golden。**RED→GREEN**（壊れた状態で RED を確認 → fix → GREEN）を `docs/findings/NNNN` に記録。
   - **「issue がまだ残ってるか確認して」＝fix 前に commit 可能な RED ゲートを `@pytest.mark.xfail(strict=True, reason="#N…")` で先に landする**（#58 実例 2026-06-24）。「バグがまだ生きてるか確認」依頼は、まず**本番経路を端から端まで再現する pytest** を著して xfail-strict を付ければ、(a) スイートを赤にせず commit でき・(b) 生きたバグの実行可能な記録になり・(c) **fix が入った瞬間に XPASS-strict で hard-fail**して xfail 撤去を強制する＝GREEN 化の取りこぼしを機械が捕まえる。fix が landしたら xfail を外して enforcing ゲートにする。修正方式が owner の設計判断待ち（skip vs carry-forward 等）のときも、ゲートは**方式非依存の不変条件**（「run が完走し 0 価格 candle が漏れない」等）で書けば両案を同時に担保できる。**loader/前処理段の fix で行数が変わると、実データ件数を額面 assert する既存テストが割れる**（#58 で `len(bars)==raw_4digit_count` が無取引日 drop で 4650→4645）——その期待 count 側にも同じ filter（`AND NOT (Open=0 AND …)`）を鏡映する。
   - **rollup レールに載せる**（上記「Action-ID rollup レールへ載せる」）: その挙動が台帳の Action-ID を持つなら、
     pytest は `@pytest.mark.scenario("<id>")`、runner は成功点で `[E2E <id> PASS]` を吐く。
5. **AFK で実走確認**（下記の罠に注意）。GREEN・exit 0・`error CS\d+` 0 件を確かめる。**起動は `scripts/run-live-e2e.ps1`**
   （`UNITY_EDITOR_PATH` で batchmode 起動・ログ `Temp/Unity_E2E.log` 固定・streaming・lockfile 自己修復・タグ verdict）。
   pytest と Unity を 1 コマンドで束ねるなら **`scripts/run-all-tests.ps1`**（merged rollup で per-Action-ID PASS/FAIL/SKIP）。
6. **`E2E-INDEX.md` のロールアップに登録/更新**。新規 runner は対応する表（Surface / Journey / **Issue release-gate slice
   runners**）へ 1 行足し、件数（行数・自動(E2E済)・要新規自動化・HITL）を台本と整合させる。⚠️ **これは台本(.md) 更新とは別作業で漏れやすい**
   （実例 #89 2026-06-20: `QuitConfirmE2ERunner` を著して AFK GREEN まで通したのに INDEX 登録が抜け、後続スライスで初めて発覚）。
   slice runner（特定 issue の release-gate を細く gate するもの）は Surface/Journey 表でなく「Issue release-gate slice runners」表に置く。
7. 完了したら CLAUDE.md 規約に従い `simplify` と `post-impl-skill-update` を併発。

## AFK probe の実走確認（罠 4 点・memory `unity-afk-probe-run` が正本）

- **recompile-skip**: `.cs` 編集直後の初回 `-executeMethod` はコンパイルで終わり実行されない → **2 回目**で走る。
- **async-import × `-quit` レース（headless TMP/asset セットアップ系・実例 2026-06-24 / #117 findings 0096 D2）**: `AssetDatabase.ImportPackage(path, interactive:false)`（TMP Essential Resources 取り込み等）は **interactive:false でも非同期**＝import worker がアセットを書く前に `-quit` が editor を落とすと、生成物がディスクに永続化されず（`Assets/TextMesh Pro/Resources/` が空・`TMP_Settings.instance` が null で後続 `CreateFontAsset` が NRE）。回避は **`-quit` を渡さず** `AssetDatabase.importPackageCompleted`／`importPackageFailed` コールバックを待ち、その中から `EditorApplication.Exit(0/1)` する（`try/catch`→`Exit(1)` の batchmode 失敗プロトコルも各経路に）。editor-method セットアップ（probe 実走でなくアセット生成）でも同じ recompile-skip の罠を踏むので、先に `-quit` 付き compile pass を1本通してから本実行すると安定。
- **TMP mesh は headless で生成されない → 合成 `TMP_TextInfo` を recolour へ直接食わせる（実例 2026-06-24 / #120 findings 0096）**: `-batchmode -nographics` では TMP_Text が canvas update ループに乗らず、`text.text=…` ＋ `text.ForceMeshUpdate()` を呼んでも `text.textInfo.characterCount==0`（mesh/textInfo が populate されない）。よって `textInfo.meshInfo[].colors32` を読む AFK assert は「TMP produced no characters」で空振りする。回避は **legacy が合成 `VertexHelper` を `ModifyMesh` に食わせていたのと同型**＝production の recolour メソッドを `public` にし（例 `PythonSyntaxMeshEffect.ApplyTokenColours(TMP_TextInfo)`）、手組みの `TMP_TextInfo`（`characterInfo[i].index`/`isVisible`/`materialReferenceIndex`/`vertexIndex` を埋め、`meshInfo[0].colors32` を base 色で pre-seed）を直接渡して書き込み結果を assert する。実 SDF レンダ経路（font/material・OnPreRenderText が発火し colors32 が upload されるか）は HITL（5× スクショ）に回す＝AFK は recolour の **write mapping** を決定論で固定、render は HITL の責務分割。なお `OnPreRenderText` で書いた `colors32` は `TextMeshProUGUI.GenerateTextMesh` が直後（`OnPreRenderText` invoke → `m_mesh.colors32=…` → `SetMesh`）に upload するので追加 `UpdateVertexData` は不要（package ソースで裏取り済み）。
- **flush race**: 通知直後の grep は 0 件に見える → shutdown sentinel（`Found no leaked weakptrs` 等）を待ってから再 grep。
- **lock-abort**: 次の Unity 起動前に GUI Editor が起動していないか確認。**起動中（`Temp/UnityLockfile` 有 ＝ macOS `pgrep -lf "Unity.app/Contents/MacOS/Unity"` が hit）は batchmode が lock-abort で衝突**（`Aborting batchmode: another Unity instance`）。owner の Editor は勝手に kill せず閉じてもらう（AskUserQuestion）。
- **logFile 相対パス無視（macOS 実例 2026-06-21 / #100）**: `-logFile Temp/foo.log` のような**相対パスは macOS Unity batchmode で honor されず**、既定 `~/Library/Logs/Unity/Editor.log` に出て「No such file」で空振りする → **必ず絶対パス**（`-logFile /tmp/foo.log` 等）を渡す。`>/dev/null 2>&1` リダイレクトは log 取得に影響しない（log は logFile 側）。
- **実 mount（NAS / 外付け）を読む runner は Bash サンドボックス無効で起動（実例 2026-06-23 / findings 0089）**: `BACKCAST_JQUANTS_DUCKDB_ROOT=/Volumes/StockData/jp` 等、`/Volumes` 配下の実マーケットデータを読む runner（`V19ReplayLiveE2ERunner` 等）は、**Bash デフォルトサンドボックスが `/Volumes` をマスクする**ため、サンドボックス下で Unity を起動すると子プロセスも `/Volumes` を継承できず load_replay_data が空振り→「約定ゼロ／SKIP」の偽結論になる。**`dangerouslyDisableSandbox: true` で起動**すること（マウント済みでも見えないのが罠。`ls /Volumes` がサンドボックス下で `MacintoshHD -> /` だけなら masked のサイン）。データ不在は SKIP=PASS にして偽 GREEN を作らない設計にし、SKIP が出たら mount 有無を疑う。memory `bash-sandbox-masks-volumes-nas` が正本。
- 実走確認は **Bash `grep -a "<TAG>"`**（ripgrep の Grep ツールも `Select-String` も `→` 入り PASS 行を取りこぼす）。
- **MOCK live 系 runner は exit 139（SIGSEGV）で終わるが verdict は PASS/FAIL タグで判定する**（#107 実例 2026-06-22）: `host.InitializePython("MOCK")`→`venue_login`→live session を立てる runner（`LiveSubscribeWiringE2ERunner` / `OrderTicketE2ERunner` SectionD 等）は `EditorApplication.Exit(0)` 後の pythonnet/Unity shutdown で segfault し **プロセス exit=139** を返す＝環境ノイズで**テスト結果ではない**。切り分けは同じ MOCK live 土台の既存 runner（`OrderTicketE2ERunner`）を走らせて 139+PASS を確認すれば「自分の回帰か環境ノイズか」が分かる。`[E2E … PASS]` タグが出ていれば GREEN。完了基準「exit 0」を額面適用して 139 を FAIL と誤断しない（pure-C# runner は Python を起こさず exit 0＝両者を混同しない）。
- **MOCK venue を full-stack「venue-free」ゲートの土台に使う**（#107 実例）: C# の本番配線（coordinator/hook 代入）と Python の購読/描画チェーンを *両方* 1 本の AFK で gate したいとき、`venue="MOCK"`（ADR-0021 の credential-less venue）＋`spike.live_adapter.mock_inject` の注入 helper で「実 `SelectRow`/mode 突入を駆動→実 RPC→`MockVenueAdapter`→depth poll→`DepthDecoder.HasDepth`」を通す。**非 vacuity の鍵＝`MockVenueAdapter` の emit は subscribe-gated**（`inject_tick` が `id in self._subscribed` を要求）なので「購読されていなければ板は出ない」＝depth が出た ⟺ 本番経路で購読された、が成立し未購読 id の負コントロールが false-pass しない。litmus（配線削除→RED）は coordinator の bulk を no-op 化して該当 section が RED になるのを実機で 1 回確認する。
- compile-only ゲート: `-batchmode -quit -projectPath <abs> -logFile <abs>`（`-executeMethod` 無し）で `error CS\d+` 0 件。**warning CS も 0 件**を目標（例: `FindFirstObjectByType` は CS0618 obsolete → `FindAnyObjectByType` を使う）。
- **新規 runner は sibling runner の `using` ブロックを丸ごと写してから書く**（#123 実例 2026-06-24）: `System.Linq` を落とすと `IReadOnlyList<string>.Contains` 等が `MemoryExtensions.Contains(ReadOnlySpan<char>,…)` に誤束縛され **CS7036 で初回 AFK を1往復無駄にする**（compile だけで終わる）。`.Contains`/`.Where`/`.Select`/`.First` を使うなら `using System.Linq;`、`IDictionary` 反射には `using System.Collections;` を最初から入れる。
- 実行: `<Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod <Name>.Run -logFile <abs>`。
  Unity は **macOS: `/Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity`** ／ Windows: `C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe`。
- **pre-existing-RED baseline（既存 runner を拡張するとき必須）**: 既存 `*E2ERunner` に新 section を足して走らせ、`??`
  チェーンが**自分の section の手前**で FAIL したら、まずそれを「自分の回帰」と決めつけない。`git stash push -u` で working
  tree を clean HEAD に戻して**同じ runner を走らせ、その section が HEAD でも RED か**を確認する（HEAD でも RED ＝
  pre-existing・自分の変更とは無関係。findings の GREEN 主張が誤っていた／Unity 版更新で挙動が変わった等）。pre-existing
  RED は `??` 短絡で**自分の新 section の実行自体をブロック**するので、理想形を狙うなら en passant で直す（test-only fix
  が多い）。#101 実例 2026-06-21: 新 section を足したら手前の S11（#99 snap wiring）が `applied offset (0,0) expected (5,0)`
  で FAIL。stash→HEAD でも同一 FAIL を再現＝#99 着地時から RED と確定。原因は **spec-min clamp**: test が
  `strategy_editor` を 200×100（minSize 280×180 未満）で spawn → `Spawn` が 280×180 へ clamp-up → 右辺がずれて
  neighbour と重なり snap 0。**「test の spawn サイズ < spec.minSize」は clamp で geometry が静かにずれる定番の偽 RED**
  ——snap/placement/overlap を assert する section は spawn サイズを minSize 以上にし、clamp 仮定を直接 guard する。
- **uncommitted live-render displacement（drag/live-render 系 section）**: 同一 controller で複数 sub-test を連結し、各 sub-test が
  **commit せず live-render だけ**する `DragApplyDelta`（プレビュー描画＝anchoredPosition を動かすが state は変えない）を呼ぶと、
  次に **明示 `BeginDrag` で rest を再 snapshot** した sub-test が「前 sub-test が動かした displaced 位置」を rest と誤認し、
  期待座標とズレて偽 RED になる（#136 実例 2026-06-25・S25c「picked window did not follow cursor」）。対処は sub-test 間に
  **`CancelDrag(id)` で live-render を rest へ revert**してから次の `BeginDrag` を呼ぶ（commit していないので state 復元は不要・
  geometry だけ戻す）。または sub-test ごとに fresh controller を組む。**code-review が見つけにくい**（GREEN になっても
  実バグでなく test harness の状態漏れ）ので、drag section は「live-render は state を変えない＝次 drag の rest は前 drag の
  displaced 位置」を最初から意識する。
- **構造的不変条件（sibling 順・layer 配線・serialized 参照）を「合成スタックを自前で組む root-free section」で assert すると
  tautology**（#103 実例 2026-06-21）。root-free runner は Viewport→Content→Layer の RectTransform を**自分で生成**するので、
  「DockLayer は背面 sibling」を自前スタックで assert しても test 自身の生成順を写すだけ＝production の scene-builder 出力を
  1 mm も gate しない（code-review が tautology と指摘）。対処は **実シーンを editmode で開いて構造を pin**＝
  `EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single)`（**PlayMode 不要・Python 不要**＝
  Awake/OnEnable は走らないので authored 階層だけ読める）→ `GetComponentsInChildren<RectTransform>(true)` で名前引き →
  `GetSiblingIndex()` の前後・`new SerializedObject(root).FindProperty("_field").objectReferenceValue` の指す先を assert。
  これは root-free runner の例外 section（最後に走らせ、`OpenScene(Single)` で前段の合成 GameObject が fake-null 化しても
  `finally` の `if (go != null)` が掃く）。**逆に snap/routing など controller のロジック不変条件は root-free のままで良い**
  ——その RED→GREEN は production 述語（例 `DockShape.IsDockKind`）を壊して取る（test の構成ではなく production を壊す litmus）。
  判定: 「assert 対象が *test が組んだ構造* に依存するなら実シーン editmode、*controller のロジック* に依存するなら合成スタック」。
- **1st-pass で stub を簡素化した結果 foreach が vacuous 化する盲点**（#140 実例 2026-06-25 / CHART-TITLE-05）: code-review
  altitude fix を受けて production resolver から Query を抜き TryGetName seam に切り替えた際、AFK runner の StubProvider も
  「Query は呼ばれないから常時 Loading を返す」と簡素化した。だが Section5 は元々「provider Kind 5 通り (Empty/NotConnected/
  Unsupported/Error/EndUnset) で各 fallback を assert」する設計で、簡素化後の foreach は `s.R` を使わず Names.Clear()+spawn
  を 5 回繰り返すだけの **vacuous loop** になっていた（5 iteration が functionally identical・section 名が示す verdict は
  unproven）。1st-pass の code-review は通したが 2nd-pass review が検出。教訓:
  - **production を簡素化したら、それを依存していた既存 AFK section の litmus 性も再確認**: 「state X で fallback Y」型の
    section は state の **観測可能な差異** を stub が advertise していなければ vacuous になる。Section5 の場合は Stub に
    `Next` field を追加して `Provider.Next = s.R` で各 iteration の Kind を実際に変えるのが正しい修正
    （production resolver が Kind を読まなくても、**将来 Query を再配線する regression を捕まえる gate** として有効になる）。
  - **vacuous detection litmus**: section 内の foreach/loop で「iteration を 1 回だけ走らせても同じ結果になるか」を自問する。
    YES なら vacuous（section 名が嘘）・NO なら genuinely per-row（OK）。
- **code-review finding 自体を AFK gate にする good practice**（#140 実例 2026-06-25 / CHART-TITLE-09）: review が指摘した
  「construction-order invariant は positional でなく structural にしろ」（`_provider` 構築順序を `_dockWindows` controller の
  前に移動）に対し、(a) 構築順を実際に移動・(b) defense-in-depth で resolver 先頭に `if (_provider == null) return specTitle;`
  guard を残し・(c) **AFK Section で `_provider = null` のまま spawn して spec.title fallback を assert** の 3 段で対応。
  これにより「将来 reorder で再び _provider が null のまま spawn しても、guard が NRE せず graceful degrade」を AFK で pin。
  教訓: review finding は fix だけで終わらせず、**「その finding が解いた invariant を将来も保つ AFK section」を 1 つ足す**と、
  同型の regression を構造的に塞げる。section 名は `<EXISTING-PREFIX>-NN` で既存台帳に合流（INDEX 件数・台本表・findings RED→GREEN 節も更新）。

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
- `/code-review`(simplify) が surface した coverage gap（「挙動 X が未テスト」）を `/pair-relay` の fix-loop で AFK runner に足すとき → 注意が「review 指摘の解消手続き」に奪われ、E2E case 追加＝本スキルの中核なのに formal invoke を飛ばす（#34 MODIFY-09b 警告行 visible 分岐, 2026-06-25）。

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
- **その挙動の Action-ID が `scripts/run-all-tests.ps1` の rollup に PASS で現れる**（行動→テストの連結が機械可読に
  閉じている）。Action-ID 行を持つ gate にタグ（`@pytest.mark.scenario` / `[E2E <id> PASS]`）が無いと未連結。

## 関連 memory / reference

- memory `unity-afk-probe-run` — AFK 実走の罠（recompile-skip / flush-race / lock-abort / grep -a）と実 Unity パス。
- memory `e2e-wave2-runner-promotion` — Probe→E2ERunner 昇格の型・直列制約・findings 記録・rename 規律。
- `Assets/Tests/E2E/Editor/E2E-CONVENTIONS.md`（§5 = per-Action-ID タグ規約）/ `E2E-INDEX.md` — 台本規約と網羅台帳。
- `scripts/run-live-e2e.ps1` / `scripts/run-all-tests.ps1` / `scripts/E2ERollup.ps1` — Action-ID rollup ランナー（Unity / merged / 共有集計）。
- `python/tests/conftest.py` — `@pytest.mark.scenario` を実 outcome から `[E2E <id> PASS/FAIL/SKIP]` に翻訳するフック。
- [`references/ttwr-bevy-legacy.md`](./references/ttwr-bevy-legacy.md) — **TTWR(Bevy) 専用**メカニクス（FLOWS.md /
  `e2e_replay.rs` / Rust Harness / state seam → resource 早見表）。backcast では使わない。
