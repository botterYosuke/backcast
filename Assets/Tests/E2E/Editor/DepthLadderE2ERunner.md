# DepthLadderE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`DepthLadderE2ERunner.cs`（第二波で実装済み・findings 0059）が自動検証する **板 ladder サーフェス**の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**この サーフェスの表示不変条件すべての網羅台帳と、E2E の
観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*。**板 ladder は表示専用でユーザー入力がほぼ無い**
> サーフェスなので、**ユーザー起点の行動は `DEPTH-01`（板を持つ銘柄を Live で表示）と `DEPTH-02`（Live↔Replay の
> モード切替で表示/非表示）の 2 つだけ**。残りは入力の無い**描画不変条件**で、観測点として台帳に載せる。
> 板は最新断面であって価格履歴/チャートではなく、**Replay では常に `None`**（Live 限定・CONTEXT.md「板 / depth」）。
> ladder は受信順を忠実描画し **defensive sort しない**（producer 契約違反を隠さない・CONTEXT.md「bid/ask ladder」）。

## 対象サーフェス

再利用可能な本番 bid/ask depth ladder ウィジェット（`DepthLadderView`）。各 chart タイルの右 `LADDER_WIDTH=120`
ストリップに per-instrument で 1 つ mount される（`BackcastWorkspaceRoot.cs:654-682`）。TTWR `overlays_ladder.rs`
parity——固定 21 行（10 ask + LAST + 10 bid・欠損は "---"）、per-side α 行背景、LAST 中央行。pane bg は
`colors.background`、bid=`status.bid`(green.11) / ask=`status.ask`(red.11) / LAST=`status.warning`。
`ThemeService.Changed` を self-subscribe し runtime テーマ切替に追従する。`!HasDepth` のとき "(no board)"
プレースホルダ 1 行（Replay / pre-stream）。

## 対象ユーザー行動

板を持つ銘柄を Live で表示（universe/sidebar で銘柄選択 → chart タイル → 板がストリームすると ladder 描画）と、
Live↔Replay のモード切替（ladder の表示/非表示＋chart の inset）。それ以外（21 行レイアウト・色・LAST・no-board・
受信順・per-instrument 分離・signature early-out）は入力の無い**描画不変条件**として観測点に載せる。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 / 観測対象 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| DEPTH-01 | 板を持つ銘柄を Live で表示（**ユーザー起点**） | `BackcastWorkspaceRoot.cs:654,1050,1086` | universe の各銘柄 chart タイルが右 strip に `DepthLadderView` を mount、Live で板 payload を decode → `Render` で best bid/ask＋LAST 行が出る | 2 銘柄 universe → `RenderDepthLadders(payload)` → `CurrentSnapshot.HasDepth + BestBidRowText/BestAskRowText/LastRowText 非 null` (S8 #161 / 0120 D-13) を assert | 自動(E2E済) | §3（旧 `WorkspaceDepthLadderProbe` §2,§4） |
| DEPTH-02 | Live↔Replay モード切替で表示/非表示（**ユーザー起点**） | `BackcastWorkspaceRoot.cs:1072` | `ApplyDepthLadderMode(isLive)`: Live=ladder 表示＋chart `offsetMax.x=-LADDER_WIDTH`、Replay=ladder 非表示＋chart full width | mode true/false を交互 invoke → `activeSelf` ＋ `chartArea.offsetMax.x` を assert | 自動(E2E済) | §3（旧 `WorkspaceDepthLadderProbe` §3） |
| DEPTH-03 | 21 行固定レイアウト（10 ask + LAST + 10 bid・"---" fill） | `DepthLadderView.cs:116,136-155` | `HasDepth` 時 ask[9..0] 逆順（worst top→best 上）→ LAST 中央 → bid[0..9]、欠損 level は "---"、常に 21 行 | depth 付き snapshot を `Render` → 行数＝21、欠損 level 行が "---" 表記を assert | 自動(E2E済) | §2（新規） |
| DEPTH-04 | per-side 色 + LAST ハイライト + テーマ適用/切替 | `DepthLadderView.cs:214` | bid=`hakoniwa_up` / ask=`hakoniwa_down` / LAST=`hakoniwa_last`、行 bg は per-side α0.22 / LAST=`hakoniwa_tile_background`、`ThemeService.Changed` で in-place 再彩色（findings 0054 P1 で `status.*` から移行） | テーマ切替後の `BestBidColor/BestAskColor/LastRowColor` (S8 #161 / 0120 D-13) を assert | 自動(Probe有・要昇格) | `ThemeProbe`（production ladder graphics をサンプル・findings 0054 で更新済）——**本 runner では据え置き** |
| DEPTH-05 | no-board 状態 "(no board)"（Replay / pre-stream） | `DepthLadderView.cs:123` | `!HasDepth` → "(no board — Replay/None or not yet streamed)" プレースホルダ 1 行のみ・α bg なし | `DepthSnapshotView.Empty` を `Render` → `CurrentSnapshot.HasDepth=false / 各 RowText getter が null (placeholder)` (S8 #161) を assert | 自動(E2E済) | §5（旧 `WorkspaceDepthLadderProbe` §4 Y no-depth） |
| DEPTH-06 | 受信順忠実描画（defensive sort しない） | `DepthLadderView.cs:136-155` | bids/asks は wire 順のまま描画、`bids[0]`/`asks[0]` を best として追跡——表示位置に関わらず | 並べ替え済みでない depth を渡し best=配列先頭を assert（sort 挿入で落ちる） | 自動(E2E済) | §2（新規） |
| DEPTH-07 | LAST 行が per_instrument price 由来 | `DepthLadderView.cs:144`／`BackcastWorkspaceRoot.cs:1096` | LAST 行 = `InstrumentPriceDecoder.Decode(state,id)` の price（`"LAST {0:0.00}"`）、null なら "LAST ---" | per_instrument[id].price=105.0 → LastRow().text=="LAST 105.00" を assert | 自動(E2E済) | §1（price locator）＋§5（旧 `WorkspaceDepthLadderProbe` §1,§4） |
| DEPTH-08 | per-instrument 分離（X の板が Y に漏れない） | `BackcastWorkspaceRoot.cs:1086` | `RenderDepthLadders` は各タイル自身の `per_instrument[id].depth` を decode——X has-depth / Y no-depth で Y はプレースホルダのまま | X 板 + Y 板無しの payload → X 描画・Y プレースホルダを assert（single-global 回帰 kill） | 自動(E2E済) | §5（旧 `WorkspaceDepthLadderProbe` §4） |
| DEPTH-09 | Replay では depth 常に None（decode skip） | `BackcastWorkspaceRoot.cs:1062`／CONTEXT.md「板 / depth」 | `!isLive` で `DriveDepthLadders` が decode 自体を skip、ladder 非表示 | Replay で payload 注入（host teardown snapshot）＋`DriveDepthLadders` → `_depthRendered` 空・板未描画を assert（decode 未実行） | 自動(E2E済) | §4（新規・旧 §3 を `DriveDepthLadders` 駆動へ拡張） |
| DEPTH-10 | signature early-out（unchanged board は再描画 skip） | `BackcastWorkspaceRoot.cs:1098,1111` | depth+last の content signature（timestamp 除く）が前回一致なら 21 行再構築を skip、drift では再描画 | 同一 payload 2 回 → 2 回目スキップ、price 変更 → 再描画を assert | 自動(E2E済) | §6（新規） |
| DEPTH-11 | 視覚 montage（実ピクセル・z-order・行背景） | `ThemeProbe.cs:168`（montage）／`DepthLadderHitlMenu` | 実描画での pane bg / per-side α 行 / LAST 帯の見た目、chart pane との前後関係 | — | HITL専用（実ピクセル・GPU/実ウィンドウ前提） | `DepthLadderHitlMenu`（playmode harness） |

> **ユーザー起点は DEPTH-01 / DEPTH-02 のみ**。DEPTH-03〜DEPTH-11 は入力の無い描画不変条件で、表示専用サーフェスの
> 観測点として台帳に載せる（行動ではない）。

## 観測点（詳細）

- **DEPTH-03/06（レイアウト・受信順）**: TTWR `overlays_ladder.rs` parity の固定 21 行は backcast-ORIGINAL な
  retained-uGUI 構造（findings 0024——TTWR は immediate-mode で view 部品が無い）。parity は VISUAL/SEMANTIC
  レベルで保持し、板は wire 順を忠実描画する。**defensive sort を入れると producer 契約違反を隠す**ので、
  ソート挿入で落ちる assert（best=配列先頭・表示位置非依存）を必ず置く。
- **DEPTH-05/09（no-board と Replay）**: 板は Live 限定の最新断面で、**Replay では常に `None`**（過去再生は板を
  持たない）。`DepthDecoder.Decode` は null/empty/whitespace/"null"/`depth:null`(Replay)/instrument 不在を
  すべて `Empty`(HasDepth=false) に落とし throw しない（`DepthDecoder.cs:23-26`）。pre-stream も同じ "(no board)"。
- **DEPTH-04（テーマ）**: `ThemeProbe` が **production の `DepthLadderView` graphics**（`ladder_bid`/`ladder_ask`/
  `ladder_last`/`ladder_bg`）を `ThemeHitlHarness` montage 経由でサンプルし、dark→non-default のテーマ切替で
  色が in-place 追従することを assert 済み（`ThemeProbe.cs:179-230`）。色ロールの正本はここを昇格元にする。
- **DEPTH-08（per-instrument 分離）**: `RenderDepthLadders` は per-id try で各タイル自身の board だけを decode し、
  一銘柄の malformed depth が他タイルを凍結しない。X の板が Y に漏れないこと（single-global 回帰 kill）は
  `WorkspaceDepthLadderProbe` §4 が正本。

## 自動判定（合格条件）

- ログに `[E2E DEPTH LADDER PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)`/`要新規自動化` 行の観測点を 1 つでも落としたら `[E2E DEPTH LADDER FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `Render` の `!HasDepth` 分岐（no-board）・21 行ループ・`ApplyDepthLadderMode`
  の Live/Replay geometry・`DepthSignature` の early-out を消すと、対応 assert が必ず落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `WorkspaceDepthLadderProbe` | batchmode・root 合成・Python-FREE | **昇格済み**（git mv → `DepthLadderE2ERunner.cs`）。§1 price decode → 本 runner §1、§2-4 mount/mode-sync/render → §3+§5。DEPTH-01/02/05/07/08 の正本 |
| `ThemeProbe` | batchmode・production graphics サンプル | DEPTH-04（per-side 色 + LAST + テーマ切替）の正本として**据え置き**。findings 0054 で bid/ask を `hakoniwa_up/down/last` へ移行し ThemeProbe を更新済（本 runner は DEPTH-04 を扱わない） |
| `DepthLadderHitlMenu` | HITL ハーネス（playmode） | DEPTH-11（実ピクセル montage / 行背景の見た目）の視覚確認用に**探索 Probe として残す** |

> 21 行レイアウト（DEPTH-03）・受信順非ソート（DEPTH-06）・Replay decode-skip の駆動（DEPTH-09 を `DriveDepthLadders`
> 経由へ拡張）・signature early-out（DEPTH-10）は既存 Probe が直接 assert していなかった——本 runner で新規に書いた。

## 将来の `DepthLadderE2ERunner.cs` 実装方針（第二波）

- `WorkspaceDepthLadderProbe` と同型に **実 `BackcastWorkspaceRoot` を反射合成**（`_font` 注入 → `ResolvePaths` →
  `BuildWorkspace`）。Python-FREE で完結する（板 payload は decode 入力の JSON 文字列を直接渡す——実 venue 不要）。
  private seam（`_depthLadders`/`_chartAreas`/`ApplyDepthLadderMode`/`RenderDepthLadders`）を反射 invoke し、
  `DepthLadderView` の RowText/Color seam (S8 #161 / 0120 D-13) で板内容を assert。
- 21 行/受信順（DEPTH-03/06）は `DepthLadderView` を単体 Build し、欠損 level 入り・非ソートの `DepthSnapshotView`
  を `Render` して `_rowsRoot.childCount`／best 行 text を反射確認（root 合成不要の軽量セクション）。
- セクション構成は操作一覧表の `自動(*)`/`要新規自動化` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを
  返す `Execute()`（null=PASS）パターン。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod DepthLadderE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**。
