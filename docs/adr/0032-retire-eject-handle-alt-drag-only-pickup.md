---
status: accepted
---

# eject つまみ（"⤴"）を廃止し単窓ピックアップの起動を Alt+drag 単独にする

owner 依頼（2026-06-25）「タイトルバーの `Alt+Drag` と同じ `⤴` ボタンを廃止」を、`grill-with-docs`（2026-06-25・owner HITL）で設計ロックした決定。

これは **[[ADR-0029]] Decision 3 の起動経路（engage-path）のみを点 supersede** する。ADR-0029 は自己保護条項を持つため**無改変**で、本 ADR が差分だけを明示 supersede する（[[ADR-0029]]→0024、[[ADR-0024]]→0019 の先例と同型）。**ADR-0029 のその他の決定（D1/D2/D4/D5/D6/D7/D8）は全て維持** —— 2 ジェスチャモデル・チャンネルを `OnBeginDrag` で固定・距離トリガ撤廃・ドロップ位置で結果決定・島移動 merge・swap サイズ維持＋[[island-scoped reflow]]・global no-reflow・磁石/spring/ESC は変わらない。本 ADR が触るのは **[[単窓ピックアップ]]チャンネルの「見える起動手段」だけ**で、チャンネルそのものは不変。

関連: [[ADR-0029]]（D3 の起動経路を本 ADR が supersede・他 decision は維持）／[[ADR-0030]]（resize つまみ。eject つまみを「全 window 一律 attach」の前例として引用していたが、その引用は decision 時点で歴史的に真であり resize グリップの存続には影響しない＝後述 Consequences）。実装下位事実は findings 0113。

## Context

ADR-0029 は owner の「invisible mode 批判」（不可視の modifier だけでは単窓ピックアップが発見できない）を構造的に消すため、**eject つまみ（title-bar 左上の常時可視 "⤴" チップ）を *主* アフォーダンス**にし、`Alt`+drag を *ショートカット* と位置づけた（D3・owner Q2＝両用）。Considered Options では「Alt-drag のみ」を**発見性が低いとして明示却下**していた。

owner は #136 実装後の playmode HITL（ADR-0029 §HITL / DRAG-20-HITL）で実際に触った結果、廃止を判断した:

1. **見た目の雑音 / UI が忙しい**: "⤴" チップが dock / editor / order / HITL の**全 title-bar に常時表示**され、whiteboard の清潔さを損なう。editor/order 窓では右側の close "✕" / cell run "▶" の raycast cluster を避けるため左上へ寄せた経緯もあり（findings 0106 code-review 着地）、結局 title-bar に常駐するチップが視覚的に煩い。
2. **Alt+drag で十分（冗余）**: ピックアップを一度覚えれば `Alt`+drag で足り、常時可視チップの**発見性の価値 < チップの視覚コスト**と owner が判断した。

すなわち本 ADR は **ADR-0029 が回避しようとした「invisible mode」のトレードオフを、owner が HITL を経た上で意図的に再受容**する決定である。発見性は失われるが、それは owner が体感の上で許容した既知のコストである。

## Decision

ADR-0029 D3 の起動経路を以下 3 点で supersede する。

1. **eject つまみ（"⤴"）を廃止**。`FloatingWindowEjectHandle`（共有ビルダ）・title-bar への attach・"⤴" glyph を撤去する。第 2 raycast target は無くなる。
2. **[[単窓ピックアップ]]の起動は `Alt`+title-bar drag 単独**。`OnBeginDrag` で `Keyboard.current` の Alt を読みチャンネルを確定する経路のみが残る。plain な title-bar drag = [[IslandMove]]、window body = canvas pan は不変（ADR-0029 維持）。
3. **チャンネル判定の入力は Alt のみに簡素化**。`FloatingWindowMath.ResolveChannel(bool hitEjectHandle, bool altHeld)` を `ResolveChannel(bool altHeld)`（`altHeld ? SingleWindowPickup : IslandMove`）へ縮約。`DragChannel` enum・チャンネルを gesture 開始で固定する不変条件・ドロップ位置で結果を決める仕組みは全て維持。

ADR-0029 のその他は不変。pickup の swap/merge/detach のドロップ結果ロジック・島内 reflow・island move 無制限距離・merge・spring・ESC は一切変えない。

## Considered Options

- **採用：eject つまみ廃止・Alt+drag 単独**。owner が HITL で「チップの視覚コスト > 発見性の便益」と判断。invisible mode の再受容は owner の体感に基づく既知のコスト。実装は dead builder の削除と入力経路 1 本化で、リスクは局所。
- **不採用：eject つまみ維持（ADR-0029 のまま）**。owner が明示的に廃止依頼。視覚的雑音が解消されない。
- **不採用：チップを消すが別の見える起動手段に差し替え（右クリック / hover 時のみ表示 / 別アイコン）**。invisible mode を避けられるが、owner が grill で「Alt+drag のみで良い」を選択（発見性より清潔さ優先）。新たな UI 要素の設計コストも見合わない。
- **不採用：`ResolveChannel` を 2 引数のまま `hitEjectHandle=false` 固定で残す**。dead 引数が残り誤読を招く。1 引数化が素直。

## Consequences

- **CONTEXT.md glossary** を本決定に整合（同時更新）: `eject つまみ`（**退役マーク**＝廃止・Alt+drag へ一本化）／`gesture channel`（SingleWindowPickup の起動を `Alt`+drag 単独へ）／`単窓ピックアップ`（起動経路から eject つまみを除去）／`resize つまみ`（eject つまみとの対比記述を「title-bar drag 入力」基準に言い換え＝退役した term への依存を外す）。
- **コード改修**: `FloatingWindowEjectHandle.cs` 削除。`FloatingWindowTitleInput`（`_ejectHandle` フィールド・`Awake` attach・`hitEject` 算出を撤去、`OnBeginDrag` は Alt のみで channel 確定）。`FloatingWindowMath.ResolveChannel` を 1 引数化。`FloatingWindowController` / `FloatingWindowResizeHandle` / `FloatingWindowResizeGrip` の「eject handle と対比」コメントを退役済みファイル参照が残らないよう言い換え。
- **AFK 正本の再構成**: `FloatingWindowE2ERunner` の Section41（DRAG-17 eject handle affordance）を**削除**、S24（`ResolveChannel` 真理値表）を **Alt 単軸の 2 行**（`false→IslandMove` / `true→SingleWindowPickup`）へ縮約、S41f reachability を撤去。実装着手前に `behavior-to-e2e` を formal invoke して section 対応を固定（findings 0113 §AFK）。
- **ADR-0030 への影響なし**: ADR-0030 D2 は resize グリップを「全 window 一律 attach」する根拠として eject つまみを前例引用したが、その前例は decision 時点で歴史的に真であり、本 ADR は ADR-0030 を**編集しない**（固定）。resize グリップは独自 IDragHandler を持つ別系統で、eject つまみ廃止の影響を受けず存続する。findings 0112 の resize グリップ設計も不変。
- **発見性の喪失（既知のコスト）**: 単窓ピックアップは `Alt`+drag という不可視 modifier でのみ起動可能になる。新規ユーザは存在に気付けない（ADR-0029 が回避しようとした状態）。これは owner が HITL を経て受容した trade-off であり、将来再び発見性が要求された場合は本 ADR を supersede する新 ADR で見える起動手段を再導入する。
- **永続スキーマ**: 変更なし。
- **下位事実は findings 0113 に固定**: 削除する touch-point の一覧・`ResolveChannel` 縮約・AFK section の RED→GREEN は findings 0113 に記録し、本 ADR は方針のみ。

## 自己保護

本 ADR の decision は固定。覆す場合（例: 見える起動アフォーダンスを再導入する）はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
実装下位事実（削除 touch-point・コメント言い換えの文言・AFK section 番号の最終割当）は本 ADR に書き戻さず `docs/findings/0113` に記録し、本 ADR を「方針: ADR-0032」として参照する。
