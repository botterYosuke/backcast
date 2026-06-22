# 0085 — Venue メニューの venue を実行時に再バインド（VENUE_MISMATCH 撤去）

報告（2026-06-22, owner）: Venue → **Tachibana(Demo)** をクリックすると `login failed: VENUE_MISMATCH`。
方針: 「起動時に 1 venue へ固定される仕様を廃止する」（grill-with-docs で設計）。

方針: [[ADR-0021]]（実行時再バインド・LIVE_VENUE は初期選択＋メニュー絞り込み・自動接続なし・MOCK 据え置き）。
本 findings はその下位事実・RED→GREEN・再走手順を固定する。

## 結論（root cause）

**設計どおりの拒否 ＋ メニュー UX の穴。** サーバは起動時に 1 venue へ束ねられ（`BackcastWorkspaceRoot.Awake`
→ `ResolveLiveVenue()`・既定 MOCK）、`live_orchestrator.venue_login` が `configured_venue != venue_id` を
VENUE_MISMATCH で拒否（D26）。一方 Venue メニュー（`MenuBarView.BuildVenueMenu`）は構成 venue に関係なく
`ConnectVariants` 4 つを常に表示。既定（LIVE_VENUE 未設定＝MOCK サーバ）で Tachibana を押す＝MOCK サーバへ
TACHIBANA login → VENUE_MISMATCH。

empirical 確認（MOCK 構成サーバ + `venue_login("TACHIBANA")`, 2026-06-22）:
```
-> {'success': False, 'error_code': 'VENUE_MISMATCH', 'venue_state': 'DISCONNECTED', ...}
```

venue 固有の状態は **adapter factory 1 個のみ**（`venue_sm` / `mode_manager` / portfolio は venue 非依存）。
factory は login 時に `self._live_adapter_factory(env)` で adapter を生成しているので、login 時に factory を
作り直せば venue 切替が成立する。さらにメニューは接続中は全 Connect variant を grey-out（`CanConnect =>
!IsConnected`）するので、**venue 切替は必ず切断済み状態からのみ**発生＝接続中ホットスワップは UI が既に防ぐ。

## 実装

### engine（`python/engine/live/live_orchestrator.py` `venue_login`）

`configured_venue != venue_id → VENUE_MISMATCH` ブロックを再バインドに置換:

- `bound_venue = _live_venue_id`、`live_session = venue_sm.current ∈ {AUTHENTICATING, CONNECTED, SUBSCRIBED, RECONNECTING}`。
- `bound_venue != venue_id` かつ `live_session` → **VENUE_MISMATCH 維持**（D2 防御・通常 UI が防ぐ）。
- factory 未構築 or `bound_venue != venue_id`（切断中）→ `build_live_adapter_factory(venue_id)` で factory 再構築・`_live_venue_id = venue_id`（D1）。build 失敗は LIVE_ADAPTER_NOT_CONFIGURED（venue は前段 `_KNOWN_VENUES` 検証済みなので通常起きない）。

### C#

- `VenueMenuViewModel.VisibleConnectItems(filterVenue, isEditor)` を新設（純ロジック）。null/空=全 variant＋editor MOCK dev、明示=その venue のみ（MOCK pin=dev 項目のみ）。
- `MenuBarView.BuildVenueMenu` は inline ループを撤去し `VisibleConnectItems` 経由。`Bind` の `devVenue` を `filterVenue` に置換（`_filterVenue` フィールド）。
- `BackcastWorkspaceRoot.ResolveExplicitLiveVenue()` を新設（明示 LIVE_VENUE whitelist or null）。`Bind` には `_venue` でなく `ResolveExplicitLiveVenue()` を渡す。
- `LiveDemoRoundtripMenu` の warn / dialog を正確化（harness は `ConnectConfigured`＝`_venue` を繋ぐので LIVE_VENUE+再 Play 前提は妥当・メニュー経由は再バインドで再 Play 不要と追記）。`Awake` の D26 コメントを ADR-0021 に更新。

## ゲート（RED→GREEN）

### Python seam — `python/tests/test_venue_mismatch_inproc_server.py`（characterization を反転）

| test | 主張 | litmus |
|---|---|---|
| `test_disconnected_login_to_other_venue_rebinds` | MOCK サーバ・切断中の `venue_login("TACHIBANA")` は VENUE_MISMATCH を返さず success（factory を mock に monkeypatch して接続を決定論化） | rebind を消すと VENUE_MISMATCH に戻り RED |
| `test_login_to_different_venue_while_connected_is_rejected` | MOCK 接続後に `venue_login("TACHIBANA")` → VENUE_MISMATCH | 接続中ガードを消すと rebind して RED |
| `test_login_to_configured_venue_succeeds` | 構成 venue への **単発** login が success（rebind なしの happy path・再 login は別 test） | — |
| `test_same_venue_relogin_while_connected_is_idempotent` | 接続中に同一 venue を **2 回目** login → success かつ **session を貼り直さない**（`mock.login_call_count == 1`） | 観測 success/state だけでは弱い（fall-through も success を返す）。**`login_call_count`** が締まった gate＝idempotent 短絡を消すと 2 回目が `_attempt` へ落ち teardown→再 login で `login_call_count == 2` → RED（実測） |

> idempotent 短絡の gate のため `MockVenueAdapter` に **`login_call_count`** 観測点を追加（既存 `logout_call_count` と同流儀）。
> review 指摘（Medium）: 旧 `test_login_to_configured_venue_succeeds` は login を 1 回しか呼ばず「同一 venue 再 login = idempotent」
> を担保していなかった＝監査表の過剰主張。専用 test ＋ login-count litmus で是正。

RED 実測: 反転直後 `test_disconnected_login_to_other_venue_rebinds` が `VENUE_MISMATCH` で FAIL → 実装後 **4 passed**。
idempotent litmus 実測: 短絡を `if False and live_err is None` に潰すと `login_call_count == 2` で FAIL。
回帰: live 系 10 ファイル（`test_live_auto_lifecycle_inproc_server` / `test_kernel_live_*` / `test_order_facade_cancel_ack` /
`test_v19_auto_live_afk` 他・`MockVenueAdapter` consumer 含む）= **103 passed**。

再走:
```
cd python && uv run pytest tests/test_venue_mismatch_inproc_server.py -q
```

### C# — `Assets/Editor/VenueMenuM3Probe.cs` `VenueMenuFilterByLiveVenue` section

未設定/editor=5項目（MOCK dev+4）・未設定/player=4項目（MOCK dev 無）・pinned TACHIBANA=2・pinned kabu=2（大小無視）・
**pinned MOCK=1（editor / player 両方・#106）**。delete-the-filter litmus: `VisibleConnectItems` が絞り込みを止めると
pinned ケースが他 venue を漏らし RED。

## follow-on #106 — player build + `LIVE_VENUE=MOCK` の空メニュー（dead-end）修正

報告: player（非 editor）build で `LIVE_VENUE=MOCK` を明示 pin すると Venue メニューの connect 項目が **0 件**になり
何も接続できない。原因は `VisibleConnectItems` の MOCK 行が `isEditor && (f==null || f=="MOCK")` で gate されていた
こと——pinned MOCK は `f=="MOCK"` だが player では `isEditor==false` で MOCK dev 項目が落ち、実 variant（Tachibana/kabu）は
LIVE_VENUE フィルタ（`v.Venue==f`、MOCK はどの ConnectVariant にも一致しない）で除外され、結果が空リストになる。

修正: gate を `f=="MOCK" || (f==null && isEditor)` に分離。**`isEditor` は「未設定（f==null）の dev 利便項目」だけを
gate し、「pinned MOCK の escape hatch」は gate しない**。MOCK を明示 pin するのは（credential-less dev venue を player に
pin する自己矛盾気味な config だが）明示的な選択なので、メニューは空 dead-end ではなく MOCK connect 1 件だけを honor する。
AC「editor + MOCK は MOCK dev のみ表示（不変）」「未設定・Tachibana/kabu pin（不変）」はラベル・項目集合とも保たれる。

ゲート（RED→GREEN, `VenueMenuM3Probe.VenueMenuFilterByLiveVenue` に player+MOCK section 追加）:
- RED 実測（2026-06-22, fix 前）: `VisibleConnectItems("MOCK", isEditor:false)` → `got 0`、probe exit 1。
- GREEN 実測（fix 後）: player+MOCK=1（`Venue=="MOCK"`）、`[VENUE MENU M3 PASS]` exit 0・compile gate `error CS` 0。
- litmus: `isEditor` を pinned-MOCK 経路に戻す（`isEditor && (f==null || f=="MOCK")`）と player+MOCK が 0 に戻り RED。

再走（macOS・絶対パス logFile・初回はコンパイルのみで 2 回目に走る罠に注意）:
```
UNITY=/Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity
# compile gate（error CS 0 を確認）
"$UNITY" -batchmode -quit -projectPath /Users/sasac/backcast -logFile /tmp/compile_gate.log
# probe
"$UNITY" -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
  -executeMethod VenueMenuM3Probe.Run -logFile /tmp/venue_probe.log
grep -aE "VENUE MENU M3 (PASS|FAIL)" /tmp/venue_probe.log
```
実測（2026-06-22）: compile gate exit 0 / `error CS` 0、probe `[VENUE MENU M3 PASS]` exit 0。

## 台帳登録（E2E coverage audit）

- `MenuBarE2ERunner.md` に **MENU-19**（Venue メニューの LIVE_VENUE 絞り込み）を追加＝`自動(Probe有・要昇格)` →
  `VenueMenuM3Probe.VenueMenuFilterByLiveVenue`。MENU-12（出現変種の enable/grey-out）と直交（MENU-19 は *出現集合*）。
- `E2E-INDEX.md` の MenuBar 行を `MENU-01..19`／総数 19／Probe有 8 に更新（Surface 合計 216 行）。
- engine 再バインド（VENUE_MISMATCH 撤去）の正本は本 findings ＋ Python seam `test_venue_mismatch_inproc_server.py`。
  C# 台本（MENU-19）は menu 出現集合のみを担当＝2 ゲート分割（engine=pytest／menu filter=probe）。継ぎ目の
  `OnVenueConnect→host.VenueLogin` 配線は本 slice で不変（generic pythonnet marshaling）なので追加 C# runner は不要。

## 関連

- [[ADR-0021]]（方針・固定）／findings 0014（D26 one-per-server の元実装）／findings 0027（mainline Venue メニュー cutover）。
- `.env.example` の `LIVE_VENUE` コメントを「ロックではなく初期選択＋メニュー絞り込み」に更新。
