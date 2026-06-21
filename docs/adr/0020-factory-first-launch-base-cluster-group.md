---
status: accepted
---

# 初回起動の base dock cluster を 1 つの Hakoniwa group に束ねる（工場出荷値）

owner 依頼（2026-06-22）「hakoniwa group の初期位置 — ドッキングしてない状態が初期値になっている。初回から 1 グループに束ねたい」を受けた決定。HITL（2026-06-22）で 2 点を確定した:

1. **グループ意味論 = Hakoniwa**（タイル固定・ドラッグでスワップ・core ロック・全体自由移動なし）。base cluster は `startup` / `run_result`（[[ADR-0019]] の core member）を含むので、束ねれば自動的に Hakoniwa group になる＝owner の「箱庭」像と自然に一致。
2. **適用は初回のみ（工場出荷値）**。saved layout がある場合は本経路を通さず、復元（`RestoreFloating`）が doc の `groupId`（ユーザーが解散していれば null）を尊重する。

## なぜ新規 ADR か

[[ADR-0019]] Decision D8 / findings 0082 §10 は「`groupId` が非 null になるのは **ユーザーの drag-release だけ**（programmatic Spawn / restore / cell coordinator は mint しない）」を owner-lock した。本決定はそこに **第 3 の birth path（工場出荷 first-launch）** を足す——D8 の「only way」言明を拡張するため、ADR-0019 を無改変のまま本 ADR が差分だけ supersede する（[[ADR-0018]] が ADR-0017 を、ADR-0019 が ADR-0017 を点 supersede した先例と同型）。

関連: [[ADR-0019]]（Hakoniwa group / drag semantics・本 ADR が D8 を 1 点拡張）／findings 0082（group 下位設計・本 ADR の下位事実は §12 と findings 0083）。

## Context

[[ADR-0019]] で group は「ユーザーが望んだ時だけ生まれる」モデルになり、初回起動では 5 つの base dock 窓（startup / buying_power / orders / positions / run_result）が `DockDefaultPlacement` のグリッド位置に **個別 window（groupId=null）** として spawn される（`BackcastWorkspaceRoot.SpawnBaseDockWindows`）。視覚的には密に並ぶが group ではないため、初回からドラッグでスワップする「箱庭」操作ができない。owner はこれを「ドッキングしてない初期値」と指摘し、初回から 1 group に束ねたいと要望した。

## Decision

- **D1 (factory grouping)**: no-resume / unresumable boot（saved layout 無し＝first launch）でのみ、5 つの base dock 窓を **1 つの共有 `groupId`** に束ねる。cluster は core を含むので **Hakoniwa group**（translate 禁止・swap・core ロック）になる。
- **D1b (flush 配置)**: 束ねた cluster は **flush（隙間なし・くっついている）** に配置する（`DockDefaultPlacement.ComputeFlushRects`・gap=0）。group のメンバーは flush-adjacent が前提（group は flush-snap で生まれる）であり、groupId だけ付けて gap を残すと「grouped だが見た目は隙間あり」＝user drag では生まれない不整合かつ owner の「束ねたい」意図に反する（owner feedback 2026-06-22「くっついてない」で確定）。
- **D2 (first-launch only)**: saved layout を復元/開いた経路では本グループ化を **呼ばない**。`RestoreFloating` が doc の `groupId` を尊重する（ユーザーが解散して保存した状態は次回も解散のまま）。no-resume boot は untitled のまま autosave しない（findings 0048 D7）ので、保存しない限り毎回 factory group が再形成され、保存すれば以後はその doc の意思に従う＝「工場出荷値のみ」が自然に成立する。
- **D3 (birth path 規律維持)**: factory grouping は座標から再導出しない——呼び出し側が member id を明示列挙する（restore と同型の「非座標 birth」）。ID を列挙する `FormGroup` だけが本経路の入口で、`Spawn` は引き続き mint しない（ADR-0019 D8 の Spawn=null 不変は維持）。

## 不採用

- **不採用：default document（`LayoutDocument.Default()`）に group 入り base 窓を載せる**。restore 経路で束ねれば birth path を増やさず済むが、(a) `Default()` は `ReplayLayoutProbe` で machine-lock されており改変の波及が広い、(b) base 窓の位置真実が `DockDefaultPlacement` と `Default()` の 2 箇所に分裂する。`FormGroup` を no-resume 分岐から呼ぶ方が位置真実を 1 本に保てる。
- **不採用：Normal group（全体自由移動 + 個別 detach 可）**。core を含む cluster は ADR-0019 で自動 Hakoniwa 化するため、Normal にするには core 判定の改変が要る＝owner の箱庭像とも乖離（HITL で Hakoniwa を選択）。
- **不採用：毎回束ね直す**。saved layout の解散状態を無視するため owner 決定（初回のみ）に反する。

## Consequences

- **controller の拡張**: `FloatingWindowController.FormGroup(ids)` を新設＝1 つの `groupId` を mint し、live な named member（≥2）に stamp。<2 live なら no-op（group は ≥2 が前提＝`DissolveIfShrunkTo` の閾値）。
- **boot 配線**: `BackcastWorkspaceRoot.FormFactoryBaseGroup()` を `ResumeLastDocumentOrDefault` の no-resume 分岐（`ApplyLayout(Default())` 直後）から呼ぶ。base id は `SpawnBaseDockWindows` と共有の `BaseDockWindowIds`。
- **既存挙動互換**: Live で startup が hidden になっても run_result core が残り（visible≥2）group は維持。窓を閉じて visible<2 になれば既存 `DissolveIfShrunkTo(2)` が解散。drag/swap/detach/cross-plane は ADR-0019 のまま。
- **AFK 正本の拡張**: `FloatingWindowE2ERunner` に S32（GROUP-14）を追加＝FormGroup が共有 groupId を stamp・Spawn は mint しない・cluster が Hakoniwa（core drag = HakoniwaCoreLock freeze）・<2 no-group を assert。実装着手前に `behavior-to-e2e` を formal invoke 済み。
- **下位事実は findings 0082 §12 / findings 0083 に固定**（GUID 形式・≥2 閾値・呼び出し位置・RED→GREEN・AFK 再走手順）。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。下位事実（FormGroup の戻り値仕様・閾値・呼び出し位置の細部）は本 ADR に書き戻さず findings 0082 §12 / findings 0083 に記録し本 ADR を「方針: ADR-0020」として参照する。
