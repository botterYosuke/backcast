# findings 0075 — #95 Phase 6 設計の木（実行状態 UI / stale / block popup / rich output / title-bar Run sunset / #90 統合）

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化）。
Phase 1 設計の木: [findings 0070](./0070-notebook-equals-backtest-grill.md)。
Phase 2 土台: [findings 0071](./0071-notebook-foundation-per-cell-run.md)。
Phase 3 `bt` ハンドル: [findings 0072](./0072-issue95-phase3-bt-handle-kernelstepper.md)。
Phase 4 B2 `bt.replay()`: [findings 0073](./0073-issue95-phase4-b2-replay.md)。
Phase 5 B3 `bt.step()`: [findings 0074](./0074-issue95-phase5-bt-step-reset-idempotency.md)。

本 findings は **#95 Phase 6**（ADR-0016 D11 最終段）の `/grill-with-docs` セッション 2026-06-21 で確定した下位決定を、会話で消えないように固定する。ADR-0016 / ADR-0012 / ADR-0013 / ADR-0006 / ADR-0007 は immutable（書き戻さない＝自己保護条項）。実装事実は本 findings に固定し ADR を「方針」として参照する。

base ブランチ: `feat/#95-phase6`（`main` = `203827b`＝Phase 5 完了点を起点）。

---

## 0. Phase 6 が出荷するもの（ADR-0016 D11 / findings 0073 §2 / 0074 §5 の履行）

Phase 4/5 が前倒しした ▶→■ トグル＋running state の上に、Phase 6 は「実行状態 UI の完成・rich output・古い Run 表面の正式撤去」を載せる:

1. **per-cell idle / running / stale 表示**（marimo 本来の stale モデルに忠実）。
2. **block popup**: 常時 block ラベル撤去 → RUN クリック時だけ「`bt` is already running」等を通知ライン（`_menuBarView.ShowMessage`）に出す。
3. **rich output**: `mo.md` / 表 / 画像（matplotlib 含む）を mimetype 契約でちゃんと描画。
4. **title-bar Run / global ▶ Run の formal sunset**（ADR-0016 D2/D4）。`RunReadinessViewModel` 退役。
5. **#90 統合**: document identity（ファイル名＋未保存マーク）をメニューバー badge へ。
6. **Phase 5 持ち越し（findings 0074 §範囲外）**: pressed-cell-aware な `bt` 検出（mixed `replay`+`step` 損失 / コメント誤判定）＋ `scenario_json` cache key の正規化。

---

## 1. owner-locked 下位決定（P6-1 〜 P6-6）

### P6-1 — stale = marimo 本来の挙動に忠実（rebuild-on-change を退役）

**owner 決定（Q1・2026-06-21）: (A) 忠実再現**。「stale は単なる見た目の『編集済み』ではなく notebook の実行モデルそのもの」（owner）。

**問題**: Phase 2 `NotebookSession` は source 変化のたびに **kernel を rebuild（teardown→新規→全 cell cold-run）** する（findings 0071 P2-2）。これだと「stale（要再実行）」状態が存在しない（押せば全部新しくなる）＝marimo のユーザーモデルから外れる。

**採用**: `NotebookSession` を rebuild-on-change から **marimo Kernel 本来の差分更新**へ切り替える。owner が裏取りした seam（installed marimo `0.20.4` で実在を再確認済み）に乗る:

- `Kernel._maybe_register_cell(cell_id, code, stale)`（`marimo/_runtime/runtime.py:911`）= cell の差分登録。code 変化なら古い cell を graph から delete→新規登録し、**変化しなかった cell は再登録しない**（globals 保持）。
- `DirectedGraph.set_stale({ids})`（`_runtime/dataflow/graph.py:271`）= 編集 cell ＋ **依存下流** へ stale 伝播。
- `Runner.compute_cells_to_run({pressed}, ...)`（`_runtime/runner/cell_runner.py:181`）は **stale ancestor を必ず走らせ**（`:187` `cell.stale`）autorun descendants を含める。

帰結:

- 編集 cell ＋ 下流 cell が「要再実行（stale）」になり、**RUN を押すまで自動では再計算しない**。
- **副産物**: kernel を作り直さないので `bt` 注入・step cache が編集ごとに壊れない（Phase 2 が残した landmine「一打鍵で live state を破棄」が *構造的に解消*）。Phase 4/5 が backend-side cache + 再注入で回避していた問題が、incremental graph では自然消滅する。
- 純粋計算 cell の reactive cascade（findings 0070 F3 の allowed footgun）は同機構の上でそのまま成立。

> 却下: (B) 軽い「編集された印」だけ（依存解析せず内部は rebuild のまま）＝下流 stale を表現できず marimo らしさが中途半端（owner「Phase 6 の理想的な完成形とは噛み合わない」）。

**spike で実証（2026-06-21・`python/spike/phase6_stale_spike.py` PASS）**: rewrite 前に installed marimo `0.20.4` で 4 不変条件を実証 — ① `_maybe_register_cell(stale=True)` は cell を**実行せず** stale 登録（`get_stale()=={a,b,c}`・`"a" not in globals`）② bare Runner press（`roots={pressed}`・autorun）が **stale ancestor を引き込む**（c 単独 press は a/b を走らせず、b press が stale ancestor a を走らせる）③ 同 id 再登録（`a=1`→`a=5`）＋ `set_stale({a})` が**下流 b へ伝播** ④ 編集を跨いで **globals 持続**（独立 press の `c==10` が残る＝rebuild が消さない）。`[PHASE6-STALE PASS]`。→ P6-1 を binding に格上げ（rewrite で実装）。bare Runner は stale を自動 clear しないので **ran cell の `set_stale(False)` は host 側で行う**（spike で確認）。

### P6-2 — rich output = `{ mimetype, data }` 契約（文字列一本化の退役）

**owner 決定（Q2・2026-06-21）: Yes**。Python→C# の出力受け渡しを **「文字列1本」から `{ mimetype, data }`** に拡張し、C# で mimetype ごとに描画を分ける。

**Python 側**: `notebook_session._output_text`（現状 `try_format`→text/plain ＋ `repr` fallback）を、marimo `try_format(obj)` の **`FormattedOutput(mimetype, data)`**（`marimo/_output/formatting.py:213`）をそのまま運ぶ形へ。`mo.output` published / last-expr の両方を mimetype 付きで返す。`run_cell` の戻り `ran[]` の各要素が `{"index", "ok", "mimetype", "data"}`（旧 `"output"` は廃止 or `text/plain` の `data` に統合）。

**C# 側 renderer 振り分け**（owner 確定の per-mimetype 契約）:

| mimetype | 描画 |
|---|---|
| `text/plain` | 従来どおり Text |
| `text/markdown` | TextMeshPro rich text 相当へ変換（見出し・太字・斜体・コード・箇条書き） |
| `text/html`（`<table>`） | 簡易グリッド or 等幅整形テキスト。**一般 HTML は危険に広げず** tag 除去 / markdown-text fallback |
| `image/png` / `image/jpeg` | base64 data を `Texture2D` に復元して画像表示（matplotlib もここ） |
| 未対応 mimetype | `data` を安全に文字列 fallback ＋ mimetype 名を薄く出す（デバッグ可視） |

> 「marimo output の HTML 全部を Unity で再現」は別プロジェクト（owner）＝やらない。renderer 契約は **将来拡張可能な正しい形**で作り、`text/html` は table に絞る。

### P6-3 — matplotlib 同梱・altair/vl-convert 見送り

**owner 決定（Q3・2026-06-21）**。現状 `python/pyproject.toml`/`uv.lock` は `pandas==2.3.3` / `marimo==0.20.4` 解決済み、**matplotlib / altair / vl-convert は未同梱**。

- **描画の仕組み（P6-2）は追加依存ゼロで完成・検証**する: `mo.md`→Markdown / pandas DataFrame→`text/html <table>` / `mo.image`(bytes)→`image/png` / text で全 renderer 経路を網羅。
- **matplotlib は Phase 6 で同梱**（scope §3 名指し・標準・出力は `image/png` なので既存画像経路に乗る）。`pyproject.toml` dependencies へ追加＋`uv.lock` 更新（ADR-0012 §3 dep pin の範囲拡張・ADR-0014/0050 shippable ビルドの venv が太るのは理想形優先で受容）。
- **altair + vl-convert は見送り**: Unity に Vega/JS 対話 renderer が無く、`vl-convert` で PNG 化しても静止画＝matplotlib と価値重複。Rust バイナリ依存＋サイズ増の割が合わない。**本当の altair 対応は「Vega 表示をどう持つか」の別設計**＝将来 additive ADR。

> 「手を抜かない」の解釈（owner）: altair まで雑に積むことではなく、**mimetype renderer 契約を将来拡張可能な正しい形で完成させ、約束された matplotlib まで通すこと**。

### P6-4 — title-bar Run / global ▶ Run の formal sunset（ADR-0016 D2/D4 履行）

**撤去カスケード（コードで検証済み・安全）**:

- `StrategyEditorRunButton`（`Assets/Scripts/StrategyEditor/StrategyEditorRunButton.cs`・title-bar 旧 single Run）を撤去。`BackcastWorkspaceRoot` の Build（`:434-435`）/ Refresh（`:1035`）/ field（`:151`）も除去。
- `OnRun()`（`BackcastWorkspaceRoot:986-1023`）の唯一の caller はこのボタン → 撤去。
- `ScenarioStartupController.TryStartRun`（run-gate wrapper）の唯一の caller は `OnRun`（`:995`） → 撤去。
- `RunReadinessViewModel`（`Update():1032-1035` でこのボタン専用に Evaluate）→ 退役（wording 定数 NoStrategy/InvalidScenario/NotOwner の最後の consumer は OnRun/TryStartRun なので一緒に消える）。

**撤去しても壊れないことの裏取り（grill 中に確認）**:

- **scenario sidecar 永続化は survive**: 実書き手 `ScenarioStartupController.Commit()` は Save As 経路（`BackcastWorkspaceRoot:1814`）＋ startup panel からも呼ばれる＝`OnRun` 専有ではない。
- **per-cell bt は survive**: `BuildNotebookScenarioJson()`（`:661`）は **live in-memory `_scenario`**（`:663` `_scenario.Universe.Ids` / `.Params`）を読む＝disk commit にも `OnRun` にも依存しない。ADR-0016 D3「commit された config」= bt 文脈では press 時の live panel 状態（cache key = scenario_json）。

**block popup（D11 / 0073 §2 履行）**: 常時 block ラベルは title-bar Run 撤去で**自然消滅**。Phase 4 が最小通知に使った `_menuBarView.ShowMessage`（findings 0073 P4-4）を polished 化し、**per-cell RUN を押してブロックされた時だけ**出す。surface する reason:

- **`bt` is already running**（D3 running guard の in-flight reject）＝Phase 6 の主役。
- **not the Python owner**（`_isOwner` false）。
- **server not ready**（`_host.ServerReady` false）。

> heavy modal は作らない（handoff §3 = 通知ライン）。NoScenario は引き続き **cell output の guidance**（Phase 5 `NoScenarioBacktester`）で出る＝popup ではない（責務分離）。

### P6-5 — #90 統合 = メニューバー document-identity badge（責務分離）

**owner 決定（Q4・2026-06-21）: (1)**。ADR-0013「1 cell = 1 floating window」では **まとまった notebook タイトルバーが存在しない**ので、document identity をどれか 1 つの cell 窓（例 `region_001`）に背負わせるとモデルが歪む（削除で dormant にもなり得る）。責務分離:

- **メニューバー badge = notebook/document 単位の identity**: `Untitled` / `strategy.py`、dirty 時 `* strategy.py`。wrap mode 等の将来の document-level 状態もここへ寄せる。source = `MarimoNotebookDocument`（`CurrentPath` / `IsDirty` / `IsBound` / `WrapMode`）。
- **各 cell 窓 = cell 単位の execution state**: idle / running / stale（P6-1）。block popup は RUN click 時のみ。

#90（File→Open 後に basename 常時可視 / 編集で未保存 marker / File→New で untitled / Run disabled reason と矛盾しない）を本 Phase で満たし **close** する。

> 却下: (2) 採用 cell に出す（「代表 cell」という嘘を作る）／(3) 取り込まない（scope の「統合」指示と噛み合わない）。

### P6-6 — pressed-cell-aware な `bt` 検出（Phase 5 持ち越し A/B の解消）

**問題（findings 0074 §範囲外）**:
- (A) `bt.replay` cell と `bt.step` cell を 1 notebook に混ぜると、**source 全体への substring 判定**（`source.Contains("bt.replay")`）が `uses_replay=True` になり `_acquire_step_bt` を bypass → step persistence 喪失。さらに replay path の `force_stop_replay()` が共有 `_replay_stop_event` を set し、cache 済み step bt を brick する。
- (B) C# 側も `source.Contains("bt.replay")` がコメント（`// bt.replay`）に誤反応して running guard 誤発火。

**採用**: P6-1 の incremental graph で **押した cell が graph に個別登録される**ので、検出を **pressed cell 単位**へ昇格:

- **Python が source-of-truth**: 押した cell の marimo パース結果（AST / `CellImpl.refs`）で kind（pure / replay / step）を判定し、その cell だけを駆動。混在 notebook でも「押した cell が replay か step か」で正しく分岐。
- **run kind を result で返す**: `run_cell` の戻りに run kind（`"pure"|"replay"|"step"`）を含め、**C# は自前 substring 検出を廃止**して result の kind で ▶→■ / running guard を駆動。これで (B) のコメント誤判定も構造的に消える（C# はもう source を grep しない）。

> AST/refs ベースにすることで「文字列リテラル / コメント内の `bt.replay`」を誤検出しない。実装の正確な seam（marimo の per-cell parse をどう引くか）は実装着地に記録。

---

## 2. 実装サブ決定（owner 質問を使わず私が確定・明確なデフォルト）

- **stale の見た目**: cell 窓の title bar / RUN ボタン周辺に状態を出す。idle = 通常（▶ green）／running = ■ red（Phase 4 既存）／**stale = amber 系のバッジ or tint**（「要再実行」）。`SetRunButtonGlyph` を 2 状態（running bool）から 3 状態（idle/running/stale）へ拡張。
- **編集→stale の通知契機**: 毎キーストロークではなく **cell input の確定（onEndEdit/blur）or 軽い debounce** で「この cell を編集した」を Python に通知 → `_maybe_register_cell` + `set_stale` → **新 stale 集合を返す** → C# が該当窓を badge。stale は *RUN 時でなく編集時* に現れる必要があるため、register/restage 専用の軽量 RPC（or `run_cell` の register-only モード）を足す。
- **`scenario_json` cache key 正規化（持ち越し C）**: raw string ではなく **canonical 正規化**（Python 側 `json.loads`→`json.dumps(sort_keys=True, separators=...)`）した値を cache key にする＝JSON key ordering による same-scenario cache miss を排除。
- **RPC 形**: rich output（P6-2）と run kind（P6-6）と stale 集合（P6-1）を運ぶため `run_cell` の戻り dict を拡張し、編集通知用の register/restage を追加。境界（`inproc_server` / `backend_service`）は JSON string のまま（既存契約）。
- **offline import-purity 維持**: matplotlib / rich renderer 経路も lazy（`_backend_impl` の module-load で matplotlib/marimo を引かない＝`test_strategy_runtime_offline` GREEN）。

---

## 3. AFK / pytest ゲート設計（`behavior-to-e2e` 2026-06-21）

### 3a. Python ゲート（**landed・GREEN**・DATA 半分は秒で決定論的に固定＝skill「DATA 経路は実 RPC e2e」）

| gate | 観測（AC） | RED→GREEN litmus | 状態 |
|---|---|---|---|
| stale incremental（`test_notebook_stale.py` 6） | 編集 cell＋下流が stale 集合／無関係 cell は入らない／press で stale ancestor 実行／globals 持続／delete 伝播 | `_restage` の delete-only pop を全 pop に戻すと idempotent RED | ✅ |
| rich output（`test_notebook_rich_output.py` 8） | `mo.md`→`text/markdown`・df→`text/html` table・matplotlib→`image/png` data URL・plain→`text/html`・exc→`text/plain`・`mo.image`→`@file` html | `_maybe_matplotlib_png` を外すと figure が空 text/html で RED | ✅ |
| pressed-cell detection（`test_notebook_step_afk.py` mixed） | mixed `replay`+`step` で押した cell の kind が正しい／コメント `bt.replay` 非誤検出 | 検出を whole-source substring に戻すと mixed が `_step_bt is None` で RED | ✅ |
| cache key 正規化 | key 順違いの同 scenario が cache hit | `_normalize_scenario_key` を `str()` に戻すと別 bt で RED | ✅ |
| golden byte-identical / offline purity | Phase 1–5 不退行・matplotlib/rich 追加後も `_backend_impl` marimo-free | — | ✅（58 passed） |

### 3b. C# AFK ゲート（Python-FREE fake executor＝skill「跨ぎは fake で C# 配線・pytest で engine」）

fake executor を `{stale:[...], ran:[{index, mimetype, data, ok}]}` を返す形へ拡張（既存 `StrategyEditorNotebookE2ERunner` の Python-FREE パターン）。`StrategyEditorNotebookE2ERunner` に Section16–19 を追加:

| Section / Action | pin する挙動 | RED→GREEN litmus | カバー状態 |
|---|---|---|---|
| S16 / STRATEGY-27,28 **per-cell stale** | 編集通知で該当窓に stale badge（amber）／press で idle へ戻り stale クリア／下流窓も badge | stale routing を「常に窓0」へ collapse → 別窓 badge で RED ／ press 後 clear を消すと badge 残留 RED | 要新規自動化 |
| S17 / STRATEGY-29 **block popup** | bt running 中の第二 per-cell RUN → 通知ライン `bt is already running`／非ブロック press は通知無し（not-owner/server-not-ready も） | block guard を外すと通知無しで RED ／ 無条件通知にすると非ブロックで誤通知 RED | 要新規自動化 |
| S18 / STRATEGY-30,31 **document badge（#90）** | Open→basename 常時可視／編集→`* name.py`／Save→`*` 消える／New→`Untitled` | badge 更新を消すと Open 後 stale title で RED（#90 AC4 文言非矛盾も） | 要新規自動化 |
| S19 / STRATEGY-32,33 **rich output routing** | `image/png`→RawImage(Texture2D) active／`text/markdown`+`text/html`→TMP rich／未対応→text fallback | 全 mimetype を Text へ collapse → image で RawImage 非 active RED | 要新規自動化 |

### 3c. Sunset probe 裁定（削除スライス規律＝契約の移送か冗長カバーかを exact assertion で確認）

| probe | 契約 | 裁定 |
|---|---|---|
| **`RunButtonE2ERunner`（#63 / findings 0063）** | title-bar Run button presence ＋ `RunReadinessViewModel` readiness（RUN-01..06）＋ **cutover 負 invariant U4/U5**（footer に transport 無し・startup に Run 無し） | **retire**: runner は deleted（`StrategyEditorRunButton`/`_editorRunButton`/`OnRun` を直参照＝撤去後 compile 不能）。RUN-01..08 の Run-trigger/readiness 契約は per-cell RUN（S13/S14/S16）＋ `test_notebook_replay_afk` へ移送済。⚠️ **ただし U4（footer transport-retired）と U5（startup-no-Run）の負 invariant は他 runner にカバーが無い**（2026-06-21 #99 マージ衝突解決時に grep で確認＝当初「冗長カバー」記載は U1-3/RUN-01..08 のみ正しく、U4/U5 は gap）。**Slice 6 wrap-up で U4→`FooterModeE2ERunner`・U5→`ScenarioStartupE2ERunner`（#99 で startup は floating window 化＝`_windows.RectOf("startup")` で引く）へ re-home する**。#99（Hakoniwa docking）が U5 の lookup を incidental に更新していたが、retire 決定（D2/D4）が #99 の incidental modify を supersede（DU 衝突＝keep deleted） |
| **`AuthorToRunJourneyE2ERunner`（#66 / findings 0066）** | author→save→**commit→run** の縫い目（`scenario.TryStartRun` の Commit ＋ `OnRun→host.TryStartRun` 受理） | **migrate**: run-trigger を per-cell RUN へ。Commit 契約は startup panel `Commit()`（survive）＋ per-cell bt run（`test_notebook_replay_afk` が DATA を pin）。journey は「author cell＋scenario commit→per-cell RUN が run_cell を scenario_json 付きで呼ぶ」C# 配線に置換 |
| **`ReplayToHakoniwaE2ERunner`** | `host.TryStartRun` 直叩きの engine-run primitive＋逐次 render | **keep**: UI trigger ではなく engine 経路。Phase 4 GIL reconfirm も使用。無改変 |

> sunset の安全根拠は exact assertion まで読む（skill 規律）: `RunButtonE2ERunner` の RUN-01..06 は「button presence＋VM Reason 表」を assert、これは per-cell RUN section が窓ごとの RUN button presence（S13 `EnsureRunButton`）で冗長カバー。VM の readiness 表は「save 必須 batch run」固有で per-cell RUN（save 不要）では無意味＝移送先なし・撤去が正。

### 3d. AFK 実走規律（serial・recompile-skip・`grep -a`・lock 確認）

新規 section は `StrategyEditorNotebookE2ERunner` の単一 compile 単位なので **1 本ずつ直列**で著し AFK 実走（memory `e2e-wave2-runner-promotion`）。compile gate（`-batchmode -quit` ＋ bash `grep -a "error CS"`）→ AFK（`-executeMethod ….Run`・2 回目で実行・`Found no leaked weakptrs` 待ち）。`E2E-INDEX.md` 登録は台本更新と別作業（漏れ注意）。

---

## 4. Phase 6 done-gate

1. 既存 #24 / golden 系 green（Phase 1–5 不退行＝主 gate・ADR-0006）
2. stale incremental（P6-1）pytest GREEN（編集 stale 集合・globals 持続・RUN で stale ancestor 実行）
3. rich output `{mimetype,data}`（P6-2）pytest GREEN（全 mimetype 経路）
4. matplotlib 同梱（P6-3）＝`image/png` 出力 pytest GREEN・`pyproject`/`uv.lock` 更新
5. pressed-cell-aware 検出（P6-6）pytest GREEN（mixed 分岐・コメント非誤検出）
6. cache key 正規化（持ち越し C）pytest GREEN
7. offline import-purity 不変（matplotlib/rich 追加後も marimo-free）
8. title-bar Run / global ▶ Run / `RunReadinessViewModel` 撤去・C# compile 0 error・orphan 無し
9. block popup（P6-4）AFK GREEN（blocked click のみ通知）
10. per-cell idle/running/stale（P6-1）AFK GREEN
11. document-identity badge（P6-5・#90 AC）AFK GREEN
12. rich output 描画（P6-2）AFK GREEN（image/markdown/table）
13. CONTEXT.md glossary 加筆（stale モデル・rich output mimetype 契約・document identity badge・Run sunset 完了）
14. E2E-INDEX 更新（新 STRATEGY セクション）
15. findings 0075 landed（本 finding）
16. `code-review(simplify)` Medium+ 0（CLAUDE.md 必須・`/pair-relay` で潰す）＋ `post-impl-skill-update`
17. #90 を pointer comment で close（#95 Phase 6 に統合）

---

## 5. Phase 6 範囲外（将来 ADR / 別 issue に委譲）

- **altair / vega 対話チャートの描画**（vl-convert PNG 化含む）＝将来 additive ADR（「Vega 表示をどう持つか」の別設計・P6-3）。
- **cross-instrument 発注**（`bt.submit_market(qty, instrument="...")`）＝将来 additive ADR（findings 0073/0074 から継続委譲）。
- **step cell の「ボタンモード」専用 affordance**（reactive cascade からの除外）＝将来 ADR（owner が footgun を「allow」した決定との衝突・findings 0074 §5）。
- **一般 HTML の完全再現**（table 以外）＝やらない（P6-2・別プロジェクト）。

これらは ADR-0016 の方針下で各 Phase / 将来 ADR の findings に固定する（ADR は書き戻さない＝自己保護条項）。
