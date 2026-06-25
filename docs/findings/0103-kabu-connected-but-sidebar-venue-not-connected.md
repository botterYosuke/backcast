# 0103 — kabu ログイン済みなのにサイドバーが「Venue not connected」（ステータス取り違え）

- **報告日**: 2026-06-25（owner 目視）
- **症状**: kabuStation にログイン成功し、メニューバー badge は `Connected: KABU`。にもかかわらず左
  サイドバー Instruments の [+ Add] picker が **`Venue not connected`** を表示し、メニューと矛盾する。
- **区分**: correctness / UX バグ（ステータスの mislabel）。挙動修正＋E2E 回帰ゲート化（`/behavior-to-e2e`）。
- **owner 判断（2026-06-25）**: 修正は「正確な文言に直すだけ」。kabu でのコード直接入力（free-form add）は
  本スライス対象外。

## 不変条件（言葉→観測点）

> **venue にログイン済み（badge が `Connected: <venue>`）なら、サイドバー picker は決して未ログイン用の
> `Venue not connected` を出さない。** 銘柄マスタを列挙できない venue（kabu MVP）は、未接続とは別の正確な文言
> （`Venue has no instrument list`）を出す。

## 根本原因

2 つの独立した SoT がそれぞれ接続状態を判定しており、片方が状態を取り違えていた。

1. **メニューバー badge**（正しい）— `VenueMenuViewModel.BadgeText` は `VenueConnectionViewModel.VenueState`
   （poll 由来）が `CONNECTED` を見て `Connected: KABU` を出す。ログインは実際に成功している。

2. **サイドバー picker**（Live モード）— `BackendAvailableInstrumentsProvider.Query("live","")` →
   Python `_backend_impl._list_instruments_live()`:
   - `runner.is_logged_in()` は **True**（通過）。
   - `adapter.enumerates_instruments` を見る。**kabu アダプタは `enumerates_instruments = False`**
     （`exchanges/kabusapi.py:69`。kabuStation API は銘柄マスタ列挙に未対応で `fetch_instruments()` が
     `[]`＝issue #253）。
   - → `error_message="LIVE_UNIVERSE_UNSUPPORTED"` を返す（engine は**正しく**未ログインと別コードを返している）。

3. **C# `MapError`（バグの所在）** — `BackendAvailableInstrumentsProvider.cs` が
   `LIVE_VENUE_NOT_LOGGED_IN` と `LIVE_UNIVERSE_UNSUPPORTED` を**両方とも** `AvailableInstrumentsResult.NotConnected`
   に潰していた。

4. `InstrumentPickerController.BuildList` が `NotConnected` → `"Venue not connected"` を描画。

→ 「ログイン済みだが銘柄非列挙の venue（kabu）」が「未ログイン」と同じ文言にされ、badge と矛盾した。
この `LIVE_UNIVERSE_UNSUPPORTED` 経路は **Python にも C# にもテストが皆無**だった。

## 修正

- `AvailableInstruments.cs`: `UniverseStatusKind.Unsupported` を新設（`NotConnected` と区別）＋
  `AvailableInstrumentsResult.Unsupported`。
- `BackendAvailableInstrumentsProvider.cs`: `LiveUniverseUnsupported` → `Unsupported`（`NotConnected` から分離）。
  `MapError` を `public static` 化（AFK runner が map を直接 gate できるように）。
- `InstrumentPickerController.cs`: `case Unsupported:` → `"Venue has no instrument list"`。
- `UniversePruneGate` は `!= Ready` で判定するため、新 enum 値追加で破壊的 prune の挙動は不変（無改変）。

## ゲート（RED→GREEN）

エンジン半分は既に正しい（characterization で固定）。RED→GREEN は **C# の map** にある。

### Python（characterization・engine の契約固定）
`python/tests/test_live_instrument_universe_unsupported.py`（新規・自己完結・fake runner/adapter）:
- `test_kabu_adapter_declares_no_instrument_enumeration` — `KabuStationAdapter.enumerates_instruments is False`（根本事実）。
- `test_logged_in_kabu_returns_universe_unsupported_not_not_logged_in` — ログイン済み kabu → `LIVE_UNIVERSE_UNSUPPORTED`。
- `test_no_session_returns_not_logged_in` — セッション無し → `LIVE_VENUE_NOT_LOGGED_IN`（2 コードが別物＝非空虚）。
- `test_enumerating_venue_does_not_short_circuit_to_unsupported` — `enumerates_instruments=True` は guard を通過し
  fetch 経路へ（store-read を空に monkeypatch して決定論化）＝short-circuit が `enumerates_instruments` 限定であることを固定。
- 実行: `cd python && uv run pytest tests/test_live_instrument_universe_unsupported.py -v` → **4 passed**。

### C#（AFK・`UniverseSidebarE2ERunner`）
- `Section2_StatusPlaceholders`: `Unsupported` → `"Venue has no instrument list"` を追加（`NotConnected` の `"Venue not connected"` と別文言）。
- `Section16_UnsupportedDistinctFromNotConnected`（新規・SIDEBAR-20）:
  - `MapError(LIVE_VENUE_NOT_LOGGED_IN) == NotConnected`、`MapError(LIVE_UNIVERSE_UNSUPPORTED) == Unsupported`。
  - picker ラベルが両者で別文言。
  - **RED→GREEN litmus**: `MapError` の `LiveUniverseUnsupported` を `NotConnected` に戻すと
    Section16 が `still collapses into NotConnected — ... bug is back` で FAIL。
- PASS タグ: `[E2E UNIVERSE SIDEBAR PASS] ... + unsupported-distinct-from-notconnected verified`。

## 台帳

- `UniverseSidebarE2ERunner.md`: SIDEBAR-09 を Unsupported 込みに更新、**SIDEBAR-20** 新設（C# map ＋ engine test の二重 gate）。
- `E2E-INDEX.md`: UniverseSidebar 行を `SIDEBAR-01..20` / 行数 20 / 自動(E2E済) 18 に更新。

## 再走手順

```
# engine 契約
cd python && uv run pytest tests/test_live_instrument_universe_unsupported.py -v

# C# AFK（recompile-skip に注意＝.cs 編集直後の初回は compile のみ、2 回目で実走）
& $env:UNITY_EDITOR_PATH -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast `
  -executeMethod UniverseSidebarE2ERunner.Run -logFile C:\Users\sasai\Documents\backcast\Temp\Unity_E2E.log
# expect: ログに [E2E UNIVERSE SIDEBAR PASS] / exit=0、error CS は 0 件
```
