# 0107 — login-dialog tkinter の cross-thread teardown（Tcl_AsyncDelete native クラッシュ）を断つ

- **報告日**: 2026-06-25（owner 目視・Unity プロセスごと落ちる native クラッシュ）
- **対象 issue**: #133（login-dialog tkinter teardown）/ #134（MPLBACKEND=Agg 回帰ゲート）/ #135（kabu 実機 HITL 確認）
- **区分**: bug（native クラッシュ）＋回帰ゲート化（`/behavior-to-e2e`）。companion: ADR-0027（#130/#131 で
  login ダイアログ widget を触った直近スライス）/ findings 0093（#122 in-proc tkinter login）/ findings 0103（kabu picker）。

## 症状

`Tcl_AsyncDelete: async handler deleted by the wrong thread` クラスの native クラッシュ。再現動線:
kabu ログイン成功（menu badge `Connected: KABU`）→ サイドバー [+ Add] picker を開く →
`PickerInstrumentFetch` 背景スレッドで `list_instruments("live","")` を走らせる → Unity が `Tcl_Panic` で落ちる。
`Editor.log` に `OUTPUTTING STACK TRACE` / `Tcl_Panic` / `Tcl_AsyncDelete`（tcl86t 起点）が出る。

## 根本原因（cross-thread Tcl teardown）

Tcl/Tk は **スレッドセーフでない**。`_tkinter.tkapp`（Tcl interpreter ラッパ）は生成スレッドで
async handler を `Tcl_AsyncCreate` する。interpreter が finalize されるとき `Tcl_AsyncDelete` は
「**生成スレッドと同一スレッド**で呼ばれること」をアサートし、違反すると `Tcl_Panic` でプロセスごと落とす。

venue ログインの in-proc ダイアログ（`run_dialog`：kabu/tachibana）と headless 判定 probe（`_try_create_tk`）は
専用ログインスレッドで `Tk()` を生成する。問題は **`root.destroy()` を呼んでも `tkapp` は即解放されない**点:

- tkinter の `Tk`／widget／`StringVar` は **参照サイクル**を作る（widget ⇄ コマンドクロージャ ⇄ root、
  `Variable` の trace 等）。サイクルは refcount では回収されず、**cyclic GC** を待つ。
- cyclic GC は「次に collection を起こした任意のスレッド」で発火する。ログイン後に
  `PickerInstrumentFetch` が GIL を保持して Python を実行している最中に GC が走ると、
  ダイアログ由来の `Tk`/`StringVar` サイクルが**別スレッドで finalize** され、`tkapp` dealloc →
  `Tcl_DeleteInterp` → `Tcl_AsyncDelete` が wrong thread で走り **panic**。
- 出荷済みの `MPLBACKEND=Agg`（matplotlib→TkAgg 遮断・PythonRuntimeLocator）は **この login-dialog 経由の
  tkinter には効かない**（別経路）ので、独立に閉じる必要がある。

### 実証（この finding の RED は実クラッシュで取れた）

修正（生成スレッドでの `gc.collect()`）を外した最小再現を専用スレッドで走らせ、メインスレッドで
`gc.collect()` すると、StringVar の `__del__`（"main thread is not in main loop"）に続いて
**`Tcl_AsyncDelete: async handler deleted by the wrong thread` が実際に発火**した（プロセス異常終了）。
＝バグの実在と「cross-thread finalize が vector」であることが経験的に確定。

## 修正（生成スレッドで明示破棄＋同一スレッド GC sweep）

「サイクルが在るか否か」ではなく「**どのスレッドで finalize するか**」が病巣なので、生成スレッド上で
interpreter を確実に finalize する。

- **`engine/exchanges/_login_dialog.py`**: 共有ヘルパ `teardown_tk(root)` を新設。**生成スレッドで
  `root.destroy()` する（idempotent。ダイアログの callback が既に destroy 済みのことが多い）だけ**にとどめる。
  > レビューで確定（simplify/test エージェント・CPython セマンティクス）: `teardown_tk` 内で `gc.collect()`
  > しても **渡された `root` は当の関数フレーム（および呼び出し側フレーム）のローカルとして生きている**ため、
  > `Tk`⇄widget⇄closure / `StringVar`-trace のサイクルはまだ reachable で回収できない。サイクルが
  > unreachable garbage になるのは **呼び出し側の tkinter 保持フレームが pop された後**なので、決定打の
  > 同一スレッド sweep は呼び出し側の post-frame collect に置く（下記）。`teardown_tk` の役割は destroy
  > のみに honest 化した（`update_idletasks` は全 callback が `quit` でなく `destroy` を呼ぶため実質 dead
  > だったので除去・もう `gc` を使わないので `import gc` も除去）。
- **`kabusapi_login_flow.py` / `tachibana_login_flow.py`**: `run_dialog` を薄い public wrapper と
  `_run_dialog_impl` に分割。**全 tkinter オブジェクトは `_run_dialog_impl` のフレームに閉じ込め**、
  impl が return した瞬間に root/widget/StringVar 参照が解放される。wrapper はその直後に `gc.collect()`
  を呼ぶ——これが **load-bearing な決定打**（フレームが pop 済みなのでサイクルがここで初めて回収され、
  生成スレッドでその場 finalize される）。result dict は plain string のみ＝tkinter を外へ漏らさない。
  impl は `try/finally: teardown_tk(root)` で mainloop 後も生成スレッドで destroy。
- **`engine/live/live_orchestrator.py`**:
  - `_try_create_tk` を「`Tk()` 成功 → `teardown_tk(root)`」に変更（probe の interpreter を生成スレッドで
    destroy）。probe サイクルは `_try_create_tk` のフレーム pop 後＝`_run` の `finally: gc.collect()` で
    生成スレッド回収される。headless では `Tk()` が interpreter を作る前に raise するので従来どおり `False`
    を返す（`NO_DISPLAY_AVAILABLE` 経路に回帰なし）。
  - `_run`（専用ログインスレッド）の `run_dialog` 呼び出しを `try/finally: gc.collect()` で包む。これは
    (a) probe root サイクルを生成スレッドで回収する load-bearing collect ＋ (b) ダイアログの daemon auth
    スレッドが run_dialog 後に最後の参照を落とす残渣も同スレッドで finalize する defense。
- **`Assets/Scripts/S1Spike/PythonRuntimeLocator.cs`**: `MPLBACKEND=Agg` 行に「DO NOT DELETE / 消すと
  background-thread Tcl_Panic が回帰・gate=MPLBACKEND-01」のガードコメントを追加（#134 AC#3）。

## ゲート（RED→GREEN・Action-ID）

### #133 — `python/tests/test_login_dialog_tk_teardown.py`
- **TKTEARDOWN-01** `test_dialog_tk_graph_is_finalized_on_the_creating_thread`:
  run_dialog が残す形（root + StringVar + コマンドクロージャ ＋ hard cycle）を**専用スレッド A** で生成し
  `teardown_tk` ＋ post-frame `gc.collect()`（run_dialog/`_run` の collect を鏡映）で畳む → **メインスレッド B**
  で `gc.collect()` しても finalize されない（`weakref.finalize` が記録した finalize スレッド == 生成スレッド A）。
  **litmus**: 末尾 `gc.collect()` を外すとサイクルがスレッド A を生き残り B で finalize → finalize スレッド == B で RED。
- **TKTEARDOWN-02** `test_try_create_tk_tears_down_its_probe_root`: `_try_create_tk` が probe root を
  `teardown_tk` に渡す（monkeypatch spy）。**litmus**: `_try_create_tk` から `teardown_tk(root)` を消すと spy 空で RED。
- **TKTEARDOWN-03** `test_real_run_dialog_finalizes_its_tk_on_the_creating_thread[kabu/tachibana]`
  （レビューで追加・**出荷した修正行を直接 gate**）: TKTEARDOWN-01 は手組みの analog ＋ test 自身の collect で
  畳むため **本物の `run_dialog` / wrapper の `gc.collect()` を一切踏まない**（＝出荷した修正行が無 gate だった）。
  本テストは **実 `run_dialog`（kabu/tachibana 両 venue）を pre-set `cancel_event` で headless-on-display 駆動**
  する: `_poll_cancel` が +200ms で event を見て `root.destroy()` → `mainloop()` が `LOGIN_TIMEOUT` で返る
  （本体・認証情報・auth スレッド不要）。`tkinter.Tk` を recording factory に monkeypatch して **生成された
  実 root を weakref で追跡**し、ワーカースレッド A で 3 回ループ後にメインスレッド B で `gc.collect()` →
  「どの root も B で finalize されない（finalize スレッド == A）」を assert。3 回ループは
  KABU-TCL-HITL-01 の「fetch 反復で落ちない」の AFK アナログ。
  **litmus（実証済み・極めて強力）**: wrapper の `gc.collect()` を消すと、最後の root がスレッド A を生き残り
  B の `gc.collect()` で finalize → **実際に `Tcl_AsyncDelete` native crash が発火**（`Windows fatal exception:
  code 0x80000003` during `Garbage-collecting` at the thread-B collect）。assertion FAIL ではなく **本物の
  プロセス異常終了で RED**＝バグの実在と修正行の load-bearing 性をテストが直接担保する（run-all-tests.ps1 は
  pytest exit≠0 で捕捉）。
- `test_try_create_tk_returns_false_without_a_display`: AC#3 no-regression（`Tk` を raise させ headless 模擬 → `False`）。
- isolation: ワーカースレッドで Tk を作るため autouse fixture で `tkinter._default_root` を各テスト前後に
  クリア（TKTEARDOWN-01 実行後に TKTEARDOWN-02 が稀に SKIP する観測の根治）。
- 実 Tcl display が要るので headless host では SKIP（中立）。owner Windows の `uv run pytest` では実走する。

### #134 — `python/tests/test_mplbackend_agg_gate.py`
- **MPLBACKEND-01** `test_mplbackend_agg_does_not_import_tkinter`: `MPLBACKEND=Agg` の subprocess で
  `import matplotlib` ＋ figure load 後に `sys.modules` に `tkinter`/`_tkinter` が入らず backend が `agg`。
- `test_litmus_tkagg_pulls_in_tkinter`: 非空虚性。`MPLBACKEND=TkAgg` にすると tkinter が import される（=Agg を
  外せば vector が復活する）ことを subprocess で確認（tkinter 不在環境のみ SKIP）。
  backend 解決は process-global なので各 assert は専用 subprocess で env を制御。

### #135 — kabu 実機 HITL（`KABU-TCL-HITL-01`・owner 専用）
kabuStation 本体 ＋ GUI が要るため AFK 不可（`MPLBACKEND=Agg` + #133 両方が入った状態での最終確認）。

**手順**:
1. アプリ起動 → kabu ログイン（menu badge が `Connected: KABU`）。
2. サイドバー [+ Add] picker を開く → instrument fetch（`PickerInstrumentFetch` 背景スレッドで `list_instruments`）を走らせる。
3. これを **複数回**踏む。

**合格条件**:
- [ ] [+ Add] picker fetch を複数回踏んでも Unity Editor がクラッシュしない。
- [ ] `Editor.log` に `Tcl_Panic` / `Tcl_AsyncDelete` / `OUTPUTTING STACK TRACE`（tkinter/tcl86t 起点）が出ない。
- [ ] picker は findings 0103 のとおり `Venue has no instrument list`（kabu は銘柄非列挙）で正常終了（クラッシュではなく placeholder）。
- [ ] 結果（PASS/FAIL・実機ログ抜粋）を本 finding の下記「HITL 結果」に追記（HITL の正本）。

## 再走手順

```
# #133 / #134 ゲート（owner Windows・display あり）
cd python && uv run pytest tests/test_login_dialog_tk_teardown.py tests/test_mplbackend_agg_gate.py -v
# expect: TKTEARDOWN-01/02 PASS, MPLBACKEND-01 PASS（+ litmus 2 本 PASS）

# rollup へ合流（Action-ID が merged rollup に PASS で現れる）
pwsh scripts/run-all-tests.ps1 -PytestArgs 'tests/test_login_dialog_tk_teardown.py tests/test_mplbackend_agg_gate.py'
```

## 自動 HITL 代替（KABU-TCL-HITL-01 の crash-class 検証・2026-06-25）

GUI クリックは crash class の必要条件ではない（病巣はスレッド配置）。GUI 無しで **本番の login→teardown
スレッド機構**を実走させ、crash class が閉じたことを自動検証した（`scratchpad/hitl135_repro.py`・owner Windows・
実 Tk display）。

- **GREEN（fix 込み・本番経路）**: 実 `LiveLoopManager._handle_prompt_login("KABU","verify")` を実 kabu
  `run_dialog` で起動し、**本番の `LOGIN_TIMEOUT`→`cancel_event`→`_poll_cancel`→`root.destroy()` 経路**で自動クローズ
  （＝「人がダイアログを閉じる」の代替。teardown 機構は success/cancel/timeout で同一）。並行して背景スレッド
  `PickerInstrumentFetch`（＝実際の crash trigger）が cyclic GC を回し続け、`MPLBACKEND=Agg` 下で matplotlib を
  import（backend=agg を確認）。**4 回／10 回**反復して:
  - プロセスが **Tcl_Panic で落ちない**（全反復生存）。
  - `teardowns=N finalised=N **cross_thread=0**`＝各ダイアログの Tk root は**生成（ダイアログ）スレッドで finalize**され、
    背景 picker スレッドでの cross-thread finalize は 0 件。
- **RED 対照（fix 撤去）**: 旧 discipline（Tk を thread A で生成→destroy→**生成スレッドで collect せず**→サイクル保持→
  thread B で `gc.collect()`）を子プロセスで再現すると、**実際に `Tcl_AsyncDelete: async handler deleted by the
  wrong thread` でクラッシュ**（child exit=0x80000003）。＝バグは実在し、生成スレッドでの collect が閉じる手段であることを実証。
- verdict: `[#135 AUTO-HITL PASS] GREEN=PASS RED_control=crashed`（10 反復でも安定）。

> これは crash class（Tcl_AsyncDelete/Tcl_Panic の有無）の自動回帰確認。実 Unity プロセス上の picker UI 表示
> （`Venue has no instrument list`）と `Editor.log` 目視は引き続き owner HITL の価値があるが、**クラッシュが
> 再発しないこと自体は本代替で機械検証済み**。

## HITL 結果（KABU-TCL-HITL-01・owner 実機・任意）

- （owner 記入）日時 / 環境 / fetch 反復回数 / Editor.log の該当行 grep 結果 / picker 文言 / PASS·FAIL。
