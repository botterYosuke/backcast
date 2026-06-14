# findings 0015 — Tachibana 起動時セッション生存検証（#35・grill 確定）

方針: **ADR-0001**（decision 7 = engine は backcast 所有・host 非依存 / decision 8 = 単一 adapter 層）。
親 **#4**（Step 2: Live/Auto parity）の子スライス。本書は #35 の下位確定事実を記録し、ADR は「方針」として
参照する（ADR は自己保護のため編集しない）。

## ゴール（issue #35）

立花アダプタの `tachibana_auth.validate_session_on_startup` が `NotImplementedError`（旧 A1.3b RED 用スタブ）の
まま。起動時の **セッション生存検証**（既存 session の liveness 確認・失効時の再ログイン誘導）を実装し、demo/live
起動時に失効 session を掴んだまま発注経路に入るのを構造的に防ぐ。

## ギャップの正体

`tachibana.py` の `login()` `session_cache` 分岐（`:196-205`）は `is_session_valid_for_today(data)`＝
**JST 日付チェックのみ**で `_apply_session_from_data` → `_ensure_ec_stream()` まで進む。同一 JST 日でも死んだ
session（`p_errno="2"` / 夜間閉局越え / サーバ無効化）がそのまま適用され、`is_logged_in=True` のまま発注経路に
入りうる。当日有効（date-validity）は necessary but not sufficient で、生存（liveness）は別概念
（CONTEXT「セッション当日有効 / セッション生存」）。

## 確定事実（grill 2026-06-14）

### D1. call site = `adapter.login()` の `session_cache` 分岐内（単一 seam）

キャッシュ復元の**全経路が必ず `adapter.login()` の `session_cache` 分岐を通る**:
- 直接 `session_cache` 経路（2 回目以降起動・`effective_source="session_cache"`）。
- prompt 成功後、TACHIBANA は `credentials_source="session_cache"` に切替えて再ロードする経路
  （`live_orchestrator.py:774-778` → `:792-796`）。

よって `is_session_valid_for_today` の直後・`_ensure_ec_stream()` の前に probe を 1 箇所置けば、死んだ
キャッシュが適用される全パスを塞げる。バグ（日付のみで session を適用）と**同一箇所に co-locate** され凝集度が
高い。`_ensure_ec_stream()` 前・`login()` return 前に弾くので、`is_logged_in` が corpse 上で True にならず、
後続の `CONNECTED` 遷移（`live_orchestrator.py:800-801`）も account-sync（`:805`）も死んだ session 上で走らない。

backend/orchestrator 起動時に 1 回だけ走らせる案（StartupLatch/L6 準拠）は却下: (a) 起動シーケンスに新 call point を
増やす、(b) 再ログイン時の再検証を別途配線する必要がある、(c) その間 login() は corpse 上で成功し今のバグ形を温存。
login() 埋め込みなら logout 後の再ログイン（`_attempt` retry）でも probe が**毎 (再)login で自然に再発火**する。

### D2. probe 本体 = `tachibana_auth.validate_session_on_startup`（純読取り 1 発 + check_response）

AC #1 が「`validate_session_on_startup` が実装され `NotImplementedError` 解消」を文言指定するため、関数本体は
`tachibana_auth` に実装し、call site を D1 の seam に置く両立解:
- `CLMZanKaiKanougaku`（買余力）相当の**純読取り** REQUEST を 1 発撃つ（`fetch_account` の probe 部分を最小再利用・
  発注副作用なし）。
- 既存 `check_response`（`p_errno="2"` → `SessionExpiredError`）で失効を立てる。adapter 側で捕え
  `ValueError("SESSION_CACHE_EXPIRED")` に翻訳 → orchestrator が既存の prompt 再ログイン誘導へ落とす（AC #2）。
- auth モジュールを adapter に結合させないため、HTTP は呼び出し側注入の `request` callable（adapter の `_request`
  束縛メソッド）で行う：`validate_session_on_startup(request)`。session は callable が内包するので別引数にしない
  （未使用引数を避け simplify する）。
- **corpse を握らない**: `_apply_session_from_data` は probe より前に `self._session` を set する。probe 失効時は
  `self._session = None` に戻してから `SESSION_CACHE_EXPIRED` を raise（さもないと `is_logged_in` が corpse 上で
  True のまま残る）。`_ensure_ec_stream()` は probe 成功後にのみ呼ぶ。

### D3. TTWR は oracle にならない / StartupLatch・L6 は削除

- TTWR 側の `validate_session_on_startup` も完全に同一の `NotImplementedError` スタブで、session_cache 分岐も
  同じく `is_session_valid_for_today` だけの同一ギャップ。両プロジェクトのどこにも call site は存在しない
  （grep 確認: `__all__` 登録・stub def・StartupLatch docstring のみ）。よって parity 制約ではなく backcast の
  orchestrator に合わせた純設計選択。
- `StartupLatch`（`tachibana_login_flow.py:31`）はリポジトリ全体で**定義以外の参照ゼロ**（import・instantiate・
  テストいずれも無し）の dead code。L6 の「1 プロセス 1 回」は D1 の再ログイン毎再検証と正面衝突し、残すと将来の
  読み手が配線して再接続時に session を再検証しない＝今回直すバグを再導入しかねない。**削除**する（`Awaitable` /
  `Optional` import も未使用化するので整理）。

## 変更インベントリ（計画）

**production Python**:
- `engine/exchanges/tachibana_auth.py` — `validate_session_on_startup(session, request_callable)` 実装
  （`NotImplementedError` 解消）。
- `engine/exchanges/tachibana.py` — `login()` `session_cache` 分岐に probe を挿入（`is_session_valid_for_today`
  後・`_ensure_ec_stream()` 前）。`SessionExpiredError`/auth 例外を `SESSION_CACHE_EXPIRED` に翻訳。
- `engine/exchanges/tachibana_login_flow.py` — `StartupLatch` 削除・未使用 import 整理。

**テスト/ゲート（AC #3 AFK）**:
- 有効 session（probe `p_errno="0"` → そのまま継続・`is_logged_in=True`）/ 失効 session（`p_errno="2"` →
  `SESSION_CACHE_EXPIRED`・`_ensure_ec_stream` 未起動）を RED→GREEN で固定。backcast に当テストは無いので新規作成。

## 実装結果（GREEN 2026-06-14）

RED→GREEN で実装。`tests/test_tachibana_startup_session.py`（5 passed）:
- validate_session_on_startup unit（生存 `p_errno=0` 継続 / 失効 `p_errno=2` → `SessionExpiredError`）。
- login(session_cache) 失効 probe → `SESSION_CACHE_EXPIRED`・`is_logged_in=False`・EC stream 未起動。
- login(session_cache) 生存 probe → 継続・EC stream 起動。
- login(session_cache) probe transport error（非 ApiError）でも corpse を握らない（fail-closed・元 semantics で伝播）。

`uv run pytest tests/` → **198 passed**。残 8 failed は pre-existing 環境要因（catalog parquet fixture 不在＋
#34 high-precision build・findings 0014 §既知事項と一致・#35 非起因）。

### code-review（simplify・2026-06-14）

4 angle 並列レビュー。実施/skip:
- **(Altitude・最重要)** session_cache 分岐の例外処理を一般化。probe は #35 が新たに足す round-trip で失敗し得るため、
  `except ApiError`（失効→`SESSION_CACHE_EXPIRED`）に加え `except BaseException: self._session=None; raise` を足し、
  **どの失敗モードでも login() が corpse を残さない**不変条件を login() 内で自己完結させた（downstream teardown に
  corpse 掃除を委ねる fragile を回避）。broad `ApiError`→`SESSION_CACHE_EXPIRED` は startup liveness gate の
  **意図的 fail-closed**として維持（買余力 read の業務エラーは session 使用不能→再ログインが正・コメントで明示）。
- **(Efficiency・skip)** prompt→session_cache 再ロードで probe が fresh session に 1 回余分に当たる。D1 で受容済み
  （login は非 hot path・1 read・freshly-saved session の疎通確認になり robustness 向上。flag/branch 追加の複雑化に
  見合わない）。
- **(Reuse・skip)** `CLMZanKaiKanougaku` payload が fetch_account と 2 行重複。codebase は payload を集約しない方針
  （`CLMOrderList` 等も inline）なので style 一貫・抽出パターン不在。
- **(Simplification)** clean（import trim / `__all__` / dead code 残無し確認）。

## behavior gate

backcast に FLOWS.md は無く、本 findings ＋ characterization test（AFK）＋ owner HITL（AC #4・demo 資格情報）が
behavior gate の等価物。新 ADR 不要（ADR-0001 が方針を所有）。
</content>
</invoke>
