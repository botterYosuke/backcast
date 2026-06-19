# 0054 — Hakoniwa 専用テーマロール（明るい配色を隔離して試行可能にする）

依頼: hakoniwa に表示されるチャート/パネルの色を明るくしたい。複数パターンを試したいので
theme システムが効くか確認し、どのパラメータを変えれば反映されるかを特定する（grill-with-docs）。

関連: 方針は #44 theme システム（findings 0020）・playbook `backcast-theme-color-change-playbook`
の延長。ADR ではなく本 findings が当スライスの正本。

## 確認結果（theme システムは効いている）

- `ChartView`（`Assets/Scripts/Chart/ChartView.cs:102`）/ `DepthLadderView`（`.../Live/DepthLadderView.cs:108`）/
  `ScenarioStartupTile`（`.../ScenarioStartup/ScenarioStartupTile.cs`）は `ThemeService.Changed += ApplyTheme`
  を自前購読し in-place 再描画。`ThemeService.Current` 差し替え＋`Changed` 発火（`SetTheme`）で即反映。
- 色 literal を直すだけなら **Play を押すだけで反映**（シーン再 bake 不要）。`BackcastWorkspaceRoot.BuildWorkspace`
  が Play 時に theme を読み直すため。

## grill で判明した構造（着手前は未把握だった事実）

hakoniwa のタイルは **2 層**で、タイル枠は **theme 化されていなかった**:

- **層1: タイル枠（カード＋ヘッダ＋ラベル）** = `BackcastWorkspaceRoot.cs:807-810` の **ハードコード literal**
  （`HAKO_ROOT_COLOR`/`HAKO_TILE_COLOR`/`HAKO_HEADER_COLOR`/`HAKO_LABEL_COLOR`）。`BuildTileChrome`（:823）が
  全タイルに塗る。**ThemeService 非購読**＝theme を変えても動かなかった。inline-color gate は Root を対象外。
- **層2: タイル中身**:
  - チャート/板の背景 = `colors.background`（= editor のコード欄と共有）
  - startup タイル = `colors.panel_background`（= footer / sidebar と共有）
  - orders/positions/run_result = `LivePanelTileView` は**文字だけ**描画（`.cs:30`）→ 見える背景は層1のタイルカード

→ 「hakoniwa だけ」明るくするには **チャート/板の `background`・startup の `panel_background`・
ハードコードのタイルカード literal** の3系統に触る必要がある。単一 knob は無く、タイル枠は theme 化すら未了。

## 設計の木（ロック済み）

- **スコープ**: hakoniwa 隔離（editor / footer / sidebar はダークのまま）。→ 共有ロールを触らず**専用ロール新設**。
- **粒度**: フル（背景5＋文字3 = 8 ロール）。全て owner literal（scale 非派生・`workspace_background` と同流儀）で、
  既存 dark ロールと distinct ＝隔離が保証される。後で scale 派生に変更可。
- **チャート/板**: 中身背景は **1 ロールに統一**（同一タイルに同居するため）。
- **文字色も hakoniwa ロール化**（背景を明るくすると暗い文字が要るため）。ローソク陽陰・bid/ask は
  取引セマンティクス（緑/赤）なので `status.*` のまま据え置き。

### 新ロール（`ThemeColors` に追加）

| ロール | 置き換え元 | 塗る対象 |
|---|---|---|
| `hakoniwa_root_background` | `HAKO_ROOT_COLOR` (0.12,0.12,0.15) | root ボックス（タイル間の隙間） |
| `hakoniwa_tile_background` | `HAKO_TILE_COLOR` (0.16,0.18,0.22) | タイルカード（orders/positions/run_result の見える背景） |
| `hakoniwa_tile_header` | `HAKO_HEADER_COLOR` (0.27,0.30,0.38) | タイルのヘッダバー |
| `hakoniwa_chart_background` | `colors.background` | チャート＋板の中身背景（統一） |
| `hakoniwa_panel_surface` | `colors.panel_background` | startup タイルの中身背景 |
| `hakoniwa_tile_header_text` | `HAKO_LABEL_COLOR` (0.92,0.92,0.94) | タイルヘッダ文字 |
| `hakoniwa_text` | `colors.text` / `LivePanelTileView` の (0.90,0.92,0.94) | チャート価格・パネル本文 |
| `hakoniwa_text_muted` | `colors.text_muted` | チャート軸・変化率・板ヘッダ |

初期値は **「The Farmer Was Replaced」farm パレット**（owner 指定 2026-06-19）。hakoniwa=畑の区画として、
grass-green フィールド(#6a9b41)・soil-brown ヘッダ(#8a6239)・tilled-earth タイル(#e3d5b0)・cream チャート/板面
(#efe7d2)・dark-soil 文字(#2f2616)。dark-scale の status green/red が cream 上で作物のように読める。値は
`ThemePalettes.cs FromScales` 参照。※pixel 単位の厳密一致ではなくゲームの周知アスペクトからの再現（owner が
スクショ/hex を出せば厳密合わせ可）。当初の light blue-grey サンプル（#aebfcf 系）は本パレットに置換。

**contrast の経緯（code-review 由来）**: サンプル背景の変遷は near-white(#f6f9fc) → light blue-grey(#d4dde7) →
**FARM cream(#efe7d2、現行 owner 採用)**。いずれも明るい背景上で「bid/ask=`status` green.11/red.11・
LAST=`status.warning` yellow.9・ローソク=`status` green.9/red.9」が低コントラストになるという同じ問題を抱える
（grill で「取引色は `status.*` 据え置き」を凍結した帰結 — これらは **dark bg 向けの step**）。

**[P1 → 解決(A)] reviewer 指摘（2回目レビュー）**: FARM cream(#efe7d2) 上の簡易 WCAG コントラストは
bid green.11=**1.54:1** / ask red.11=**1.71:1** / LAST yellow.9=**1.28:1** / long green.9=**2.46:1** で、
板の価格/サイズ・LAST が実用上かなり沈む水準（「読みにくいかも」ではなく不可レベル）。owner が **(A) Hakoniwa
専用の取引色ロール追加** を選択（2026-06-19）。実装: cream-legible な3ロールを追加し、grill の「取引色は
`status.*` 据え置き」を Hakoniwa に限って見直した（アプリの他所は status.* のまま）。
- `hakoniwa_up`   = #2e6e31（crop-green）← ローソク陽・change% gain・ladder bid（旧 status.long / status.bid）
- `hakoniwa_down` = #a02d1f（barn-red） ← ローソク陰・change% loss・ladder ask（旧 status.short / status.ask）
- `hakoniwa_last` = #7a5a12（dark-amber）← ladder LAST テキスト（旧 status.warning）
※ grill が分けていた bid/ask(step11) と candle long/short(step9) の区別は、cream 上ではどちらも要濃色のため
`up`/`down` の2色に統合した（findings 0024 の step11 divergence は Hakoniwa では畳む）。ロール合計 8→11。

## 「どのパラメータを変えれば反映されるか」（owner 向けの答え）

- パターン試行の編集点は **`Assets/Scripts/Theme/ThemePalettes.cs` の `ThemeColors.FromScales` 内、
  上記 8 ロールの literal**。ここを書き換えて Play で即反映（再 bake 不要）。
- 背景だけ明るくしたい → `hakoniwa_chart_background` / `hakoniwa_tile_background` / `hakoniwa_panel_surface`
  / `hakoniwa_root_background` を上げる。文字が読みにくくなったら `hakoniwa_text` / `hakoniwa_text_muted` を下げる。

## ゲート / 実装メモ

- `ThemeProbe`（batchmode `-executeMethod ThemeProbe.Run`）を **RED-first** で更新:
  Section1（8 ロールの値＋互いに distinct）・Section2（NonDefault≠dark）・Section4（wiring-kill: タイル枠が
  `Changed` で再適用されるか）。`Theme.cs NonDefault()` に 8 ロールの `Distinct(i)`（空き index 55–62）を追加。
- **タイル枠の `Changed` 購読**: 層1 は従来 build 時 1 回塗りで非購読だった。`BackcastWorkspaceRoot` の
  既存 `ApplyViewportTheme`（`Changed` 購読済み）を拡張し、タイルカード/ヘッダ/ラベルの Image/Text 参照を保持して
  再適用する。Play 時の build 読み直しだけでも owner の「パターン試行」は満たすが、theme システム契約
  （#44 AC②）と probe wiring-kill のため runtime 再適用も配線する。
- 既知バグ: `tools/theme_inline_color_gate.sh` は retire 済みファイルを TARGETS に残しており元から FAIL する
  （playbook 既知）。本スライスの権威ゲートは `ThemeProbe`。
- Editor がプロジェクトを開いていると batchmode probe は多重起動で起動不可（owner に閉じてもらう or Play 目視）。

## 実装後の検証 / code-review 反映

- `ThemeProbe.Run` batchmode = **[THEME PASS]**（derivation + NonDefault≠dark + service semantics + wiring kill）。
  Section1 に 8 ロール値＋isolation Ne、Section2 に NonDefault≠dark、Section4d にタイル枠 wiring-kill を追加。
- code-review(simplify high) 反映:
  1. **leak 修正**: `_hakoChrome`（タイル枠 retention）は theme 切替時の lazy prune だけだと、theme を切替えない
     セッションで chart tile の spawn/despawn churn 毎に dead entry が溜まる。`DespawnChartTile` に eager prune
     （`RemoveAll(c => c.card == null)`）を追加。base tile は deactivate のみで destroy されないため churn 元は
     chart tile だけ。
  2. **contrast 既定の見直し**（上記「contrast の経緯」節）。
  3. stale コメント修正（ScenarioStartupTile の off-state ロール名）。
- 2回目レビュー反映:
  - **P2 修正**: chrome の wiring-kill が手動 `PaintTileChrome` 呼び直しで購読経路を検証できていなかった
    （`Changed += ApplyHakoniwaChromeTheme` を消しても pass する偽ゲート）。`ThemeProbe` Section5 を新設し、
    **実 `BackcastWorkspaceRoot` を BackcastWorkspaceProbe と同じ headless compose（scene open + reflect _font +
    ResolvePaths + BuildWorkspace）で組み、`SetTheme(NonDefault)` だけ呼んで** root/card/header/label が追従する
    ことを assert（購読を消すと RED）。⚠️ Editor が開いていて batchmode 再実行が未了 — owner が Editor を閉じて
    `ThemeProbe.Run` を回すか Play で目視確認が必要。
  - **P3 修正**: 本 findings の contrast 節が light blue-grey 前提のまま FARM palette と矛盾していたのを整合。
  - drive-by: `DepthLadderView` ヘッダコメントの旧ロール名（pane bg=colors.background / LAST=element_background）を更新。
  - **P1 は未解決**（上記 [P1 OPEN]）。
- 残す既知の非対称（low・本番に runtime theme 切替が無いため latent）: `LivePanelTileView` の本文テキストは Build 時
  に `hakoniwa_text` を 1 回読むだけで `Changed` 非購読。タイル枠カードは `Changed` で再適用されるので、将来 runtime
  で theme を切替えると本文だけ固定される。production は literal+Play 経路なので実害なし。light mode (#51) で
  runtime 切替を入れる際に LivePanelTileView を購読化する。
