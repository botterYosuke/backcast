# StrategyEditorZoomCrispnessE2ERunner — 台本（Journey / 操作網羅台帳）

`StrategyEditorZoomCrispnessE2ERunner.cs` が自動検証する issue **#121「Strategy Editor の zoom 鮮明性
回帰ゲート」** の release gate。実装者は `.cs` と本 `.md` をセットで読む。
下位事実: [findings 0096 §gate](../../../../docs/findings/0096-tmp-sdf-strategy-editor-migration.md)。
採番・カバー語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、配置は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **採番訂正**: issue #121 本文は「findings 0093 に RED→GREEN」と書くが、**0093 は #122（in-proc tkinter
> login）が使用済み**。TMP zoom 回帰ゲートの正本は **findings 0096 §gate**（本台本がそれを実装）。

> **位置づけ**: 構造的回帰ネット。実 `StrategyEditorContentBuilder.Build` で編集面＋出力面を組み立て、
> サーフェスが TMP(SDF) パイプラインに乗っている（legacy uGUI bitmap-atlas Text/InputField でない）ことを assert。
> Python-FREE・scene 不要。

## 埋める死角

InfiniteCanvas のズーム（`Content.localScale` 0.2–5×）は Strategy Editor を**ancestor の transform scale**で
拡大する。legacy uGUI `Text`/`InputField`＋ダイナミックフォントはグリフを**表示ピクセルサイズ**で atlas に
ラスタライズし、それを transform で引き伸ばすため 5× でビットマップがボケる（findings 0096 root cause）。
TMP(SDF) はシェーダが輪郭を**画面スケールで再構成**するため transform 非依存で鮮明。#117–#120 で編集面・
出力面・構文ハイライトを TMP/SDF へ移行済み。本 runner は「編集器が二度と legacy bitmap 経路へ退行しない」
ことを固定する。

## 最重要の不変条件（litmus）

- **delete-the-production-logic**: 編集面を legacy `UnityEngine.UI.Text`/`InputField` へ戻す（あるいは出力面を
  legacy `Text` へ）と **ZOOM-01 が RED**（editor subtree に legacy コンポーネントが現れる）。
- いずれかの TMP サーフェスの font を **非 SDF（bitmap/raster）font asset** に差し替えると **ZOOM-02/03 が RED**
  （atlasRenderMode が SDF でない / shader が Distance Field でない）。
- **HITL 境界**: 5× での**実際の鮮明さ（スクショ）は owner 確認**。headless `-nographics` は画素を
  ラスタライズ／サンプルできない。本ゲートは鮮明性の**構造的前提**（SDF パイプラインが全面・legacy bitmap 面ゼロ）
  ＋ scale 非依存不変条件（ZOOM-04）を固定する＝スクショが鮮明になる**機構**を担保する。

## 操作一覧表（網羅台帳）

| Action ID | 検証対象 | 入口（symbol） | 観測点 | 自動判定 | カバー状態 | HITL |
|---|---|---|---|---|---|---|
| ZOOM-01 | 編集面が TMP・legacy bitmap 面ゼロ | `StrategyEditorContentBuilder.Build` | editor subtree の `UnityEngine.UI.Text`/`InputField` 数・`StrategyInputField is TMP_InputField` | legacy Text==0・legacy InputField==0・field is TMP_InputField・textComponent is TMP_Text | 自動(E2E) | — |
| ZOOM-02 | 編集面 font が SDF | `TMP_Text.font`（textComponent＋placeholder） | `atlasRenderMode` に SDF・atlas material shader が "Distance Field" | 両 TMP_Text とも SDF render mode ＋ Distance Field shader | 自動(E2E) | 5× コード鮮明スクショ |
| ZOOM-03 | 出力面（rich/console）が SDF | 同上（出力 2 ペイン） | 同上 | 出力 TMP_Text ≥2 が全て SDF | 自動(E2E) | 5× 出力鮮明スクショ |
| ZOOM-04 | fontSize が transform scale 非依存 | ancestor `localScale=5×` ＋ `ForceMeshUpdate` | 編集面 `TMP_Text.fontSize` | 5× 前後で fontSize 不変（SDF がズームを供給・bitmap 再ラスタでない） | 自動(E2E) | — |

> **font の許容**: ZOOM-02/03 は **Cascadia SDF（#117 production）でも default SDF fallback** でも PASS する。
> 鮮明性の不変条件は「SDF であること」なので両者を許容する（どちらの SDF font かは #117/findings 0096 D1 の責務）。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath D:\Documents\backcast \
        -executeMethod StrategyEditorZoomCrispnessE2ERunner.Run -logFile <abs log>
# expect: [E2E STRATEGY EDITOR ZOOM CRISPNESS PASS] + per-id [E2E ZOOM-0N PASS] / exit=0
#         （確認は Bash `grep -a "E2E ZOOM"`）
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# ランチャ経由: pwsh scripts/run-live-e2e.ps1 -Method StrategyEditorZoomCrispnessE2ERunner.Run
```
