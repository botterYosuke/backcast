# findings 0113 — eject つまみ（"⤴"）廃止・単窓ピックアップは Alt+drag 単独

方針: **[[ADR-0032]]**。本 findings はその下位設計の木を固定する。
grill: `grill-with-docs`（2026-06-25・owner HITL）。supersede: findings 0106 §0-Q2（eject つまみ＋Alt 併用）／§1 の `hitTarget == EJECT_HANDLE` 分岐／§3 の起動経路に含まれる eject つまみ／§14 の `FloatingWindowEjectHandle.Attach` 配線・S41/S24 の eject 行。**維持**: findings 0106 のそれ以外全て（2 チャンネル固定・距離撤廃・ドロップ結果・swap reflow・island move 無制限・merge・spring・ESC）。本 findings は ADR-0029/findings 0106 を**書き換えず**、廃止差分だけを記録する。

## §0 owner 判断（HITL・2026-06-25）

- **廃止する**（Alt+drag のみで良い）。理由 = ① 見た目の雑音／UI が忙しい（全 title-bar 常駐チップ）、② Alt+drag で十分（冗余）。
- invisible mode（不可視 modifier 起動）を owner が HITL を経て**意図的に再受容**（ADR-0032 Context）。発見性喪失は既知のコスト。

## §1 起動経路の変更（findings 0106 §1 の `BeginDrag` を縮約）

```
function BeginDrag(A, modifiers):            # hitTarget 引数を削除
    if modifiers.alt:
        channel = SINGLE_WINDOW_PICKUP       # Alt+title-bar drag のみ
    else:
        channel = ISLAND_MOVE
    snapshot I₁ ids + rest rects + nonIsland rects
```

- `hitTarget == EJECT_HANDLE` 分岐を削除（findings 0106 §1 の eject 経路を退役）。
- それ以外（distance 概念なし・singleton の畳み込み・channel 固定）は不変。

## §2 削除/改修 touch-point（実装の正本リスト）

| ファイル | 操作 |
|---|---|
| `Assets/Scripts/FloatingWindow/FloatingWindowEjectHandle.cs` | **ファイル削除**（dead builder）。`.meta` も削除 |
| `Assets/Scripts/FloatingWindow/FloatingWindowTitleInput.cs` | `_ejectHandle` フィールド削除・`Awake()` の attach 削除・`OnBeginDrag` の `hitEject` 算出削除し `ResolveChannel(altHeld)` へ。コメント（L33-37/L79-82/L100/L116-119）を Alt 単独へ更新 |
| `Assets/Scripts/FloatingWindow/FloatingWindowMath.cs` | `ResolveChannel(bool hitEjectHandle, bool altHeld)` → `ResolveChannel(bool altHeld) => altHeld ? SingleWindowPickup : IslandMove`。doc コメント（L41/L47/L138-142）から eject つまみ参照を除去 |
| `Assets/Scripts/FloatingWindow/FloatingWindowController.cs` | コメント L501/L670 の「eject handle / Alt」→「Alt」 |
| `Assets/Scripts/FloatingWindow/FloatingWindowResizeHandle.cs` | コメント L4/L9/L52 の eject handle 対比を「title-bar drag 入力」基準へ言い換え（削除済みファイル名を残さない） |
| `Assets/Scripts/FloatingWindow/FloatingWindowResizeGrip.cs` | コメント L8 の「UNLIKE the eject handle」を同様に言い換え |
| `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.cs` | §AFK 参照 |
| `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.md` | DRAG-17 記述削除・S24 記述更新 |
| `CONTEXT.md` | `eject つまみ` 退役マーク・`gesture channel`/`単窓ピックアップ`/`resize つまみ` 更新 |

> **`ResolveChannel` は名前付き seam のまま 1 引数で残す**（inline しない）。AFK の S24 が pin する「チャンネル判定の唯一の入力由来選択」を文書化する seam として残す方が読みやすい。中身は trivial（`altHeld ? Pickup : IslandMove`）だが、ADR-0029 の「channel は OnBeginDrag で 1 度だけ確定」の概念点として残す価値がある。

## §3 ADR-0030（resize つまみ）との関係

ADR-0030 / findings 0112 の resize グリップは**不変**。resize グリップは独自 IDragHandler を持ち `ResolveChannel` に入らない別系統なので、eject つまみ廃止の影響を受けない。ADR-0030 D2 が eject つまみを「全 window 一律 attach」の前例として引用しているが、ADR-0030 は固定で**編集しない**——前例は decision 時点で歴史的に真。CONTEXT.md の `resize つまみ` 項だけは「[[eject つまみ]] と違い独自 IDragHandler」の対比を、退役 term に依存しない言い回し（title-bar drag 入力との対比）へ更新する（glossary は ADR ではないので更新可）。

## §AFK 正本再構成（実装着手前に `behavior-to-e2e` を formal invoke して固定）

`FloatingWindowE2ERunner` の対応:

| 旧 section | 本 ADR での扱い |
|---|---|
| Section41（DRAG-17 eject handle affordance: S41a-f） | **削除**。eject つまみが無くなるため affordance contract 自体が消える |
| S24（`ResolveChannel` 真理値表 4 行: false/false→Island, true/false→Pickup, false/true→Pickup, true/true→Pickup） | **縮約**: 1 引数 2 行（`ResolveChannel(false)→IslandMove` / `ResolveChannel(true)→SingleWindowPickup`）。S24a-e（`ResolveDropOutcome`）は**不変** |
| S25-S40（pickup の render/swap/merge/detach/reflow） | **維持**。これらは `BeginDrag(id, start, DragChannel.SingleWindowPickup)` でチャンネル直接注入なので eject 廃止の影響なし |

- **litmus（非空虚の確認）**: S24 で `ResolveChannel(true)` を `IslandMove` に壊すと RED になること。Section41 削除後、`FloatingWindowEjectHandle` への参照が AFK 全体で 0 になることを compile で確認（削除漏れ検出）。
- **AFK の正直な限界（findings 0106 の §AFK 注記を継承）**: 入力層 glue（`OnBeginDrag` の Alt poll → `BeginDrag(channel)`）は実 Keyboard を要するため batchmode AFK では駆動できない。pure `ResolveChannel` は AFK が、glue は owner playmode HITL が守る二層は変わらない（eject 経路が消えた分、HITL で確認する統合点は「Alt+drag で pickup に入る」1 本に減る）。

## §HITL（owner playmode 目視）

- title-bar から "⤴" チップが消え、whiteboard が清潔になったこと。
- `Alt`+drag で従来どおり単窓ピックアップ（swap/merge/detach）が起動すること。
- close "✕" / cell run "▶" の押下が（チップ消失で）以前より素直なこと。

## §4 実装着地（2026-06-25）

- **削除確認**: `FloatingWindowEjectHandle.cs`（+ `.meta`・guid `ea11072c2a21d474baa19febe03a6c4c`）を削除。`FloatingWindowEjectHandle` 型・`_ejectHandle`・`hitEject(Handle)`・`EjectHandle`/`EjectGlyph` ノード名・`"⤴"` glyph・削除 guid への参照は Assets ツリー（.cs / .unity / .prefab / .asset / .meta）全 grep で **0 件**（dangling 参照・Unity missing-script リスクなし）。
- **入力経路 1 本化**: `FloatingWindowTitleInput` から `_ejectHandle` フィールド・`Awake()`（eject attach 専用だった）・`OnBeginDrag` の `hitEject` 算出を撤去。`OnBeginDrag` は `Keyboard.current` の Alt のみ読んで `ResolveChannel(altHeld)` を呼ぶ。resize グリップ attach は `Initialize → AttachResizeGrip` 経路で Awake 非依存のため影響なし。
- **`ResolveChannel` 縮約**: `FloatingWindowMath.ResolveChannel(bool altHeld) => altHeld ? SingleWindowPickup : IslandMove`（名前付き seam として 1 引数で存続・§2 方針どおり inline しない）。呼び出し元（`FloatingWindowTitleInput` / S24）は全て 1 引数へ更新済み。
- **AFK 緑**: `FloatingWindowE2ERunner` の Section41（DRAG-17）を削除し実行チェーンから除去、S24 の `ResolveChannel` 真理値表を Alt 単軸 2 行（`false→IslandMove` / `true→SingleWindowPickup`）へ縮約。S24a-e（`ResolveDropOutcome`）・S25-S40（pickup の render/swap/merge/detach・`BeginDrag` 直接注入）は不変。litmus（S24 で `ResolveChannel(true)` を `IslandMove` に壊すと RED）成立。`E2E-INDEX.md` は `42 pass / 5 manual / 1 retired`（total 48 据え置き）、runner `.md` は DRAG-17 退役マーク・DRAG-20-HITL に eject 廃止確認を追記。
- **対比コメント言い換え**: `FloatingWindowController` / `FloatingWindowResizeHandle` / `FloatingWindowResizeGrip` の「eject handle と対比」コメントを「title-bar drag 入力（`ResolveChannel` が確定する経路）との対比」へ言い換え、削除済みファイル名を残さない。
- **docs 整合**: ADR-0029 / ADR-0030 は自己保護条項どおり**無改変**（`git diff` 空）。CONTEXT.md は専用 4 項（`eject つまみ` 退役マーク・`gesture channel`・`単窓ピックアップ`・`resize つまみ`）に加え、他 glossary 項（Hakoniwa group / window group / D_DETACH / floating window）の gesture-channel 記述から eject つまみの live 起動経路参照も Alt 単独へ更新（code-review 指摘）。findings 0106 に supersession バナー追記。
- **code-review 着地**: `parallel-agent-dev` レビュー（source 正しさ / E2E カバレッジ / dead コード / docs 整合の 4 次元並行）で Critical/High なし。Medium 2 件（CONTEXT.md 他 glossary 項の eject 残参照・本 §4 未記入）を本着地で解消。
