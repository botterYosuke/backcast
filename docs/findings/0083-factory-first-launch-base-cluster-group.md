# findings 0083 — 初回起動の base dock cluster を 1 Hakoniwa group に束ねる（#105 / ADR-0020）

owner 要望（2026-06-22）「hakoniwa group の初期位置がドッキングしてない状態になっている。初回から 1 グループに束ねたい」を、AFK probe で RED→GREEN にして実装した記録。設計判断は [[ADR-0020]]、group 下位設計は findings 0082 §12。

## 不変条件（観測点）

> 保存レイアウト無しの初回起動（`ResumeLastDocumentOrDefault` の no-resume 分岐）が走った直後、5 つの base dock 窓（startup / buying_power / orders / positions / run_result）は **全員が同一の非 null `groupId`** を持ち、その group は core（startup / run_result）を含むので **Hakoniwa group**（drag = swap / core ロック・全体自由移動なし）である。逆に saved layout を復元/開いた経路は本グループ化を呼ばず、doc の `groupId` を尊重する（工場出荷値のみ）。

## 実装

- `Assets/Scripts/FloatingWindow/FloatingWindowController.cs`
  - `FormGroup(IReadOnlyList<string> ids)` 新設＝1 つの `MintGroupId()` を live member（≥2）に `SetGroupId`。<2 live は null・no-op。Spawn=null 不変は維持（findings 0082 §10/§12）。
  - `Entry` invariant コメントを「groupId 非 null になる 3 経路（drag-release / restore / #105 factory）」へ更新。
- `Assets/Scripts/FloatingWindow/DockDefaultPlacement.cs`
  - `ComputeFlushRects(n)` 新設＝gap=0 の first-launch 配置。group のメンバーは flush-adjacent が前提（ADR-0019: group は flush-snap で生まれる）なので、factory cluster も隙間なしで配置＝「1 group＝くっついている」。`SpawnBaseDockWindows` と AFK gate S32(e) が共有（flush 契約の単一 source）。
- `Assets/Scripts/Live/BackcastWorkspaceRoot.cs`
  - `BaseDockWindowIds`（static）を新設＝`SpawnBaseDockWindows` と `FormFactoryBaseGroup` の共有真実。
  - `SpawnBaseDockWindows` を `ComputeFlushRects`（gap=0）へ＝初回配置を flush 化（owner feedback 2026-06-22「くっついてない」＝groupId は付くが 12px gap で隙間が見えた・grouped-but-not-flush は user drag では生まれない不整合状態でもあった）。
  - `FormFactoryBaseGroup()` ＝ `_dockWindows?.FormGroup(BaseDockWindowIds)`。
  - `ResumeLastDocumentOrDefault` の no-resume 分岐（`ApplyLayout(LayoutDocument.Default())` 直後）から呼ぶ。**ただし `restoredSavedLayout` フラグで gate**：saved layout が READ できて `ApplyLayout(doc)` で復元したが `_coordinator.Open` が false で fall-through した場合は、既に doc の persisted groupId を復元済みなので factory grouping を**呼ばない**（さもなくば復元 groupId を新規 GUID で clobber する＝ADR-0020 D2 違反。code-review F1・2026-06-22 で検出・修正）。

## RED→GREEN

- **gate**: `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.cs` S32（`Section32_FactoryBaseGroupFormsHakoniwa`・GROUP-14・root-free）。
  - (a) 5 base 窓 spawn 直後は全員 groupId=null（Spawn は mint しない）。
  - (b) `FormGroup` 後は全員が同一非 null groupId。
  - (c) startup（core）を D_DETACH 超で drag → `HakoniwaCoreLock` で geometry freeze（＝Hakoniwa group・全体 translate 禁止）。
  - (d) live<2 / null 入力は no-group。
  - (e) `ComputeFlushRects(5)` の 3×2 グリッドで隣接タイル（0-1,1-2,0-3,1-4）が `IsFlushAdjacent`（eps=1px）＝flush。負の制御として gapped 版（`ComputeRects(5)`・DefaultGap）は非 flush＝flush は gap=0 由来（非空虚）。
- **RED litmus**: `FormGroup` の本体を `return null;`（mint せず）にすると S32(b)/(c) が即 FAIL。配置 gap を非 0 に戻すと S32(e) が FAIL。実装を戻すと GREEN。新 API なので最初の RED は「テストがコンパイルを通らない（FormGroup 未定義）」でも立つ。
- 台本: `FloatingWindowE2ERunner.md` GROUP-14 行（カバー状態 `自動(E2E済)`）／`E2E-INDEX.md` ロールアップ更新（GROUP-01..14・行数 37/auto 32）。

## AFK 再走

```
/Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit \
  -projectPath /Users/sasac/backcast \
  -executeMethod FloatingWindowE2ERunner.Run \
  -logFile /tmp/fw_e2e.log
# 確認: grep -a "E2E FLOATING WINDOW" /tmp/fw_e2e.log  → PASS 行・exit 0
# 罠: .cs 編集直後の初回起動は recompile で終わるので 2 回目で実走（memory unity-afk-probe-run）
# 罠: logFile は絶対パス必須（相対は macOS batchmode で無視）／GUI Editor 起動中は lock-abort
```

## 注意（mid-session File→Open の端ケース）

base 窓は `BuildWorkspace` で 1 度だけ spawn され全 session 生存する。boot で factory group を組んだ後、同一 session で groupId=null の旧 doc を File→Open すると、`RestoreFloating` の legacy-null 許容（findings 0082 F1・doc の null は live group を stomp しない）により factory group が残りうる。owner 決定の射程は **boot** なので本スライスではこの端ケースを変更しない（必要なら別スライスで「明示 group クリア」を検討）。
