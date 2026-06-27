# 0126 — macOS 終了時 SIGSEGV（#170）: 根本原因は確認・しかし提案 fix は全滅、duckdb 上流バグで blocked

**Issue**: #170 / **Status**: BLOCKED（upstream duckdb/duckdb#13904・#13940 待ち）
**Repro probe**: `Assets/Editor/DuckDbShutdownReproProbe.cs`（passing gate ではない＝clean GREEN 不在）
**関連 memory**: [[macos-shutdown-segfault-duckdb-threadstate]]

## 1. 依頼と結論サマリ

「#170 の原因が正しいか確かめてから修正」。

- **根本原因は issue の記述どおり正しい**（実クラッシュ署名 ＋ 実コードで裏取り）。
- **issue が「本命」とした threadstate 再 attach は実機で無効**。`Shutdown()`・`close()` も無効。
- **唯一効くのは `os._exit(0)`（`__cxa_finalize` ごとバイパス・issue が「代替」と格下げした案）**。
- 真因は **duckdb の C++-static 接続が `__cxa_finalize` で destruct される既知の上流バグ**（duckdb/duckdb#13904 / #13940・1.5.3 でも OPEN）。Python の refcount / `close()` / `Py_Finalize` の管轄外。
- owner 判断で **duckdb 側調査 → #170 は blocked 保留**。効かない threadstate-guard 実装は revert 済み。

## 2. 根本原因（確認済み・正しい）

実クラッシュ署名（`~/Library/Logs/DiagnosticReports/Unity-*.ips` 18件＋本調査で再現した複数件、全て同一）:

```
SIGSEGV @ 0xb0 (KERN_INVALID_ADDRESS), com.apple.main-thread
  _PyThreadState_Detach            ← NULL threadstate を deref（offset 0xb0）
  PyEval_SaveThread
  duckdb::DuckDBPyConnection::~DuckDBPyConnection()
  __cxa_finalize_ranges
  exit
  -[EditorApplication applicationWillTerminate:]   （実 app）/ Application::Exit(int)（probe）
```

機序: `WorkspaceEngineHost.InitializePython` の `PythonEngine.BeginAllowThreads()`（`WorkspaceEngineHost.cs:173`）が
**メインスレッドを GIL-free / threadstate 不在**にしてプロセス全体を回す（ADR-0001 d4）。`Shutdown()` は意図的に呼ばない
（ADR-0001「interpreter left alive」）。プロセス終了で `exit() → __cxa_finalize` が DuckDB の残留接続デストラクタを
**メイン上で**走らせ、その `PyEval_SaveThread`（GIL 解放）が `_PyThreadState_Detach(NULL)` → 0xb0。in-code の
`duckdb.connect()` は3か所すべて `try/finally: close()`（`duckdb_bars.py:270/355`・`jquants_listed_info.py:73`）＝
残留は **duckdb モジュール内部**（既定接続 / instance cache の C++ static）。Windows は `__cxa_finalize` 順序が違い無再現。

## 3. 実機検証マトリクス（verdict = プロセス exit code）

`DuckDbShutdownReproProbe` を Unity batchmode で実走（duckdb 1.5.3 / py 3.13.11・macOS）。
`.ips` 署名は throttle 前の run で捕捉確認（RED/GREEN とも同一 0xb0 duckdb dtor）。

| # | residual | 適用した fix | exit | 結果 |
|---|---|---|---|---|
| 1 | なし | — | **0** | clean |
| 2 | なし | EndAllowThreads | **0** | clean |
| 3 | duckdb default | none = **RED** | 139 | `0xb0` `PyEval_SaveThread←~DuckDBPyConnection`（本番署名） |
| 4 | duckdb default | **EndAllowThreads 再attach（issue 本命）** | 139 | 同一 0xb0 ❌ |
| 5 | duckdb default | EndAllowThreads + **PythonEngine.Shutdown()** | 139 | 同一 0xb0 ❌ |
| 6 | duckdb `con.close()` | none | 139 | ❌ |
| 7 | duckdb **connect()+close()（本番パターン）** | none | 139 | ❌ |
| 8 | duckdb default / connect_close | **os._exit(0)** | **0** | clean ✅ |

### なぜ提案 fix が効かないか
- **#4 threadstate 再 attach**: `Run()` で `EndAllowThreads(ts)` してもプロセス終了は Mono/CLR teardown
  （ログに `Cleanup mono` / `ALC ILPP context unloading`）を経てから `__cxa_finalize` に至る。その時点で
  threadstate は再び消えており、dtor は同じ 0xb0。**早期 re-attach は finalize まで生存しない**。
  issue の `EditorApplication.quitting` 経路でも quitting は teardown より前なので同じ。
- **#5 `Shutdown()`（Py_Finalize）**: 残留は duckdb の **C++ static**（既定接続 / instance cache）で、
  `Py_Finalize` でも destruct されず `__cxa_finalize` まで生き残る。duckdb は `_clean_default_connection`
  capsule を持つが Python 側 `atexit` 登録は無く、本番は `Py_Finalize` を呼ばないので走らない。呼んでも C++ static には届かない。
- **#6/#7 `close()`**: Python 接続オブジェクトを閉じても duckdb 内部 static は残る。本番パターン（connect+close）でも 139。

### ⚠️ ReportCrash throttle の罠（診断ノート）
GREEN(#4) を最初 `sleep 2` で `.ips` 判定したら「新規 .ips 無し」＝偽陰性だった。macOS `ReportCrash` は
**非同期**（>2s 遅延）。`sleep 18` で同一 0xb0 `.ips` が出現。さらに連続クラッシュで throttle がかかり以降 `.ips` が
出ないことがあるので、**verdict は exit code（139/0）を正本**にし、`.ips` 署名は最初の数回で確認する。

## 4. 上流 duckdb バグ（真因・未修正）

- **duckdb/duckdb#13904**「Process gets aborted during interpreter shutdown when a duckdb connection is present in a child thread」
  ＝本件と同型（worker thread の接続が shutdown で abort）。**OPEN / reproduced・1.0.0→（少なくとも）1.5.3**。
  1.1.0 の `ConnectionGuard` 追加で悪化。maintainer 曰く「接続 dtor が finalize 中に GIL を release しようとして
  threadstate 不在で落ちる」。
- **duckdb/duckdb#13940**「SIGSEGV in python bindings」＝`~DuckDBPyConnection`/`~DuckDBPyRelation` dtor の同類・OPEN。
- **PR #14926**「release shared connection pointer before it goes out of scope」＝
  「`gil_scoped_acquire` が shared_ptr より先に破棄され、最終参照の dtor が GIL 無しで走る」を修正。**本件と機序一致だが
  #13904 自体は未 close**＝released 版（≤1.5.3）に我々のケースの修正は入っていない。
- 上流推奨の回避（接続を閉じる / 長寿命接続を持たない）は **C++ static 残留には届かず本件に無効**（#6/#7 で実証）。

## 5. 取りうる対応（owner 判断＝「duckdb 側調査・#170 blocked」）

1. **保留（採用）**: 上流 #13904 の修正版を待つ。`duckdb` upgrade 時に本 probe で再評価。
2. `os._exit(0)` バイパス（唯一実効・hacky）: 実 quit のみ・C# 後始末完了後に呼ぶ必要があり、Unity 自身の最終
   teardown / editor 状態保存をスキップする副作用（「quit unexpectedly」誘発の懸念）。HITL 検証が前提。**今回は不採用**。
3. duckdb の instance cache を late teardown で明示破棄する API があれば検討（現状未露出）。

なお本クラッシュは `exit()→__cxa_finalize` の**最終段**＝C# `OnApplicationQuit` の後始末（autosave / `PlayerPrefs.Save`）
が**完了した後**に起きるので、issue 記載の「後始末が飛ぶ」副作用は実際にはほぼ起きておらず、**実害はクラッシュダイアログ/レポートのみ**の公算が高い（実 app HITL で要確認）。

## 6. 残した成果物

- `Assets/Editor/DuckDbShutdownReproProbe.cs` — 再現＆検証ハーネス（env トグルでマトリクス再走可）。passing gate ではない。
- `WorkspaceEngineHost.cs` は **無改変（threadstate-guard は revert 済み）**＝効かない fix を出荷しない。

## 7. 再走手順

```
UNITY=/Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity
# RED（本番署名を再現・exit 139）
"$UNITY" -batchmode -nographics -quit -projectPath <abs> -executeMethod DuckDbShutdownReproProbe.Run -logFile <abs>
env BACKCAST_170_RESTORE=0 "$UNITY" ... # 同上（restore は無関係＝どちらも 139）
# CONTROL（duckdb 無し・exit 0）
env BACKCAST_170_DUCKDB=0 "$UNITY" ...
# os._exit バイパス（唯一 exit 0）
env BACKCAST_170_OSEXIT=1 "$UNITY" ...
```
verdict は **exit code**。`.ips` は最初の数回で `0xb0`/`PyEval_SaveThread`/`~DuckDBPyConnection` を確認（以降 throttle）。
Editor 起動中は batchmode が lock-abort（owner に閉じてもらう）。
```
```
