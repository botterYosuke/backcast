# Venue Contract Verification Findings — kabu / tachibana 不変条件の characterization test 固定

- Issue: #19 (Venue contract verification — kabu/tachibana 不変条件を characterization test + Windows pytest で固定)
- Parent: #4 (Step 2: Live/Auto parity) ／ Epic: #1
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（移植した engine が venue 契約を所有する）
- ドメイン一次資料: `.claude/skills/kabusapi/SKILL.md`（R1–R10）, `.claude/skills/tachibana/SKILL.md`（R1–R10）, kabu `ptal/error.html`（2026-05-20 検証）
- 配置の根拠: 本 findings は **下位事実（不変条件カタログ・対応テスト・検証結果）** であり ADR には書き戻さない。
- 実行環境（dev）: Intel x86_64 / macOS 13.7.8 / CPython 3.13.x / `cd python && uv run python -m pytest`

> **状態: Mac leg GREEN（2026-06-13）。** 53 tests passed（既存 1 + 本 slice 52）。設計は `grill-with-docs`
> + `/kabusapi` + `/tachibana` で確定し、単一言語・spec ロック済・中スコープのため主会話で直接実装。
> **Windows leg は AC として未達**（owner の AFK ゲート、§5）。Mac GREEN をもって issue 完了とはしない。

---

## 1. なぜ characterization test か（code-read では不十分）

移植済み `python/engine/exchanges/` の挙動は TTWR から持ち込まれたもので、**一次資料（venue 仕様 /
skill）と食い違う箇所が code-read だけでは見えなかった**。実行可能なテストで現挙動を炙り出すことで、
(a) 正しい契約を回帰ガードで固定し、(b) 移植時に紛れ込んだ誤りを一次資料に照らして訂正できる。

実際、本 slice の characterization で **2 件の実 divergence** を検出した（§3 / §4）。いずれも skill が
一次資料で一意に正解を定めており、現挙動の温存（KNOWN DIVERGENCE 化）ではなく **訂正** で閉じた。

---

## 2. 不変条件カタログ（INV）と対応テスト

`comparison.md §7` / `INV-K1-CAP` は TTWR 由来の **dangling 参照**で backcast には存在しなかった。
本 findings を backcast 側の一次台帳とし、`kabusapi_register.py:18` の参照を本 §2 に差し替えた。
各テストの docstring から INV-ID を参照する（文書だけの不変条件は作らない）。

| ID | 不変条件 | 一次資料 | 対応テスト | 結果 |
|---|---|---|---|---|
| INV-K1-CAP | PUSH 銘柄登録は REST/PUSH 合算 **50 上限**・暗黙 evict せず満杯で例外（Q-K5） | kabu R6 | `test_kabu_register_cap.py` | GREEN（pin） |
| INV-K2-RATE | token-bucket: 発注 5/s・余力/情報 10/s・register は 5/s no-burst(cap=1) | kabu R5 | `test_kabu_ratelimit.py` | GREEN（pin） |
| INV-K3-POLL | 約定通知は GET /orders を **1 秒 polling**・idle 自己終了・失敗で指数バックオフ | kabu R8/skill | `test_kabu_orders_poll.py` | GREEN（pin） |
| INV-K4-PUSH-CODEC | PUSH JSON frame → (trade, depth) 正規化・depth 常時発行・"unknown" side 禁止 | kabu R8 | `test_kabu_push_codec.py` | GREEN（pin） |
| INV-K5-ERRCODE | HTTP 401=失効 / 429=流量 / body 4002006=登録上限 / 4002001=銘柄不在 / 4001005=パラメータ誤り の **訂正** | kabu R7, error.html | `test_kabu_error_codes.py` | GREEN（**訂正**, §3） |
| INV-T1-FRAME | EVENT frame は ^A/^B 解釈・**^C は raw 保持**・空/^B 無し項目 skip | tachibana R7/R8 | `test_tachibana_event_frame.py` | GREEN（pin） |
| INV-T2-DEPTH | `_extract_depth`: GBP/GBV/GAP/GAV 1..10・**price と size 両方非空のみ採用** | tachibana skill | `test_tachibana_depth.py` | GREEN（pin） |
| INV-T3-SECRET | 第二暗証番号は env/log 非保持・resolver が唯一の供給源 | tachibana R10 | `test_tachibana_secret.py` | GREEN（**漏洩訂正**, §4） |
| INV-T4-SJIS | 応答は Shift-JIS strict 既定・`errors="ignore"` 禁止・replace はログ経路のみ | tachibana R7 | `test_tachibana_event_frame.py` | GREEN（pin） |

---

## 3. 訂正 #1 — kabu エラーコードの取り違え（INV-K5-ERRCODE）

**一次資料（kabu `ptal/error.html` 2026-05-20 / skill R7）の正:**

| 事象 | 正しい分類 |
|---|---|
| 流量超過（スロットリング） | **HTTP 429** |
| token 失効 / 未認証 | **HTTP 401** |
| 登録銘柄 50 上限超過（レジスト数エラー） | body Code **4002006** |
| 銘柄が見つからない | body Code **4002001** |
| パラメータ変換エラー | body Code **4001005**（token 失効ではない） |

**移植時の現実装（誤り）:** `kabusapi_auth.py` が body `4002006`→`KabuRateLimitError`、`4002001`→
`KabuRegisterFullError` と **register-full と rate-limit を取り違え**、流量超過を body Code 扱いに
していた。`kabusapi_register.py` も上限到達時に `KabuRegisterFullError(4002001)` を投げていた。
さらに body `4001005`（パラメータ変換エラー）を `KabuTokenExpiredError` と誤分類し、`kabusapi_login_flow.py`
の UI も code 4001005 を `KABU_TOKEN_EXPIRED` に丸めていた（**パラメータ不正で不要な再認証を誘発**）。

**根拠（owner 調査）:** TTWR の 2026-05-18 コミットで古い前提を意図的に実装した履歴を確認。移植時の
偶発ではなく **移植元から継承した既存バグ**。実 venue 再検証は不要（HTTP 分類と定数対応は一次資料だけで
確定でき、注文動作そのものは変えない）。

**修正（RED→fix, characterization で温存せず訂正）:**
- `check_response`: HTTP 401 → `KabuTokenExpiredError` / HTTP 429 → `KabuRateLimitError` /
  body 4002006 → `KabuRegisterFullError` / 4001005・4002001 ほかは generic `KabuApiError`。
- `RegisterSet.register`: 51 件目は `KabuRegisterFullError(code=4002006)`。
- `kabusapi_login_flow._map_exception`: code 4001005 → `KABU_TOKEN_EXPIRED` を撤去（→ `AUTH_FAILED`）。
  token 失効は HTTP 401 → `KabuTokenExpiredError`（`isinstance` 分岐）で正しく扱う。
- docstring 訂正: `KabuRateLimitError`→「HTTP 429」/ `KabuRegisterFullError`→「Code 4002006」/
  `KabuTokenExpiredError`→「HTTP 401（4001005 は token 失効ではない）」。
- kabusapi SKILL.md の R3/R7 stale 記述（「4001005 で失効が返る/自動 retry」「4002006 は backoff」）も訂正。

**波及（閉じている・むしろ意図に一致）:** `kabusapi_ws.py:95` は reconnect-replay の `register` PUT で
`KabuRateLimitError` のみ swallow して継続する設計。同 102-104 のコメントは「register full は
propagate して dead session として観測可能にする」と**意図を明記**していた。訂正前は body 4002006
（＝register full）が `KabuRateLimitError` に誤分類され握り潰されていた（コメントと矛盾）。訂正後は
register-full が `KabuRegisterFullError` として正しく伝播し、**WS replay 経路が文書化された意図に一致**する。

---

## 4. 訂正 #2 — 第二暗証番号 / 仮想 URL の平文ログ漏洩（INV-T3-SECRET, R10 違反）

**characterization が検出した実漏洩:** `httpx` は request を INFO で `HTTP Request: GET <full-url>`
と出力する。Tachibana は R2 により `{virtual_url}?{JSON}` 形式で送るため、この URL には
**(a) 発注時の `sSecondPassword`（平文）** と **(b) session-secret な仮想 URL（ND= token を埋め込む）**
が乗る。httpx/httpcore ロガーの抑制は **どこにも無く**、既定ログレベルは INFO（`login_dialog_runner.py`
basicConfig INFO）。→ **production の全 tachibana request で資格情報がログ漏洩**していた。`mask_secrets`
はこちらが組む payload にしか効かず、ライブラリ自身の request ログには届かない。

**判断（owner）:** AC が「第二暗証番号が env/log に出ないこと」を要求しており、#21（secret flow）は #19 に
blocked。手渡しでは漏洩を残したまま #19 を閉じることになる。xfail は production の資格情報漏洩を許容する
根拠にならない。→ **security-critical correction として #19 内で閉鎖**。

**修正:** `engine/live/logging.py` に `suppress_third_party_http_logs()` を新設（`httpx` / `httpcore`
を WARNING へ）。両 venue adapter の `__init__` から呼ぶ（in-proc 経路・cache-restore 含む）。

**追補（owner Finding 1）:** 当初は adapter `__init__` だけに置いたが、`tachibana_login_flow._run_auth`
（login dialog subprocess）は **adapter を経由せず `tachibana_auth.login()` を直呼び**するため、その経路
（`sUserId`/`sPassword` が R2 で URL に乗る）が未カバーで漏洩が残っていた。→ `tachibana_auth.login()`
の冒頭でも `suppress_third_party_http_logs()` を呼び、**あらゆる secret-bearing request の前**に効かせる。
（kabu の token は POST body / X-API-KEY ヘッダで URL に乗らないため URL ログ漏洩は無いが、adapter
`__init__` 抑制は defense-in-depth として維持。）

**回帰テスト（`test_tachibana_secret.py`）:** 実 submit_order 経路で
(1) `sSecondPassword` の値が caplog に無い、(2) session-secret な仮想 URL marker が caplog に無い、
(3) httpx の "HTTP Request" ログが沈黙、(4) resolver が第二暗証番号の唯一の供給源、
(5) リクエスト自体は正常送信され秘密は wire 上には乗る（ログ非保持だけ）、を assert。
さらに **adapter を経由しない `tachibana_auth.login()` 直呼び**（dialog subprocess 経路）でも
`sUserId`/`sPassword` と "HTTP Request" ログが caplog に出ないことを別テストで固定（Finding 1 回帰ガード）。
**#21 では suppression を再実装せず、Unity secret flow 全体監査でこの回帰テストを継承する。**

---

## 5. 現挙動を pin した曖昧箇所（将来変更は裏取り要）

一次資料から一意に正解が決まらないため characterization で現挙動を固定し、変更は実 demo payload /
公式 EVENT 資料の裏取りを前提とする:

- **^C 未分割（INV-T1-FRAME）:** `parse_event_frame` は ^A/^B のみ解釈し ^C 複数値は value 文字列に
  raw 保持。構造化は将来課題。
- **片側欠落 depth（INV-T2-DEPTH）:** `_extract_depth` は price/size の両方が非空の段だけ採用し片側
  欠落段は落とす（`if bp and bv`）。「前値保持 / 0 補完」などの統合方式は一次資料から一意に決まらない。
  空 size を `float("")` に渡さないことは現実装で保証される。

---

## 6. Windows pytest 手順（AC・owner の AFK ゲート）

本 slice のテストは platform-agnostic（httpx-mock / codec は websockets 非依存 / 実 venue・Mono・
kabuステーション本体 不要）。Mac で全 GREEN を確認済み。Windows GREEN は owner が下記で実測する:

```
cd python
uv sync --dev
uv run python -m pytest
```

実測結果には **日時・Windows/Python バージョン・pytest summary・新規失敗と既知失敗の分類** を本節へ
追記する。pre-existing 失敗（findings 0005 の Windows pipe FD 等）と本 slice の新規失敗を切り分けること。
**Windows leg が GREEN になるまで #19 の Windows AC は未達**として扱う。

### Windows 実測ログ（owner 追記）

- [ ] 未実施（owner の Windows 機で実行し、ここに summary を貼る）

---

## 7. AC 充足状況

- [x] kabu: 50 上限 / 1 秒 polling / token-bucket / PUSH WS codec を固定する characterization test。
- [x] tachibana: ^A^B^C frame 分割・`_extract_depth`・第二暗証番号が env/log に出ないことを固定する test。
- [ ] **Windows で pytest GREEN**（§6・owner の AFK ゲート）。
- [x] 不変条件と現実装の差分を記録（§3 エラーコード訂正 / §4 secret 漏洩訂正 / §5 pin した曖昧箇所）。
