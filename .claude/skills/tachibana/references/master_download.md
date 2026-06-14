# マスタダウンロード（CLMEventDownload）の特殊ルール

`CLMEventDownload` は他の REQUEST と流れが違う:

- ストリーム形式（Python の `urllib3` で `preload_content=False` 相当、または httpx の `stream` API）で全量配信。
- 1 レコードの終端は `}`、**全体の終端はレコード `{"sCLMID":"CLMEventDownloadComplete", ...}` の到着**。Python サンプルは `str_terminate = 'CLMEventDownloadComplete'` を定数化している。
- 接続先は `sUrlMaster`（`sUrlRequest` ではない — [`e_api_get_master_tel.py:578-580`](../samples/e_api_get_master_tel.py/e_api_get_master_tel.py#L578)）。
- `sJsonOfmt` は `"4"` を使う（1 行 1 JSON 形式、ファイル保存・後続パース向け。`"5"` を使うと区切れなくなる）。
- 受信チャンクをバイト列で蓄積し `byte_data[-1:] == b'}'` で 1 レコード分として Shift-JIS デコード → `json.loads`（[`e_api_get_master_tel.py:492-518`](../samples/e_api_get_master_tel.py/e_api_get_master_tel.py#L492)）。
- データ量が大きいため、メモリ展開ではなくストリーム処理を守ること。
- **遅延が大きい（落とし穴 / Issue #32）**: 全量 master DL は数十秒〜分オーダーになりうる。adapter は
  `_MASTER_READ_TIMEOUT = 600.0` を予算化している。これを呼ぶ同期経路
  （`server_grpc` の `ListInstruments(live)` store-miss fallback → `LiveRunner.fetch_instruments_blocking`）に
  **共有の短い timeout（`_live_timeout_s = 5s`、login/account/subscribe 用）を流用してはいけない**。
  5s だと `concurrent.futures.TimeoutError`（`str()` が空）になり UI に空の `Error: fetch_instruments failed:`
  を出す。専用の長い timeout（`_instruments_timeout_s`）を使うこと。
- **二重 DL に注意**: `InstrumentsScheduler` の login 直後 refresh と、UI の `[+ Add]`（picker）が同時に
  `fetch_instruments()` を呼ぶと master DL が 2 本走りうる。adapter 側で singleflight（in-flight task を
  `asyncio.shield` で共有 + done-callback で `_instruments_inflight` を消す）を持たせて 1 本に集約する。

## マスタデータ識別子（`sTargetCLMID`）

- `CLMIssueMstKabu` 銘柄マスタ（株）
- `CLMIssueSizyouMstKabu` 銘柄市場マスタ（株）
- `CLMIssueMstSak` 銘柄マスタ（先物）
- `CLMIssueMstOp` 銘柄マスタ（OP）
- `CLMIssueMstOther` 日経平均・為替など
- `CLMOrderErrReason` 取引所エラー理由コード
- `CLMDateZyouhou` 日付情報

## 株式銘柄ビルドの field 名（⚠️ order 系と別名・#36 で実バグ確認）

`build_instruments_from_master_records`（`tachibana_master.py`）が読む master
record の field 名は、order/余力系の field 名と **違う**。混同すると 1 銘柄も
ビルドされず `fetch_instruments()` が実質 `[]` を返す（manual
`mfds_json_api_ref_text.html` のレコード定義で確認 / #36）:

| 欲しい値 | 正しい field | どの record | 注意 |
| :--- | :--- | :--- | :--- |
| 上場市場 | `sZyouzyouSizyou` | `CLMIssueSizyouMstKabu` | `00`=東証（現状これのみ）。`market_to_suffix` で suffix 化 |
| 売買単位（既定） | `sBaibaiTani` | **`CLMIssueMstKabu`** | 正本はこちら（issue 単位、例 `"100"`） |
| 売買単位（市場別上書き） | `sSizyoubetuBaibaiTani` | `CLMIssueSizyouMstKabu` | **サンプルは空 `""`** が多い。非空のときだけ上書き |
| 呼値コード | `sYobineTaniNumber` | `CLMIssueSizyouMstKabu` | CLMYobine テーブル参照キー |
| 銘柄名 | `sIssueName` | `CLMIssueMstKabu` | |

**落とし穴**: `sSizyouC`（市場）/ `sBaibaiTaniNumber`（売買単位）は **order 系
record（`CLMKabuNewOrder` / `CLMZanKaiKanougaku` / `CLMOrderList` 等）にしか
存在せず**、株式 master record には無い。`sBaibaiTaniNumber` は manual 全体に
1 箇所も無い。移植元 The-Trader-Was-Replaced も同じ誤読を運んでいたので、master
build を触るときは必ずこの表で field 名を裏取りすること。売買単位は文字列で来る
ため `"0"` / 負値 / 非数値は無効として弾く（0 単位は下流で除算 0 を起こす）。

**実データ事実（demo master 全量 DL / #36 検証）**: manual は `sZyouzyouSizyou`
を `00`=東証 のみ規定するが、**実ストリームには `02`/`05`/`07` が実在**する
（skip 件数 02:316・05:138・07:70、推定 名証/福証/札証 だが一次裏取り未済）。
`market_to_suffix` は `00`→`TSE` のみ写像し他は fail-closed skip するため、
**東証 4450 件は取れるが地方単独上場 106 銘柄は欠落する**。実コードの取引所確定と
suffix 追加は #45（推測でなく重複上場トリアングで裏取り）。地方コードを推測で
マップしないこと（誤同定は skip より危険）。
