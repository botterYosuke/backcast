# FloatingWindowE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`FloatingWindowE2ERunner.cs`（第二波・実装済み）が自動検証する **floating window サーフェス**の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**このサーフェスでユーザーができる行動すべての網羅台帳と、
E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 「セルを追加→`.py` 合成→窓が出る」等の cell-DAG 横断ストーリーは *Journey E2E* / Strategy Editor 系台本が担い、
> 本 Surface 台本は「title drag/click・spawn/close・z-order・永続化が正しい canvas 論理状態を起こすか」までを観測する。

## 対象サーフェス

infinite canvas の Content 上を **自由配置（free placement）**で漂う window（Strategy Editor / Order 等）の枠組み
（`FloatingWindowController` ＋ 入力境界 `FloatingWindowTitleInput`）。全 window は Content 直下の単一
**FloatingWindowLayer**（identity transform）の子なので pan/zoom に自動追従する。**Hakoniwa の tile swap とは別物**
（tile は grid slot のみ・自由配置不可／floating window は canvas 論理座標で position+size を自由に持つ）。**chart は
floating window ではない**（Hakoniwa tile）。z-order = layer 内 sibling index（後の sibling ほど前面）、persist は
`zOrder` int。window 構築（factory）と削除（destroy）は injected。座標は canvas 論理座標（top-left pivot・findings 0008 §2）。
実装は #15、cell-as-floating-window 拡張は #81。

## 対象ユーザー行動

title bar drag で move、title press/drag で click-to-front、spawn（factory・auto-placement cascade）、close(X)、
z-order 正規化と sibling/zOrder の対応、rect/z/visible の永続化、dormant の hide / reveal（#81 adopt）、
canvas 上での追従、旧 doc / 異常値の back-compat・sanitize。body drag は move せず canvas pan に落ちる経路、move/raise の
実感は HITL。resize は **未実装（ADR-0013 Decision 5・将来 slice）**なので行動行に理由付きで載せる。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| WINDOW-01 | title bar を drag して window を move | `FloatingWindowTitleInput.cs:54,60`（`OnDrag`）→`FloatingWindowController.cs:171`（`MoveByLogical`） | screen delta→viewport-local→`/zoom` で canvas 論理 delta、window の anchoredPosition（top-left）が更新、追従が維持 | `ViewportDeltaToLogical` 算術（zoom 依存）＋`MoveByLogical` 後の anchoredPosition を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S1/S3） |
| WINDOW-02 | title bar を click/press して最前面へ（click-to-front） | `FloatingWindowTitleInput.cs:42,49`（`OnPointerDown`/`OnBeginDrag`）→`FloatingWindowController.cs:178`（`BringToFront`） | `SetAsLastSibling`（last sibling=最前面）、`Capture` の `zOrder` が反映（TTWR `WindowManager.max_z` の capability parity） | `BringToFront` 後の `GetSiblingIndex`／`Capture().zOrder` を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S4） |
| WINDOW-03 | window を spawn（canvas 論理 top-left に factory 生成） | `FloatingWindowController.cs:65`（`Spawn`） | 既知 kind を factory で生成＋layer に親付け、anchoredPosition=top-left・sizeDelta=論理 px・pivot=(0,1)、未知 kind は skip（null）、重複 id は first-wins、size は spec.minSize へ clamp UP | `Spawn` の placement/pivot/clamp/unknown-skip/dup-first を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S3/S6） |
| WINDOW-04 | 新 window が既存窓を避けて配置（auto-placement cascade） | `FloatingWindowController.cs:90`（`SpawnAuto`）→`SpawnPlacement.Next`（`NotebookCellCoordinator.cs:78`） | anchor（viewport centre）を全 live window の top-left から対角 cascade（marimo `calcSpawnPosition`・衝突母集合は `_windows` 全体・anchor を verbatim top-left に） | `CaptureTopLefts` を母集合に `SpawnPlacement.Next` の cascade を assert（`FloatingWindowProbe` は基本 `Spawn` のみで cascade 未カバー） | 自動(E2E済) | `FloatingWindowE2ERunner`（S7） |
| WINDOW-05 | close(X) で 1 window を despawn | `FloatingWindowController.cs:161`（`Close`）／`NotebookCellCoordinator.cs:107` | 対象 id のみ destroy＋deregister（adopted scene 窓は触らない・File→New / findings 0027 D3）、未知 id は false | `Close(id)` 後の `Has`/`Count`＋adopted 窓存続を assert（`FloatingWindowProbe` は `Apply` 全置換のみで単体 `Close` 未カバー） | 自動(E2E済) | `FloatingWindowE2ERunner`（S8） |
| WINDOW-06 | z-order の正規化（sibling index ↔ zOrder int） | `FloatingWindowController.cs:230`（`Apply`）→`FloatingWindowMath.SiblingOrder` | 非連続/重複/負の `zOrder` を stable に contiguous 0..n-1 sibling index へ、`Capture` は live sibling を 0-based rank へ再ランク | `SiblingOrder` 算術＋`Apply`/`Capture` 後の sibling index を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S2/S4） |
| WINDOW-07 | window の位置/サイズ/z/可視が保存・復元される | `FloatingWindowController.cs:200`（`Capture`）／`:230`（`Apply`） | rect（x,y=top-left／w,h）＋`zOrder`＋`visible` が `Save`→disk→`Load`→`Apply` で round-trip（on-disk text に各 field・zOrder は永続層で正規化しない・visible=false は登録のまま hidden） | 非デフォルト rect・非連続 z・visible=false の disk round-trip＋fresh controller 復元を assert（vacuous-green kill） | 自動(E2E済) | `FloatingWindowE2ERunner`（S5） |
| WINDOW-08 | dormant 化（hide）と reveal（show＋最前面） | `FloatingWindowController.cs:111`（`Hide`）／`:187`（`Show`）／`NotebookCellCoordinator.cs:84,101` | adopt 窓は delete で `SetActive(false)` の dormant（never-Destroy）、次の AddCell で `Show`＝`SetActive(true)`＋`BringToFront`（#81 reveal-on-insert・ADR-0013 Decision 4） | `Hide`→`Show` の active/ sibling 遷移を assert（永続 visible=false 側は S5、reveal cycle は S9） | 自動(E2E済) | `FloatingWindowE2ERunner`（S9＋S5 visible=false leg） |
| WINDOW-09 | window が pan/zoom に追従（identity layer 経由） | `FloatingWindowE2ERunner.cs`（S3 identity layer cross-check）／`FloatingWindowController.cs:73`（`SetParent(layer)`） | layer が Content 下で identity、window.position は Content×Layer 合成で `CanvasViewMath.LogicalToViewport` 通りに追従、move 後も成立 | identity layer 経由で Unity の transform 合成と pure-math を突き合わせ（engine==math） | 自動(E2E済) | `FloatingWindowE2ERunner`（S3） |
| WINDOW-10 | 旧 doc / 異常値を安全に読む（back-compat・sanitize） | `LayoutStore.NormalizeFloatingWindows`／`LayoutStore.LoadFromJson` | 旧 sidecar（floatingWindows 無し）→empty、重複 id→first-wins、非有限/≤0 size→drop（x/y 非有限→0）、未知 kind→store 保持・spawn skip、spec-min clamp、破損→default | 各正規化ケースを `NormalizeFloatingWindows`/`LoadFromJson`＋restore controller で assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S6） |
| WINDOW-11 | move/raise の実感（title=move・body=canvas pan の経路分離） | `FloatingWindowTitleInput.cs`（title bar のみ raycast target） | 実ポインタ title drag で window が滑らかに移動・最前面化、body drag は下層 InfiniteCanvas へ落ちて pan、z-order の前後が視覚的に正しい | — | HITL専用（実ピクセル＋実マウス＋EventSystem raycast・GPU/実ウィンドウ前提） | `FloatingWindowHitlMenu` |
| WINDOW-12 | window 端を drag してリサイズ | — | — | — | 対象外（未実装・将来 slice。ADR-0013 Decision 5／`floating window` エントリ「resize は #15 の汎用 window system に含めない＝将来 slice」） | — |

> **chart を floating window と呼ばない**（Hakoniwa tile が正・`dispatcher.rs` が `PanelKind::Chart` spawn を拒否）。
> `zOrder` を Hakoniwa の `slot` に相乗りさせない（別 field・別レイヤ）。floating window rect は panel の 0..1 正規化
> `LayoutRect` ではなく canvas 論理座標の position+size。

## 観測点（詳細）

- **WINDOW-01/02/03/06/07/09/10**: `FloatingWindowProbe` が既に正本で **三方向 cross-check**（pure 算術 ×
  identity FloatingWindowLayer 経由の実 RectTransform 合成 × `LayoutStore` 直列化）。S3 が placement＋child-follow＋
  `MoveByLogical`、S4 が z-order live 適用＋`BringToFront`、S5 が非 vacuous disk round-trip（on-disk TEXT 証明）、
  S6 が back-compat/sanitize。E2ERunner はこれらを昇格。
- **WINDOW-04（auto-placement cascade）**: `SpawnAuto`/`SpawnPlacement.Next` の対角 cascade は production では
  `NotebookCellCoordinator` 経由でのみ exercised され、昇格元 `FloatingWindowProbe` S1–S6 は明示座標の `Spawn` のみで
  `SpawnAuto` 直接 assert は無かった。**S7（新規）**が `controller.SpawnAuto` を直接駆動: 空 canvas は anchor verbatim、
  同 anchor 反復で対角 `DefaultOffset` cascade（1 step / 2 step）、さらに**非 cell の Order 窓**を anchor に置いて
  「collision 母集合 = `CaptureTopLefts` の全 `_windows`」を非空虚に固定。
- **WINDOW-05（close X）**: 単体 `Close(id)`（adopted 窓を残して 1 窓だけ落とす）は File→New の cutover 経路で使われるが、
  昇格元 `FloatingWindowProbe` は `Apply` 全置換 remove のみで単体 `Close` 直接 assert は無かった。**S8（新規）**が
  2 窓 spawn → 存在を先に Check（vacuous-green kill）→ 片方 `Close` で対象のみ destroy+deregister・sibling 存続・
  未知 id は false を assert。
- **WINDOW-08（dormant hide/reveal）**: 永続 `visible=false` の復元は S5 がカバー済み。`Hide`→`Show`（reveal-on-insert・
  #81）の active/sibling 遷移は昇格元では未カバーだったので **S9（新規）**が駆動: `Hide` で `SetActive(false)`＋registered
  維持・count 不変、`Show` で `SetActive(true)`＋`BringToFront`（last sibling）。'other' 窓を前面に置いて raise を非空虚化。

## 自動判定（合格条件）

- ログに `[E2E FLOATING WINDOW PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)`/`要新規自動化` 行の観測点を 1 つでも落としたら `[E2E FLOATING WINDOW FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `MoveByLogical` の加算・`BringToFront` の `SetAsLastSibling`・`SiblingOrder` の
  stable sort・`Close` の単体 destroy を消すと、対応する assert が必ず落ちること。新規 section も同様:
  `SpawnPlacement.Next` の対角 step（`p = new Vector2(p.x+offset, p.y+offset)`）を消す（= anchor を常に返す）と S7 が落ち、
  `Show` の `SetActive(true)` または `BringToFront` を消すと S9 が落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `FloatingWindowProbe` | batchmode・pure＋identity-layer 実 RectTransform＋直列化（**昇格元 — `FloatingWindowE2ERunner` へ git mv・改名済み**） | S1–S6 を assert 1 行も削らず移送（WINDOW-01/02/03/06/07/09/10、S5 は WINDOW-08 の visible=false leg）＋ S7/S8/S9 を新規追加（WINDOW-04/05/08）。旧 class 名は dead |
| `FloatingWindowHitlMenu` | HITL ハーネス | WINDOW-11 の実感・経路確認用に**探索 Probe として残す**（昇格対象外・名称据え置き） |

## `FloatingWindowE2ERunner.cs` 実装方針（第二波・実装済み）

> section ↔ Action ID は **(B) 自然な検証単位＋`Covers:`**（E2E-CONVENTIONS.md「runner の section ↔ Action ID 対応方針」）。
> 1 section が複数 Action ID を cover する（例 S3=WINDOW-01/03/09、S4=WINDOW-02/06、S6=WINDOW-03/10）。各 section の
> `// Covers: WINDOW-xx` を正とし、Action ID ごとに人工分割しない。昇格元 probe の `Execute()`（null=PASS）gate 形は温存。

- `FloatingWindowProbe` 同型に **headless な Viewport→Content→FloatingWindowLayer（identity）の RectTransform ツリー**を
  組み、factory は bare RectTransform を mint・destroy は `DestroyImmediate`。`FloatingWindowController` を pure に駆動
  （実 `BackcastWorkspaceRoot` 合成は不要・Python-FREE）。
- WINDOW-04/05/08 の新規セクション S7/S8/S9 は `SpawnAuto`/`Close`/`Hide`/`Show` を直接駆動（cascade は `CaptureTopLefts`
  を母集合に・非 cell 窓も含むことを固定、close は対象のみ・sibling 存続、reveal は active/sibling 遷移を assert）。負の
  assert は対象存在を先に Check して vacuous-green を回避。実 root は不要だった。
- disk round-trip（WINDOW-07/10）は production sidecar を汚さない一時パス（`floating_window_e2e`）へ。
- セクション構成は操作一覧表の `自動(*)` 行を S1〜S9 として並べ、最初の失敗メッセージを返す
  `Execute()`（null=PASS）パターン。teardown は spawned GameObject の `DestroyImmediate` ＋ 一時 dir 削除。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod FloatingWindowE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。
