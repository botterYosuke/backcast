---
status: proposed
supersedes-conditionally: ADR-0001 (decisions 2/3/4/6, for the Windows deploy target)
---

# Windows-Mono で nautilus Rust core を走らせた thread の teardown が crash する — engine 配置の再決定（in-proc 長命 thread vs 別プロセス）

`grill-with-docs` / #18（Windows live prerequisites）の S0 Windows leg 診断で判明した
**deploy OS（Windows）固有の事実**に基づき、ADR-0001 が前提とした「Python/Nautilus を Unity プロセスに
全埋め込み」の **Windows での成立性**を再決定する。ADR-0001 は self-protected のため本 ADR で **supersede**
する（ADR-0001 は編集しない）。**本 ADR は `proposed`。Decision は owner 判断で確定する（勝手に `accepted`
へ昇格しない）。**

関連: ADR-0001（decision 2 全埋め込み / 3 Unity死=Python死 / 4 重い計算は C# sub-thread / 6 正常終了で
broker 残注文取消）, ADR-0002（runtime 配置）, findings: `docs/spike/s0-result.md §1.1–§1.3`,
`docs/findings/0005-s2-spike-live-loop.md §8.1`。

## Context（#18 で初めて分かった事実）

deploy OS = Windows（kabuステーションが Windows 専用）。Mac-Mono leg は全 GREEN だったが、**Windows-Mono で
S0（threaded nautilus backtest）が FAIL**。段階マーカー＋owner 指定の診断ラダー＋2 spike で root cause を切り分けた:

1. **load は通る**: `import nautilus_trader` + 精度 pin（PRECISION_BYTES=8）まで Windows-Mono+pythonnet で成功。
2. **run は thread context 依存**:
   - C# `new Thread` 上で `BacktestEngine.run()` → **実行中に SIGSEGV**（§1.1。logging / strategy callback /
     catalog data はいずれも除外済＝diagnostic A/B）。
   - **main thread** で run → **GREEN**（`bars=204`、diagnostic C）。
   - **Python-owned `threading.Thread`** 上で run → **完走**（`bars=204`、§1.2/§1.3）。
   - ⇒ run 自体は off-main の **python-owned** thread で通る。問題は「C# が作った foreign thread」での実行。
3. **teardown は必ず crash（本 ADR の核心）**: nautilus Rust core を走らせた thread を畳む段で Windows-Mono が
   native crash する。**per-run でスレッドを終了**（§1.2 spike1）でも、**長命 daemon thread を生かしたまま
   process exit**（§1.3 spike2）でも、同様に SIGSEGV。S2-spike の teardown が clean だったのは、その thread が
   **pure-Python asyncio loop（Rust core 非実行）** だったため。
4. **付随**: 本番同等 `log_level=ERROR` で multi-run すると nautilus の **logger は process-global singleton** の
   ため 2 回目の engine 生成が Rust panic（init-once logging が別途必要）。

### なぜ重大か（ADR-0001 への影響）

- **decision 4**（重い計算を C# sub-thread で実行）: C# foreign thread での run が crash するため**現状不成立**。
- **decision 6**（**正常終了**で venue 残注文を best-effort 取消）: **正常終了が必ず crash する**なら graceful
  shutdown 自体が成立せず、resting order 取消が**達成不能**。これは「crash 時は取りこぼし受容」とは別問題で、
  *normal* shutdown が存在し得なくなる安全要件の毀損。
- **decision 2/3**（全埋め込み＝同一プロセス＝Unity死=Python死）: 埋め込み自体は run まで通るが、teardown crash が
  解けない限り Windows で**安定運用できない**。

### native crash-dump 解析（dump 1 件・cdbX64・2026-06-13）

owner 指示の限定調査（dump 1 件・修正実装/追加 spike なし）。`%LOCALAPPDATA%\CrashDumps\Unity.exe.10148.dmp`
（§1.3 confound run = `log_level=ERROR`/RUNS=1・run 完走→exit crash）を `cdbX64.exe` で解析:

- **exception**: `c0000005` Access violation。faulting: `ntdll!RtlFlsSetValue+0xb4`（`mov [r12+10h], rbp`、
  書込先 FLS スロットが解放済/無効）。
- **native stack = 無限再帰 → stack overflow**（"Stack overflow detected" を debugger が明示）:
  ```
  ntdll!RtlFlsSetValue   ← FLS スロット書込で AV
  KERNELBASE!FlsSetValue
  ucrtbase!_vcrt_FlsSetValue
  ucrtbase!_vcrt_getptd_noexit / _vcrt_getptd   ← CRT per-thread data 取得
  ucrtbase!_CxxFrameHandler3                    ← その AV の C++ 例外ハンドラが…
  ntdll!RtlDispatchException / KiUserExceptionDispatch ← …再び FLS に入り → AV → ∞
  ```
- **faulting module = ntdll / ucrtbase（Windows C ランタイムの thread-local/FLS teardown）**。
  python313 / mono / nautilus の自前コードではない。
- **loaded modules（多重 CRT を確認）**: `nautilus_pyo3_cp313_win_amd64`（Rust core・自前 CRT/FLS callback）、
  `python313`/`python3`、`ucrtbase` + `VCRUNTIME140`/`VCRUNTIME140_1`、**side-by-side の `msvcp140_<hash>` 複数**
  （Unity/Mono/Rust/torch 等が各々プライベートに load）、`msvcp100`。

**機構の解釈**: thread/process 終了時、複数 CRT インスタンスが登録した **FLS（fiber/thread-local storage）callback**
が、別 CRT に解放された per-thread data を deref → AV。さらにその AV を処理する CRT の例外ハンドラ自身が FLS を
使うため**再入 → 無限再帰 → stack overflow**。**「nautilus Rust core を走らせた thread の teardown」=
複数 CRT の thread-local destructor 競合**であり、§1.1（C# thread・run 中 crash）/§1.2（per-run 終了 crash）/
§1.3（process exit crash）が**同一根**であることと整合する。

**判定（owner 基準への当てはめ）**: 原因は **「Rust/CPython/Mono 間の終了順・thread-local destructor（FLS）」**＝
owner が「根治性を示せない場合は案 B」とした類型に**該当**。我々のコードに局所的・保守可能な 1 点修正は無く
（多重 CRT の FLS teardown 順は Unity/Mono/CPython/nautilus のビルド構成に内在）、「設定で偶然落ちない」は採用
根拠にしない方針。→ **案 B（別プロセス）を支持する根拠**。

## Decision（fork — owner が確定）

下記 2 案を比較する。**本 ADR では選択せず**、owner が確定する。確定後、本 ADR を更新（または確定案で新 ADR）。

### 案 A: in-proc 維持 ＋ teardown crash を解く

nautilus を Unity プロセス内（Mono+pythonnet）で動かす ADR-0001 路線を維持し、**teardown crash の根治**を図る。
未消化の調査余地: ① crash dump（`%LOCALAPPDATA%\CrashDumps\Unity.exe.*.dmp`）の native 解析で crash 関数を特定
（CPython thread teardown / nautilus tokio runtime / pythonnet finalizer のどれか）。② nautilus Rust runtime
（tokio/global logger）を process 寿命で 1 回だけ初期化し、engine thread を**完全に終了させない**運用に倒す
（ただし §1.3 で process exit でも crash したため、"終了させない" だけでは不足の可能性）。③ Mono ランタイム設定
/ pythonnet バージョン。

- **Pros**: ADR-0001 の zero-copy（C#↔Python・viz GraphicsBuffer #8）と「Unity死=Python死」が無改造で保たれる。
  要望者がレイテンシ理由で却下した IPC を導入しない。
- **Cons**: teardown crash が **根治可能か不明**（spike 2 本＋3 診断で run は通すも teardown は両 model で crash）。
  owner 指定で「場当たり修正はしない」方針のため、根治には native dump 解析という重い一歩が要る。decision 6 の
  安全要件が解けるまで #4 は進めない。

### 案 B: engine を別 CPython プロセスへ（ADR-0001 decision 2 の反転）

nautilus engine を **Mono 外の素の CPython プロセス**で動かし、Unity(C#) とは IPC で接続する。teardown crash は
Mono ホスト固有のため、**nautilus を Mono 上で走らせない**ことで構造的に回避する。

- **Pros**: Windows-Mono teardown crash を確実に回避（素 CPython では S0 backtest が完走済）。nautilus の Rust
  runtime / logger も独立プロセスで素直。graceful shutdown（decision 6 の resting order 取消）が成立する。
- **Cons**: **ADR-0001 が明示的に却下した構成**（要望者がレイテンシ理由で loopback/gRPC を却下）。**C#↔Python の
  zero-copy が困難**（viz #8 の GraphicsBuffer 直送が崩れる→ IPC 一括 or C# 側計算で代替）。「Unity死=Python死」を
  **能動的に保証**する機構が必要（子プロセス + Job Object / parent-watch dead-man's-switch。ADR-0001 が
  「不要」とした配管を導入することになる）。TTWR は元々 PyO3 in-proc だったため engine 側の IPC 境界を新設する
  実装コスト。

### 案 C: nautilus Rust core を排し、pure-Python の Backcast Execution Kernel に置換（owner 採用方針 2026-06-13）

NautilusTrader（Rust core `nautilus_pyo3` 含む）を **backcast 専用の最小 pure-Python 取引エンジン**に置換する。
**Rust/PyO3 を排除 → 多重 CRT/FLS teardown crash を構造的に消す**（残る native は CPython の ucrtbase のみ）。
in-proc を維持（zero-copy・Unity死=Python死 を保つ）。**S2-spike が pure-Python asyncio loop の Windows-Mono
lifecycle を GREEN にしている**ため、別プロセス化（案 B）より先に検証する価値がある。

**最小スコープ（`Backcast Execution Kernel`）**:
1. `EventLoop` — market/order/fill を時刻順で決定的に処理。2. `Strategy` — `on_start/on_bar/on_tick/on_order/on_stop`。
3. `OrderEngine` — 注文状態遷移・部分約定・取消・拒否・重複防止。4. `Portfolio` — 建玉・平均取得・実現/含み損益・現金。
5. `RiskEngine` — **既存 `pre_trade_gate`/`post_trade_gate`/`safety_rails` を接続**。6. `ReplayBroker` — bar/tick から
決定的約定。7. `LiveBroker` — **既存 kabu/tachibana adapter を使用**。8. `EventSink` — **既存 order/position/bar/run-result
JSON 契約を維持**。Replay と Live で同一 strategy API。

**golden 契約（Nautilus を standalone CPython 上の比較 oracle として温存）**: 注文状態列 / fill 数・価格 / position
数量 / realized PnL / 最終 cash・equity / sink イベント順 が一致してから Live へ。最初の tracer bullet =
`spike_buy_sell` 相当（204 bars → BUY→fill→open→SELL→fill→close→run result）。

**作らない**: 多資産・多 venue 汎用 framework / HFT / 高度な注文種別 / 汎用 message bus / options・futures・FX /
Nautilus 互換 API 全体 / 高度な reconciliation。

- **Pros**: crash 根治（Rust core 不在＝多重 CRT/FLS 競合が起きない）。in-proc 維持で zero-copy・Unity死=Python死を
  保持（案 B の IPC・watchdog 不要）。kabu/tachibana adapter・gates・sink を再利用。Replay/Live 統一 API。
- **Cons**: 取引エンジンの自前実装・保守責任（ただし最小スコープに限定）。Nautilus との golden parity を継続検証する
  コスト。約定モデル/状態機械の正しさを自前で担保する必要。

### 判定材料 / 暫定リコメンド（owner 確定待ち）

- run が off-main python thread で完走する事実は「埋め込み完全否定」ではないが、**teardown crash が両 ownership
  model で再現**し、それが **decision 6（正常終了の安全要件）を直接毀損**する点が決定的。
- **native dump 解析（上記・owner 指示の最後の限定調査）が完了**し、root cause は **多重 CRT の FLS/thread-local
  teardown 競合（ntdll/ucrtbase 内・無限再帰 stack overflow）**＝owner が案 B 行きとした「終了順・thread-local
  destructor」類型と確定。**案 A に局所的・保守可能な 1 点修正は見いだせない**（多重 CRT 構成に内在）。
- 当初リコメンドは案 B（別プロセス）だったが、**owner は案 C（pure-Python Backcast Execution Kernel）を採用方針**
  とした（2026-06-13）。case-B 行きの根拠（多重 CRT/FLS teardown）は **「nautilus Rust core を排せば消える」**ため、
  案 C は **in-proc・zero-copy・Unity死=Python死 を保ったまま root cause を構造的に除去**できる（案 B の IPC/watchdog
  を回避）。S2-spike の pure-Python asyncio lifecycle が Windows-Mono GREEN である事実が裏付け。
- **採用方針 = 案 C**。ただし **本 ADR は `proposed` 据え置き**、`accepted` 昇格は **案 C の最初の tracer bullet
  （pure-Python kernel が Nautilus golden と一致 ＋ Windows-Mono で clean teardown）が GREEN になってから**
  （owner 指定）。tracer issue: **#24**（Backcast Execution Kernel・golden 契約）。それまで案 B は fallback として残す。

## Considered Options

- **案 A（in-proc 維持＋teardown 根治）** / **案 B（別プロセス + IPC）** / **案 C（pure-Python kernel に置換・採用方針）** — 上記。
- **不採用（記録のみ）: Windows を deploy 対象から外す** — kabuステーションが Windows 専用のため不可。
- **不採用（記録のみ）: Mac のみ**（Windows-Mono を諦め Mac-Mono で運用）— deploy = Windows の要件に反する。

## Consequences

- 確定まで **#18（および依存する #19–#23 / #4 全体）は blocked**。ADR-0001 は `proposed` 据え置き
  （S0 Windows AC① 未達のまま）。
- S2-spike の Windows leg は GREEN（findings 0005 §8.1）— asyncio loop seam（Rust core 非実行）は Mono で健全。
  案 B でも案 A でも、live の **venue I/O / asyncio marshal 層**は再利用可能（teardown crash は nautilus Rust
  backtest/run core 固有）。
- 案 B を採る場合、ADR-0002（runtime 配置）も別プロセス前提に見直しが要る（venv は別プロセスが load）。

## 自己保護

本 ADR の Decision が確定したら固定する。覆す場合は本 ファイルを編集せず supersede する新規 ADR を起こす。
スライス内で確定する下位事実（IPC 機構・watchdog 実装等）は本 ADR に書き戻さず当該スライスの `docs/findings/`
に記録し、本 ADR を「方針: ADR-0004」として参照する。
