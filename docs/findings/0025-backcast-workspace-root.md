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
CenterWorkspace (= 無限空間フィールド・全画面 stretch・最背面 sibling)
└─ Viewport (Image[不透明背景] + RectMask2D + InfiniteCanvasInputSurface)
   └─ Content (pan = anchoredPosition / zoom = localScale)
      ├─ HakoniwaRoot
      │  └─ Chart tile (ChartView)
      └─ FloatingWindowLayer
         └─ Strategy Editor (floating window)
```

**背景＝無限空間は全画面（owner 2026-06-15）**：フィールド（Viewport）は中央に inset せず**ウィンドウ全体に
stretch**し、**最背面 sibling**に置く。menu / sidebar / footer の chrome はその上に重なる（menu/sidebar は OnGUI
で常に最前面、footer は uGUI なのでフィールドより後の sibling＝上に描画）。これによりサイドバー/メニュー/フッター
の裏も無限空間フィールドになり、カメラの skybox は Viewport の不透明背景で完全に隠れる。chrome が Content の外で
ある点（CONTEXT「infinite canvas」）は不変。

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

**owner-run HITL（2026-06-16・全 pass）**：owner が実機 Play で全項目を確認し ADR-0009 を `accepted` に昇格：
- ① menu/sidebar/中央チャート/footer/**floating Strategy Editor** が1画面同時表示（AC①）・無限空間フィールドが
  全画面（chrome の裏も背景・skybox 消失）・Python engine 1つ生成（`pass!!`）
- ② infinite canvas の pan/zoom・**zoom 倍率を変えた状態の Hakoniwa tile swap**（タイル chrome ＝ root 背景／
  タイル背景／ヘッダー帯を追加して視認＋swap 配線。HITL ③ で「タイルが背景と同色で区別不能・swap 未配線」を検出し修正）
- ③ Replay 実行 → チャートに bar streaming・footer の play/pause/step/speed/stop
- ④ `File→Save`→再 Play で layout 復元（HITL ④ で「File ドロップダウンが menu-bar 領域にクリップされ非表示」を
  検出し、バー下の独立 area＋`GUI.depth` で修正）
- ⑤ single Play-owner：root 稼働中に Menu Bar HITL 起動 →`refused: PythonEngine already owned`（拒否ログを追加）／
  `BackcastWorkspaceRoot` 無効化で Play → Menu Bar HITL が単独所有し `[MENU BAR HITL PASS] (14)`。Play 停止時の
  teardown も `Stop: threads joined … interpreter left alive` ＋ `teardown complete` を確認（AC③ orphan 不在）

**HITL で検出・修正した不具合**（memory [[hitl-surfaces-bugs-afk-gates-miss]] の通り AFK が見逃した UI/視認系）：
全画面フィールド（skybox 露出）・Hakoniwa タイル視認＋swap 配線・File ドロップダウンのクリップ・単一オーナー拒否ログ。

### 実装ファイル

- 新規 runtime: `Assets/Scripts/Live/{BackcastWorkspaceRoot, ReplayEngineHost(+OnceGate/WorkspaceOwnership),
  MenuBarView, GuiRectUtil}.cs`、`Assets/Scripts/Universe/UniverseSidebarView.cs`
- 新規 Editor: `Assets/Editor/{BackcastWorkspaceSceneBuilder, BackcastWorkspaceProbe}.cs`
- 新規 scene: `Assets/Scenes/BackcastWorkspace.unity`（EditorBuildSettings = これ1つ）
- 変更: `FloatingWindowController`（`Adopt`/`ApplyGeometry` 追加）、`ScenarioStartupHitlHarness`
  （AutoBootstrap=false＋`IsInitialized` ガード）

---

## §12 配線漏れ追補（2026-06-16）: one universe per workspace（sidebar ⇄ scenario ⇄ OnRun）

issue #59 のフォローアップ配線漏れ（owner 報告）。合体 scene 上で sidebar の銘柄 追加/削除 UI は動くが
**編集が実行ユニバースに反映されない**。原因は `BuildWorkspace` が sidebar に `new InstrumentRegistry()`
（孤立した phantom registry）を渡しており、`OnRun()` が読む `_scenario.Universe`／startup タイルが編集する
SoT と**別物**だったこと。#31 の `UniverseSidebarController` は「host が共有 registry を渡す」前提で設計
（`UniverseSidebarController.cs:8-12`）＝ cutover shell（#59）の配線責務であって新規実装ではない。

owner 判断（2026-06-16・「仮状態を排したクリーンな完成形」）で**双方向整合まで**実装：

1. **共有 SoT 配線（本丸）**: `UniverseSidebarController(_scenario.Universe, …)` に差し替え。これで
   sidebar・startup タイル・`OnRun` の三者が単一 `InstrumentRegistry` を共有する。
2. **registry → タイル欄の一方向同期**（owner 補正：sidebar 側は OnGUI 即時モードで元々ライブ／ステイルに
   なるのは held-mode uGUI の startup タイル text 欄だけ）。`InstrumentRegistry` に `public event Action Changed`
   を追加し**実変化時のみ**発火（no-op dup Add/absent Remove/idempotent ReplaceAll は発火しない）。
   `ScenarioStartupTile` が購読し、**フォーカスしていないとき**だけ `SetTextWithoutNotify(join(Ids))` で再同期
   （タイプ中の reformat と自己 ReplaceAll 再入を回避）。`ScenarioStartupTile.Dispose()` で購読解除、root の
   teardown（`StopAndDispose`）が呼ぶ＝orphan ハンドラなし。
3. **SelectedSymbol は据え置き**: 既定 `[startup, chart]` workspace に consumer（depth tile）が無く、共有配線は
   投機的になるため fresh インスタンスのまま（depth 表面 #57 の担当）。
4. **候補ソースは Mock のまま defer**（実供給は #46 kabu list / #41 prune / DuckDB `listed_info`）。

### 検証（RED→GREEN・backcast に FLOWS.md は無く AFK probe が正本ゲート）

- `BackcastWorkspaceProbe.Section8_SharedUniverse`（新）: 実 root を headless 合成（`ResolvePaths`＋`BuildWorkspace`
  を reflection 起動・Python 非起動）し `ReferenceEquals(sidebar.Registry, _scenario.Universe)` ＋ run-side add が
  sidebar rows に映る非空虚性を assert。**RED**（phantom registry）→ **GREEN**。
> #54 改名（findings 0054）: 下記 `ScenarioStartupProbe.Section6/7/8/9` は `ScenarioStartupE2ERunner`（同 Section 番号）へ移送済み。
> 再走は `-executeMethod ScenarioStartupE2ERunner.Run` → `[E2E SCENARIO STARTUP PASS]`。以下は当時の RED→GREEN 記録。
- `ScenarioStartupProbe.Section6_RegistryChangeNotifies`（新）: `Changed` が実変化時のみ発火を assert。
- `ScenarioStartupProbe.Section7_TileResyncsFromSharedUniverse`（新）: 共有 SoT への add がタイル欄に再同期し、
  `Dispose` 後は再同期しない（ハンドラ漏れなし）を assert。**RED**（購読前）→ **GREEN**。
- 回帰 GREEN: `UniverseSidebarProbe` / `ReplayLayoutProbe` / `HakoniwaProbe` / `FloatingWindowProbe` /
  `InfiniteCanvasProbe` / `StrategyEditorProbe` いずれも PASS（`InstrumentRegistry.Changed` は additive）。

### 変更ファイル（§12）

- `InstrumentRegistry`（`Changed` event・実変化時発火）、`ScenarioStartupTile`（購読＋`OnUniverseRegistryChanged`＋
  `Dispose`）、`BackcastWorkspaceRoot`（line 182 共有配線＋teardown で `_tile.Dispose()`）。
- probe: `BackcastWorkspaceProbe`（Section8）、`ScenarioStartupProbe`（Section6/7）。

### 追補2（2026-06-16・code-review high/recall 検出の陳腐化2件）

§12 本丸の共有 SoT 配線後も、**別 writer が SoT を進めたあと旧 writer の差分基準が陳腐化する**経路が2つ残っていた（review high/recall・Medium 2件＋同梱 Low 1件）。どちらも「共有 SoT は正しいが、片側の *キャッシュした基準値* が古いまま」型。

1. **[Medium] cross-writer 陳腐化（writeback 再 prime 漏れ）**: `OnRun()` の `TryStartRun` は sidecar を **CURRENT** universe で Commit する。だが sidebar writeback の `_lastFlushed` を再 prime しないと、その後の sidebar ×/add（唯一 disk へ flush する経路）が「disk に既にある集合」ではなく **Run 前の古い `_lastFlushed`** と diff してしまう。タイルで足した id を Run で commit したケースでは `_lastFlushed` が一段古いまま → 次の sidebar 編集が「差分なし」と判定 → **Flush SKIP → phantom id が disk に残存**（silent disk divergence）。
   **Fix**: `OnRun()` の Commit 成功直後・`_isOwner` gate の前に `_sidebarCtrl.PrimeWritebackFromCurrent();`（`BackcastWorkspaceRoot.cs`）。batchmode は非 owner なので Commit＋再 prime まで到達してから gate で bail する＝probe が re-prime 行の到達を検証可能。
   **RED**: `BackcastWorkspaceProbe.Section9_RunCommitRePrimesWriteback`（実 `OnRun` を reflection 駆動 → 後続 sidebar × が sidecar を `[A]` に戻すか）。未適用時 FAIL=`reprime: sidebar × did NOT flush (writeback stale: _lastFlushed not re-primed at Commit -> phantom B on disk)`。

2. **[Medium] focus-loss 再同期漏れ（held-mode 欄の stale 生存）/ [Low] PullUniverseField 抽出**: §12 の focus-guard はタイプ中の reformat を避けるため `isFocused` のとき欄の再同期を **skip** する。だが skip された欄は SoT が進んでも **stale な phantom id を表示したまま**残り、ユーザが次に 1 文字打つと `OnUniverseChanged → ReplaceAll(stale ids)` が走って **sidebar の add を消す**（focused-skip 経路の取りこぼし）。
   **Fix**: `_universeField.onEndEdit` に再 pull を配線（blur/submit で SoT から欄を引き直す・registry は決して変えない）。再同期ロジックは `PullUniverseField()`（null ガード）に抽出し、`SyncFieldsFromController`・`OnUniverseRegistryChanged`（`!isFocused` ガードは呼び周りに保持）・`onEndEdit` の三者が共有（`ScenarioStartupTile.cs`）。
   **RED**: `ScenarioStartupProbe.Section8_TileBlurResyncsStaleField`（欄を stale 化 → `onEndEdit.Invoke` → 欄が SoT に戻り registry は不変か）。未適用時 FAIL=`blur: onEndEdit did NOT re-pull field from SoT (stale survives -> next keystroke ReplaceAll(stale))`。

3. **[probe 忠実化] Unity 6000.4 `InputField.SetTextWithoutNotify` が onValueChanged を発火する（環境バグ）**: GREEN 実走で Section8 が impl 適用後も RED 文言のまま FAIL。診断ログで原因確定 — legacy `UnityEngine.UI.InputField.SetTextWithoutNotify("A.TSE")` が `OnUniverseChanged("A.TSE")`（onValueChanged）を発火していた（`SetTextWithoutNotify` の名に反する 6000.4.11f1 の挙動）。元の Section8 は `fld.SetTextWithoutNotify(stale)` で欄をステイル化していたが、この発火で `ReplaceAll(stale)` が走り **registry 自体が `[9999,A]` に汚染** → blur 再 pull が汚染 SoT を引いて欄が戻らず（テスト不能）。**実機の focus-skip ステイルは欄を一切書かない**（`OnUniverseRegistryChanged` が focused 中 skip）ので registry は綺麗なまま＝probe の模擬が不忠実だった。**probe 修正**: Section8 のステイル化を backing field `m_Text` の reflection 直接設定に置換（notify も rebuild も ReplaceAll も起こさない）し「欄 stale・registry 綺麗」を忠実に再現。**impl 側は無修正で正しい**（production の `PullUniverseField` は常に registry 自身の ids を書き戻すため、この発火による `ReplaceAll(同一 ids)` は冪等＝`Changed` 不発火・無害。だから `BackcastWorkspaceProbe`（共有 SoT 系）は元から PASS）。

**検証状態（GREEN 確定 2026-06-16）**: 両 RED を batchmode で観測（`error CS`=0・正規の FAIL 文言）→ impl 3 Edit 適用 → 上記 probe 忠実化 → batchmode 全 GREEN。`ScenarioStartupProbe`=`[SCENARIO STARTUP PASS]`、`BackcastWorkspaceProbe`=`[BACKCAST WORKSPACE PASS] all sections green`。回帰 GREEN: `UniverseSidebarProbe`/`ReplayLayoutProbe`/`HakoniwaProbe`/`FloatingWindowProbe`/`InfiniteCanvasProbe`/`StrategyEditorProbe` いずれも `error CS`=0・PASS。なお owner が途中で interactive Editor（Unity Hub 起動）を開き project lock を保持した間は Multiple-instance で batchmode 不可となるため、owner HITL を尊重し Editor を kill せず解放後に実走した。

**変更ファイル（追補2）**: `BackcastWorkspaceRoot.cs`（`OnRun` の Commit 後 re-prime）、`ScenarioStartupTile.cs`（`onEndEdit` 配線＋`PullUniverseField` 抽出）、probe: `BackcastWorkspaceProbe.Section9`（新・Finding 1）／`ScenarioStartupProbe.Section8`（新・Finding 2＋ステイル化を `m_Text` reflection に忠実化）。

---

## 提案：ADR 化

§1 の方針転換（① RuntimeInitialize bootstrap → scene-authored production entry、② `BackcastWorkspace.unity` を
唯一の build entry に新設、③ ScenarioStartup を demote し `ReplayEngineHost` を昇格、④ single Play-owner を
scene root に一本化）は **hard-to-reverse・surprising・real trade-off** の 3 条件を満たすため ADR 候補。
ADR-0005/0003/0001 は **supersede せず参照**し、本 findings を「方針: ADR-000X」で指す（ADR は自己保護条項で固定）。
