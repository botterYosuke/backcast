# 0056 — E2E 第二波 3本目: InfiniteCanvasE2ERunner 昇格

**日付**: 2026-06-19 / **ブランチ**: `e2e/replay-to-hakoniwa-runner`
**関連**: [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md)（runner 配置・命名） /
[台本](../../Assets/Tests/E2E/Editor/InfiniteCanvasE2ERunner.md) /
[findings 0054](0054-e2e-scenario-startup-runner.md)（昇格の型・1本目） /
[findings 0055](0055-e2e-footer-mode-runner.md)（2本目） /
[findings 0006](0006-infinite-canvas.md)（#13 infinite canvas 本体・三方向独立ゲートの根拠） /
[E2E-CONVENTIONS](../../Assets/Tests/E2E/Editor/E2E-CONVENTIONS.md)（section ↔ Action ID 方針）

## 文脈

第二波 3本目。infinite-canvas（pan/zoom）サーフェスを `InfiniteCanvasProbe.cs`（throwaway AFK gate, Assets/Editor）から昇格。
型は findings 0054 で確立した「throwaway probe → E2ERunner（git mv・改名・旧削除）＋(B) 自然な検証単位＋`Covers:`＋AFK RED→GREEN」。
選定理由: 検証ロジックが純 C#／実 RectTransform で Python-FREE・render-FREE、既存 probe（S1〜S7）が台本 CANVAS-01/02/03/04/06/07/08 を
被覆済みで delete-the-logic litmus が明快、台本で唯一 `要新規自動化` の CANVAS-05（input 境界の scroll-tick clamp）が
新規 section の手本になる。

## 昇格モデル（findings 0054 の型に従う）

- throwaway `Assets/Editor/InfiniteCanvasProbe.cs` を `git mv` で `Assets/Tests/E2E/Editor/InfiniteCanvasE2ERunner.cs` へ
  移動済み（.meta も移して GUID 保全＝**オーケストレーターが直列で実施**）。本 findings 著述時点で中身は旧 class 名のまま移動済み、
  本作業で class を `InfiniteCanvasProbe` → `InfiniteCanvasE2ERunner` に改名し PASS/FAIL タグを `[E2E INFINITE CANVAS PASS/FAIL]` に統一、
  `EditorApplication.Exit` を無条件化（self-failing gate）。旧 probe は昇格 = 同一ファイルの改名なので別途削除は不要（git mv 済み）。
- **gate 形 = `Execute()`-形を温存**: 各 section が null=PASS、最初の失敗文字列を返す `?? チェーン`（ScenarioStartup と同形・
  FooterMode の Check-counter 形ではない）。"温存"優先で Check-counter 形へは書き換えない。

## (B) section ↔ Action ID 方針（`Covers:` 明記）

実証済み probe の section body は **assert 1 行も削らず移送**。共有 pure 算術（`CanvasViewMath`）を Action ID ごとに人工分割せず、
各 section header に `// Covers: CANVAS-xx` を付与（台本の操作一覧表「既存Probe」列と双方向に追跡）。対応:

| Section | Covers | 観測内容 |
|---|---|---|
| S1 PanArithmetic | CANVAS-01 | pan = −dScreen/zoom（resolution-independent）、zoom 不変 |
| S2 ZoomClamp | CANVAS-03 | zoom `[0.2,5.0]` 飽和、範囲内ステップ無改変 |
| S3 CursorInvariant | CANVAS-02, CANVAS-04 | カーソル下論理点が通常＋clamp ステップで不変（両非 no-op） |
| S4 ChildFollowAndController | CANVAS-01, CANVAS-02, CANVAS-06 | 実 RectTransform child-follow（engine==math）＋Apply/Capture 境界 |
| S5 DiskRoundTripNonVacuous | CANVAS-07 | `canvasView` の on-disk TEXT 証明＋fresh load（vacuous-green kill） |
| S6 BackCompatAndSanitize | CANVAS-08 | 旧 v1／zoom 0→1／99→5.0／非有限→identity／破損→default |
| S7 ParallaxForegroundLayer | （Action 行なし・depth-cue 拡張） | foreground が base より MORE 移動（engine==math）。回帰網保全のため温存 |
| **S8 ScrollTickClamp（新規）** | **CANVAS-05** | input 境界 `OnScroll` の per-event tick clamp |

> S7 は操作一覧表に対応 Action 行を持たない（昇格元 probe が持つ depth-cue 拡張）。FooterMode の SUPPORTING PIN と同様、
> 回帰網を落とさないため温存し、Action 行には数えない旨を section コメントと台本「既存 Probe との対応」に明記。

## 要新規 section の内容（CANVAS-05 = S8）

`InfiniteCanvasInputSurface.OnScroll`（`InfiniteCanvasInputSurface.cs:66`）の per-event 上限
`float ticks = Mathf.Clamp(eventData.scrollDelta.y, -MAX_SCROLL_TICKS, MAX_SCROLL_TICKS)`（`MAX_SCROLL_TICKS=4`）は
**input 境界のロジック**で、controller を直叩きする S1〜S7 の射程外（probe は input-surface 駆動の assert を持たない）。
S8 は実 MonoBehaviour `InfiniteCanvasInputSurface` を viewport へ attach し `Initialize(controller, viewport)`、
`PointerEventData`（`scrollDelta.y` を注入）を `OnScroll` に渡して駆動する:

1. **liveness / vacuous-green kill（先頭）**: in-range scroll（2 notch）→ zoom `1.1^2`（範囲内）。surface が unwired
   （`Initialize` no-op／`OnScroll` 早期 return）なら zoom が 1 のままで FAIL する＝下の clamp assert が dead path で
   false-green になる穴を塞ぐ（手本 FooterMode の presence-guard と同精神）。
2. **clamp（up）**: raw wheel ~120 → 4 notch に capped → zoom `1.1^4`（≈1.4641）、かつ MAX_ZOOM(5.0) へ飽和しない。
3. **clamp（down）**: 対称に −120 → −4 notch → zoom `1.1^-4`（≈0.683）、MIN_ZOOM(0.2) へ飽和しない。

zoom 倍率は cursor 位置に非依存（cursor は pan のみを動かす）なので、`PointerEventData.position=Vector2.zero`・
`EventSystem.current`（batchmode で null 可、ctor は格納するのみで raycast しない）で十分。期待値は production と同じ
`Mathf.Pow(1.1f, ticks)` で計算するため浮動小数の比較ズレは出ない（EPS=1e-3）。

## RED→GREEN（delete-the-logic litmus・オーケストレーターが実行）

新規 S8 を litmus 対象に選定:

- **RED**: production `Assets/Scripts/InfiniteCanvas/InfiniteCanvasInputSurface.cs:66` の
  `float ticks = Mathf.Clamp(eventData.scrollDelta.y, -MAX_SCROLL_TICKS, MAX_SCROLL_TICKS);` を
  `float ticks = eventData.scrollDelta.y;`（clamp 撤去）に一時破壊 → ticks=120 → factor=`1.1^120` → zoom が MAX_ZOOM へ飽和 →
  `[E2E INFINITE CANVAS FAIL] S8: large up-scroll not capped to 4 notches (got 5, expected 1.4641...)`（Run の exit-1 分岐）。
  **他 S1〜S7 は PASS のまま＝新 section だけが回帰を捕捉**（非空虚）。
- **GREEN**: 復元 → `[E2E INFINITE CANVAS PASS] ...`、全 8 section PASS。
- （補助 litmus）既存ロジックの破壊も明快: `CanvasViewMath.PanByScreenDelta` の `/zoom` 項撤去→S1、`ZoomAtCursor` の
  cursor 補正撤去→S3/S4、`NormalizeCanvasView` の clamp 撤去→S6 が落ちる（台本「自動判定」§ に既記）。
- compile-only ゲート（`-executeMethod` 無し）で `error CS\d+` 0 件を先に確認すること。

## 再走手順

```pwsh
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -batchmode -nographics -quit -projectPath "C:\Users\sasai\Documents\backcast" `
  -executeMethod InfiniteCanvasE2ERunner.Run -logFile "$env:TEMP\ic.log"
# 期待: [E2E INFINITE CANVAS PASS] ... / UNITY exit 0
```

- 確認は **Bash `grep -a "E2E INFINITE CANVAS" <log>`**（PASS 行が `→` を含む場合 ripgrep/Select-String は取りこぼす＝
  memory `unity-afk-probe-run`）。compile-only は `-executeMethod` を外した同コマンドで `grep -aE "error CS[0-9]+"` が 0 件。
- 運用の罠（findings 0055 で踏んだ 3 点を踏襲）: ①compile-only Unity の lock-abort race（次起動前に `Get-Process Unity` が空かを確認）、
  ②production `.cs` 編集直後の初回 AFK は recompile/domain-reload で `-executeMethod` がスキップされ得る（2 回目で実行）、
  ③flush race（shutdown sentinel `Found no leaked weakptr` / `Cleanup mono` を確認してから grep）。

## 改名の波及（旧名 `InfiniteCanvasProbe` の残存参照・オーレストレーターが現行化）

本サブエージェントは現行化しない（報告のみ）。`git grep InfiniteCanvasProbe` 相当で検出した残存:

- **active な cross-reference（現行化推奨）**: `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.cs:154`
  （`// ... exercised by InfiniteCanvasProbe Section7, not here.`）— 現行ゲート名を指す参照なので
  `InfiniteCanvasE2ERunner S7` に更新が望ましい。
- **historical findings（履歴として保持・falsify しない）**: `0006`（本体・RED→GREEN ログ）, `0007`, `0008`, `0010`,
  `0025`, `0027` が当時の `InfiniteCanvasProbe` を narrative として参照したまま。operational な再走コマンド／「現行ゲート」と
  読める箇所があれば forward-pointer 注記（本 findings 0056 で改名済みと記録）。`.claude/skills/tdd/SKILL.md`,
  `Assets/Scripts/InfiniteCanvas/InfiniteCanvasHitlHarness.cs` のコメントも要確認（散文言及は履歴として残してよい）。
- `E2E-INDEX.md` のロールアップ（CANVAS の E2E済/要昇格/要新規 カウント）も現行化対象。

## compile 未検証の明記

Unity を起動しない作業（オーソリングのみ）のため **本 `.cs` の compile は未検証**。型の整合は読解で確認済み:
`InfiniteCanvasInputSurface.Initialize/OnScroll`・`InfiniteCanvasController`・`CanvasView.MIN_ZOOM/MAX_ZOOM`・
`PointerEventData`（`position`/`scrollDelta` は public setter）は production と一致。懸念点として、batchmode で
`RectTransformUtility.ScreenPointToLocalPointInRectangle`（cam=null overlay）が S8 で finite 値を返すかは実走で要確認だが、
S8 の zoom 倍率 assert は cursor 値に非依存なので NaN が出ても pan のみに影響し zoom 判定は成立する見込み。最終的な
compile-only ゲート＋AFK RED→GREEN はオーケストレーターが直列で実施する。

## 検証完了（GREEN・オーケストレーター実施 2026-06-19）

§再走手順のコマンドを直列 AFK 実走し **GREEN を確定**（authoring の「compile 未検証 / AFK 未実施」を解消）:

- `-executeMethod InfiniteCanvasE2ERunner.Run` → 初回実行で `[E2E INFINITE CANVAS PASS] pan arithmetic + zoom
  clamp[0.2,5.0] + cursor-centred invariant ... + input-boundary scroll-tick clamp (raw wheel ~120 capped to 4
  notches, no zoom saturation) ...`／UNITY exit 0／shutdown sentinel `Found no leaked weakptrs.` 確認。
- compile 健全: `error CS\d+` 0 件（PASS 行が出た＝全 8 section コンパイル＋実行成功）。recompile-skip は出ず初回で実行。
- `.meta` GUID 保全確認: 新 `InfiniteCanvasE2ERunner.cs.meta` の GUID は旧 `Assets/Editor/InfiniteCanvasProbe.cs.meta`
  と一致（`81760220adf184cfdadbb366d2e3121d`）＝git mv で保全済み。
- rename diligence: §改名の波及で「現行化推奨」とした active stale cross-ref `FloatingWindowE2ERunner.cs:154`
  （`InfiniteCanvasProbe Section7`）を `InfiniteCanvasE2ERunner S7` に現行化済み。
- **RED litmus は未再実行**（§RED→GREEN の delete-the-logic 計画として文書化済み）。昇格元 `InfiniteCanvasProbe` が既に
  RED→GREEN 確立済みの稼働ゲートだったこと＋新規 S8 が先頭に liveness/vacuous-green-kill leg を持つことで非空虚性を担保。
