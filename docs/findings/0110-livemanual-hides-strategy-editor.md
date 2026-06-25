# findings 0110 — LiveManual では Strategy Editor（authoring 表面）を非表示にする

方針（参照のみ・変更しない）: [ADR-0026](../adr/0026-settings-dialog-consolidates-venue-mode-scenario.md)（mode の正本＝Settings／runtime poll SoT は footer `FooterModeViewModel.DisplayMode`）, [ADR-0013](../adr/0013-cell-as-floating-window-notebook-aggregate.md)（cell = floating window・notebook 集約）, [ADR-0017](../adr/0017-hakoniwa-dockable-floating-windows.md) / [ADR-0018](../adr/0018-two-plane-parallax-floating-windows.md)（front-plane = `strategy_editor` cell ＋ `order` の 1.2× プレーン）。
関連: findings 0050（cell-as-floating-window）／0028（mode-conditional base tiles・dock 側の前例）／0079・0075（notebook authoring 表面）／0101（order ticket）。

本 findings は `/grill-with-docs`（2026-06-25・owner HITL Q1–Q3）で確定した下位決定を会話で消えないように固定する。**ADR は新設しない**（理由は §4）。

> **状態: 設計確定。** 実装着手は `behavior-to-e2e` を formal invoke して AFK gate を RED 先行で固定してから（§5）。

---

## 0. 要望（owner）

> 「Manual モードでは "Strategy Editor" を非表示にしてください。Manual モードは人間が注文ボタンをクリックして株の売買を行うモードなので、python ソースは必要ない。」

口語「Manual」= engine 正準 enum の **`LiveManual`**（実発注・手動・CONTEXT.md mode glossary）。

## 1. 現状（コードで両端まで裏取り済み）

| seam | 場所 | 事実 |
|---|---|---|
| mode SoT（runtime poll） | `BackcastWorkspaceRoot.DriveFooter()` → `_footerMode.DisplayMode` | 毎フレーム engine poll で上書き。値は `Replay`/`LiveManual`/`LiveAuto`（`FooterModeViewModel`）。picker は ADR-0026 で Settings へ移設したが poll SoT は不変 |
| **完全対称の前例** | `DriveOrderTicket()`（`BackcastWorkspaceRoot.cs:1453`） | Order ticket 窓は **`DisplayMode == LiveManual` のときだけ `SetActive(true)`**、それ以外は `SetActive(false)`。build 時の既定も `_orderWindow.gameObject.SetActive(false)`（:531）。**純粋な可視性トグル**（geometry・content 保持・永続化スキーマ不変） |
| Strategy Editor 表面 | front-plane controller `_windows`（1.2×・`_floatingLayer`） | adopt 済み region_001（`WINDOW_ID = "strategy_editor:region_001"`・never-Destroy）＋ cell-as-floating-window（ADR-0013）で追加された各 cell 窓。**全部 `kind == strategy_editor`**（`:758` factory・`:2431` で `w.kind != KIND_STRATEGY_EDITOR` を nonCell 扱い）。`[+] Add Cell` ボタン（`BuildAddCellButton`）も authoring 表面 |
| run lifecycle | cell live run は **LiveAuto + venue 接続でのみ launch**（`liveLaunchActive` = `DisplayMode == LiveAuto`・`:490`） | LiveManual に居る間は cell live run が active になる経路が無い（mode 離脱時の teardown は findings 0026） |

**意味論の根拠**: Replay = backtest で Python 必要・LiveAuto = cell が戦略を自動駆動するので Python 必要 → この 2 モードでは表示維持。LiveManual = 人間が order ticket で発注、Python authoring 不要 → 隠す。結果として front-plane は **LiveManual で order ticket だけ・Replay/LiveAuto で strategy_editor だけ** という order ticket の鏡像になる。

## 2. 確定した設計（Q1–Q3・owner HITL binding）

### Q1 — 非表示の対象モードは **LiveManual のみ**
Replay/LiveAuto では Strategy Editor 表示維持。LiveManual のときだけ非表示。Order ticket（LiveManual のみ表示）の完全な inverse。「Live 中は隠す（=LiveAuto も隠す）」案は却下（LiveAuto は cell が戦略を駆動するので Python 表面が必要＝矛盾）。

### Q2 — 隠す範囲は **authoring 表面一式**
`kind == strategy_editor` の **全 front-plane 窓**（adopt 済み region_001 ＋ 追加 cell 窓すべて）＋ **`[+] Add Cell` ボタン**を隠す。「窓だけ隠して Add ボタンを残す」は不整合（LiveManual で cell を増やせてしまう）として却下。

### Q3 — 隠し方は **純可視性トグル**（Order ticket と対称）
- `SetActive(false)` で隠し、LiveManual を抜けたら `SetActive(true)` で元の姿に復帰。**geometry・content・永続化スキーマは不変**（窓を close しない・layout 保存形式を変えない）。
- 隠れている間に走行中の cell live run があっても **run は止めない**（可視性のみ。run の停止は mode 離脱の teardown が別途担う＝findings 0026）。LiveManual では live run が active になる経路は無い（§1）ので実害は無いが、不変条件として「隠す = 可視性のみ」を固定。

## 3. 実装スケッチ（実装時 §6 に証跡を追記）

- `Update()` に `DriveStrategyEditor()` を追加（`DriveOrderTicket()` の直後／同じ poll サイクル）。`liveManual = _footerMode != null && _footerMode.DisplayMode == FooterModeViewModel.LiveManual` を計算し、**`!liveManual`** を全 strategy_editor front-plane 窓と Add Cell ボタンの `activeSelf` に反映（差分時のみ `SetActive`）。
- strategy_editor 窓の列挙は `_windows`／coordinator が既に保持する（`:2431` が同じ判定で列挙している）。新規に列挙経路を増やさず既存 seam を流用する。
- build 時の初期 active 状態: 既定 mode は Replay（`mode_manager` 初期値）なので strategy_editor は最初 visible（現状維持）。最初のフレームで `DriveStrategyEditor` が mode に追従するので明示初期化は冗長だが、order ticket（`:531`）に倣い build 末で初期 mode に合わせても良い（実装時判断）。

**下位機構の未決（実装中に pin）**:
- 永続化 `Capture` が **inactive な窓も geometry を拾うか**（Save が LiveManual 中に走っても strategy_editor の位置が落ちないこと）。order ticket も同じく LiveManual 以外で inactive になり得るので既存挙動を確認し、落ちるなら capture は active 非依存にする。
- z-order / focus: 隠している間に front-plane の focus 対象が strategy_editor を指したまま残らないか（order ticket 切替で既に踏んでいる経路なので回帰確認で足りる見込み）。

## 4. ADR を新設しない理由

ADR の 3 条件（hard to reverse／surprising without context／genuine trade-off）をいずれも満たさない:
- **reversible**: 可視性トグル 1 つ。撤回コスト極小。
- **not surprising**: 既存の order ticket（LiveManual のみ表示）の対称な鏡像。ADR-0026 の mode SoT・ADR-0013 の cell 窓モデルの上に乗るだけ。
- **自明な対称拡張**で genuine な選択肢の対立が無い。

→ 下位事実は本 findings に固定し、ADR-0026/0013/0017/0018 を「方針」として参照（ADR は無改変）。

## 5. behavior-to-e2e ハンドオフ（実装着手の入口）

挙動が変わる（mode に応じた窓の可視性）ので、実装前に `behavior-to-e2e` を formal invoke し AFK gate を RED 先行で固定する。正本ゲートは **Python-FREE な C# AFK probe**（control logic）。

新規ゲート（Action-ID タグで rollup に載せる）:
- **mode → strategy_editor 可視性**: `DisplayMode` を Replay→LiveManual→LiveAuto→LiveManual と動かし、strategy_editor 全窓＋Add Cell ボタンの `activeSelf` が `Replay=true / LiveManual=false / LiveAuto=true` になることを assert。order ticket は逆相（LiveManual=true）であることも同フレームで確認（front-plane の排他＝鏡像）。
- **可視性のみ（非破壊）**: LiveManual へ遷移しても strategy_editor 窓が **Destroy されない**（同一 instance 保持）・geometry が保たれ、LiveManual を抜けると元位置で再表示されること。
- **回帰**: 既存 `OrderTicketE2ERunner` / `LiveManualTradeJourneyE2ERunner` / `FooterModeE2ERunner` / `StrategyEditorNotebookE2ERunner` が GREEN 継続（特に per-cell RUN／cell add-delete が mode 非依存で動くこと）。

候補 runner: 既存の `FooterModeE2ERunner`（mode poll 駆動）か `StrategyEditorNotebookE2ERunner` を拡張（重複新規しない・behavior-to-e2e が棚卸し）。

### owner-run HITL
footer/Settings で Replay⇄LiveManual⇄LiveAuto を切替 → LiveManual で Strategy Editor（全 cell 窓＋[+]）が消え order ticket が出る・Replay/LiveAuto で Strategy Editor が元位置で戻り order ticket が消える。Save→再 Play で位置復元（LiveManual 中に Save しても strategy_editor 位置が落ちない）。

## 6. 実装証跡（#138・2026-06-25・AFK RED→GREEN）

`behavior-to-e2e` formal invoke → gate を著し RED 先行で固定 → 実装 → GREEN。

**production 変更**:
- `FloatingWindowController.HideKind(kind, recordHidden)` / `ShowHidden(hidden)`（新規・対）: kind 単位の **mode 可視性プリミティブ**。`HideKind` は当該 kind の *active* 窓だけを `SetActive(false)` し、隠した id を `recordHidden`（呼び元所有の `HashSet<string>`）へ記録。`ShowHidden` は記録された id **だけ** を再表示して set を clear。いずれも **allocation-free**（struct dict enumerator・毎フレ呼んでも GC を生まない）で、`Show(id)` と違い `BringToFront` を呼ばない（z 不変）。
  - ⚠️ **当初の `RectsOfKind`（List 返し）は撤去**——(1) 毎フレ `new List` で alloc 過敏な `Update` poll に逆行、(2) show 側が全窓 blanket `SetActive(true)` で **dormant region_001 を蘇生**する欠陥（下記）を内包していた。レビューで両方を本対で解消。
- `BackcastWorkspaceRoot`:
  - `_addCellOverlay`（新規 field）= `BuildAddCellButton` が作る AddCellOverlay GameObject を保持（[+] Add Cell を一括 hide する対象）。
  - `_strategyEditorHiddenByMode`（新規 field・`HashSet<string>`）= LiveManual 突入時に **この toggle が隠した** 窓 id。離脱時に「自分が隠したものだけ」を戻すための remembered-set。
  - `DriveStrategyEditor()`（新規）= `DriveOrderTicket` の鏡像。`liveManual` なら `HideKind`、そうでなければ `ShowHidden`。`_addCellOverlay` は singleton なので `!liveManual` を直接反映。純可視性のみ（geometry/content/永続化不変・run 不停止）。
  - `Update()` の `DriveOrderTicket()` 直後に `DriveStrategyEditor()` を追加（同 poll サイクル＝order ticket と同一の ≤1 frame DisplayMode latency・対称）。

**§3 の未決下位機構の決着 ＋ レビュー由来の追加決定**:
- **【レビューで発見・修正】dormant region_001 を蘇生させない**: `DeleteCell(region_001)` は never-Destroy 殻を `Hide`（`SetActive(false)`＋`_region001Dormant=true`）にする（ADR-0013 D4）。当初実装の「非 LiveManual で全 strategy_editor 窓を blanket `SetActive(true)`」は、この dormant 殻を **毎フレ Replay でも復活**させ #81 の hide-not-destroy を破壊していた。修正＝remembered-set（`HideKind`/`ShowHidden`）で「mode が隠したものだけ」を戻す——dormant 殻は `HideKind` の active 条件で記録されないので戻されない。**STRATEGY-55 が RED 先行で機械担保**（blanket 実装で実 AFK が `STRATEGY-55: dormant region_001 shell resurrected ... in Replay` を吐き exit 1）。
- **Capture が inactive 窓 geometry を拾うか**: 問題なし。`CapturePositions()`/`Capture()` は `_windows` の `RectOf(region).anchoredPosition`（geometry）を読むので `SetActive(false)` 影響を受けない（active 状態と RectTransform 値は独立）。**STRATEGY-54 が LiveManual 中（窓 inactive）の `CapturePositions()` を実呼びし pre-hide と一致を assert（AC5 を機械担保・以前の「54 が AC5 を担保」記述はこの追加で初めて真）**。
- **z-order/focus**: 純 `SetActive` トグルで `_windows` の slot/groupId/z は不変（`ShowHidden` も `BringToFront` を呼ばない）。回帰は OrderTicketE2ERunner（同型の SetActive 可視性）GREEN で確認。

**AFK gate（`StrategyEditorNotebookE2ERunner` Section25・STRATEGY-53/54/55・実 root・Python-FREE）**:
- 実 scene は region_001 dormant で起動するため、Section は `coord.New()`→`AddCell()` で **region_001＋region_002 の 2 窓**を確定生成（multi-window「全窓」AC1 と `HideKind` ループを実走・単窓退行は GREEN にならない）。`_orderWindow` も hard-guard（rename で vacuous PASS しない）。
- STRATEGY-53 = mode→可視性＋front-plane 排他（Replay/LiveAuto で **両窓**＋[+] 表示・order ticket 非表示／LiveManual で両窓＋[+] 非表示・order ticket 表示）。
- STRATEGY-54 = 非破壊（Replay→LiveManual→Replay で region_001/region_002 が同一 `GetInstanceID`・`anchoredPosition`/`sizeDelta` 保持・再表示）＋ **AC5**（LiveManual 中 `CapturePositions()` が pre-hide と一致）。
- STRATEGY-55 = dormant 非蘇生（region_001 の cell を削除→dormant→Replay drive と LiveManual round-trip で殻が再表示されない・region_002 は通常通り toggle）。
- **RED→GREEN（実機 AFK で確認・2026-06-25）**: `DriveStrategyEditor` を当初の blanket `SetActive(show)` に戻す→ `[E2E STRATEGY NOTEBOOK FAIL] STRATEGY-55: dormant region_001 shell resurrected by the mode toggle in Replay`・exit 1 で **RED** → remembered-set 復元で `[E2E STRATEGY-53/54/55 PASS]`・exit 0・rollup `3 PASS / 0 FAIL`・`error CS` 0 で **GREEN**。
- **回帰**: `OrderTicketE2ERunner`（`[E2E ORDER TICKET PASS]`）／`FooterModeE2ERunner`／`LiveManualTradeJourneyE2ERunner`／`StrategyEditorNotebookE2ERunner` 既存全 section いずれも exit 0 GREEN＝Update path 追加・front-plane 共有で退行なし。

**台帳更新**: `StrategyEditorNotebookE2ERunner.md`（STRATEGY-53/54/55 行＋litmus）／`E2E-INDEX.md`（STRATEGY-01..55・55/51・Surface 225 行）。

**owner-run HITL（pending・任意目視）**: footer/Settings で Replay⇄LiveManual⇄LiveAuto を切替 → LiveManual で Strategy Editor（全 cell 窓＋[+]）が消え order ticket が出る・Replay/LiveAuto で元位置復帰。AFK が可視性・排他・非破壊を担保済みなので目視は最終確認のみ。

---

> 🤖 `/grill-with-docs`（2026-06-25）セッション記録（Claude Code）。下位事実は本 findings に固定し ADR を「方針」として参照（ADR は編集しない）。
