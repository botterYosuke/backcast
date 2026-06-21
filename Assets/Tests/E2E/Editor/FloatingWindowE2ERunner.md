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
| WINDOW-02 | title bar を click/press して最前面へ（click-to-front） | `FloatingWindowTitleInput.cs`（`OnPointerDown`/`OnBeginDrag`）→`FloatingWindowController.NoteUserFocus`（`BringToFront`＝`SetAsLastSibling`＋#101 で focus 記録） | `SetAsLastSibling`（last sibling=最前面）、`Capture` の `zOrder` が反映（TTWR `WindowManager.max_z` の capability parity）。#101: title-bar press は `NoteUserFocus`（=BringToFront＋focus 記録）、programmatic raise は `BringToFront`（focus 非記録） | `BringToFront` 後の `GetSiblingIndex`／`Capture().zOrder` を assert（S4）、focus 記録は S15 | 自動(E2E済) | `FloatingWindowE2ERunner`（S4／S15） |
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
| SNAP-01 | drag リリースで近接窓の辺へ磁石スナップ（pure 算術） | `FloatingWindowMath.SnapOffset`（#99 / findings 0075 §1） | flush（右↔左・上↔下）＋同辺整列（左↔左 等）の最近傍 Δ、x/y 独立、閾値超→0、threshold≤0/NaN→0、最近傍 tie-break | `SnapOffset` の flush/align/独立/閾値/guard/nearest-wins を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S10） |
| SNAP-02 | drag リリースの snap を live window へ適用（controller wiring） | `FloatingWindowController.SnapOnRelease`（`FloatingWindowTitleInput.OnEndDrag`） | 自分を除外・hidden 窓は無視・dragged のみ anchoredPosition 更新（group 伝播なし）・適用 Δ を返す・unknown→zero・tight threshold で reject | 2 窓で 5px flush を default 閾値で吸着、neighbour 不動、hidden/unknown/tight を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S11） |
| DOCK-01 | dock kind（chart + 5 base singleton）が catalog に存在 | `FloatingWindowCatalog.Default`（#99 / findings 0075 §2） | chart は multi-instance、buying_power/orders/positions/run_result/startup は singleton、各 accent・defaultSize≥minSize、unknown-kind tolerance 不変 | 6 dock kind の resolve・size 健全性・既存 kind 非退行・unknown tolerance を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S12） |
| DOCK-02 | base+chart の初回（リセット）グリッド配置（pure 算術） | `DockDefaultPlacement.ComputeRects`（#99 / findings 0075 §4） | `ceil(√n)` グリッドの絶対 canvas 論理 rect、row-major slot 0=top-left、y up-positive、重なり無し、n≤0→空。**base 5 枚専用**（chart は DOCK-04 が supersede） | n=0/5/4/1 の slot 位置・cell サイズ・非重複を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S13） |
| DOCK-03 | 新 dock 窓をターゲット窓の辺へ flush 吸着配置（pure 算術） | `DockSnapPlacement.PlaceAdjacent`（#101 / findings 0078 §1） | 探索順 右→下→左→上、flush＋同辺整列、strict 非重複選択、gap=0 flush、サイズ verbatim、全辺埋→対角カスケード | flush-right / 各辺フォールバック / overflow カスケード（非重複 sweep）/ strict-touch / gap を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S14） |
| DOCK-04 | `[+Add]`／universe add で chart を固定サイズ＋フォーカス吸着 spawn | `FloatingWindowController.SpawnDockedToFocus`／`BackcastWorkspaceRoot.SpawnChartWindow`（#101 / findings 0078 §2/§3） | サイズ = spec 固定（520×360・総数 N 非依存）、位置 = focus 窓（`NoteUserFocus`＝title-bar press のみ記録）へ flush、no-focus/closed→最近傍 fallback、programmatic BringToFront は focus 非記録、dup/unknown guard | 3 枚ともサイズ一定、focus 勝ち、BringToFront 非偽造、fallback、guard を controller 直駆動で assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S15） |
| DOCK-05 | `[+Add]` chart の大きさ一定＆吸着の実感（目視） | `BackcastWorkspaceRoot.SpawnChartWindow`（実 playmode） | 何枚目でも 520×360・直前フォーカス窓の辺にくっつく・add/remove・mode 切替・persist round-trip で崩れない | — | HITL専用（実ピクセル＋実 spawn＋実レイアウト保存。findings 0078 §6） | — |
| PLANE-01 | パンで奥（元箱庭 6 種）と手前（エディタ/発注）に速度差が出る＝奥行き | `BackcastWorkspaceRoot.BuildWorkspace`（`_dockLayer` 1.0× ／ `_floatingLayer` 1.2×）→`CanvasViewMath.ParallaxLayerOffset`（#103 / ADR-0018 / findings 0075 §10） | DockLayer は背面 sibling（Content の早い子）、factor 1.0=offset 0（Content を 1× で乗る）、floating は factor 1.2=offset (1-1.2)·pan、同一論理 top-left の 2 窓がパン後に異なる viewport 位置へ（engine==math 合成） | 背面 sibling・`ParallaxLayerOffset(1.0)=0`/`(1.2)≠0`・dock 1× 追従・floating 1.2× 追従・速度差≠0 を assert（合成スタック=S16）＋**実シーン**で DockLayer が FloatingWindowLayer の背面 sibling・`_dockLayer`/`_floatingLayer` serialized 参照を検証（S19） | 自動(E2E済) | `FloatingWindowE2ERunner`（S16／S19） |
| PLANE-02 | プレーンをまたぐ吸着が起きない（同一プレーン内のみ吸着） | `FloatingWindowController.SnapOnRelease`（per-controller 母集合）／`BackcastWorkspaceRoot` の 2 controller 分離・`DockShape.IsDockKind` ルーティング（#103 / ADR-0018 / findings 0075 §10） | dock 窓を手前窓の辺へ近づけてリリースしても吸着しない（手前窓は別 controller で母集合外）、同一プレーン内（奥どうし）は従来どおり吸着、dock spawn の focus は奥プレーン内で解決 | 別 controller 2 窓で cross-plane snap=0、同一 controller で 5px 吸着、dock focus は奥プレーン窓へ flush、`IsDockKind` ルーティング parity を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S17） |
| PLANE-03 | 保存/復元が両プレーンのウィンドウを kind でルーティングして round-trip | `BackcastWorkspaceRoot.CaptureLayout`（両 controller union）／`RestoreFloating`（`DockShape.IsDockKind` で plane 振り分け）（#103 / ADR-0018 / findings 0075 §10） | 両 controller を 1 つの `floatingWindows` に union → disk → 復元時に dock 6 種→奥 layer・order→手前 layer へ振り分け、hidden startup は hidden のまま、cross-plane leak なし、スキーマ追加 0 | capture union（4 窓）→disk→kind ルーティング restore で各窓が正しい layer に親付け・hidden 保持・leak なしを assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S18） |
| PLANE-04 | パンで奥行きが見える・またぎ吸着しない・mode 切替・persist round-trip の実感（目視） | `BackcastWorkspaceRoot`（実 playmode・シーン再ビルド後） | パンで奥 1.0× と手前 1.2× の速度差が視認できる、奥パネルと手前エディタは近づけてもくっつかない、Replay/Live 切替で startup show/hide、保存→再起動で両プレーンが復元 | — | HITL専用（実ピクセル＋実パン＋実レイアウト保存。ADR-0018 / findings 0075 §10） | — |
| GROUP-01 | window group の `groupId` が persist round-trip する（schema 追加 / Spawn=null 不変） | `FloatingWindowLayout.groupId`（`LayoutDocument.cs`）／`FloatingWindowController.Spawn(... groupId)`／`Capture` / `Apply` pass-through（#104 / ADR-0019 D1 / findings 0082 §1, §11） | groupId フィールドが `floatingWindows[*]` に additive で載り on-disk JSON に `"groupId":"grp_<hex32>"`、新規 controller への `Apply` で復元、旧 sidecar（フィールド無し）は null、`Spawn` 直後の groupId は null（attach はユーザ drag-release だけ） | 4 窓 doc（2 共有 + 1 単独 + 1 別 group）の Save→Load→Apply 復元、旧 sidecar→null、existing-entry の Apply 更新 path も groupId pass-through を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S20） |
| GROUP-02 | flush 隣接判定が attach トリガとして正しい（pure 算術） | `FloatingWindowMath.IsFlushAdjacent`（#104 / ADR-0019 D4 / findings 0082 §3） | 辺一致 `eps=1px` 以内 ∧ 直交軸 overlap > 0 で flush（4 方位）、same-edge 整列だけ（離れて並ぶ）/ corner-only 接触（overlap=0）/ eps 外 / eps≤0 / NaN は false | 4 flush 方位・same-edge alignment with gap・corner-only・eps 境界・degenerate eps を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S21） |
| GROUP-03 | merge cascade が Hakoniwa identity を保護する（pure 算術） | `FloatingWindowMath.ResolveMergeWinner`（#104 / ADR-0019 D5 / findings 0082 §4） | (1) Hakoniwa group 単独 → 勝ち、(2) Hakoniwa 複数 → Hakoniwa subset 内で member 数最大、(3) 同数 → StringCompareOrdinal 辞書順最小、(4) 全 null → null（caller mint GUID） | Hakoniwa-priority・Hakoniwa subset 内 size cascade・size max・dict tie-break・全 singleton・lone non-null survivor・null/empty 入力を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S22） |
| GROUP-04 | drag リリース時に flush 隣接で groupId 付与/merge（controller wiring） | `FloatingWindowController.SnapOnRelease`（#104 / ADR-0019 D4, D5 / findings 0082 §3, §4） | 単独 release で no-attach、flush 隣接で grp_<hex32> mint（dragged + partner 共有）、same-edge align 単独では非 attach、Hakoniwa-priority cascade、size-max cascade、`SpawnDockedToFocus` flush 配置でも groupId=null（attach はユーザ drag-release のみ） | 6 シナリオを controller 直駆動で assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S23） |
| GROUP-05 | drag mode 7 モード境界判定（pure 算術） | `FloatingWindowMath.EvaluateDragMode` ／ `D_DETACH=64f`（#104 / ADR-0019 D6 / findings 0082 §6） | (a) SoloDrag（非 group）、(b) NormalGroupTranslate（near 非 Hakoniwa）、(c) NormalGroupDetach（far 非 Hakoniwa）、(d) HakoniwaSwap（near Hakoniwa hasTarget）、(e) HakoniwaSnapBack（near Hakoniwa no target）、(f) HakoniwaDetach（far Hakoniwa 非 core）、(g) HakoniwaCoreLock（far Hakoniwa core）、(h) D_DETACH 境界は strict `>` で near 側 | 8 mode 分岐を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S24） |
| GROUP-06 | 通常 group ドラッグで一体移動（NormalGroupTranslate live wiring） | `FloatingWindowController.DragApplyDelta`（#104 / ADR-0019 D7 / findings 0082 §6） | dragged + 同一 visible/live groupId メンバ全員に frameDelta 適用、groupId 不変、Detach 状態（>D_DETACH）は live geometry 凍結、SoloDrag は単独移動、singleton group（visible<2）は SoloDrag fallback | 4 ケースを assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S25） |
| GROUP-07 | detach commit + 連鎖 dissolve helper + Close cascade + Hide 温存 | `FloatingWindowController.ReleaseDrag` / `DissolveIfShrunkTo` / `Close` / `Hide`（#104 / ADR-0019 D7 / findings 0082 §5, §10） | NormalGroupDetach commit で `dragged.groupId=null` + 残 visible ≥2 → group 維持・残 1 → 連鎖 dissolve、HakoniwaDetach も同じ、Close も同 helper、Hide は groupId 温存（Replay/Live 復活）、helper 直接呼びは threshold で dissolve | 7 ケースを assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S26） |
| GROUP-08 | Hakoniwa group は一体移動禁止＋core 不抜＋mode-switch 動的再評価 | `FloatingWindowController.DragApplyDelta` / `BuildDragContext`（#104 / ADR-0019 D3, D7 / findings 0082 §2, §5） | Hakoniwa group の dragged drag → live 凍結（translate ban）、core を D_DETACH 超で drag → HakoniwaCoreLock → release で rest snap-back、mode-switch hide で startup hidden ⇒ visible<2 ⇒ orders は SoloDrag 可動、Show 復帰で再昇格、非 Hakoniwa group の hidden-sibling でも SoloDrag fallback | 5 ケースを assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S27） |
| GROUP-09 | swap drop target 解決（pure 算術） | `FloatingWindowMath.ResolveDropTarget`（#104 / ADR-0019 D7 / findings 0082 §7） | カーソル直下メンバ、重なり最前面 sibling（siblingIndex max）優先、dragged 自身は除外、cursor が空 ⇒ null、null/empty 入力 ⇒ null | 6 ケースを assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S28） |
| GROUP-10 | Hakoniwa swap commit （(x,y,w,h) 4 値交換） + target なし snap-back | `FloatingWindowController.CommitHakoniwaSwap` / `ReleaseDrag`（#104 / ADR-0019 D7 / findings 0082 §7） | HakoniwaSwap mode で release: dragged↔target が `(x,y,w,h)` を 4 値交換、kind/id/groupId 不変、group footprint 不変、target なし `<D_DETACH` で HakoniwaSnapBack ⇒ rest 戻し | 2 シナリオを assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S29） |
| GROUP-11 | cross-plane group restore split（plane 跨ぎ groupId を分割） | `BackcastWorkspaceRoot.SplitCrossPlaneGroups` ／共有 `DissolveIfShrunkTo`（#104 / ADR-0019 D9 / findings 0082 §9） | 単一 plane group は不変、cross-plane は member 数多数派 plane を残し負け側 groupId=null、同数なら dock 優先（Hakoniwa identity bias）、独立 group の混在は別個に解決、共有 dissolve helper（Slice D と同一）で残 1 を連鎖 dissolve | 6 ケースを assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S30） |
| GROUP-12 | 7 mode 別の drag ghost preview 構造（枚数 / rect / style / 前面 sibling / commit-on-release） | `DragGhostLayer.Render` ／ `FloatingWindowController.ComposeDragGhosts`（#104 / ADR-0019 D8 / findings 0082 §8） | SoloDrag/NormalGroupTranslate=0 枚、NormalGroupDetach/HakoniwaDetach=1 枚 SOLID at cursor、HakoniwaSwap=2 枚（dragged SOLID at target rect + target DASHED at dragged rest）、HakoniwaSnapBack/HakoniwaCoreLock=1 枚 SOLID at rest、container は最後 sibling（前面）、Clear で 0 枚、ReleaseDrag で commit-on-release clear、ALPHA=0.45 定数 | bare-stack DragGhostLayer + spawned RectTransform 経由で 7 mode + Clear + commit-on-release を assert | 自動(E2E済) | `FloatingWindowE2ERunner`（S31） |
| GROUP-13 | drag ghost の見た目と felt drag（実 alpha 0.45 / kind accent / dashed vs solid border / D_DETACH の felt 距離 / パン中の plane+ghost 整合） | `DragGhostLayer.CreateUguiGhost`（実 uGUI Image + CanvasGroup）／実 playmode（#104 / ADR-0019 D8 / findings 0082 §8） | 実 alpha・kind accent 色・dashed vs solid 描画品質・drag 中の felt 距離・パン中に ghost が plane と一緒に動く・swap で 2 枚同時表示の視認性 | — | HITL専用（実ピクセル＋GPU ＋実マウス／レンダリング品質。findings 0082 §8） | — |

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
- セクション構成は操作一覧表の `自動(*)` 行を **S1〜S15** として並べ、最初の失敗メッセージを返す
  `Run()`（null=PASS）パターン。teardown は spawned GameObject の `DestroyImmediate` ＋ 一時 dir 削除。
  #99 で S10/S11（snap=SNAP-01/02）・S12/S13（dock catalog & default placement=DOCK-01/02）、#101 で S14（DockSnapPlacement
  =DOCK-03）・S15（focus-adjacent dock spawn=DOCK-04）を追加。**#101 で S11 の pre-existing 偽 RED も修正**（sub-minSize
  spawn が spec-min clamp で右辺ずれ→snap 0 になっていた。findings 0078 §4）。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod FloatingWindowE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。
