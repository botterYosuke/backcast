# findings 0133 — Replay 分足が同一 X に重なる＋Mesh 65000 頂点で落ちる（basis が日足に化ける）

**方針: [[ADR-0034]]（chart S1）の下位決定**（ADR 本体は不変・自己保護条項あり）。実装 issue: **#184**。関連: [[0119-chart-virtualized-mesh-upgrade]]（mesh 単一バッチ＋virtualization＋fit-all D-4）/ [[0129-replay-cold-preview-draws-full-catalog-history]]（cold preview が全期間を描く）。

> 番号注記: 0116/0117/0119/0120/0123/0126/0131 は並行ブランチで番号重複している。本 finding は `ls docs/findings/ | sort` の次空き番号 0133 で採番。

## 症状（owner・実機 HITL・2026-06-30）

Replay を **Minute** モードで `bt.replay(bars_per_second=2)`（観察のみ・売買ゼロ）で回すと、`7203.TSE` チャートで:

1. **ローソク足が同じ X 位置に縦に重なって描かれる**（横に進まない）。
2. X 軸が **日付目盛り**（2024-03-28 / 03-30 / 04-01）になる——分足なのに「約5日が画面に映っている」表示。
3. 画面下に `ArgumentException: Mesh can not have more than 65000 vertices`。

## 真因（コードで確定）

**3症状はすべて「中身は分足データなのに `ChartViewState.basis_ms` が DAILY（86,400,000ms）に化けている」という単一状態から出る。** `fit_all_on_autoscale=true`（Replay）で ~5日スパンのとき:

- `ResetView` は `spanSlots = (last-first)/basis + 1`。basis=DAILY だと spanSlots≈5 → `fit = plotW/5` が `[1,64]` に収まり（≈60px）「日足5本」として全期間を画面に収める（`ChartViewState.cs:104-118`）。
- `TimeToX` は分足の間隔を DAILY で割る: `60000 / 86_400_000 × 60px ≈ 0.04px` → 全分足が同一列に → **重なる**。
- 可視窓 `winEnd = translation + plot/cell × basis` が5日を張る → その範囲の**密な分足を全数**カル通過 → 数千本 × 12 頂点 → **65000 頂点超過で例外**（`ChartView.cs:427` / `505-530`、`EmitQuad` は1本=12頂点）。
- 5日窓だと `ChartScale` が**日足目盛り**を振る → 日付軸。

**なぜ basis が DAILY に化けるか（2つの寄与をコードで確認）:**

1. **basis は最初の1フレームの bar 間隔から推定され、以後二度と直さない**（`ChartView.Render` `if (!ViewState.basis_ms.HasValue)` → `InferBasisMs`、`ChartView.cs:295-299`）。host（`BackcastWorkspaceRoot`）は **`SetGranularity` を一度も呼ばない**（呼ぶのはテストのみ）。よって basis は「最初に届いた足の間隔の推定値」で固定される。最初のフレームが日足っぽい間隔だと、後から分足が流れても DAILY のまま固定。
2. **cold preview と stream の granularity が別ソースで食い違える**: [[0129-replay-cold-preview-draws-full-catalog-history]] で cold preview は**カタログ全履歴**を描く。preview は C# パネルの granularity 由来、stream はシナリオ由来で独立（Python trace で確認）。最初に届く全履歴フレームの間隔が basis を決めてしまう。

## 決定（owner・2026-06-30・AskUserQuestion）

**Q1「ものさし（basis）の正本を何にするか」→ シナリオの granularity をそのまま使う。**
host が各 `ChartView` に scenario の granularity（今回は Minute）を**直接渡して** basis を固定し、「最初のフレームから推定して固定」という現状を廃する。granularity が単一の正本（SoT）。bar-diff 推定（`InferBasisMs`）は basis 未指定時のフォールバックに降格。これで collapse と日付軸が根本から治る。さらに basis=MINUTE が正しく入れば、fit-all は分足5日を MIN セル幅にクランプして直近の小窓（~数百本）に右寄せするので、**65000頂点超過もこのシナリオでは併せて解消**する。

これは ADR-0034（basis は granularity 由来）の**確認・精緻化**であって反転ではない（∴ ADR 本体は無編集・本 finding に記録して ADR を参照）。

**Q2「65000頂点クラッシュへの『何があっても落ちない』保険クランプを入れるか」→ 入れない（別 issue 起票・本セッションでは触らない）。**
`OnPopulateMesh` に「頂点予算 65000 を超えそうなら間引く/打ち切る」上限を設ける案は、basis 修正とは独立した防御網（将来別原因でバー数が膨らんでも落ちない）。owner 判断で本セッションのスコープ外とし、issue 化のみ。→ **issue #183**（Chart: OnPopulateMesh に頂点予算ガード）。

## 修正（予定・host が granularity を配線）

`BackcastWorkspaceRoot` の chart render 経路（`Update` 内 `_chartViews` ループ・`SetFitAllOnAutoScale` の隣）で、scenario の granularity を `ChartView.SetGranularity` へ渡す。`_scenario.Params.Granularity` が SoT（`RequestChartPreviewsForAllLiveCharts:1415` と同じ source）。毎ポール呼ぶので `SetGranularity` は **basis が変わるときだけ再アンカー**するようガード（`SetFitAllOnAutoScale` と同じ idempotent パターン）。inference は basis 未指定時のフォールバックとして温存。

## 修正（実装済み・host が granularity を配線）

- `ChartView.SetGranularity` を **idempotent 化**（`if (ViewState.basis_ms == b) return;`）。host が毎ポール呼んでも basis が変わらなければ ResetView を再実行せず、ユーザーの pan/zoom を奪わない。
- 新規 `Assets/Scripts/Chart/ChartHostWiring.cs`：`Apply(cv, fitAll, granularity)` が `SetFitAllOnAutoScale`→`SetGranularity` を毎ポール配線する plain helper（MonoBehaviour-free）。AFK gate が**同一メソッド**を駆動するので re-implementation の tautology を避ける。
- `BackcastWorkspaceRoot.Update`：従来の inline `kv.Value.SetFitAllOnAutoScale(fitAll)` を `ChartHostWiring.Apply(kv.Value, fitAll, gran)` に置換。`gran` は `_scenario.Params.Granularity`（`Minute`→Minute / それ以外→Daily、`RequestChartPreviewsForAllLiveCharts:1415` と同じ正規化）。
- bar-diff 推定（`InferBasisMs`）は basis 未指定時のフォールバックとして温存。

## gate（RED→GREEN・AFK Unity batchmode probe・2026-06-30 実走）

`Assets/Tests/E2E/Editor/ChartReplayBasisE2ERunner.{cs,md}`（Surface E2E・Python-FREE・合成 OhlcPoint）。台本どおり **日足プレビュー frame → 分足ストリーム**を production の `ChartHostWiring.Apply` 経由で `ChartView` に流す:

- **CHARTBASIS-01**: `basis_ms == BASIS_MINUTE_MS`（DAILY に化けない）＋隣接分足の `TimeToX` 差 ≥ 0.5px（collapse しない）。
- **CHARTBASIS-02**: `RenderedBarCount * 12 < 65000`（頂点予算内）。

**RED→GREEN 実証**（`pwsh scripts/run-live-e2e.ps1 -Method ChartReplayBasisE2ERunner.Run`）:
- 修正あり → `[E2E CHART REPLAY BASIS PASS]`：`basis_ms=MINUTE` / 隣接差=1px / `RenderedBarCount=540`（頂点 6480）/ 例外なし。exit 0。
- **delete-the-logic litmus**（`ChartHostWiring.Apply` の `cv.SetGranularity(granularity)` を撤去）→ `[E2E CHART REPLAY BASIS FAIL] … basis_ms=86400000, want 60000`、かつログに **`ArgumentException: Mesh can not have more than 65000 vertices`**（owner 実機の症状そのもの）。修正を戻すと再び GREEN・例外 0 件。
- compile-only gate（`-CompileOnly`）：`error CS` 0 件。

> 真因再現の肝＝**先に日足っぽい frame を Render して inference に DAILY を掴ませる**こと。分足だけ Render すると `InferBasisMs` が正しく MINUTE を返し fix 無しでも GREEN になる（バグが出ない）。台本 LITMUS 節に明記。
