# run_result を dock plane から screen-anchored 右上ポップアップへ cutover（content-derived 表示＋manual close latch）

**Status:** accepted (2026-06-27)。本 ADR は **run_result についてだけ** 次の 3 ADR を**点 supersede** する: **ADR-0017 Decision 1**（旧 base tile / chart family は *すべて* floating window＝run_result も floating window）と **ADR-0018**（元箱庭 6 種は奥プレーン `DockLayer` 1.0倍に乗る＝run_result も `DockLayer`）を「run_result は floating/dock 集合から外れ screen-anchored popup へ」で覆い、**ADR-0020**（factory base cluster ＝ `{buying_power, orders, positions, run_result}` の 4 窓）を base 集合 **4→3** に縮める。加えて **#138 / findings 0110 §7「DriveRunResult＝run_result tile を LiveManual で hide する back-plane 特例」を subsume**（method は退役、その anti-stale-LiveManual *契約* は本 ADR D3 の content gate `DisplayMode==LiveAuto` に畳まれる）。これら被 supersede ADR はいずれも自己保護条項を持つため **3 ADR は無改変**で本 ADR を起こす（差分は本 ADR が持つ）。ADR-0024（puzzle-feel drag・Hakoniwa core special 退役で `IsCoreKind` は既に dead）・ADR-0029（grab/eject/pickup）は別 decision の固定 oracle として参照のみ・無改変。実装 issue: **#172**（ポップアップ cutover・content-derived 表示）＋ **#173**（× close ＋ dismiss latch）。

## Context

run_result（戦略 run の成績サマリ）は現在、奥プレーン `DockLayer`（1.0倍パララックス）の**常設 base dock panel**として、buying_power / orders / positions と並ぶ 4 番目の base singleton で存在する（`BackcastWorkspaceRoot.BaseDockWindowIds` 4 要素・`SpawnBaseDockWindows`・`FloatingWindowCatalog.KIND_RUN_RESULT`）。dock panel なので canvas を pan するとパララックス層と一緒に動き、ガターを予約し、レイアウト sidecar（`floatingWindows`）に geometry が persist される。

run_result は他 3 base panel（buying_power / orders / positions）とは性格が違う:

- 他 3 つは**常時意味を持つ**（口座・注文・建玉は mode を問わず継続的に存在する継続状態）。
- run_result は**戦略 run のサマリ**で、run が無いとき（LiveManual・Replay 未開始）は構造的に「(no run)」しか出ない。**run がある瞬間にだけ意味を持つ ephemeral な通知**である。

owner はこの性格差を UI に反映させたい（2026-06-27）: run_result を「常設 dock の住人」から、**run データがあるときだけ右上に浮く screen-anchored ポップアップ card** へ格上げする。ポップアップは `ScreenSpaceOverlay` Canvas 直下（infinite canvas の `Content` の子では **ない**）に置くので、**canvas を pan しても screen 座標が動かない＝3D 空間（パララックス層）から除外**される。canvas に重ねて浮き（ガター予約なし）、固定サイズ、drag/resize 不可。

設計の木と codebase 裏取り（F1–F9）は findings 0125。技術的障壁は無く（既存の screen-anchored overlay パターン＝`BuildAddCellButton` が `ScreenSpaceOverlay` Canvas を `transform` 直下に作る正本・findings 0125 F6）、論点は **(a) content-derived 表示の hasContent 述語を mode-correct に定義すること（#138 の sticky-flag 罠を踏まないこと）** と **(b) dock plane からの純減を漏れなく行うこと（catalog / IsDockKind / base group / persist）** の 2 点に集約される。

## Decision

1. **run_result を screen-anchored 右上ポップアップへ cutover する（#172）。** `ScreenSpaceOverlay` Canvas 直下に固定サイズ・右上アンカーの card を置く。`Content`（パララックス層）の子ではないので pan で screen 座標が動かない。drag/resize 不可（title ＋ #173 の × close のみ）。表示文字列は既存 format 関数を無改変で再利用し、ターゲットを dock tile の Text からポップアップ card 内の Text へ付け替えるだけ。

2. **表示は content-derived（#172 このスライス）。** run データがあるとき出現し、honest-empty になったら消える。`hasContent` の真偽で card root を `SetActive` する。手動 close の latch は #173（D7–D8）。
   - **Replay**: `PushReplayTiles` の `string.IsNullOrWhiteSpace(portfolioJson)` 枝が honest-empty。非空なら running view（`FormatReplayRunResultRunning`）or 完了 full stats（`FormatReplayRunResultComplete`）。
   - **LiveAuto**: telemetry/lifecycle（`HasLifecycle || HasTelemetry`）。
   - **LiveManual**: 構造的に出ない（D3 参照）。

3. **LiveManual では出ない — ただし「自然に出ない」のではなく content gate で明示的に hide する（#138 契約の畳み込み）。** `LivePanelViewModel`（`_host.Panel`）は **process 内で 1 度だけ生成され、`HasLifecycle`/`HasTelemetry` フラグは決して reset されない sticky** である（`WorkspaceEngineHost.cs:60`・findings 0125 F4）。よって LiveAuto run の後で LiveManual へ flip すると、**sticky フラグが立ったまま**になり、`hasContent = HasLifecycle || HasTelemetry` だけでは **stale な LiveAuto telemetry がポップアップに漏れる**（これはまさに #138 が `DriveRunResult` で hide した理由＝findings 0110 §7「would otherwise show STALE LiveAuto telemetry」）。

   したがって **live の hasContent 述語に mode ゲートを畳む**:
   ```
   hasContent_live = (HasLifecycle || HasTelemetry) && DisplayMode == LiveAuto
   ```
   `DriveRunResult`（method）は退役するが、その **hide-in-LiveManual *契約*は content gate に移送されて保持**される。「ポップアップは LiveManual で出ない」は AC が要求する不変条件であり、sticky フラグの罠を踏まないために **mode 明示ゲートが必須**である（issue #172 本文の「自然に出ない」は不正確な圧縮で、実体は本 D3＝findings 0125 F4/F7 の訂正）。LiveManual で run_result を hide していた `DriveRunResult` の SetActive 経路は不要になる（ポップアップ root の可視は hasContent gate が一元管理）。

4. **dock plane から run_result を完全退役（純減・スキーマ追加 0）。**
   - `FloatingWindowCatalog.KIND_RUN_RESULT`（catalog spec / Default 生成）を削除。
   - `DockShape.IsDockKind` の run_result 分岐を削除 → dock kind = chart ＋ 3 base singleton（buying_power / orders / positions）。
   - `DockShape.IsCoreKind` → **空集合**（ADR-0024 以降 production 挙動経路から未参照＝dead-code simplify・findings 0125 F2）。
   - `BaseDockWindowIds` 4→3 ＋ `SpawnBaseDockWindows` の run_result spawn を削除。
   - `DriveRunResult`（#138 LiveManual-hide 特例・呼び出し含む）退役（D3 で契約移送済み）。
   - `CaptureLayout` / `RestoreFloating`: run_result を書かない・読まない（`floatingWindows` 次元から外れる）。既存保存の run_result geometry は **migrate せず無視**（読み込み時に未知 id として fall-through）。

5. **base factory group は 3 窓 `{buying_power, orders, positions}` で成立。** `FormGroup` は live メンバが **≥2** で group を mint する（`FloatingWindowController.cs`・findings 0125 F5）。3 窓は閾値を満たすので、run_result を外しても base group は成立する（共有 groupId を stamp）。

6. **content drive の format 関数は無改変で再利用。** `FormatReplayRunResultRunning` / `FormatReplayRunResultComplete` / `FormatRunResult` と poll 経路（`PushReplayTiles` / `PushLiveTiles`）はそのまま。`LivePanelTileView`（Text への dumb sink・`ShowText`/`ShowReplayEmpty`/`Refresh` API）も再利用し、Build 先の body をポップアップ card に付け替える。Replay/LiveAuto の表示値は従来どおり。

7. **× close ＋ dismiss latch（#173）。** ポップアップに × close ボタンを足す。可視述語を拡張:
   ```
   visible = hasContent && !_runResultDismissed
   ```
   × で `_runResultDismissed = true`。**同一 run 中**は running→complete 遷移でも、一度 × したら再出現しない。latch は **session 内のみ**（永続化しない・毎 run 再 arm）。

8. **dismiss latch の再 arm は「新しい run の rising edge」で、Replay と LiveAuto を対称に保つ（#173）。**
   - **Replay**: run 間で portfolio が honest-empty に落ち（`PushReplayTiles` の `IsNullOrWhiteSpace(portfolioJson)` 枝）、次 run で再投入される。`hasContent` の **falling→rising edge** を検出して latch をリセット。
   - **LiveAuto**: telemetry/lifecycle フラグは **sticky**（D3）。boolean では 2 回目の LiveAuto run で falling edge が出ず再 arm しない。よって **`run_id` の変化**（`LatestLifecycle.RunId` / telemetry の `run_id`・findings 0125 F8）を rising edge として使う。

   ★ **Replay と LiveAuto で「再 arm が起きる条件」を対称に保つこと。** 片側だけ実装すると「LiveAuto で一度閉じると二度と出ない」死角になる（#164 M1 re-arm 対称化の教訓と同型）。実装は「run 識別子（runIdentity）の変化で latch を再 arm」へ統一し、runIdentity を Replay = hasContent falling→rising の単調カウンタ / LiveAuto = run_id 文字列、で供給する。

## Considered Options

- **採用：screen-anchored ポップアップ＋content-derived（D1–D2）。** owner の明示要求。run_result の ephemeral な性格（run がある瞬間だけ意味を持つ）を UI altitude に正しく反映し、常設 dock のガター予約と persist を不要化する。トレードオフ：dock plane の「全 base panel が常設の住人」という一様性を崩す——run_result だけ別 plane（overlay）になる。これは性格差を反映した**意図的な非対称**で、findings 0125 D3 が anti-stale 契約まで含めて固定する。
- **不採用：dock panel のまま「run があるとき visible」を toggle する。** persist 次元に残り続け（saved layout に run_result geometry が残る）、pan で動き、ガターを予約する。owner が「3D 空間から除外」を明示要求したため却下。
- **採用：live hasContent に mode ゲートを畳む（D3）。** sticky フラグの罠（#138）を content-derive モデル内で正しく扱う唯一の方法。`DriveRunResult` method は退役するが契約は保持。
- **不採用：content だけで判断（mode ゲート無し）。** issue 本文の額面。LiveAuto run 後に LiveManual へ flip すると stale telemetry が漏れ、AC「LiveManual で出ない」を破る（findings 0125 F4 で実証）。却下。
- **不採用：`LivePanelViewModel` の sticky フラグを mode flip で reset する。** 他の consumer（footer の `LiveAutoTransportViewModel` が `LatestLifecycle.RunId`/`Status` を読む・findings 0125 F8）に副作用が及び、#138 が absolute-toggle を選んだ理由（self-heal）を崩す。VM はそのまま・本 ADR は popup 側の述語だけで解決する。
- **採用：dismiss latch の対称再 arm（runIdentity・D8）。** Replay の hasContent edge と LiveAuto の run_id を 1 つの runIdentity 概念に統一し、両 mode で「次の run で再出現」を保証する。
- **不採用：boolean falling edge だけで再 arm。** LiveAuto の sticky フラグでは 2 回目以降の run で falling edge が出ず「一度閉じると二度と出ない」死角（#164 と同型）。却下。

## Consequences

- **#138 / findings 0110 §7 の `DriveRunResult` 決定が supersede される。** 本 ADR が正本。`DriveRunResult` method と呼び出し（`Update`→`DriveRunResult()`）を削除し、その anti-stale-LiveManual 契約は D3 の content gate（`DisplayMode == LiveAuto`）が引き継ぐ。findings 0110 §7 には dangling とならないよう「supersede: ADR-0037（契約は popup の hasContent gate へ移送）」の stale-marker を該当 slice 側に足す（ADR-0036 等の ADR ファイルは無編集の方針に倣い、findings は slice 記録なので marker 追記可）。
- **既存 probe / golden / test の更新が要る**（regression net）: run_result を **dock base singleton として pin** する assertion（`BaseDockWindowIds` 4 要素・base group 4 メンバ・`KIND_RUN_RESULT` catalog spec・`DriveRunResult` の LiveManual-hide・run_result geometry の persist/restore・`IsDockKind(run_result)==true`・`IsCoreKind(run_result)==true`）。これらを 3-base-window ＋ popup-content-derive ＋ no-persist へ更新する。新規 AFK 正本（run-result popup probe・latch probe）を新設し scenario rollup に載せる。
- **persist スキーマは純減（追加 0）。** `floatingWindows` 次元から run_result が外れる。既存 saved layout の run_result entry は `RestoreFloating` で未知 id として fall-through（`ctrl.Has`/spawn 経路に乗らない）し、無害に無視される。migration コードは書かない。
- **base group は 3 メンバで成立**（`FormGroup` ≥2 閾値を満たす）。group drag / magnetic snap（ADR-0024）は 3 窓で従来どおり。
- **format 関数・poll 経路・`LivePanelTileView` は無改変で再利用**され、Replay（running/complete）/ LiveAuto の表示値は従来どおり。
- **ポップアップの位置/可視は session 内のみ**（D6/D7）。boot で「閉じた」状態も geometry も復元しない。
- **実 pan の奥行き目視**（popup が canvas と一緒に動かない）と LiveAuto 2 連 run の latch 再 arm は owner HITL で確認（headless では pan の視覚確認が要るため）。
- 下位の実装事実（card chrome の構築 seam・sortingOrder・runIdentity の供給配線・probe 更新の正確な section・退役する exact symbol/行）は本 ADR に書き戻さず **findings 0125** に記録し、本 ADR を「方針: ADR-0037」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。点 supersede した ADR-0017 D1 / ADR-0018 / ADR-0020 と subsume した #138 / findings 0110 §7 の旧決定（`DriveRunResult` の SetActive hide）は**無改変**で履歴として残し、本 ADR が差分を持つ（run_result の floating/dock 集合除外・base 集合 4→3・LiveManual SetActive hide → content gate）。被 supersede ADR の自己保護条項（「覆すならこのファイルを編集せず新 ADR」）に従い、それらは編集しない。ADR-0024 / ADR-0029 は別 decision の固定 oracle として踏襲し編集しない。下位の実装事実は findings 0125 に記録し、本 ADR を参照する。
