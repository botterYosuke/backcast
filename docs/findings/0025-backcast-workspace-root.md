# findings 0025 — Backcast workspace root（本線ワークスペース合体 scene・#59）

Epic #1 ／ 方針: **ADR-0005**（TTWR `src/ui` との 1:1 表面 parity）・**ADR-0003**（layout capability parity）・
**ADR-0001**（Unity in-proc 埋め込み Python／UI 死＝執行死）。`grill-with-docs`（2026-06-15）で導出。

issue #59 は「個別 UI 表面を 1 画面に配置・合体させる本線 scene（chrome frame ＋ workspace 合体ルート ＋
単一起動エントリ）」を作る。本 issue の射程は **枠・配置・合体・起動エントリのみ**で、各表面の中身の充実は
各 issue（menu bar=#42 / sidebar=#31(closed) / chart=#55/#56 / 板=#57 / settings=#43 / footer Live/Auto=#39）。

正式 term は **「Backcast workspace root」**（CONTEXT.md に登録）。「mainline」は移行期語なので恒久型名に使わない。

---

## §1 合体 scene の実体 = scene-authored（owner 判断 B）

- `Assets/Scenes/BackcastWorkspace.unity` を**新設**し、**EditorBuildSettings の先頭かつ唯一の有効 scene** にする。
  `SampleScene`（Camera/Light/Volume の Unity テンプレート）は production entry として名称不適のため流用しない。
- UI 部品（menu bar / sidebar / center workspace / infinite canvas / HakoniwaRoot と主要 tile / footer と操作
  ボタン / floating editor 窓の枠）を **scene 上の GameObject として authoring** する。プレハブ化は必須でない。
- `[RuntimeInitializeOnLoadMethod]` 自動 bootstrap は **HITL 向けの暫定手段**。本線は scene authored が正。
- 既存ビルダー（`ChartView.Build` / `ReplayFooterView.Build` / `HakoniwaController` ctor / `StrategyEditorContentBuilder`
  等）は scene 上に不足する内部要素の生成・初期化に**暫定利用してよい**。段階的に serialized reference へ移行する。
- OnGUI 実装（menu bar / sidebar）も可能な範囲で scene 上 View component へ載せる（§6）。
- **owner 判断**：作業量が #59 当初想定を超えてもこの方針を優先する（B-thin =空コンテナ＋全動的生成は不採用）。

→ 非可逆な方針転換（RuntimeInitialize bootstrap → scene authored production entry・新 scene を唯一 build entry・
ScenarioStartup demote）のため **ADR 化対象**（本 findings 末尾で提案）。

## §2 命名（確定）

| 役割 | 型/名称 |
|---|---|
| scene | `BackcastWorkspace`（`Assets/Scenes/BackcastWorkspace.unity`） |
| scene root component（Python/layout/単一オーナー・authored View 結線） | `BackcastWorkspaceRoot` |
| Replay 駆動 durable クラス（engine lifecycle/launcher/poll/transport RPC） | `ReplayEngineHost` |
| menu bar の scene View host | `MenuBarView` |
| sidebar の scene View host | `UniverseSidebarView` |

CONTEXT 正式 term = **「Backcast workspace root」**。

## §3 中央 workspace の空間ネスト = P-all（既存権威定義と一致）

```
CenterWorkspace (menu/footer/sidebar を除いた scene-authored 矩形)
└─ Viewport (Image + RectMask2D + InfiniteCanvasInputSurface)
   └─ Content (pan = anchoredPosition / zoom = localScale)
      ├─ HakoniwaRoot
      │  └─ Chart tile (ChartView)
      └─ FloatingWindowLayer
         └─ Strategy Editor (floating window)
```

- **Hakoniwa も FloatingWindowLayer も Content 配下**＝pan/zoom に一緒に追従する（CONTEXT.md「infinite canvas」/
  findings 0006/0007/0008 で確定済み。P-float =チャート固定は不採用）。
- Hakoniwa swap は `RectTransformUtility.ScreenPointToLocalPointInRectangle` で screen→root-local 変換しており
  Content の pan/scale 下でも成立する設計。**HITL で zoom 倍率を変えた状態の swap を必ず確認**する。

## §4 既定 workspace の中身（質問7-A 採用）

- 既定 Hakoniwa グリッド = **`[startup, chart]` の 2 tile**。Startup は `PanelKind::Startup` slot 0（CONTEXT.md 確定）、
  Chart は slot 1。Replay 設定（`ScenarioStartupTile`）と footer transport を成立させるために設定タイルは必要。
- Strategy Editor は **FloatingWindowLayer に重ねる**（floating window）。
- これで AC①「menu bar / sidebar / 中央チャート / footer / Python floating editor の 1 画面同時表示」を満たす。

## §5 Replay engine 駆動 = R-extract（質問7-B 採用）

- `ScenarioStartupHitlHarness` は明示的に **throwaway host**。launcher/poll/transport を root へ丸コピーすると
  composition root が Python orchestration まで抱える巨大クラスになる。
- **durable な `ReplayEngineHost`** に engine lifecycle / launcher / poll / transport RPC を抽出する。
  これは将来予測のための抽象化ではなく、throwaway harness から production orchestration を分離する**必要な昇格**。
- `ScenarioStartupController` / `ReplayLifecycle` / `ReplayTransportViewModel` は再利用。ChartView への状態反映は
  root または薄い presenter で行う。
- `ScenarioStartupHitlHarness.AutoBootstrapEnabled` を **false**（demote）。harness は抽出 Host を使えれば使い、
  難しければ当面残置（削除は別 issue / post-impl 判断）。

## §6 menu bar / sidebar の scene View 化 = V-host（質問6 採用）

- scene-authored の `MenuBarView` / `UniverseSidebarView` MonoBehaviour を各コンテナへ配置。
- 既存 `MenuBarViewModel` / `UniverseSidebarController`（brain）を**保持し `OnGUI()` から描画**（uGUI 全面書き換えは不要）。
- 描画領域は**ハードコードせず各コンテナの RectTransform から算出**し、表示・入力が領域外へ漏れないようクリップする
  （`GUILayout.BeginArea` 等）。
- CenterWorkspace は menu/footer/sidebar を除いた scene-authored 矩形に配置。
- **sidebar の production View 化は #59 で扱う**（#31 は closed・既存 brain 再利用・V-host は暫定 production host）。
  menu bar の uGUI 化・OnGUI 撤去は #42（open）側の後続責務。

## §7 single Play-owner（質問4・契約）

契約：**root 有効なら mainline が所有し、Python 系 HITL は安全に拒否される。root を無効化して Play した場合のみ、
選択した HITL が所有する。**

- 通常 Play では scene root が唯一の Python owner。
- 個別 Python HITL を実動させる場合は **Play 前に Inspector で root GameObject を無効化**する。
- root 稼働中に Python HITL を選んだ場合は `PythonEngine.IsInitialized` を確認して**明示的に起動拒否**する
  （mainline engine の再利用も Shutdown もしない）。
- `[SerializeField] bool _ownPlay` トグルは **Python engine の自動起動だけ**を制御する（UI authoring/初期化は skip しない）。
- `Application.isBatchMode` では Python 初期化を抑止。
- IsInitialized ガード追加は最低限 `ScenarioStartupHitlHarness`（＋ AutoBootstrap 無効化）。DepthLadder /
  LiveAdapterTracer / MenuBarHitl / VenueLoginSecret は既にガードあり。共有 `EnginePlayOwner` レジストリ全面改修は不要。

## §8 layout 永続化（質問5・8B・契約）

- **パス**：`LayoutPathResolver.DefaultPath()`（= `persistentDataPath/layout.json`、本番既定の単一グローバル sidecar）。
- **対象 4 次元**：① Hakoniwa tile（panels）② canvas pan/zoom（canvasView）③ floating windows（floatingWindows）
  ④ Strategy Editor 開ファイル（strategyEditors）。
- **Hakoniwa は `LayoutBinder` ではなく `HakoniwaController.Capture/Apply`**（slot 順の正本を扱う）。canvas は
  `InfiniteCanvasController.CaptureView/ApplyView`、floating は `FloatingWindowController.Capture/Apply`。root が
  各コントローラの capture を呼び **1 ドキュメントに合成**する。
- **復元順序**：canvas → Hakoniwa → floating window → Strategy Editor。
- **scene-authored の Strategy Editor 窓は破棄・再生成せず**、`FloatingWindowController` へ**既存窓として登録**して
  geometry を適用する（登録用の小メソッド追加が要る）。保存データ上の**追加窓だけ spawn** する。
- **保存トリガー（3 経路・autosave なし）**：
  1. 明示的な Layout Save 操作（menu bar File→Save、§9）
  2. `OnApplicationQuit`
  3. Editor の Play 停止用 `OnDestroy` fallback
- **二重保存防止フラグ**を設け、**root が所有している間だけ**保存する。

## §9 menu bar File = Layout（質問8A・ADR-0005 / findings 0017 parity）

- **File→Save** = capture して既定パスへ保存。
- **File→Open** = mode 副作用（`FileOpenModeSideEffect`・Live 用）を評価後、既定パスから復元。#59 Replay 主線では
  mode 副作用は実質 no-op（将来 #39/#42）。
- **File→New** = `MenuBarViewModel.FileNew()` に従い workspace clear、**実行中は拒否**（ADR-0001 安全）。
- Save/Open/New の実 I/O と workspace 操作は **root が担当**（VM は決定のみ）。

## §10 終了 teardown（質問8B・確定順序）

ADR-0001 の in-proc 埋め込みで Unity 終了＝Python 即死（orphan 構造的に不在）。Replay は実弾なし（broker 残注文取消
＝ADR-0001 decision 6 は Live/Auto 限定）。`OnApplicationQuit` と `OnDestroy` は同一の **idempotent な Stop/Dispose** に集約：

1. 二重実行防止フラグを立てる
2. layout を一度だけ保存（root 所有時のみ）
3. 実行中なら `force_stop_replay`（**必須**＝launcher の同期 `start_engine` を終了させる。ただし server 未公開など
   呼べない状態は skip）
4. poll 停止
5. poll / launcher を bounded join
6. join 失敗時は警告しブロックし続けない
7. `PythonEngine.Shutdown()` は呼ばない（S0-sanctioned・interpreter は process 終了で消える）

**既知制限（code-review 2026-06-15）**: セッション内で Replay を再 Run すると、前回の `DataEngine`/`InprocLiveServer`
の PyObject ハンドルが Dispose されず残る（`TryStartRun` は `_pollServer=null` にするのみ）。現行の同期 DuckDB→
kernel Replay は `start_engine` が完了時に自己 `force_stop_replay` するため lingering thread は無く実害は pythonnet
ハンドルの蓄積のみ（domain reload 無効の Editor 再 Play 跨ぎで残る）。`start_engine` が将来 live loop/runner を
回すようになったら `inproc_server.close()` 相当の teardown 配線が必須（別 issue）。

## §11 検証サーフェス（質問10・FLOWS.md は作らない／正本は本 findings）

### ① AFK probe（headless・Python-free・batchmode 可）= `BackcastWorkspaceProbe`（#59 必須ゲート）

- `BackcastWorkspace.unity` が Build Settings で**唯一の有効 scene**であること
- 必須 GameObject/component と serialized reference が存在すること
- authored hierarchy が Viewport / Content / {HakoniwaRoot, FloatingWindowLayer} を満たすこと
- menu / sidebar / footer が Content **外**であること
- Startup が slot 0、Chart が slot 1 であること
- temp path と実在する一時 `.py` を使った **非 default 4 次元 round-trip**（capture→Save→Load→Apply で
  `LayoutDocument.StructurallyEqual`）
- scene-authored editor 窓が**再生成されず**、登録・復元されること
- Stop/Dispose を複数回呼んでも save / force-stop が**各 1 回**であること
- ownership 判定は **PythonEngine を起動せず**、注入した predicate で検証すること
- frame layout：menu/sidebar/footer を除いた CenterWorkspace 矩形が領域外漏れしないこと

### ② owner-run HITL（表示・実機）= `BackcastWorkspace` を Play

- menu / sidebar / chart / footer / floating editor が 1 画面同時表示（AC①）
- Python engine が 1 つだけ生成（AC③）／Play 停止で force-stop・thread join 完了・例外なしのログを確認
- zoom 倍率変更下で Hakoniwa swap が成立（§3 懸念点）
- footer Run→bar streaming、File→Save→再 Play→復元（AC②）。layout 復元後も zoom 下の swap・floating window
  位置・editor file が復元されること
- root 稼働中に Python 系 HITL を起動すると安全に拒否される／root 無効化時は選択した Python HITL が単独起動できる

### 回帰ゲート（再実行）

既存の Layout / InfiniteCanvas / Hakoniwa / FloatingWindow / StrategyEditor 各 probe を回帰ゲートとして再実行する。

### 実機実証（2026-06-15・headless GREEN）

Unity 6000.4.11f1 `-batchmode -nographics` で全 probe GREEN（コンパイルエラー・例外なし）：

- **`BackcastWorkspaceProbe.Run`**（新 AFK 必須ゲート・全7セクション）→ `[BACKCAST WORKSPACE PASS]`。
  scene 構築＋唯一の有効 build scene＋必須 serialized ref／hierarchy（Viewport/Content/{HakoniwaRoot,
  FloatingWindowLayer}・chrome は Content 外）／Startup=slot0・Chart=slot1＋swap／非 default 4次元 disk
  round-trip（temp path＋実在 temp .py）／adopt 非再生成（同一インスタンス復元・追加窓のみ spawn）／OnceGate
  各1回／ownership 判定（Python 非起動・injected predicate）。
- **回帰 GREEN**：`FloatingWindowProbe` / `HakoniwaProbe` / `InfiniteCanvasProbe` / `ReplayLayoutProbe` /
  `StrategyEditorProbe` いずれも PASS（`FloatingWindowController.Adopt/ApplyGeometry` 追加の回帰なし）。

残る owner-run HITL（表示・zoom 下 swap・実 Replay streaming・実 Play 停止 teardown・per-part 拒否）は
表示＋実 catalog が要るため owner 確認に委ねる。これら HITL 受入完了で ADR-0009 を `accepted` へ昇格する。

### 実装ファイル

- 新規 runtime: `Assets/Scripts/Live/{BackcastWorkspaceRoot, ReplayEngineHost(+OnceGate/WorkspaceOwnership),
  MenuBarView, GuiRectUtil}.cs`、`Assets/Scripts/Universe/UniverseSidebarView.cs`
- 新規 Editor: `Assets/Editor/{BackcastWorkspaceSceneBuilder, BackcastWorkspaceProbe}.cs`
- 新規 scene: `Assets/Scenes/BackcastWorkspace.unity`（EditorBuildSettings = これ1つ）
- 変更: `FloatingWindowController`（`Adopt`/`ApplyGeometry` 追加）、`ScenarioStartupHitlHarness`
  （AutoBootstrap=false＋`IsInitialized` ガード）

---

## 提案：ADR 化

§1 の方針転換（① RuntimeInitialize bootstrap → scene-authored production entry、② `BackcastWorkspace.unity` を
唯一の build entry に新設、③ ScenarioStartup を demote し `ReplayEngineHost` を昇格、④ single Play-owner を
scene root に一本化）は **hard-to-reverse・surprising・real trade-off** の 3 条件を満たすため ADR 候補。
ADR-0005/0003/0001 は **supersede せず参照**し、本 findings を「方針: ADR-000X」で指す（ADR は自己保護条項で固定）。
