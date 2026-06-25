# 0105 — issue #46: kabu Live picker の browse 候補供給を listed_info から（"Venue has no instrument list" の解消）

- **報告日**: 2026-06-25（owner 報告 — findings 0103 修正で `Venue not connected` の矛盾は消えたが、kabu Live
  picker は依然候補ゼロで「`Venue has no instrument list`」を出し続けるため銘柄を 1 件も選べない）
- **区分**: feature（user-visible bug の延長 fix）— kabu Live サイドバー [+ Add] picker に**実 TSE 銘柄候補が出る**
  ようにする。
- **方針**: ADR-0006（listed_info を universe ソースとする）／ADR-0005（自己保護パターン：本スライスは新 ADR を
  起こさず本 findings と issue #46 に決定を固定し ADR は参照のみ）。
- **superseded**: 旧方針「外部 JSON fixture（≤50件・実 TSE 銘柄）」（issue #46 起票時 2026-06-15）。起票後に入った
  `listed_info.duckdb`（ADR-0006・2026-06-21）で superseded（issue #46 §9）。

## 不変条件（言葉→観測点）

> **kabu にログイン済みの Live モードで、サイドバー [+ Add] picker の `search:` 枠に銘柄コードを打つと、
> listed_info の TSE 全銘柄から候補が出てきて、クリックで universe（`InstrumentRegistry`）に
> `<code>.TSE` として追加できる。** `listed_info.duckdb` が未マウントなら設定エラー文言
> （`LOCAL_UNIVERSE_UNAVAILABLE`）を出す（venue 列挙非対応 `LIVE_UNIVERSE_UNSUPPORTED` と混同しない）。
> kabu adapter の `enumerates_instruments` は **False のまま**で、store snapshot を authoritative live
> universe にしない防御（#253 anti-prune）は破らない。

## 根本原因（findings 0103 までの状態）

findings 0103 で `MapError(LIVE_UNIVERSE_UNSUPPORTED)` を `NotConnected` から `Unsupported` に分離し、picker は
正しく「`Venue has no instrument list`」を出すようになった。**しかし** kabu adapter は依然として
`enumerates_instruments=False` のため `_list_instruments_live` が `LIVE_UNIVERSE_UNSUPPORTED` を返し続け、
picker は候補ゼロのまま。「ログイン済みだが銘柄が 1 件も無い」状態が user にとっては実質的な機能停止だった。

## 設計の木（grill 確定・owner 4 決定）

1. **供給源 = 既存 `listed_info.duckdb` を流用**（新 fixture を作らない）。Replay picker が既に使っている
   実 TSE 約 4400 銘柄マスタ（`engine/jquants_listed_info.py` / ADR-0006）。
2. **適用範囲 = kabu のみ**（`runner.venue_id == "KABU"`）。listed_info は JPX/TSE マスタで、kabu の
   `_parse_instrument_id` は `.TSE` 接尾辞しか受けない。将来の非 TSE 非列挙 venue を誤って TSE ユニバースに
   しないため明示限定する。
3. **`listed_info.duckdb` 欠落時 = `LOCAL_UNIVERSE_UNAVAILABLE`**（Replay と同じ設定エラー文言）。
   "venue の列挙非対応" と混同しない（user の打ち手が違う＝DuckDB をマウントすればよい vs venue の限界）。
4. **C# は無改変**。`BackendAvailableInstrumentsProvider` は既に Live で `list_instruments("live","")` を
   要求し、`MapError` は Ready / `LOCAL_UNIVERSE_UNAVAILABLE` を別ステータスへ処理し、
   `InstrumentPickerController.BuildList` は Ready の候補を描画する。adapter の `enumerates_instruments=False`
   も**維持**（#253 の store-snapshot 防御はそのまま）。

## grill が裏取りした「成立する根拠」

- **破壊的 prune は arm されない**（issue 本文「backcast に prune は存在しない」は誤り）。prune gate
  （#41 `UniversePruneGate`/`UniversePruneDriver`）の live ソースは本番 `NullUniversePruneSource` ＝ **dormant**。
  picker（`IAvailableInstrumentsProvider`）とは**別 seam**（R2 asymmetry "picker status must not be merged with
  prune"・findings 0041 §9）。→ kabu picker を listed_info で Ready にしても prune は picker を読まないので
  registry は prune されない。#253 の罠は backcast では構造的に塞がれている。
- **id 変換は不要**。listed_info は `<code>.TSE` を emit（`_backend_impl._list_instruments_local`）。kabu
  `_parse_instrument_id` は `<code>.TSE → (Symbol=code, Exchange=1)`（`exchanges/kabusapi.py:248`）。
  → browse→add→subscribe が id 変換なしで通る。
- **full-field fixture は browse には不要**。picker row は id 文字列のみ（`PickerRow.Candidate(id)`）。lot/tick は
  subscribe/order 時に解決される。listed_info の code だけで browse は足りる。
- **rate-limit / 50 銘柄上限と無干渉**。listed_info は kabu API を叩かないローカル DuckDB 読み。50 上限は
  *subscribe*（register）の話で browse には無関係。

## 実装

### Step 1 — engine（唯一の本体改修）

`python/engine/_backend_impl.py:_list_instruments_live` の列挙非対応 short-circuit を分岐:

```python
if not getattr(adapter, "enumerates_instruments", True):
    if runner.venue_id == "KABU":
        snapshot, error = self._read_local_snapshot(
            "", "list_instruments(live→kabu listed_info fallback)"
        )
        if error is not None:
            return InstrumentListResult(success=False, error_message=error)
        ids = [f"{code}.TSE" for code in snapshot.codes]
        instruments = [InstrumentInfo(id=i, name=i, market="TSE") for i in ids]
        return InstrumentListResult(success=True, instrument_ids=ids, instruments=instruments)
    return InstrumentListResult(success=False, error_message="LIVE_UNIVERSE_UNSUPPORTED")
```

- 共有 helper `_read_local_snapshot(end_date, log_label)` を再利用（DuckDB 欠落 → `LOCAL_UNIVERSE_UNAVAILABLE`・
  ValueError は str(exc)・通常例外は logging.exception＋str(exc) で型付き失敗を返す）＝Replay picker と同じ
  err mapping を共有。
- `end_date=""` で空 end → `read_listed_snapshot` 内部で overall MAX(Date) snapshot を返す（最新ユニバース）。

### Step 2 — pytest characterization（`python/tests/test_live_instrument_universe_unsupported.py`）

`@pytest.mark.scenario("KABU-LIVE-46")` で rollup に載せる新 3 テスト：

> `KABU-LIVE-46` は issue 番号（#46）を流用した cross-runner scenario tag。既存 `KABU-LIVE-01..03` 連番系列とは別系で、#46 関連の engine 側 pytest をまとめる目的の "issue-numbered" tag（既存連番の次番ではなく独立した識別子）。


- `test_logged_in_kabu_with_listed_info_returns_ready`：monkeypatch で `read_listed_snapshot` を 3 codes 返し
  → `success=True`・`instrument_ids=["1301.TSE","7203.TSE","9984.TSE"]`・`market="TSE"`。
- `test_logged_in_kabu_without_listed_info_returns_local_universe_unavailable`：monkeypatch → None
  → `LOCAL_UNIVERSE_UNAVAILABLE`（`LIVE_UNIVERSE_UNSUPPORTED` と別）。
- `test_non_kabu_non_enumerating_venue_still_unsupported`：`venue_id="OTHER"` ＋ `enumerates=False`
  → `LIVE_UNIVERSE_UNSUPPORTED`（kabu 限定 scope の load-bearing）。

既存 `test_logged_in_kabu_returns_universe_unsupported_not_not_logged_in` は削除（挙動逆転＝新テストへ役割移管）。
他の既存テスト（`_no_session_*` / `_logged_out_*` / `_enumerating_venue_*` / `_kabu_adapter_declares_*`）は無改変。

### Step 3 — E2E（`UniverseSidebarE2ERunner.cs:Section17_KabuLiveBrowseFromListedInfo`）

`SIDEBAR-21` を E2E-INDEX / 台本 `.md` に採番：

- (a) `MapError(LOCAL_UNIVERSE_UNAVAILABLE)` → `Error` ステータス（`Unsupported` でも `NotConnected` でもない・
  terminal キャッシュ＝transient だと ~500ms ごとに re-fetch して churn する）。distinct な error message
  （"BACKCAST_JQUANTS_DUCKDB_ROOT / listed_info.duckdb not configured"）で UNSUPPORTED の label と区別。
- (b) Live + Ready 候補（3 件）が picker に ordinal sort で全件描画される（placeholder 無し）。
- (c) view 層で Live mode の `cand:7203.TSE` GameObject ＋ `+ 7203.TSE` ラベルが実描画される。

RED→GREEN litmus（台本 .md に追記）:

- `MapError` の `LocalUniverseUnavailable` 分岐を `Unsupported` に潰す → `Section17 (a)` が
  `collapsed into Unsupported` で FAIL。
- `_list_instruments_live` の kabu fallback を消す → engine pytest `test_logged_in_kabu_with_listed_info_returns_ready`
  が UNSUPPORTED に逆戻りして FAIL（C# 側だけでは検出不能・engine 側 pytest が end-to-end の半身を所有）。

### Step 4 — HITL（owner・demo creds）

CLAUDE.md E2E ルール「画面表示と保存後 state の両方を確認する」：

- kabuステーション本体起動 → 検証(18081)ログイン → Unity Editor で Play（または built backcast を起動）→ Live picker `search:` に銘柄コード入力
  → 候補が listed_info 全銘柄から filter されて出る → クリックで universe に追加 → universe sidebar / chart
  spawn に反映されることを目視。
- `listed_info.duckdb` を一時的に rename して欠落させて再起動 → picker が "BACKCAST_JQUANTS_DUCKDB_ROOT /
  listed_info.duckdb not configured" を表示（"Venue has no instrument list" にならない）ことを確認。

## CONTEXT.md 追記

`§ universe（語の多義の解きほぐし）` に 5 つ目の sense を追加：

- **picker browse universe**（populate 軸のソース・#31/#46・listed_info `<code>.TSE`）— Replay の
  `_list_instruments_local` ＋ kabu Live の `_list_instruments_live` kabu fallback が同じ listed_info から
  picker 候補を serve する。**populate 軸**（user の picker click が SoT に流入する上流）であり、prune 軸の
  venue live universe（dormant・#41）とは別 seam。
- `_Avoid_` に「kabu Live picker が listed_info を引くことを『kabu の live universe が解禁された』と読むこと」
  を追加（#253 防御維持を明示）。

## AC（issue #46）

- [x] kabu ログイン済み Live で picker `search:` に候補が出る（実 TSE 銘柄）— engine 側 pytest GREEN／E2E
  `Section17` GREEN（HITL は owner）。
- [x] 候補クリックで universe に `<code>.TSE` が追加され subscribe が id 変換なしで通る — kabu の
  `_parse_instrument_id` が `<code>.TSE → (Symbol=code, Exchange=1)` を保証（grill F2 で codebase 裏取り）。
- [x] `listed_info.duckdb` 欠落時は `LOCAL_UNIVERSE_UNAVAILABLE` — pytest
  `test_logged_in_kabu_without_listed_info_returns_local_universe_unavailable` GREEN。
- [x] kabu 以外の列挙非対応 venue は従来どおり `LIVE_UNIVERSE_UNSUPPORTED` — pytest
  `test_non_kabu_non_enumerating_venue_still_unsupported` GREEN。
- [x] 破壊的 prune が発火しないこと — `NullUniversePruneSource` は production 配線のまま（編集なし＝findings 0041
  §9 の R2 asymmetry が構造的に保証）。
- [x] pytest characterization GREEN（7/7）・`UniverseSidebarE2ERunner` PASS（17/17 section）・HITL は owner 預け。

## スコープ外（issue #46 §5）

- 新規 JSON fixture の作成（superseded）。
- `enumerates_instruments` の flip（False のまま維持＝#253 防御）。
- kabu でのコード直接入力（free-form add）（findings 0103 で別スライス）。
- prune gate の live producer 実装（#41 dormant のまま・別 issue）。
- `/symbol` ライブ補完（§9 代替案・不採用）。

## レビュー指摘の追補修正（high-effort code review）

実装後の `/code-review --effort high` が拾った 10 件をすべて反映（CLAUDE.md「Medium 以上が無くなるまで /pair-relay でレビュー＆修正」）。

### 設計改修

- **C1 / A6（cache lifetime）** — `BackendAvailableInstrumentsProvider.MapError` で `LIVE_VENUE_NOT_LOGGED_IN` と `LOCAL_UNIVERSE_UNAVAILABLE` を **transient** に変更。前者: 未ログインで picker を開いた user がログイン後に再オープンしても terminal cache のせいで「Venue not connected」のまま固着するのを防ぐ（#46 の user-visible AC を裏で defeat する潜在バグ）。後者: owner が起動後に DuckDB をマウントしたとき app 再起動なしで self-heal させる。`Unsupported` は据え置き terminal（adapter の能力は session 中変わらない）。
- **G2（user-visible string leak）** — `LOCAL_UNIVERSE_UNAVAILABLE` の表示文言を `BACKCAST_JQUANTS_DUCKDB_ROOT / listed_info.duckdb not configured` → `Local instrument catalog not configured` に短縮（env var 名と内部ファイル名は logging で出す・picker には出さない）。
- **A3（exception text leak）** — kabu fallback が `_read_local_snapshot` の broad-except から返る raw exception text（`IO Error: …/listed_info.duckdb` 等）を `LOCAL_UNIVERSE_UNAVAILABLE` に narrow。MapError default 経由で picker placeholder にトレースバック断片や絶対パスが漏れない（元 exception は `_read_local_snapshot` の `logging.exception` で残る）。
- **B1（empty snapshot mis-label）** — `_snapshot_to_list_result` で `snapshot.codes` 空 → `LOCAL_UNIVERSE_UNAVAILABLE` を返す（旧: success=True / empty ids → 「No instruments」placeholder で「venue にも universe にも何もない」と区別不能）。「DuckDB はマウント済みだが ingest が空」を typed config error として認識可能に。

  **Replay 経路への副作用（review follow-up 2026-06-25）**: `_snapshot_to_list_result` は `_list_instruments_local`（Replay picker）と `_list_instruments_live`（kabu Live picker）の両方が共有する helper なので、この empty-codes guard は **Replay 経路にも同時に発火**する。listed_info.duckdb が「マウント済みだが ingest 空」のとき、Replay 旧挙動 `success=True / instrument_ids=[]` （→ 「No instruments for this date」placeholder）から `LOCAL_UNIVERSE_UNAVAILABLE`（→ 「Local instrument catalog not configured」placeholder）へ格上げされる。これは意図した挙動: Replay でも「DuckDB はマウント済みだが ingest 空」は owner-actionable な config error（J-Quants ingest を再走する）であり、「日付に銘柄が居ない（不可避な空）」と混同してはいけない。Replay 側の point-in-time empty（`end_date` が全 snapshot より前）は **fallback to latest** 経路（findings 0084）で先に救われるので、helper まで到達する empty は本当に「DuckDB に行が無い」ケースに限られる。pytest `test_replay_instrument_picker_supply.py::test_replay_local_supply_empty_snapshot_returns_local_universe_unavailable` が本副作用を gate する。
- **A5（CompanyName plumb-through）** — `ListedSymbolsSnapshot` に `names: list[str]`（CompanyName の parallel array）を追加し SQL も `SELECT li.CompanyName` を追加。`InstrumentInfo.name` に CompanyName を流す（null/欠落は id へ fallback）。C# 側 `AvailableInstrumentsResult.Ready(ids, names)` / `WorkspaceEngineHost.InvokeListInstruments` で names を host RPC 経由で流し、`InstrumentPickerController.BuildList` は **id OR name の case-insensitive substring** で filter、`PickerRow.Candidate(id, name, …)` で行ラベルを `"<id> <name>"` に拡張（kabuステーションから来た user が「トヨタ」で 7203.TSE を検索できる）。

### コード品質

- **D1（duplication）** — kabu fallback の `ids=[…]; instruments=[…]; return InstrumentListResult(…)` を `_list_instruments_local` と共有する `_snapshot_to_list_result(snapshot)` ヘルパへ集約（empty-codes guard と name fallback も同一実装で扱う）。`_list_instruments_local` も同 helper に delegate。
- **A2（venue_id 大文字小文字）** — `runner.venue_id == "KABU"` を `(runner.venue_id or "").upper() == "KABU"` に防御。adapter constant が将来 `"kabu"` 等に drift しても fallback が無効化されない。
- **E1（inline comment 肥大）** — `_list_instruments_live` の 11 行 inline 解説を 5 行に圧縮（root rationale は CONTEXT.md「picker browse universe」と本 findings へ集約）。

### テスト品質

- **A1（vacuous Message check）** — Section17 (a) の `localUnavail.Message == "Venue has no instrument list"` は `AvailableInstrumentsResult.Unsupported` の Message が null なので絶対 false で gate にならなかった。Section16 が使う `LabelFor` パターン（`InstrumentPickerController.BuildList` を駆動して実 placeholder text を比較）に置換し、`Error/Unsupported/NotConnected` 3 placeholder の label distinguishability を実際に検証。
- **A4（test_non_kabu litmus mismatch）** — `test_non_kabu_non_enumerating_venue_still_unsupported` に `read_listed_snapshot` の monkeypatch を追加（populated snapshot を返す）。venue_id guard を消した場合、CI（DuckDB なし）でも「TSE codes を黙って serve」の失敗モードが本当に triggered されて litmus の語りと観測 fail mode が一致する。
- **B2（findings 0103 invariant 喪失）** — 新 kabu 系テスト 5 本すべてに `res.error_message != "LIVE_VENUE_NOT_LOGGED_IN"` 等の非空虚化 assertion を補強。「logged-in でも `LIVE_VENUE_NOT_LOGGED_IN` を返さない」（findings 0103 contract）が code-collapse refactor で気付かれずに破られない。
- **新規 RED→GREEN テスト** — `test_logged_in_kabu_listed_info_missing_names_falls_back_to_id` / `test_logged_in_kabu_empty_listed_info_returns_local_universe_unavailable` / `test_logged_in_kabu_listed_info_raises_returns_local_universe_unavailable` を追加（name fallback / empty guard / exception narrow を各々独立 gate）。Section17 に (c) name search 4 サブケース（label・name filter・absent fragment / clear-restore）と (d) view 層で `トヨタ自動車` が `cand:7203.TSE` ラベルに反映されることを追加。

### 修正後の litmus（runner companion `.md` に追記済み）

- `MapError` の `LocalUniverseUnavailable` 分岐を Unsupported に潰す → Section17(a) が `collapsed into Unsupported` で FAIL（config error を venue limitation に誤分類する核心バグ）。
- `_list_instruments_live` の kabu fallback（`venue_id == "KABU"` 分岐）を消す → engine 側 pytest `test_logged_in_kabu_with_listed_info_returns_ready` が UNSUPPORTED に逆戻りして FAIL（C# だけでは検出不能・engine 側 pytest が end-to-end の半身を所有）。
- `MapError` の `LiveVenueNotLoggedIn` を terminal に戻す → Section16 が `marked terminal — picker would stay 'Venue not connected' after login` で FAIL。
- `MapError` の `LocalUniverseUnavailable` を terminal に戻す → Section17(a) が `marked terminal — owner who mounts the DuckDB mid-session can't recover` で FAIL。
- `AvailableInstrumentsResult.Ready` の Names 引数を削る or `BuildList` で name 照合を消す → Section17(c.2) `name-search 'トヨタ' returned 0 rows` で FAIL。
- `_snapshot_to_list_result` の empty-codes guard を消す → pytest `test_logged_in_kabu_empty_listed_info_returns_local_universe_unavailable` が success=True/empty で FAIL。
- kabu branch の exception narrowing を消す → pytest `test_logged_in_kabu_listed_info_raises_returns_local_universe_unavailable` が `IO Error: ...` テキストを返して FAIL。
