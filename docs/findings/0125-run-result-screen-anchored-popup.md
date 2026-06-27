# findings 0125 — run_result を dock panel から退役し screen-anchored 右上ポップアップへ

方針: [[ADR-0037]]（run_result は dock panel 退役・run 結果は screen-anchored 右上ポップアップ）。
本 findings は ADR-0037 の下位設計の木・除去シンボル・latch 機構・AFK 正本（RED→GREEN）を固定する。

grill: `grill-with-docs`（2026-06-27・owner HITL）。owner 依頼「run_result パネルを廃止。表示することがある期間だけ
右側にポップアップ表示（3D空間から除外）」。

## 設計の木（owner-locked）

- **Q1 表示の寿命** = run データが出たら出現 → **× で手動 close** → **次の run まで再出現しない**（latch）。
  （自動消滅トースト・自動常駐は不採用）
- **Q2 対象 run** = **Replay ＋ LiveAuto の両方**。content の有無だけで決まり mode 分岐なし。LiveManual は telemetry
  が無く構造的に出ない。
- **Q3 位置/占有** = **右上に浮く card**。canvas に **重ねる**（ガター予約なし・レイアウトを押しのけない）。
  `ScreenSpaceOverlay` Canvas 直下（`Content` の子では **ない**）＝**パンしても動かない＝「3D空間から除外」**。

## 下位事実

### F1 — content-derived 表示 ＋ manual-close latch ＋ 新-run rising-edge 再 arm

- `hasContent` = Replay は `LatestPortfolioJson` が非空 / LiveAuto は telemetry（`HasLifecycle` ∨ `HasTelemetry`）。
- 可視 = `hasContent ∧ !_runResultDismissed`。× で `_runResultDismissed = true`。
- **再 arm（latch リセット）= 新しい run の rising edge**:
  - Replay: run 間で portfolio が honest-empty に落ち（`PushReplayTiles` の `string.IsNullOrWhiteSpace(portfolioJson)`
    枝）、次 run で再投入される。`hasContent` の **falling→rising edge** を検出して `_runResultDismissed = false`。
  - LiveAuto: telemetry flag は **sticky**（`LivePanelViewModel.Apply` は flag を reset しない＝findings 0110 §7 で既述
    の性質）。boolean では 2 回目の LiveAuto run で falling edge が出ず再 arm しない。よって **`run_id` の変化**を rising
    edge として使う（lifecycle の run_id を保持し、変わったら再 arm）。
  - ★ 注意（#164 M1 re-arm 対称化の教訓と同型）: Replay と LiveAuto で「再 arm が起きる条件」を **対称に**保つ。
    片側だけ実装すると「LiveAuto で一度閉じると二度と出ない」死角になる。AFK gate で両経路の再 arm を pin する。

### F2 — content path は再利用（ターゲット付け替えのみ）

- 既存: `PushReplayTiles` → `_runResultView.ShowText(running ? FormatReplayRunResultRunning : FormatReplayRunResultComplete)`、
  `PushLiveTiles`/`PushBaseTiles` → `_runResultView.Refresh(p)`（`FormatRunResult`）。
- 変更: `_runResultView`（dock tile 上の `LivePanelTileView`）を **ポップアップ body の view** へ差し替えるだけ。
  format 関数（`FormatReplayRunResultRunning/Complete`・`FormatRunResult`）は **無改変で再利用**。

### F3 — 除去スコープ（純減）

- `FloatingWindowCatalog.KIND_RUN_RESULT`（catalog spec / Default 生成）退役。
- `DockShape.IsDockKind`: run_result 分岐削除 → dock kind = chart + {buying_power, orders, positions}。
- `DockShape.IsCoreKind`: **空集合**へ（全 kind に false）。ADR-0024 以降 production 挙動経路から未参照（定義 1 + comment 1
  だけ＝dead）。grep 確認済み。
- `BackcastWorkspaceRoot.BaseDockWindowIds`: 4→3（run_result 除去）。`SpawnBaseDockWindows` の run_result spawn 除去。
- `BackcastWorkspaceRoot.DriveRunResult`（#138 LiveManual-hide 特例・findings 0110 §7）退役＝ポップアップは LiveManual で
  自然に出ないので subsume。
- `CaptureLayout` / `RestoreFloating`: run_result を書かない・読まない。`floatingWindows` 次元から外れる。既存保存の
  run_result geometry は **migrate せず無視**（スキーマ追加 0・ADR-0003 D4 と同精神）。

### F4 — base factory group は 3 窓で成立（ADR-0020 縮小）

- `FormGroup({buying_power, orders, positions})` は ≥2 閾値（`DissolveIfShrunkTo(2)`）で成立。run_result を外しても group は
  壊れない。「promoting core」概念は ADR-0024 で退役済み（`IsCoreKind` が dead だったため挙動影響なし）。

## AFK 正本（実装着手前に `behavior-to-e2e` を formal invoke）

run-result ポップアップ probe を新設（pure な latch ロジックは headless probe、screen-anchored / pan は AFK runner）。pin する
不変条件:

1. content があると出現・無いと出ない（Replay running/complete・LiveAuto telemetry の各 content で可視）。
2. × で閉じると `_runResultDismissed` が立ち、**同一 run 中**は running→complete 遷移でも再出現しない。
3. **新しい run の rising edge で再 arm**——(a) Replay portfolio 再投入、(b) LiveAuto 新 run_id、の **両経路**で閉じた後に
   再出現すること（片側欠落＝RED）。
4. 両モード（Replay ＋ LiveAuto）で出る。LiveManual では content 無しで出ない。
5. **screen-anchored**: canvas を pan しても popup の screen 座標が動かない（dock window は動く＝対比で pin）。
6. **非永続**: hidden/visible/閉じた状態を save→boot しても popup の位置・可視を復元しない（`floatingWindows` に乗らない）。
7. base group は 3 窓 `{buying_power, orders, positions}` で成立（FormGroup が共有 groupId を stamp・≥2）。

実 pan の奥行き目視（popup が canvas と一緒に動かないこと）は owner HITL。

## 番号注記

ADR は次空き番号 0037（0036 まで使用済み）。findings は次空き番号 0125（0124 まで使用済み）。
