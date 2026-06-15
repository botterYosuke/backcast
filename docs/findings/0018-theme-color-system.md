# findings 0018 — テーマ（配色）システム（#44）: 集中配色定義 → 全 UI 一貫適用・切替

方針: ADR-0005（1:1 surface parity・theme は明示 in-scope サーフェス）。移植元: TTWR `src/ui/theme/`。
本ファイルが #44 スライスの正本。ADR-0005 は自己保護条項により**編集せず**、本 findings から「方針: ADR-0005」で参照する。

## 結論（grill-with-docs 2026-06-15・owner 確定）

TTWR の Zed 風デザインシステムのうち**配色レイヤーのみ**を Unity/C# へ忠実移植し、単一 global `ThemeService` に集約、
各 UI サーフェスが参照＋切替で塗り直す。dark を出荷、切替の正しさは合成 ProbeTheme で非空虚に検証。実 light と
非配色トークンは follow-up。

## ハード証拠（TTWR src 実機照合）

- **TTWR theme は 1,436 行・配色＋非配色を #48 一本で導入**（`theme/mod.rs:3-7`「this module now hosts the complete
  Theme … (ThemeColors, StatusColors, SyntaxColors, PlayerColors, Radius, Layout, Appearance)」）。#48/#50 の切れ目は
  「配色 vs 非配色」ではなく「トークン構造体ぜんぶ（#48）」vs「syntect/tree-sitter 変換＋mono 配線＋light 実装（#50）」。
  → **配色のみへの絞り込みは TTWR #48 からの意図的逸脱**（後述「逸脱の記録」）。
- **配色は導出方式**: `ColorScale`(Radix 12 段)→`ColorScales`(neutral=slate/accent=iris/red/green=grass/yellow=amber/blue
  の 6 本)→`from_scales()` で `ThemeColors`(54 ロール)/`StatusColors`(info/warn/error/success＋long/short/bid/ask の 8 系×
  solid/bg/border)/`SyntaxColors`(8)/`PlayerColors`(8) を**自動導出**（`theme/mod.rs:418-583`）。値は Radix 公開 hex÷255 の
  sRGB float（`scale.rs:60`）。
- **light は stub**: `ColorScales::light()` は dark を返す（`mod.rs:342-347`「out of scope for #48」）。`Theme::light()` も
  dark 同値（`mod.rs:618-622`）。→ **shipped で dark==light**。
- **appearance は分岐に使われない**: `Appearance::Light` の出現は代入 3 か所のみ、`match appearance`/`== Light` は皆無。
  → light は scale 6 本を差し替えるだけで成立し `from_scales` 含め無改修（導出方式の恩恵）。`appearance` は「今どっちか」の
  ラベル専用（#43 が読んで永続・表示する用途と地続き）。
- **swap 機構は未実装**: `SetTheme`/`ResMut<Theme>`/`insert_resource(Theme)` はテスト内のみ。`Res<Theme>` の読み手は 47 か所。
  `mod.rs:404`「runtime theme swapping lands later」。→ **切替の伝播は TTWR に前例が無い**。
- **non_default_theme()**: 全フィールドを決定的別値に変異させた検証用テーマ（`mod.rs:680`・serde round-trip 用）が既存。
- **backcast 側**: 集中テーマ 0 ファイル。インライン配色の本番サイトは 4 つ（`FloatingWindowCatalog.cs`・
  `ScenarioStartupTile.cs`・`PythonSyntaxMeshEffect.cs`・`StrategyEditorContentBuilder.cs`）＋ harness 埋め込みの
  chart/ladder（`ReplayChartHarness`/`ScenarioStartupHitlHarness`/`DepthLadderHitlHarness`/`ReplayPanelsHarness`）。
  既存 global は 0（singleton/service/static 可変ヒット無し）。色表記は既存も 0–1 sRGB float（TTWR と同形式・1:1 写し可）。

## grill 決定経路（Q1–Q9）

1. **Q1 スコープ = 配色レイヤーのみ**。理由: ①#44 タイトル/AC が全部「配色」②TTWR の spacing/typography/elevation/layout は
   Bevy(px/FontFamily/density) 密結合で Unity(RectTransform/TMP/LayoutGroup) に 1:1 で写らず現状消費者ゼロ ③配色だけは
   UI フレームワーク非依存でそのまま写り消費者 4 つ実在。
2. **Q2 導出方式 (A) 忠実移植**。`scale.rs` の Radix 実値＋`from_scales` マッピングごと移植。切替＝スケール 6 本差し替えで
   54 ロール自動追従（AC#2 がほぼタダ）。
3. **Q3 配置 (A) single global `ThemeService`**（TTWR Resource 同型）＋変更通知。分立 harness（共有ルート不在）に最も素直で
   通知点を 1 か所に集約できる。
4. **Q4 実体 (A1) 素のシングルトンサービス**（ScriptableObject ではなく）。TTWR の Theme は「コードで定数から組む resource」で
   inspector 編集用途が無いため、コード建て harness にゼロ儀式で乗る素のサービスが最も忠実。Enter Play Mode の domain reload が
   Play ごとに static をリセット → HITL/AFK 分離は TTWR の「テストごと world 新規」相当で保たれる。
5. **Q5 塗り直し (a) 各サーフェス `ApplyTheme()`**。平面（`image.color` 再代入）も描画時消費（`PythonSyntaxMeshEffect:
   BaseMeshEffect.ModifyMesh` の色フィールド再代入＋`SetVerticesDirty()`）も「現在テーマを読み直して塗り直す/再描画」で統一。
   role 札（themed-component）は描画時消費を別扱いにせざるを得ず却下。
6. **Q6→Q9 で再考: バリアント = 案A**。当初 Q6 で「実 Radix light 移植」確定 → Q9 で「shipped light==dark stub」前提崩れを発見
   → **案A 確定: dark のみ出荷＋`Light()` dark stub（TTWR 踏襲）＋`Appearance` enum。実 light は follow-up**。検証は実 light に
   依存させず ProbeTheme で固める（下記）。
7. **Q8 色マッピング確定**（下表）。
8. **Q7 置換範囲 = 層1＋層2、層3 除外**。層2（chart/ladder）を繋いで初めて `status.long/short/bid/ask` に実消費者が付く
   （dead token 回避）＋ AC#2 の HITL でチャートも切替で変わって「全 UI 一貫」を目視実証できる。harness が `ThemeService.Current`
   を読むのは正当 consumer（本番コンポーネント抽出とは独立）。
9. **Q9 検証分担確定**（下記）。

## parity の切り分け（重要）

| 側面 | parity 関係 |
|---|---|
| 配置（単一 global・全 UI 参照） | TTWR Resource 同型（47 read 構造） |
| 初期化（遅延 dark） | `init_resource::<Theme>()`＋`Default for Theme = dark()` 同型 |
| 配色導出（scale→from_scales→54 ロール） | 忠実移植 |
| `Appearance` enum / `Light()` dark stub | TTWR 踏襲（実 light 未出荷も同じ） |
| **切替伝播（`event Changed`→`ApplyTheme()`）** | **backcast 独自**。TTWR は Bevy のフレーム毎 change-detection に乗るだけで明示通知機構が無く、Unity に「建て済み UI の自動再読」が無いため。#44 AC#2 が切替を要求する一方 TTWR #48 は swap を後回し |

## 逸脱の記録（parity-first 原則）

- **配色のみ移植**は TTWR #48（トークン構造体ぜんぶ一本）からの意図的逸脱。根拠は上記 Q1。非配色トークン
  （spacing/typography/elevation/radius/layout）は Unity 差異により別 issue（みなしご防止の follow-up を起票）。
- **実 light 未出荷**（案A）。TTWR は light() seam を予約済み・stub 出荷なので **shipped 状態は TTWR と一致**だが、Q6 で一度
  「実 light 移植」を確定した後の差し戻し。実 light（Radix slate/iris/red/grass/amber/blue の light 12 段×6）は #43 が必要と
  するタイミングで follow-up。`ColorScales.Light()` を埋めるだけで再配線ゼロ。

## 色マッピング（Q8 確定）

| backcast インライン | → semantic role | 備考 |
|---|---|---|
| ScenarioStartupTile PANEL_BG | `colors.panel_background` | |
| 同 FIELD_BG | `colors.element_background` | |
| 同 TEXT | `colors.text` | |
| 同 ERR | `status.error` | |
| 同 SEL（選択ハイライト） | `colors.element_selected` | |
| 同 BTN | `colors.element_background` | Highlight 時 SEL/BTN 切替は維持 |
| StrategyEditor input bg | `colors.background`（neutral.1 系） | |
| 同 base text | `colors.text` | mesh effect の baseColor |
| Syntax Keyword | `syntax.keyword` | |
| Syntax String | `syntax.string` | |
| Syntax Comment | `syntax.comment` | |
| Syntax Number | `syntax.number` | TTWR では amber に変わる（区別は維持） |
| Syntax Definition | `syntax.function` | def/class 名 |
| **Syntax Decorator** | **`syntax.type_`** | TTWR に decorator role 無し。type_=accent.12 ほぼ白（@ 行頭で文脈明確・variable 近似でも誤読低）。`variable`/`operator` は未消費・定義のみ |
| FloatingWindow editor accent | `players[0]`（accent.9 iris 青紫） | 純青は Players に無いが iris で blue-ish |
| FloatingWindow order accent | `players[2]`（yellow.9 amber） | 青紫 vs 琥珀で現行区別維持 |
| chart candle UP / DOWN | `status.long` / `status.short` | |
| chart bg / text | `colors.background` / `colors.text` | |
| depth ladder bid / ask | `status.bid` / `status.ask` | |

**原則**: `from_scales` は 54 ロール全部を導出で生むので**完全派生パレットを定義**し、消費は上表のサブセット。未消費ロール
（syntax variable/operator、未使用 ThemeColors）は parity 完全性のため定義のみ・消費者ゼロでも可。

## 検証（AC#4・Q9 確定）

- **AFK `ThemeProbe.Run`**（`-batchmode -nographics -executeMethod`・Python 不要・既存 probe が headless uGUI を建てて
  `.color` 検証する流儀に倣う）:
  1. **導出 parity**: 代表ロール = 正しいスケール段（`background==neutral.1`/`text==neutral.12`/`status.long==green.9`/
     `short==red.9`/`bid==green.11`/`ask==red.11`/`accent==accent.9`/`syntax.keyword==accent.11`/`type_==accent.12` 等）。
  2. **`NonDefault != Dark`**（数ロール）= 合成パレットが本物に効くことの証明。
  3. **ThemeService 意味論**: `Current` 既定 dark／`SetTheme` で `Changed` 1 回発火／`Current` 反映。
  4. **配線の非空虚 kill（肝）**: 各テーマサーフェスを headless で建て、代表グラフィックを dark トークンと一致 assert →
     `SetTheme(NonDefault)`＋`ApplyTheme` → 同グラフィックが NonDefault 値へ変わったか assert。平面は `image.color`、描画時消費は
     色ソースフィールド（`effect.keyword`・candle `UP/DOWN`・ladder bid/ask）をサンプル。未変換インライン色 surface は変わらず fail。
     ProbeTheme（全ロール別値）採用で shipped dark==light に依存しない非空虚 kill。
- **静的 grep ゲート**: インライン色の残存ゼロ。許可リスト=theme/scale 定義ファイル＋層3 デバッグ chrome。対象=層1＋層2。
  runtime probe が触らない surface の取りこぼしを補完。スコープを丁寧に（false-positive 回避）。
- **HITL モンタージュ harness**（owner 目視・新規）: パネル＋チャート＋エディタ片＋window accent 2 つを 1 画面、トグルキーで
  dark↔NonDefault。mesh/candle の実再描画（AFK で見えない）が一貫切替するのを目視。

## 既知リスク

- **Linear color space**（`ProjectSettings m_ActiveColorSpace: 1`）: TTWR の生 sRGB float をそのまま `new Color(r,g,b,1f)` に
  写す（既存 backcast と同じ流儀・現状破綻なし）。HITL で輝度ズレが出たら一律変換方針を別途決める。

## follow-up（みなしご防止）

1. **実 Radix light スケール**（slate/iris/red/grass/amber/blue の light 12 段×6＝72 値・`ColorScales.Light()` を埋める・
   再配線ゼロ）。#43 settings のテーマ選択が実意味を持つタイミングで。
2. **非配色トークン**（spacing/typography/elevation/radius/layout）の Unity ネイティブ移植。
