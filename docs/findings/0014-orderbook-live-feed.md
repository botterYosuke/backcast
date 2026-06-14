# S9b Findings: kabu / 立花の実 depth を DepthUpdate 注入経路に流し板を live 更新

- Issue: #27（S9b — orderbook 実フィード・実 venue depth → `per_instrument[id].depth`）
- Parent: #17（S9 orderbook）／ #4（Step 2: Live/Auto parity）。方針: [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（status: **proposed**）
- 先行 slice: [0012 — orderbook depth ladder](./0012-orderbook-depth-ladder.md)（#26 = C# decode・mock depth・実 venue 非依存）。本 slice はその §4「実フィードは後続 slice」を充足する。
- ドメイン参照: [kabusapi SKILL](../../.claude/skills/kabusapi/SKILL.md)（R6 50 銘柄上限・R5 流量・R8 PUSH=1 JSON）／ [tachibana SKILL](../../.claude/skills/tachibana/SKILL.md)（R7 Shift-JIS・R8 空配列・`_extract_depth`）
- 配置の根拠: ADR self-protection（slice 内の下位事実は ADR に書き戻さず本 findings に記録）。**backcast に `tests/e2e/FLOWS.md` は無く、behavior-to-e2e の flow 正本は本 findings**（0012 §6 と同じ）。

---

## 1. 判定サマリ

#27 着手時点で、**実 venue depth の注入経路は全段すでに本番配線済み**だった（grill-with-docs 2026-06-14 でコード実証）:

| 段 | kabu | 立花 | 実体 |
| --- | --- | --- | --- |
| codec → depth dict | `KabuPushFrameProcessor.process` | `FdFrameProcessor._extract_depth` | `*_ws_codec.py`（単体テスト済） |
| adapter → `DepthUpdate` | `kabusapi.py:324-333` | `tachibana.py:417-435` | emit 済 |
| subscribe に depth 要求 | — | — | `live_runner.py:80` → `{"trades","depth"}` |
| bus → cache | `DepthCache._handle` | 同 | bus-fed |
| cache → `per_instrument[id].depth` | — | — | `_backend_impl.py:661-674`（depth-only 銘柄の補完まで） |
| unsubscribe → 板消滅 | — | — | `live_orchestrator.py` `depth_cache.remove`（D20） |
| C# ladder decode/描画 | venue 非依存 | venue 非依存 | `DepthDecoder.cs`（#26 で GREEN 実証） |

したがって本 slice は #22（safety machinery）/ #26 と同じ **「既配線の機構が実 venue 失敗モードでも正しく発火することを RED→GREEN で証明する」slice**。新規 production コードは下記 §2.2 の 1 点（立花 codec の不正段防御）のみで、それも AC が要求する characterization から RED で導いた。

## 2. 中核の設計判断（grill-with-docs 2026-06-14）

### 2.1 注入シーム耐性 = adapter/models の `DepthLevel` 制約差を `DepthCache` が吸収

`adapter.DepthLevel`（`adapter.py:118`）は `price/size` に**制約なし**（price≤0・NaN でも `DepthUpdate` を構築できる）。一方 `models.DepthLevel`（`models.py:39`）は `price gt=0` / `size ge=0`。`DepthCache._handle` は `DepthUpdate.bids/asks` → `models.DepthSnapshot` を再構築する際、不正段で `models.DepthLevel` が reject すると **当該 update のみ try/except で skip**（consume loop は継続）。

これが効くのは `DepthCache` が `BusConsumerTask`（`failure_policy="terminate"`）の subclass だから — `_handle` が外へ raise すると consumer task が**死に**、以降の板が一切更新されなくなる。`_handle` 内の try/except が「1 銘柄の不正 tick で全 depth feed が永久停止する」のを防ぐ唯一の砦。`test_depth_cache.py` がこの不変条件を実 `MarketDataBus` 駆動で固定（price=0 段 → skip・loop 生存・後続 good 着弾・`last_error is None`）。

### 2.2 立花の不正段は **codec で段ごと skip**（float() 接続断バグの RED→GREEN）

**bug-class sweep の結論**: 「truthy だが parse 不能な段値が `float()` を try なしで踏んで feed を落とす」クラスは **立花のみ**該当する:

- **kabu は WS dispatch 層で防御済み**: `kabusapi_ws.py:136-146` が `on_message` を `try/except BaseException` で包み warning+`continue`。codec の `float()` が割れても frame を捨てて feed 継続。
- **立花は無防備だった**: `tachibana_ws.py:170` の `await callback("FD", ...)` は `_recv_loop` 内で try に包まれず、例外は recv_task → 再 raise（同 243-246）→ **接続断・再接続churn**。`_cb`（`tachibana.py:419-425`）の `float(lv["price"])` は、codec `_extract_depth` が truthiness（`if bp and bv`）しか見ず生文字列を通すため、特別気配マーカ等の非数値段で割れる。

issue 本文「不正段は当該段を skip し feed を止めない（既存 DepthCache の方針を踏襲）」に従い、**`_extract_depth` の段フィルタを「空欄」から「空欄・非数値・非有限（NaN/Inf）」へ拡張**（`_is_finite_quote` = `Decimal(v).is_finite()`）。これで:

- `""`（欠損段）→ 従来どおり skip
- `"—"` 等の非数値 → 段ごと skip（`float()` を割らせない＝接続断を防ぐ）
- `"0"` / 指数表記 → 有限数なので段は残す（price≤0 の最終弾きは §2.1 の `DepthCache` gt=0 が担う二段防御）

`test_tachibana_depth.py` に RED→GREEN（`test_depth_skips_non_numeric_level`）＋境界（`test_depth_keeps_zero_and_scientific_notation`）を追加。

**out-of-scope の隣接観測（altitude review 2026-06-14 で再確認）**: `_cb` の trade 側（`float(trade["price"])`、`tachibana.py:442-443`）も同じ「無防備な float() in recv loop」クラスを共有する。depth 経路（#27 AC）は codec の段フィルタ＝**データ層の正しい altitude**（depth-level 検証の単一箇所）で塞いだ。

「kabu と同じく recv loop の callback dispatch 全体を `try/except` で包む」一括防御（`tachibana_ws.py:150-205`）は **#27 では意図的に見送る**。理由は kabu の order 経路が GET /orders polling で WS と分離しているのに対し、**立花は EC（注文約定通知）フレームが depth/trade と同じ `_cb` dispatch（`tachibana_ws.py:184`）を流れる**ため。blanket な `except BaseException: continue` は depth tick の取りこぼし（無害＝次 tick で復旧）と EC fill 通知の取りこぼし（**有害＝約定を黙って落とす**）を区別できない。EC の失敗ポリシー（reconnect して EC を再取得するか／別経路で照合するか）は設計を要し、trades/orders slice（#23 系）の領分。したがって depth は codec で段ごと skip（feed を止めない・約定を落とさない）、WS callback 全体の resilience は EC ポリシーと併せて #23 で扱う、というスコープ境界は意図的なもの。

### 2.3 kabu 50 銘柄上限・流量は既存テストで充足

AC「kabu 50 銘柄上限・流量制約でも feed が停止しない」は `test_kabu_register_cap.py`（R6）・`test_kabu_ratelimit.py`（R5・#237）で既出。本 slice では再実装せず、depth 経路の耐性（§2.1）として `DepthCache` 層で担保する。

## 3. behavior-to-e2e flow（release-gate 項目）

> **FLOW-S9b-1: 実 venue depth が不正段でも feed を止めず板に live 反映され、銘柄除外で消える**

| 観点 | 内容 |
| --- | --- |
| 保証したい挙動 | kabu/立花の実 depth が `DepthUpdate` 正規化 → `DepthCache` → `get_state_json` の `per_instrument[id].depth` に注入され、不正段（空 size・欠損段・非数値・price≤0）でも feed が停止せず、unsubscribe で板が消える |
| seam | venue codec → adapter `DepthUpdate` → `MarketDataBus` → `DepthCache._handle` → `get_state_json`（`_backend_impl.py:661-674`）→（#26）`DepthDecoder` → ladder |
| 自動 gate（kind:python） | `pytest tests/test_depth_cache.py tests/test_tachibana_depth.py tests/test_kabu_push_codec.py tests/test_kabu_register_cap.py tests/test_kabu_ratelimit.py` |
| characterization | `test_depth_cache.py`: 正常cache（wire 順）/ price≤0 段 skip+loop生存+後続着弾 / 初回不正でもブロックせず / 空板も cache（空板≠板なし）/ remove 板消滅 / remove 冪等 / orchestrator unsubscribe→remove 配線（D20）。`test_tachibana_depth.py`: 非数値/非有限段 skip（RED→GREEN）。 |
| 目視 gate（kind:manual-gate） | §4 owner HITL |
| Replay 保証 | Replay では `depth=None` → ladder 非表示（#26 F4 で実証済） |

## 4. HITL ゲート（criteria 1,2）→ #23 owner gate に統合

criteria 1,2（実 demo 接続で板が bid/ask ladder に表示・live 更新）は **#23（Live demo roundtrip done-gate）の owner gate に統合**する（2026-06-14 owner 合意）。理由:

- **実 venue depth を視覚的に描く経路が #27 単体では存在しない**。唯一の ladder HITL ハーネス `DepthLadderHitlHarness`（#26）は **mock 専用**（`venue_login("MOCK")` + `emit_depth_ladder` 注入）で、実 demo depth を流せない。本線 UI（production live panel）への載せ替えは findings 0012 §4 で deferral 済み。
- その production venue UI を結線するのは **#23**（issue 本文「production venue UI を結線」）。実 demo depth の視覚確認は #23 の平日 demo roundtrip（発注→約定→建玉）の中で同じ画面に乗せて行うのが自然＝二重の HITL を作らない。
- **#27 のデータ経路は #23 を待たずに担保済み**: 注入シーム（codec→DepthUpdate→DepthCache→`per_instrument[id].depth`）は §3 の自動 gate（`test_depth_cache.py` / `test_tachibana_depth.py`）で、C# decode/描画は #26 で venue 非依存に GREEN 実証済み。残るのは「実 demo の実データが同経路に乗るか」の目視のみで、それが #23 の owner gate。

#23 で実施する際の前提（環境制約のメモ）:

- **立花**: `LIVE_VENUE=TACHIBANA`（demo 既定）+ `.env` の `DEV_TACHIBANA_USER_ID`/`PASSWORD`（電話認証済み口座・debug ビルド）。**FD 板フレームは TSE 場中（平日 9:00–15:00 JST）のみ流れる**＝閉場・週末は実 depth が出ない。手順は tachibana SKILL S7（Zed F5 / in-proc）。
- **kabu**: **Windows + kabuステーション本体起動が必須**（kabusapi SKILL S1/S5、Mac/Linux 不可）。検証ポート 18081 + `DEV_KABU_API_PASSWORD`。50 銘柄 universe を立て続けに subscribe すると burst で `4001006` を踏むため register 経路の no-burst throttle を確認（SKILL R5）。
- subscribe 解除 / 銘柄除外で板が消えることを目視（criterion 4 の視覚確認・配線は §3 で自動固定済）。

## 5. 検証ステータス（Mac leg・Python 自動 gate GREEN）

- [x] `test_tachibana_depth.py` 7 passed（非数値段 RED→GREEN 実証済）
- [x] `test_depth_cache.py` 7 passed（注入シーム耐性 + remove 配線）
- [x] kabu codec / register cap / ratelimit 既存テスト GREEN（再確認）
- [x] **criteria 1,2（実 demo の視覚 HITL）は #23 owner gate に統合**（§4・2026-06-14 owner 合意）。#27 のデータ経路は自動 gate + #26 venue 非依存 decode で担保済みのため、本 slice はこの統合をもってコード上クローズ可能。

> #27 のコード成果物（codec fix・characterization・findings）は完了。実 demo の視覚目視は #23 の done-gate で `[LIVE DEMO ROUNDTRIP PASS]` と併せて取得する。

> 本 findings は ADR 昇格を主張しない（ADR-0001 は `proposed` 維持）。
