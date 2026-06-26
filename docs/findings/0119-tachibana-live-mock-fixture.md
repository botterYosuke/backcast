# findings 0119 — tachibana live を回帰テストする mock fixture（実 venue 採取・codec-replayable）

> **番号注記**: 本 finding は当初 `0118` で採番されたが、同日 commit 372f1e9 の
> `0118-live-universe-churn-kabu-mock.md`（live universe churn gate）と番号衝突したため `0119` へ改番した
> （grill-with-docs「finding 番号の重複を確認」）。churn gate（0118）はこの fixture を tachibana のデータ供給層として使う。

2026-06-26。findings 0117（kabu mock）の tachibana 版。live チャート/集約の挙動を
**実 venue に繋がず決定的に**回帰テストするための mock fixture と、その採取・再生
ツールを整備した。**他の作業者はこれを正本として使うこと。**

## なぜ mock が要るか（kabu と同型 + tachibana 固有）

live 集約パイプライン（adapter codec → aggregator → reducer → chart）の回帰を
実 tachibana で回すのは不可能/不安定:

- **Windows + 立花 e支店 ID + ザラ場**が前提（CI 不可・tachibana skill R1 / S0）。
- demo 環境（`demo-kabuka.e-shiten.jp`）が FD push を流すかは未確認——流さなければ
  prod 採取しかない（kabu の verify 18081 と同型のリスク）。
- 約定の到着は非決定的——assert が書けない。
- 第二暗証/公開鍵認証/per-ticker WS の lifecycle を全部立てないと live 経路に入れない。

→ **一度だけ実 venue（demo or prod）から採取し、その FD frame ストリームを再生する**のが唯一の決定的経路。

## kabu (findings 0117) との非対称点

| 項目 | kabu | tachibana |
|---|---|---|
| 採取 seam | `adapter._on_frame(msg: dict)` 1 点 wrap | `adapter._make_callback` を wrap（per-ticker WS hub × N の `_cb` を統一録音） |
| frame の型 | parsed JSON dict | `(frame_type, fields:dict[str,str], recv_ts_ms)`（Shift-JIS bytes → `^A^B^C` parse 済み） |
| 録音 record キー | `frame` | `instrument_id` + `frame_type` + `fields` |
| 環境 | prod(18080) 必須（verify 無配信） | demo 既定 / prod は `TACHIBANA_ALLOW_PROD` 不要（ADR-0027 で廃止）—— prod creds が解決できれば本番接続 |
| 認証 | `PROD_KABU_API_PASSWORD` 1 個 | v4r9 公開鍵認証: `DEV_TACHIBANA_AUTH_ID(_DEMO)` + `DEV_TACHIBANA_PRIVATE_KEY_PATH(_DEMO)` |
| cleanup | `logout` → `PUT /unregister/all`（body グローバル＝live アプリ要閉じ） | `logout` は session 破棄のみ（per-ticker `hub.aclose`、body グローバル副作用なし）。ただし prod 採取中は本番セッション 1 本占有 |
| frame_type | 1 種（depth+trade 同梱の push dict） | FD（depth+trades）/ KP（keepalive）/ ST（status・エラー）/ EC（注文約定通知）/ SS（システム状態）/ US（運用状態） |

## 正本（commit 済み）

| パス | 役割 |
|---|---|
| `python/tests/fixtures/tachibana_live_mock_4sym.json` | **テスト入力の正本**。demo 採取（2026-06-26 11:11 JST・7203/8306/9984/285A・150 秒）から FD frame のみを抽出した **codec-replayable mock**（91KB / 406 frames）。`p_date`, `p_cmd`, `p_1_DPP`, `p_1_DV`, `p_1_DPP:T`, `p_1_GBP1`, `p_1_GBV1`, `p_1_GAP1`, `p_1_GAV1` の 9 キーを保持（FdFrameProcessor の trade 抽出＋best-quote 1 段＝side classification に必要十分） |
| `python/spike/tachibana_capture_mock.py` | **採取ツール**（read-only）。`demo|prod` を引数で切替、demo 既定。`adapter._make_callback` を wrap して全 ticker × 全 frame_type（FD/KP/ST/SS/US/EC）を 1 ストリームに集約。raw 全 frame（10 段板含む）を `python/spike/captures/tachibana_mock_<UTC>.json` に書く。**raw は .gitignore**（必要時に再生成） |
| `python/spike/tachibana_replay_multi.py` | mock を実パイプラインで再生し**4 銘柄同時更新**を検証（`FdFrameProcessor`（ticker 別）→ `TickBarAggregator` → `reducer` の `per_id_ohlc_points` が iid 別に育つ＝Unity per_instrument の真値）。FD のみ再生、KP/ST/EC/SS/US は skip カウントだけ表示 |

## 規約（live 由来の挙動をテストするとき）

1. **実 venue に繋がない**。`tachibana_live_mock_4sym.json`（または raw capture）を
   読み、`FdFrameProcessor` → `TickBarAggregator(interval=Minute)` →
   `live_kline_to_reducer_kline` → `apply_event` で再生する（production と同じ
   実クラス。再実装しない）。
2. partial-push は `live_orchestrator` と同じ毎 1.0s・変更検出ガード付き
   （`tachibana_replay_multi.py` がリファレンス実装）。
3. **EC frame（注文約定通知）は read-only 採取では発生しない**。約定経路の mock
   が欲しい場合は別 spike で demo 少量発注 → EC frame 採取（本 findings スコープ外）。
4. これを `behavior-to-e2e` の正本（AFK Unity probe + pytest + Action-ID rollup）に
   載せて自動ゲート化する。Unity 描画側は kabu の `KabuLiveChartRenderE2ERunner`
   （findings 0111）を tachibana fixture 用にミラーする。

## 採取手順（demo 既定）

```
cd python
./.venv/Scripts/python.exe spike/tachibana_capture_mock.py \
    "7203.TSE,8306.TSE,9984.TSE,285A.TSE" 150 demo
# → python/spike/captures/tachibana_mock_<UTC>.json
```

- 0 FD frame だった場合: demo が無配信か場が動いていない可能性。`env=prod` で再採取
  （kabu の verify→prod fallback と同型）。スクリプトが warning を表示する。
- prod 採取中は本番セッションを 1 本占有する。別 GUI を閉じることを推奨。
- **demo は WS が頻繁に `ST p_errno=2` で reconnect する**（2026-06-26 実測: 150 秒で
  conn=#8 まで再接続。それでも 406 FD / 117 depth / 116 trade は採取できる）。raw
  artifact の `frame_type_counts` で ST/SS/US を見ると churn 度合いがわかる。
  prod が同じ churn を出すかは要確認（demo 側 session timeout が短い可能性）。

## 再生確認

```
cd python
./.venv/Scripts/python.exe spike/tachibana_replay_multi.py  # 最新 capture を自動選択
```

期待出力: 4 銘柄すべての `per_id_ohlc_points` が同時に育ち、`all populated concurrently: True`。

## 実測（このセッションで確認済み・2026-06-26 11:11 JST demo 採取）

- 4 銘柄が `per_id_ohlc_points` を**同時充填**: 7203=11pts / 8306=14pts / 9984=50pts /
  285A=30pts、各 3–4 カラム（150 秒で約 3 分足）。軽量 fixture と raw で**完全一致**
  （codec 忠実）。
- 密度差: 9984/285A は partial 47/30 で密、7203/8306 は 9/12 で疎。市場の活性が反映。
- kabu fixture（同条件で 73/62/150/151 pts）よりは疎い—— demo の FD push 頻度が低い
  と WS churn の影響。**fixture としては使えるが、回帰 assertion 値は kabu の数字を
  そのまま流用してはいけない**（venue 別に observed 値を baseline 化する）。

## 軽量 fixture 抽出手順（採取後・初回のみ）

raw capture（`python/spike/captures/tachibana_mock_*.json`）は KP/ST/SS/US と
10 段板を含むため重い（典型 450KB / 150 秒）。コミット用軽量 fixture は
**FD frame のみ + `FdFrameProcessor` が読む 9 キー**に絞る。詳細は
`engine/exchanges/tachibana_ws_codec.py::FdFrameProcessor` 参照（trade は
`p_1_DPP` + `p_1_DV`、side classification は best-quote `p_1_GBP1` / `p_1_GAP1`
のみ参照）。

```python
# 抽出スケッチ（採取後に手で 1 回流す）:
import json
src = json.load(open("python/spike/captures/tachibana_mock_<UTC>.json", encoding="utf-8"))
KEEP = {
    "p_date", "p_cmd",                              # frame envelope
    "p_1_DPP", "p_1_DV", "p_1_DPP:T",               # trade (price, cumulative volume, time)
    "p_1_GBP1", "p_1_GBV1",                         # best bid
    "p_1_GAP1", "p_1_GAV1",                         # best ask
}
light_frames = []
for f in src["frames"]:
    if f["frame_type"] != "FD":
        continue
    fields = {k: v for k, v in f["fields"].items() if k in KEEP}
    light_frames.append({**{k: f[k] for k in ("t_ms", "recv", "instrument_id", "frame_type")}, "fields": fields})
light = {"meta": {**src["meta"], "frame_count": len(light_frames),
                  "frame_type_counts": {"FD": len(light_frames)},
                  "note": "lightweight: FD only, best-quote level-1 + DPP/DV/DPP:T"},
         "frames": light_frames}
json.dump(light, open("python/tests/fixtures/tachibana_live_mock_4sym.json", "w", encoding="utf-8"),
          ensure_ascii=False, separators=(",", ":"))
```

抽出後に `tachibana_replay_multi.py` に軽量 fixture path を渡して raw と
**byte-identical な per-symbol 結果**（trade/closed/partial/pts/columns 全列一致）
を確認すること（codec 忠実性ガード）。

## 関連

- findings 0117 — kabu live mock fixture（本 findings の sibling 設計）
- ADR-0023 — v4r9 公開鍵認証
- ADR-0027 — `TACHIBANA_ALLOW_PROD` 廃止（prod 解禁は creds 解決で判定）
- tachibana skill: 採取 spike で読む env / 認証規約は skill 本文の R1 / R3 / R10 と一致
