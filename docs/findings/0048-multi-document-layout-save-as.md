# findings 0048 — #69 menu bar: File→Save As + native file picker + 任意パス Open（multi-document layout surface）

issue #69（#42 slice 2 follow-up (b) / findings 0017 §9(a) / findings 0027 follow-up (b) から分離）。
方針: **ADR-0005（1:1 表面 parity・固定）/ ADR-0003（layout 永続化・capability parity）/ ADR-0001**。
oracle = TTWR `src/ui/layout_persistence/dialogs.rs`（`rfd::AsyncFileDialog` / `finish_layout_save`）
＋ `src/ui/scenario_sidecar/write.rs`（`atomic_mutate_scenario_object`）＋ `src/ui/strategy_persistence.rs`。
`grill-with-docs`（2026-06-18）で導出。backcast に FLOWS.md は無いため本 findings が RED→実装→GREEN→HITL の正本。
ADR は自己保護条項を持つため本 findings に実装事実を記録し、ADR は参照のみ（書き戻さない）。

## 0. 正しい文書モデル（owner 訂正 2026-06-18・CONTEXT.md L380・コード裏取りで確定）

**このアプリが扱うファイルは2つだけ**: `<strategy>.py` と `<strategy>.json`。**layout は `<strategy>.json` の中（`layout` キー）に書く**（TTWR 参照）。

| ファイル | 中身 | 所有 |
|---|---|---|
| `<strategy>.py` | 戦略コード | #16 / #78（canonical absolute .py path＝文書 identity・TTWR `buffer.original_path` 等価） |
| `<strategy>.json` | **`scenario` キー（engine 所有・v3・#29）と `layout` キー（Unity 所有・#12/#69）が共存** | sidecar（`foo.py`→`foo.json`・`Path.ChangeExtension`） |

→ **文書 = `<strategy>.json` ＋ `<strategy>.py` のペア**（TTWR document model そのもの）。`<strategy>.json` の `scenario`/`layout`
は**別キー・別所有**で1ファイルに共存（`load_scenario` は layout-only sidecar を許容＝CONTEXT.md L380）。

### 現状 vs #69 で埋めるギャップ（コード裏取り）
- **scenario キー**: 既に `ScenarioSidecarStore`（#29）が `<strategy>.json` へ **Newtonsoft JObject で merge-write**（`scenario` を書き `layout` 等の兄弟を verbatim 保全・TTWR `atomic_mutate_scenario_object` の C# port・`File.Replace` atomic）。
- **layout キー**: **未統合**。現状 layout は global root-level `persistentDataPath/layout.json`（`LayoutStore`＝JsonUtility・root 直書き、`LayoutPathResolver.DefaultPath`）。`ScenarioSidecarStore.cs:18-21` が「layout は JsonUtility のまま・LayoutStore untouched」と明記。
- → **#69 = `ScenarioSidecarStore` の鏡像 `LayoutSidecarStore` を作り、`<strategy>.json` の `layout` キーへ merge-write（`scenario` を保全）。Save As / Open（native picker）を文書ペア上に載せる。**

## 1. 確定事項（grill 2026-06-18）

### (D1) `LayoutSidecarStore`（`ScenarioSidecarStore` の鏡像・Newtonsoft merge-write）
- `<strategy>.json` の **`layout` キー**へ `LayoutDocument` 相当を書く。`scenario` キー・他の兄弟を **verbatim 保全**（Newtonsoft `JObject`・`File.Replace` atomic）。read は `layout` キーを `LayoutDocument` へ。
- Newtonsoft は **このストア内に封じ込め**（ADR-0005 decision 2・`ScenarioSidecarStore` と同規律）。caller は `WriteLayout(strategyPath, doc)` / `ReadLayout(strategyPath)` のみ見る。
- **clobber 安全が原理的に解決**: scenario sidecar へ書いても `scenario` キーを潰さない（逆も真）。私が前案で恐れた「Save As が scenario を上書き」事故は、共存 merge では起こらない。

### (D2) native file picker = Win32 P/Invoke 自前（comdlg32・owner 確定）
TTWR native rfd の parity を third-party 依存ゼロで。根拠（owner・コード裏取り）:
- **backcast は実質 Windows desktop 専用**: pythonnet で CPython 同一プロセス埋め込み（`Python.Runtime.dll`・Android/iOS 不成立）＋ JP 証券 API（tachibana/kabu）＋ `platform=win32`。ProjectSettings の iPhone/Android/tvOS は Unity 既定枠でターゲット根拠ではない。
- **落とした候補**: EditorUtility＝standalone player から `UnityEditor` 除去で runtime 不在＝失格。SimpleFileBrowser＝uGUI 自前描画で native parity を崩し #77 z-order chrome と衝突＝非推奨。UnityStandaloneFileBrowser＝中身は Win32 薄ラッパで未保守 vendored binary を抱える分だけ不利。
- **実装**: `comdlg32.dll` `GetOpenFileNameW`/`GetSaveFileNameW` を `OPENFILENAME` marshalling で叩く。**modal・main thread**（dialog が自前メッセージループ）。blocking 自体が TTWR single-modal `PendingFileDialog` 規律（同時1つ・相互排他）を満たす（async polling state machine 不要）。Plugins 同梱前例＝pythonnet。
- **trade-off（受容）**: modal 中 Unity main thread 一時停止（Python engine は別 thread 継続・dialog close 後の poll で catch up）。

### (D3) picker は `IFileDialog` seam 背後（swap-point 規律・AFK 注入 seam）
`LayoutStore`＝`JsonUtility`／`ScenarioSidecarStore`＝Newtonsoft を swap-point 内に隠す既存規律と同型。TTWR `PendingFileDialog.inject_resolved` parity。
- interface: `string OpenLayout()` / `string SaveLayoutAs()`（選択パス or null＝cancel）。production `Win32FileDialog`（comdlg32）／AFK `StubFileDialog`（返値注入・本物の dialog を呼ばない）。
- root が `IFileDialog` 保持（既定 Win32・AFK probe は stub 注入）。**modal blocking なのでメソッド戻り値で path を得る**（state machine 不要）。

### (D4) Open 失敗（破損/不読/空）= abort + notice・現 workspace 保全（owner 確定・oracle 裏取り）
TTWR `restore.rs:303-310` `apply_layout_system` は `LayoutStore::restore()→Result` を受け `Err` で `continue`（現 workspace 不触）。TTWR の Open 経路に `Default()` フォールバックは無い。`Default()` 退避は **boot 専用**の backcast 固有挙動。

| 経路 | 起点 | 失敗時 | 結果 |
|---|---|---|---|
| **boot** | 空 workspace | fail-soft→`Default()` | 既定で起動＝破壊なし |
| **Open** | 編集中 workspace | **strict→abort+notice** | 現 workspace 保全（TTWR `Err→continue`） |

- `LayoutSidecarStore.TryReadLayout(strategyPath, out doc)`（bool）で成否を区別（破損 JSON / `layout` キー無し / 空 → 失敗）。
- 失敗時: `ApplyLayout` を呼ばず `ShowMessage("Open: '<name>' は無効な layout")`・`original_path` と現 workspace を据え置く。
- 部分ファイル（valid だが scenario-only 等）の優雅な扱いは valid 入力の別レイヤ＝#69 範囲外。

### (D5) 下位 UX（findings に固定）
- **picker filter** = 戦略ファイル（`*.py` か `*.json` は §2-A の決着に従う）。**上書き確認** = `OFN_OVERWRITEPROMPT`。拡張子無し選択時は補完。
- **Open-while-Live → LiveAuto** 副作用は既存（`FileOpenModeSideEffect`・findings 0017 §1）を踏襲。load の前に送出。
- **文書名のバー表示**（TTWR `menu_bar.rs:703` title label・cache marker）= 任意 parity・AC 非要件 → follow-up。

## 2. 確定事項（続き・owner 確定 2026-06-18）

### (D6) Save As = **文書ペアごと fork**（`.py` も新パスへ書く・owner 確定 A=Yes）
Save As→`<newname>` は **`<newname>.py`（戦略コード）＋`<newname>.json`（`scenario`＋`layout` キー）の両方**を書き、editor を新 `.py` に rebind。
TTWR `finish_layout_save`（`.py` を `merge_fragments` で co-write）parity。dangling 参照を避ける唯一の整合形。
- **`StrategyDocument.SaveAs(newPath)`（新規・rebind）** が要る（現 `Save()` は bound `_path` のみ）＝#16 領域。
- Save As orchestration（root）: `editor.SaveAs("<newname>.py")` → `ScenarioSidecarStore.SetStartupParamsAndInstruments("<newname>.py", …)`（scenario キー）→ `LayoutSidecarStore.WriteLayout("<newname>.py", doc)`（layout キー）→ `_currentLayoutPath = "<newname>.py"`。
- **#78/#79 整合**: 明示 Save As＝ユーザー意図の新コピー（cache の暗黙 fork ではない）。run cwd（#79）は `<newname>` dir。

### (D7) global `layout.json` 廃止・2ファイル統一（owner 確定 B=B2）
layout は `<strategy>.json` の `layout` キーにのみ存在。global `persistentDataPath/layout.json`（`LayoutStore` root 直書き・`LayoutPathResolver.DefaultPath`）を **production boot/quit から撤去**。
- **`_currentLayoutPath`（= open 文書の `.py`・session 状態・TTWR `buffer.original_path` 等価）**: in-memory。
- **untitled（文書未 open）**: `File→Save` は **Save As に委譲**（TTWR `dialogs.rs:272-275`）。untitled の layout は永続されない（B2 の帰結＝永続は文書を要する）。
- **`File→Save`（文書 open 中）**: `LayoutSidecarStore.WriteLayout(_currentLayoutPath, doc)`（`<strategy>.json` の `layout` キー更新・`scenario` 保全）。
- **quit autosave**: 文書 open 中なら同 `WriteLayout`（TTWR autosave-to-`original_path` parity）。untitled は no-op。
- **resume**: 最後に open した `.py` パスを **PlayerPrefs**（"file" ではないアプリ内部 state）へ保存。boot で読み、存在すれば Open（その `<strategy>.json` の `layout` キーから復元）／無効なら untitled+default workspace。i12 resume baseline を global file 無しで満たす。
- **`LayoutStore` の再利用**: `LoadFromJson`/`Sanitize`（panels/windows/canvasView/hakoniwaProfiles 正規化）は parser 非依存で価値が高い → `LayoutSidecarStore` が `layout` サブオブジェクトの JSON 文字列を `LayoutStore.LoadFromJson` に渡して再利用（write は `JsonUtility.ToJson(doc)`→`JObject.Parse`→`root["layout"]`）。`LayoutStore`/`LayoutPathResolver` の harness/probe 利用（temp パス）は不変。

> ⚠️ **回帰注意**: 現 boot `Awake:213 RestoreLayout` / quit `StopAndDispose:1649 SaveLayout` は global layout.json 駆動。B2 で両者を文書駆動へ rewire する。`ReplayLayoutProbe` 等の `LayoutStore` temp パス利用は test-scoped で不変。

## 3. 実装（2026-06-18・本スライス成果）
- **`LayoutSidecarStore.cs`（新規）**: `ScenarioSidecarStore` の鏡像。`<strategy>.json` の `layout` キーを Newtonsoft JObject で merge-write（`scenario` 兄弟を verbatim 保全・`File.Replace` atomic）。`WriteLayout` / `TryReadLayout`（strict・Open abort 用）。LayoutDocument 形は `JsonUtility.ToJson`／`LayoutStore.LoadFromJson` を bridge して再利用（parser fork 無し）。
- **`FileDialog.cs`（新規）**: `IFileDialog`（`SaveStrategyAs`/`OpenStrategy`→選択 .py or null）＋ `StubFileDialog`（AFK 注入）。
- **`Win32FileDialog.cs`（新規）**: comdlg32 `GetOpenFileNameW`/`GetSaveFileNameW`（OPENFILENAME marshalling・file buffer は HGlobal＋`PtrToStringUni`）。**`OFN_NOCHANGEDIR` 必須**（process cwd を動かさない＝#79 と engine 相対パス保護）。非 Windows は graceful null。
- **`StrategyDocument.SaveAs(newPath)` / `StrategyEditorView.SaveAs`（新規・#16）**: 新 .py へ atomic write して rebind（失敗時は文書不変）。
- **`BackcastWorkspaceRoot` rewire**: `_currentLayoutPath`（原文書 .py・TTWR `original_path`）/ `_fileDialog`＋`SetFileDialog`（AFK seam）/ `OnFileOpen`（picker→`TryReadLayout`→abort-on-invalid→`EnsureAdoptedEditorState`→`ApplyLayout`＋mode 副作用）/ `OnFileSave`（untitled→Save As 委譲・open→`WriteLayout`）/ `OnFileSaveAs`（ペア fork: `editor.SaveAs`→`_scenario.Commit`→`WriteLayout`）/ `OnFileNew`（+`_currentLayoutPath=""`）/ `ResumeLastDocumentOrDefault`（boot・PlayerPrefs ポインタ）/ `AutosaveCurrentDocument`（quit）。**global `layout.json` 撤去**（`SaveLayout`/`RestoreLayout`/`LayoutPathResolver.DefaultPath` を production から除去）。
- **`MenuBarView`**: `Save As…` の disabled 解除＋`onSaveAs` 配線、`Open…` を picker 起動に。`Bind` に `onSaveAs` 追加（呼び出しは root のみ・他は VM 利用で不変）。

## 4. 検証
- **AFK probe（実装済・`Assets/Editor/MultiDocLayoutProbe.cs`）**: S1 layout-key round-trip / S2 scenario⇄layout 共存（両方向で相手キー保全＝clobber 無し）/ S3 `TryReadLayout` strictness（missing/malformed/no-key→false・valid→true）/ S4 `StrategyDocument.SaveAs`。Python-free・決定的。`[MenuItem("Probes/Run MultiDoc Layout Probe")]`。
- **回帰**: `BackcastWorkspaceProbe`（`ResolvePaths`/`BuildWorkspace`/`ApplyLayout`/`CaptureLayout`＋`LayoutStore` temp round-trip・いずれも未変更）/ `MenuBarVerify`（pure VM・未変更）→ 影響なし想定。
- **batchmode gate GREEN（2026-06-18・Unity 6000.4.11f1 実機 batchmode・残存 Unity プロセス終了後に実走）**:
  - `MultiDocLayoutProbe.Run` → **`[MULTIDOC LAYOUT PASS]`**（S1〜S4 全green）・`error CS` 0・`Exiting batchmode successfully`。
  - 回帰 `BackcastWorkspaceProbe.Run` → **`[BACKCAST WORKSPACE PASS] all sections green.`**（root rewire が layout restore/CaptureLayout/ApplyLayout を壊さない）・`error CS` 0。
  - 回帰 `MenuBarVerify.Run` → **`16 pass / 0 fail — ALL PASS`**（menu VM logic 不変）・`error CS` 0。
- **owner HITL PASS（2026-06-18・実機 Play・全 Phase A〜F green）**:
  - **A** Save As→native picker で任意パス（実機: `D:\Downloads\aaaaaastrategy.py`）に `.py`＋`.json` 生成・editor が新 .py に rebind。検査で `aaaaaastrategy.json` の `layout` キー round-trip 確認（version 1 / floatingWindows 2 / strategyEditors filePath = .py）。scenario 空だったため `Commit` 無書き＝**layout-only sidecar**（設計どおり・CONTEXT.md L380）。
  - **B** Save→開いている文書の `<strategy>.json` `layout` キー更新（`scenario` 保全）。
  - **C** New→untitled 化後、Open で文書（layout/strategy）復元。
  - **D** 破損 JSON / 空 `layout` キー（`{"layout":{}}`）を Open→**`無効な layout` notice＋現 workspace 保全**（strict `TryLoadFromJson`・Default-wipe しない）。
  - **E** 未保存変更→Play 停止（quit-autosave が文書へ書く）→再 Play で **last document resume**（PlayerPrefs `backcast.lastDocument`）＋未保存変更も復元。
  - **F** MOCK 接続→File→New（LiveManual 副作用）→File→Open while Live→**`mode: LiveAuto`** 遷移（findings 0017 §1 既存挙動を #69 が保持）。
- **session 中の owner refinement（本 commit に同梱）**: `AtomicFile`（共有 atomic-write helper・`ScenarioSidecarStore`/`LayoutSidecarStore` 双方が利用＝/simplify で skip した cross-store 抽出を owner が実施）／`LayoutStore.TryLoadFromJson`（present-but-degenerate layout キー＝empty/version≤0 を strict に false＝Open abort・Default-wipe 回避）＋ probe S3e/S3f／**dead `LayoutPathResolver.cs` 削除**（B2 で production 未使用）。

## 5. follow-up / 既知の制限（silent drop にしない）
- **Open は additive**（現 `ApplyLayout` 仕様＝既存 windows を破棄せず重畳）。別文書 Open 後に前文書の追加 window が残り得る。clear-then-apply 化は adopt 不変条件（findings 0025 §8）に注意した別 refinement。
- **既存ユーザーの global `layout.json` は orphan 化**（B2 移行コスト・一度だけ layout が default に戻る。ファイルは読まれず放置）。
- **文書名のバー表示**（TTWR `menu_bar.rs:703`）= 任意 parity・未実装。

## 6. code-review(simplify) 修正（2026-06-18・high-effort・3独立エージェント検証）
- **[Medium・correctness] `TryReadLayout` の strictness 完成**: present-but-invalid な `layout` キー（`{}` / `version<=0`）が fail-soft `LoadFromJson`→`Default()` に化けて `OnFileOpen` が現 workspace を**ワイプ**していた（D4 が防ぐはずの破壊）。→ `LayoutStore.TryLoadFromJson(json, out doc)`（strict・空/malformed/version≤0 で false）を新設し `TryReadLayout` をそれ経由に。`LoadFromJson`（boot 用 fail-soft）は `TryLoadFromJson` へ delegate（DRY）。probe S3 に present-but-invalid→false ケース（S3e）追加。
- **[simplify] atomic write の3重化を解消**: `AtomicFile.WriteAllText`（単一実装）を新設し `LayoutSidecarStore` / `ScenarioSidecarStore` を集約（両者 byte 同一だった）。`StrategyDocument.WriteAtomic` は別意味論（Guid temp・Utf8NoBom・bool）のため独立維持。
- **[simplify/dead] `LayoutPathResolver.cs` 削除**（caller ゼロを確認）。`LayoutStore` 本体は残す（`LoadFromJson`/`TryLoadFromJson` が `TryReadLayout` 経由で production-live・`Save`/`Load` は harness/probe scoped）。
- **[simplify/speculative] `LayoutSidecarStore.WriteLayout` を `void` 化**（誰も使わない `WritebackOutcome` 戻り値＋毎 save の `GetLastWriteTimeUtc` stat を除去。layout に watcher は無い）。
- 残（Low・未着手）: Save As 部分失敗時の文書状態乖離 / Open・Resume の 3 行 apply 列の helper 抽出 / `ParseFile` の2 store 重複。
