# AccountSummaryBarE2ERunner — 台本（Surface E2E）

issues **#174-178**「アカウントサマリーバー」（ADR-0038 / findings 0126）。buying_power / orders / positions の dock
パネルを退役し、口座状態をヘッダー（メニューバー）直下の **screen-anchored 全幅バー**（4 スロット・主数値＋色）＋
**ホバー詳細 card** で出す。退役パネルの format 関数はホバー card が **byte 一致**で再利用する。

正本＝この `.md`（合否仕様）／自動判定＝`AccountSummaryBarE2ERunner.cs`。Python-FREE で実 `BackcastWorkspaceRoot` を
scene-open → `BuildWorkspace` し、Replay は `WorkspaceEngineHost.TestPortfolioJsonOverride`（#65 poll seam）、Live は
`LivePanelViewModel.Apply`（#20 sink seam）で駆動する。実 pan の奥行き目視・実アイコン差し替え・実 SDF 画素は owner HITL。

実行:
```
<Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod AccountSummaryBarE2ERunner.Run -logFile <abs>
# expect: [E2E ACCOUNT SUMMARY BAR PASS] ASB-01..ASB-10 / exit 0
```

## 操作一覧表

| Action ID | 行動（ユーザー観測） | 入口(file:line) | 観測点 | 自動判定 | カバー状態 | 既存Probe |
|---|---|---|---|---|---|---|
| ASB-01 | バーがヘッダー直下に全幅・常時表示で出る（4 スロット・未取得「—」） | `AccountSummaryBarView.Build` / `BackcastWorkspaceRoot.BuildWorkspace` | 自前 ScreenSpaceOverlay canvas・Content の子でない・4 スロット・主数値「—」・icon frame 在 | `bar.Canvas.renderMode==ScreenSpaceOverlay`・`!IsChildOf(_content)`・`PrimaryText(i)=="—"` | 自動(E2E済) | — |
| ASB-02 | 4 スロット主数値が Replay snapshot の実値 | `PushReplayAccountBar` | equity/bp/建玉数/注文数 が pf1 と一致 | `PrimaryText` 一致 | 自動(E2E済) | — |
| ASB-03 | ① の数値色が含み損益の符号で緑/赤 | `PushReplayAccountBar` | uPnL≥0→`hakoniwa_up` / uPnL<0→`hakoniwa_down` | `PrimaryColor(0)` 一致 | 自動(E2E済) | — |
| ASB-04 | Live 純資産＝`Cash + Σ(qty×avg+uPnL)` 導出＋色 | `PushLiveAccountBar` / `AccountSummaryFormat.DeriveLiveEquity` | 建玉ありで導出 equity 一致・負 uPnL で赤・bp/建玉/注文（telemetry.OrderCount） | `PrimaryText/Color` 一致 | 自動(E2E済) | — |
| ASB-05 | ホバーで「今のパネルと同じ詳細」card・外れると消える | `AccountSummaryBarView.SetHovered` / `SetDetail` | ②③④=`FormatReplay*` と byte 一致・①=口座サマリー4行・enter→表示/exit→非表示 | `CardText` 一致・`CardVisible` トグル | 自動(E2E済) | — |
| ASB-06 | パンしてもバーが動かない（screen-anchored） | `BackcastWorkspaceRoot.BuildWorkspace`（root 直下・overlay） | Content は動くがバー screen 位置不変 | `bar.Strip.position` 不変・`content.position` 変化（非空虚） | 自動(E2E済) | — |
| ASB-07 | save→boot でバー位置/可視を復元しない（非永続） | `CaptureLayout` | floatingWindows にバー entry 無し | capture にバー id/kind 不在 | 自動(E2E済) | — |
| ASB-08 | 旧保存 layout が退役 3 kind を名指しても restore skip | `RestoreFloating` / `FloatingWindowCatalog` / `DockShape.IsDockKind` | buying_power/orders/positions は spawn skip・chart は restore（非空虚）・IsDockKind=false | `!Has(retired)`・`Has(chart)`・`!IsDockKind` | 自動(E2E済) | — |
| ASB-09 | Replay / LiveManual / LiveAuto 全モードで常時表示 | `BuildWorkspace`（hide 経路なし） | モード poll 後もバー active | `bar.gameObject.activeInHierarchy` | 自動(E2E済) | — |
| ASB-10 | アイコン枠に 3D プリミティブ（RenderTexture→RawImage 差し替え seam） | `AccountSummaryIconStage.Build` / `SetIconTexture` | 各スロット RawImage に非 null texture | `IconTexture(i)!=null` | 自動(E2E済) | — |
| — | 実 pan の奥行き目視 / 実アイコン（sprite）差し替え / 実 SDF lit 画素 | — | 目視 | — | HITL専用（実画素・GPU・実 art） | — |

## RED→GREEN litmus（production を壊すと落ちる）

- ② のホバーを `FormatReplayOrders` に誤配線 → ASB-05 RED（byte 不一致＝routing 崩れ）。
- equity 導出から uPnL 項を落とす → ASB-04 RED。
- バーを `_content` の子にする → ASB-06 RED（パンで動く）。
- 退役 spec を `Default()` に残す → ASB-08 RED（restore skip 破綻）＋ S12d（FloatingWindowE2ERunner）RED。
- ① の色を符号非依存に固定 → ASB-03 RED（負ケース）。
