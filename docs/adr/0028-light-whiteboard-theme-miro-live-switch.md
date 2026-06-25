---
status: accepted
---

# Light（Miro 風ホワイトボード）テーマの追加とライブ切替 — TTWR parity（dark 既定・HUD 専用）からの意図的 divergence

`grill-with-docs`（2026-06-25, owner HITL）で導出。owner の「デザインを Miro 風に変えて」を分解した結果、
**操作モデル（無限キャンバス＋ドラッグできるカード）は既に Miro 的なので流用し、配色（見た目）だけを Miro 風にする**
と確定。具体的には **Dark（現行の宇宙 HUD テーマ）を温存したまま、明るい Light テーマを追加し、ユーザが
設定ダイアログ（[[ADR-0026]]）でライブ切替できるようにする**。Light は Radix light scales で共有 chrome を明るくし、
キャンバス側は appearance 別の light 直値、さらに **ドット方眼の盤面・ドロップシャドウ・角丸カード**という Miro
的な構造要素を Light のときだけ付与する。

## なぜ ADR か（3 条件）

- **不可逆**：浮遊カードのフレーム/装飾を `ThemeService.Changed` 購読化し、HUD↔Card の装飾サブツリーを構造ごと
  ライブ再生成する配線は多数のサーフェスに触れ、後戻りコストが大きい。
- **文脈なしでは驚く**：本リポジトリは TTWR の忠実 PORT で「parity first・divergence は要正当化」が憲法。
  未来の読者は「なぜ dark 専用のはずの配色層に、構造差まで持つ Light テーマがあるのか」と必ず訝る。
- **実トレードオフ**：parity 厳守（dark のみ・HUD のみ）と owner の Miro 体感要件が衝突。複数の現実的選択肢
  （下記 Considered Options）から特定の理由で 1 つを選んだ。

## Decision の細目（下位決定の正本は本 ADR ではなく findings 0108）

- **Dark は退役しない**。Light を**追加**し、`Appearance`（Dark / Light）を切替値に格上げ。移植元 TTWR も
  dark/light 両方を持つので「light の存在自体」は parity 沿い——divergence は **light を実 shipped 変種にし、
  かつ Miro 的な構造差（方眼・影・角丸）を載せる**点に限る。
- **範囲＝アプリ全体**。共有 chrome（エディタ/フッター/サイドバー/メニュー/モーダル）は **本物の Radix
  light scales**（#51 で予約・`ColorScales.Light()` の本実装）で導出。`from_scales` は appearance 非依存
  なので**下流の再配線ゼロ**で明るくなる。
- **キャンバスの隔離直値**（`workspace_background` ＝盤面・`hakoniwa_*` ＝タイル/チャート/板）は **appearance 別の
  light 直値**を持つ。スケール非依存・隔離継ぎ目（findings 0054）は維持し、共有 scale が盤面/ローソク足を勝手に
  塗り替えないこと。ローソク足の up/down/LAST は **light 盤面で読める**値にする。
- **色じゃない Miro 要素 = ドット方眼の盤面背景・カードのドロップシャドウ・角丸カード**を **Light のときだけ**適用。
  **カラフルな付箋アクセントは不採用**（タイトル帯は accent 系のまま）。Dark は現行の宇宙 HUD（`HudFrameChrome`）の
  まま。→ [[Card chrome ⇔ HUD chrome]]：appearance で装飾を**構造ごと**切替。
- **切替はライブ**。`ThemeService.SetTheme`→`Changed` で全サーフェスがその場で再適用。**浮遊カードのフレームは
  現状 `Changed` を購読しておらず build 時焼き込み**なので、本 ADR で**フレームを購読化＋装飾再生成**する（最も
  load-bearing な配線）。
- **スイッチ UI = 設定ダイアログ**（ADR-0026 の集約口）に Appearance セグメントを追加（`SettingsModeSegmentView` を雛形）。
- **永続化 = アプリ全体グローバル**（`PlayerPrefs` 等の単一キー、per-document sidecar ではない）。boot 時に最初の
  テーマ適用前へ読み込み、次回起動でも選択を復元（#43 appearance persist 相当）。Appearance を per-strategy /
  per-layout に紐づけない（戦略を開くたびにテーマが変わる不具合を避ける）。

## Considered Options

- **採用：Dark 温存＋Light をライブ切替で追加**。parity 沿い（TTWR は dark/light 両方）・宇宙テーマの資産を捨てない・
  owner の「動かしたままその場で切替」体感要件を満たす。
- **不採用：Light を既定にして Dark を退役**。parity（dark 既定）からの強い divergence で、宇宙テーマ資産を破棄。
  owner は「dark を残して切替」を選択。
- **不採用：キャンバス盤面だけ Light（chrome は dark のまま）**。低リスクだが、owner は「アプリ全体を明るく」を選択。
- **不採用：build 時にテーマを固定しライブ切替しない**（盤面を再ビルドして反映）。配線は軽いが、owner は
  「動かしたままその場で全部切替」を選択。

## Scope（ADR-0005 / ADR-0026 との関係）

- ADR-0005（1:1 表面 parity）からの divergence は **配色 / 装飾の見た目に限る**。レイアウト構造・操作・seam は不変。
- 切替 UI は ADR-0026 の Settings 集約口に**追加**するのみ（ADR-0026 の decision は不変・参照のみ）。
- ADR-0005 / ADR-0026 は自己保護条項により**無改変**——本 ADR がそれらを参照する関係をここに記録するのみ
  （両 ADR ファイルへは書き戻さない）。

## Consequences

- `ColorScales.Light()` の dark 丸投げを解消し、本物の Radix light 12 段（neutral/accent/red/green/yellow/blue）を実装。
- `ThemeColors.FromScales` がキャンバス隔離直値を appearance 別に出し分けられるよう、appearance を受け取る形へ拡張
  （現状は `ColorScales` のみ受領）。
- `HudFrameChrome` と並ぶ `CardFrameChrome`（仮称・影＋角丸）を新設し、appearance で出し分け。浮遊 window フレームを
  `ThemeService.Changed` 購読化して装飾を再生成。盤面 viewport に Light 用ドット方眼背景を追加（Dark では無効）。
- Settings ダイアログに Appearance セグメント＋`PlayerPrefs` 永続化＋boot 読み込みを追加。
- `ThemeProbe.cs:87` の stale assert（`workspace_background == #7fa4be` の旧農場値）を現値へ修正し、light/構造切替の
  非空検証を追加。
- 下位の実装事実（採用ウィジェット・seam・E2E gate）は本 ADR に書き戻さず findings 0108 に記録し、本 ADR を
  「方針: ADR-0028」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
