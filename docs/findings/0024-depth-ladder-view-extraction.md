# findings 0024 — depth ladder 描画を本番コンポーネント `DepthLadderView` に抽出（#54）

方針: ADR-0005（1:1 surface parity・depth ladder は明示 in-scope サーフェス）/ ADR-0001（Unity+pythonnet frontend）。
移植元 parity 参照: TTWR `src/ui/chart/overlays_ladder.rs`。
本ファイルが #54 スライスの正本。ADR-0005 は自己保護条項により**編集せず**、本 findings から「方針: ADR-0005」で参照する。
兄弟スライス #53（findings 0023 `ChartView` 抽出）と同型の「OnGUI→uGUI 本番部品抽出」であり、判断の多くを #53 に揃えている。

## 結論（grill-with-docs 2026-06-15・owner 確定）

bid/ask depth ladder の描画を、`DepthLadderHitlHarness.cs` の OnGUI（IMGUI）埋め込みから、単一の本番
uGUI コンポーネント `Assets/Scripts/Live/DepthLadderView.cs`（MonoBehaviour）へ抽出した。HITL harness
（`DepthLadderHitlHarness`・realtime）と #44 montage（`ThemeHitlHarness`・AFK 検証）が**同一部品**を使い、
`ThemeService.Changed` を部品側で購読して theme 切替に追従する。検証（AFK `ThemeProbe`）は **偽 ladder
（Panel＋LadderRow テキスト）ではなく本番 `DepthLadderView` の Graphic** を sample する。

消費者は **(1) `DepthLadderHitlHarness`（HITL・mock venue 40 tick realtime）** と **(2) montage（AFK 検証）**
の 2 つ。本線 scene（floating window / hakoniwa パネル化）への載せ替えは findings 0012 §4 の deferral を踏襲し
**今回も対象外**（mainline DI が来るスライスの follow-up）。

## 確定した設計（grill での owner 判断つき）

### Q1 スコープ＝A案「純粋な抽出」（owner 確定）
現 backcast の OnGUI の見た目をそのまま uGUI 部品へ移すだけ。TTWR `overlays_ladder.rs` の richer な要素
（21 行固定・LAST 中央行・段不足 `---` プレースホルダ・bid/ask 薄塗り背景 alpha 0.22）は **#54 では入れず
follow-up issue に切り出す**（みなしご防止）。#53 が「抽出であって機能追加ではない／TTWR の追加 chrome は
follow-up」と決めた規律を踏襲。

### Q2 構図＝#53 と対称・OnGUI 全撤去（owner 確定）
- `DepthLadderView`（新規・本番 uGUI）。
- `DepthLadderHitlHarness` を OnGUI→uGUI に改修。板・ヘッダ・spread セパレータは `DepthLadderView` 内へ。
  **診断 status 行も uGUI Text 化し OnGUI を全撤去**（#53 の `ScenarioStartupHitlHarness` が status を別
  uGUI Text にして OnGUI を残さなかったのと対称。owner 指摘で「板だけ uGUI・status は OnGUI 据え置き」案を
  却下し全撤去に変更）。
- montage の偽 ladder（`ThemeHitlHarness` の `Panel`＋`LadderRow`）を `DepthLadderView.Render(mock)` に差し替え、
  `ladder_bg`/`ladder_bid`/`ladder_ask` の Samples を本番 Graphic に向け直す（#53 で `chart_bg`/`candle_up/down`
  をやったのと同じ）。`LadderRow` ヘルパは未使用化して削除。
- `ThemeProbe` Section4c を本番 ladder graphic に差し替え（null ガード付きで非空虚 kill）。

### Q3 背景色ロール＝`colors.background`（owner 確定・parity 根拠）
`DepthLadderView` 自身の背景 Image は **`colors.background`**。TTWR `overlays_ladder.rs:206` の LadderPane bg が
`theme.colors.background.with_alpha(0.95)` で、chart pane（`render_main.rs`）と **同じ background ロールを共有**して
いるのが正本。兄弟 `ChartView`（`ChartView.cs` bg=`colors.background`）とも一致する。
- 当初案の `surface_background` は #44 の偽 ladder placeholder が任意に選んだ色であって正本ではない。今回 montage の
  Samples と `ThemeProbe` 期待値はどのみち触るため「温存」のコスト/メリットは無く、placeholder 色を本番に固定すると
  TTWR 逸脱が 1 つ恒久化するため却下。
- 変更は 3 行（`ThemeHitlHarness` の `Set("ladder_bg", surface_background)` 削除＝部品が自前で塗る、`ThemeProbe`
  の `ladder_bg` 期待 2 箇所 `surface_background→background`）。

## parity の切り分け（重要）

| 側面 | parity 関係 |
|---|---|
| ladder の bg ロール（`colors.background`） | TTWR `overlays_ladder.rs:206` 同値（pane bg = `colors.background`）。alpha は下記逸脱 |
| 板の構造（買い板/売り板を段組みで描画・wire 順忠実） | TTWR 同趣旨（DepthDecoder が wire 順復元・findings 0012） |
| **色ロール bid=`status.bid`(緑11) / ask=`status.ask`(赤11)** | **backcast 独自**。TTWR は `status.long`(緑9)/`status.short`(赤9) を ladder に流用。backcast は #44 theme で bid/ask 専用ロール（Radix step 11）を新設済み（`ThemeProbe` Section1 が green.11/red.11 を parity SoT として固定）。issue #54 AC も明示。**#44 で確定済みの既存逸脱**であり #54 が新規導入するものではない |
| **retained uGUI 構造（Render で行を rebuild）** | **backcast 独自**。TTWR は immediate-mode の Bevy system（毎フレーム despawn+respawn）。Unity に「建て済み UI の毎フレーム自動再描画」が無いため。findings 0023 と同根の framework 差による強制逸脱 |
| **`ThemeService.Changed` 購読 → `ApplyTheme()`** | **backcast 独自**（findings 0020/0023 と同根） |
| ask の表示上 reverse（最高 ask が上） | 現 backcast OnGUI の presentation 選択を踏襲（decode の re-sort ではない・findings 0012 §2.2）。TTWR は best が中央寄り（LAST 中央 ladder のため）だが、A案で LAST を入れないため現 backcast の「最高 ask 上・spread セパレータ・bid wire 順」を維持 |

## 逸脱の記録（parity-first 原則）

1. **alpha 未移植**: TTWR pane bg は `with_alpha(0.95)`。兄弟 `ChartView` は alpha を入れず不透明 `colors.background`。
   対称性を優先し #54 も**不透明 `colors.background`** とする。「alpha 0.95 は未移植（`ChartView` と同じ scope 切り）」。
   HITL の「黒地に独立パネル」見えは不透明でも問題なく出る。
2. **色ロール bid/ask vs long/short**: 上表のとおり #44 既定の逸脱。`status.bid`/`status.ask` を使う（AC 明示・#44 確定）。
3. **21 行固定 / LAST 行 / 段不足 `---` / 薄塗り背景は未移植**: A案で scope 外。TTWR `overlays_ladder.rs` の該当要素は
   **follow-up issue に切り出す**（みなしご防止）。
4. **本線載せ替え未対象**: floating window / hakoniwa パネル化は findings 0012 §4 の deferral 踏襲。

## DepthLadderView API（確定）

- `Build(RectTransform parent)` — parent 内に bg Image（`colors.background`）＋列ヘッダ＋行 root を組む。
  `ThemeService.Changed += ApplyTheme`、`OnDestroy` で解除（`ChartView` と同じ作法）。
- `Render(DepthSnapshotView snapshot)` — 既存行を捨てて再生成。`HasDepth==false` は "(no board)" プレースホルダ 1 行。
  `HasDepth==true` は ask（表示用 reverse）→「— spread —」→ bid（wire 順）。bid テキスト=`status.bid`、
  ask テキスト=`status.ask`、ヘッダ/セパレータ/プレースホルダ=`text_muted`。
- `ApplyTheme()` — bg/既存行を `ThemeService.Current` から塗り直す（行は内部 list で kind 付き保持し再着色）。
- probe seam: `Background`（bg Image）・`BestBid()`/`BestAsk()`（最良＝先頭の bid/ask 行 Text。montage が mock を
  Render し `ThemeProbe` が本番 ladder の bid/ask 色を sample する）。

## 検証（AC④）

- **AFK `ThemeProbe.Run`**（Section4c を更新）: montage の `ladder_bg`/`ladder_bid`/`ladder_ask` が **本番
  `DepthLadderView` の graphics** を指すようになり、dark→NonDefault 切替で本番 ladder 色が追従することを非空虚に
  kill。`BestBid()`/`BestAsk()` の非 null も assert（#53 の candle null ガードと同型）。`ladder_bg` 期待は
  `colors.background`。
- **`DepthDecodeProbe` は不変**（decode 回帰ゲート）。
- **未ゲート（既知・Low）**: Render の行レイアウト幾何（reverse 並び・spread 位置）と `HasDepth==false` 経路は
  色のみ sample のため自動未検証。実 bar の描画は HITL 目視のまま（AC④ 設計）。
- **HITL 目視**: `Tools > Backcast > Depth Ladder HITL`（Play 中）。mock venue が 40 tick の drift する板を流し、
  本番 `DepthLadderView` 上で ask/spread/bid が realtime 更新されることを owner が目視。status 行は uGUI Text。
  demo venue での HITL は owner 手動。

## follow-up（みなしご防止・別 issue 起票候補）

1. **TTWR ladder parity の追加要素**: 21 行固定・LAST 中央行・段不足 `---` プレースホルダ・bid/ask 薄塗り背景
   （alpha 0.22）・pane bg の alpha 0.95（`overlays_ladder.rs`）。A案で scope 外にした分。
2. **本線 scene への載せ替え**: floating window / hakoniwa 上の depth パネル化（findings 0012 §4 deferral）。
   立花 demo の実 depth を本番 ladder で見る AC④ の「demo venue HITL」leg は、この本線 consumer が
   存在しないと回せない（HITL harness は venue=`"MOCK"`・IID `8918.TSE` ハードコードのため）。①と同梱で起票。

## 検証ステータス（実機実証・2026-06-15・Windows・Unity 6000.4.11f1）

mock 経路は全 GREEN。owner マシン（Windows 11・`C:\Program Files\Unity\Hub\Editor\6000.4.11f1`）で実走。

### Step 1 — AFK `ThemeProbe.Run`（権威ゲート・Python-free）GREEN
```
<Unity 6000.4.11f1> -batchmode -nographics -quit -projectPath <proj> -executeMethod ThemeProbe.Run
→ return code 0
[THEME PASS] derivation + NonDefault≠dark + service semantics + wiring kill all green
```
`error CS` ゼロ（`DepthLadderView` ＋ 改修3ファイルのクリーンコンパイル）。Section4c の wiring kill が
**本番 `DepthLadderView` の graphics**（`BestBid()`/`BestAsk()` 非 null・`ladder_bg==colors.background`）を
dark→NonDefault で非空虚に kill。

### Step 2a — montage HITL 目視 GREEN
`ThemeHitlHarness`（AutoBootstrap 一時 true）の Play で、mid-right に本番 `DepthLadderView` が ASK 赤 /
`— spread —` / BID 緑 / 黒地（`colors.background`）で描画。**T トグルで bid/ask/bg 3色が dark↔NonDefault
追従**（AC②③ を実機確定）。em-dash セパレータも豆腐化せず描画。

### Step 2b — mock venue 実時間 drift HITL GREEN
`Tools > Backcast > Depth Ladder HITL`（`ScenarioStartupHitlHarness` AutoBootstrap を一時 false で Play 譲渡）で、
mock の 5×5 板が `status: streaming depth… / updates:` 増加とともに realtime 更新。ASK reverse（999.3→995.3、
最高 ask が上）/ BID wire 順（994.3→990.3）/ 色ロール正。Pump→`DepthLadderView.Render` の changed-tick
ガード経由で全置換。`!HasDepth` 時の "(no board)" プレースホルダも別フレームで確認。
- **副次（記録価値）**: findings 0012 §5 が記録した Windows-Mono live-path native crash が**再現せず完走**
  （#50 の nautilus 撤去・kernel-native 化の効果）。
- **環境メモ**: 検証時、`python/.venv` に declared dep `duckdb>=1.5` が未導入で drive worker が
  `No module named 'duckdb'` で停止 → `uv pip install duckdb`（=1.5.3）で解消。#54 と無関係の venv provisioning ギャップ。

### Step 3 — demo venue（立花）実 depth HITL：未実施（スコープ外・deferral）
HITL harness は MOCK ハードコードで、`DepthLadderView` の本線 consumer も未配置（上記 follow-up ①②）。
AC④ の「demo venue で HITL 検証」は本線載せ替えスライスに同梱する。**#54 のスコープ（抽出＋harness＋montage）は
Step 1/2a/2b で実機確定済み**（描画・theme・realtime 経路は本スライスで完結）。
