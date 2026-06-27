# AccountSummaryBarE2ERunner — 台本（Surface E2E）

issues **#174-178**「アカウントサマリーバー」（ADR-0038 / findings 0126）。buying_power / orders / positions の dock
パネルを退役し、口座状態をヘッダー（メニューバー）直下の **screen-anchored 全幅バー**（4 スロット・主数値＋色）＋
**ホバー詳細 card** で出す。退役パネルの format 関数はホバー card が **byte 一致**で再利用する。

**視覚リファインメント（owner 2026-06-27・findings 0126 §視覚リファインメント D8-D11）**: 帯（strip）背景は **透明＋
クリック透過**（Universe sidebar 同型・`color.a==0`／`raycastTarget==false`）／スロットは **左詰め固定幅**（右は空白）／
主数値は **アイコン下に縦積み**／金額は **k/M 短縮表記**（バーは `1.23M`・ホバー card は `Money` フル桁の byte 一致再利用）。

**日本語ラベル＆フォント（owner 2026-06-27・findings 0126 §日本語ラベル）**: ホバー card は「何の項目か」が分かるよう
**日本語ラベル付き**（純資産／含み損益／確定損益／現金／買付け余力／数量／取得単価 …）。未取得時も **裸の `—` でなくラベル付き
プレースホルダ**（`純資産: — …`）を出す。card テキストは **CJK 対応の動的 OS フォント**（Yu Gothic 等）に配線（主数値は数値ゆえ
Latin フォント維持）。`LegacyRuntime.ttf` は Latin 専用で、TMP（コードエディタ）は OS フォールバックしないが、legacy `Text` は
dynamic font 経由で OS フォールバックする（＝`Font.HasCharacter` は dynamic font に判別力なし）。実グリフ描画は GPU 依存ゆえ HITL。

正本＝この `.md`（合否仕様）／自動判定＝`AccountSummaryBarE2ERunner.cs`。Python-FREE で実 `BackcastWorkspaceRoot` を
scene-open → `BuildWorkspace` し、Replay は `WorkspaceEngineHost.TestPortfolioJsonOverride`（#65 poll seam）、Live は
`LivePanelViewModel.Apply`（#20 sink seam）で駆動する。実 pan の奥行き目視・実アイコン差し替え・実 SDF 画素は owner HITL。

実行:
```
<Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod AccountSummaryBarE2ERunner.Run -logFile <abs>
# expect: [E2E ACCOUNT SUMMARY BAR PASS] ASB-01..ASB-15 / exit 0
```

## 操作一覧表

| Action ID | 行動（ユーザー観測） | 入口(file:line) | 観測点 | 自動判定 | カバー状態 | 既存Probe |
|---|---|---|---|---|---|---|
| ASB-01 | バーがヘッダー直下に全幅・常時表示で出る（4 スロット・未取得は主数値「—」だがホバー card はラベル付き） | `AccountSummaryBarView.Build` / `BackcastWorkspaceRoot.BuildWorkspace` / `AccountSummaryFormat.EmptyDetail` | 自前 ScreenSpaceOverlay canvas・Content の子でない・4 スロット・主数値「—」・icon frame 在・①card に「純資産」②card に「買付け余力」 | `renderMode==ScreenSpaceOverlay`・`!IsChildOf(_content)`・`PrimaryText(i)=="—"`・`CardText(0).Contains("純資産")` | 自動(E2E済) | — |
| ASB-02 | 4 スロット主数値が Replay snapshot の実値・データ消去で「—」復帰 | `PushReplayAccountBar` / `PushAccountBarEmpty` | equity/bp/建玉数/注文数 が pf1 と一致・portfolio クリアで全スロット「—」（#61 anti-stale） | `PrimaryText` 一致＋クリア後「—」 | 自動(E2E済) | — |
| ASB-03 | ① の数値色が含み損益の符号で緑/赤・idle テーマ切替で色再解決 | `PushReplayAccountBar` / `AccountSummaryBarView.ApplyTheme` | uPnL≥0→`hakoniwa_up` / uPnL<0→`hakoniwa_down`・data 非 push のテーマ flip で tint→色 再解決（ADR-0028 trap） | `PrimaryColor(0)` 一致＋flip 後も追従 | 自動(E2E済) | — |
| ASB-04 | Live 純資産＝`Cash + Σ(qty×avg+uPnL)` 導出＋色・④＝FilledOrderCount | `PushLiveAccountBar` / `AccountSummaryFormat.DeriveLiveEquity` | 建玉ありで導出 equity 一致・負 uPnL で赤・bp/建玉・④＝`FilledOrderCount`（LiveManual でも出る・telemetry.OrderCount ではない） | `PrimaryText/Color` 一致（④=="2"≠telemetry 3） | 自動(E2E済) | — |
| ASB-05 | ホバーで「今のパネルと同じ詳細」card・外れると消える | `AccountSummaryBarView.SetHovered` / `SetDetail` | ②③④=`FormatReplay*` と byte 一致・①=口座サマリー4行・enter→表示/exit→非表示 | `CardText` 一致・`CardVisible` トグル | 自動(E2E済) | — |
| ASB-06 | パンしてもバーが動かない（screen-anchored） | `BackcastWorkspaceRoot.BuildWorkspace`（root 直下・overlay） | Content は動くがバー screen 位置不変 | `bar.Strip.position` 不変・`content.position` 変化（非空虚） | 自動(E2E済) | — |
| ASB-07 | save→boot でバー位置/可視を復元しない（非永続） | `CaptureLayout` | floatingWindows にバー entry 無し | capture にバー id/kind 不在 | 自動(E2E済) | — |
| ASB-08 | 旧保存 layout が退役 3 kind を名指しても restore skip | `RestoreFloating` / `FloatingWindowCatalog` / `DockShape.IsDockKind` | buying_power/orders/positions は spawn skip・chart は restore（非空虚）・IsDockKind=false | `!Has(retired)`・`Has(chart)`・`!IsDockKind` | 自動(E2E済) | — |
| ASB-09 | Replay / LiveManual / LiveAuto 全モードで常時表示 | `BuildWorkspace`（hide 経路なし） | モード poll 後もバー active | `bar.gameObject.activeInHierarchy` | 自動(E2E済) | — |
| ASB-10 | アイコン枠に 3D プリミティブ（RenderTexture→RawImage 差し替え seam） | `AccountSummaryIconStage.Build` / `SetIconTexture` | 各スロット RawImage に非 null texture | `IconTexture(i)!=null` | 自動(E2E済) | — |
| ASB-11 | 帯背景が透明＋クリック透過（sidebar 同型・テーマ flip でも透明維持）（D8） | `AccountSummaryBarView.Build` / `ApplyTheme` | strip Image の `color.a==0`・`raycastTarget==false`・flip 後も alpha 0 | `stripBg.color.a==0`・`!raycastTarget` | 自動(E2E済) | — |
| ASB-12 | スロットが左詰め固定 68px ピッチ（右は空白・全幅 stretch でない）（D9） | `AccountSummaryBarView.BuildSlot` | slot root `anchorMin.x==anchorMax.x==0`・隣接ピッチ 68・右端 < strip 幅 | `anchorMax.x==0`・`Δx==68`・rightEdge<stripW | 自動(E2E済) | — |
| ASB-13 | 主数値がアイコンの下に縦積み（D10） | `AccountSummaryBarView.BuildSlot` | icon top-anchored（anchorY 1）・primary bottom-anchored（anchorY 0） | `icon.anchorY==1`・`primary.anchorY==0` | 自動(E2E済) | — |
| ASB-14 | バー主数値は金額を k/M 短縮・ホバー card はフル桁維持（D11） | `AccountSummaryFormat.MoneyCompact` / `PushReplayAccountBar` | 7 桁で bar=`1.23M`・compact≠full・hover ②＝`FormatReplayBuyingPower`（raw 1234567 在） | `PrimaryText==MoneyCompact`・`!=Money`・`CardText` フル桁 | 自動(E2E済) | — |
| ASB-15 | ホバー card が日本語ラベルを描けるよう専用 CJK フォントに配線（主数値は Latin 維持） | `BackcastWorkspaceRoot.CreateCjkFont` / `AccountSummaryBarView.Build`（cardText.font=_cjkFont） | card フォントが主数値フォント（Latin）と別オブジェクト・OS に日本語フェイスがある時は必須 | OS CJK フェイス在→`CardFont(0)!=PrimaryFont(0)`／不在→SKIP | 自動(E2E済・配線)＋HITL（実画素） | — |
| — | 実 pan の奥行き目視 / 実アイコン（sprite）差し替え / 実 SDF lit 画素 / **ホバー card に日本語が豆腐でなく描画される** | — | 目視 | — | HITL専用（実画素・GPU・実 art） | — |

## RED→GREEN litmus（production を壊すと落ちる）

- ② のホバーを `FormatReplayOrders` に誤配線 → ASB-05 RED（byte 不一致＝routing 崩れ）。
- equity 導出から uPnL 項を落とす → ASB-04 RED。
- ④ を `telemetry.OrderCount` に戻す → ASB-04 RED（FilledOrderCount=2 を期待・telemetry は 3）。
- portfolio クリア時の「—」リセットを外す → ASB-02 RED（stale 値が残る）。
- `ApplyTheme` の tint 再解決を外す → ASB-03 RED（idle テーマ切替で色 stale）。
- バーを `_content` の子にする → ASB-06 RED（パンで動く）。
- 退役 spec を `Default()` に残す → ASB-08 RED（restore skip 破綻）＋ S12d（FloatingWindowE2ERunner）RED。
- ① の色を符号非依存に固定 → ASB-03 RED（負ケース）。
- 帯を不透明 `panel_background`／`raycastTarget=true` に戻す → ASB-11 RED（透過・click-through 破綻）。
- スロットを 1/SLOT_COUNT stretch レイアウトに戻す → ASB-12 RED（左詰め固定幅でない）。
- 主数値をアイコンの右（中央寄せ）に戻す → ASB-13 RED（縦積みでない）。
- バー主数値を `Money`（フル桁）にする → ASB-14 RED（compact==full の vacuity guard が発火）。
- 未取得時の card detail を `EmptyDetail` でなく裸の `—` に戻す → ASB-01 RED（①card に「純資産」不在）。
- card テキストの font を `_cjkFont` でなく `_font`（Latin）に戻す → ASB-15 RED（OS に日本語フェイス在のとき card==primary font）。
