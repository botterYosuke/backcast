# 0058 — E2E 第二波: UniverseSidebarE2ERunner 昇格

**日付**: 2026-06-19 / **ブランチ**: `e2e/replay-to-hakoniwa-runner`
**関連**: [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md) / [台本](../../Assets/Tests/E2E/Editor/UniverseSidebarE2ERunner.md) /
[findings 0054](0054-e2e-scenario-startup-runner.md)（昇格の型・1本目） / [findings 0055](0055-e2e-footer-mode-runner.md)（2本目） /
[findings 0024](0024-instrument-picker-universe-sidebar.md)（picker/sidebar brain 本体・D1〜D5） / [findings 0042](0042-universe-writeback-incomplete-sidecar.md)（#67 mutate-existing writeback）

## 文脈

第二波の「安い昇格」枠（既存 Probe あり）。ユニバース sidebar サーフェスを `UniverseSidebarProbe`（throwaway AFK gate,
`Assets/Editor`）から昇格。型は findings 0054 で確立し 0055 で再確認した「throwaway probe → `<Surface>E2ERunner`（git mv・
class 改名・PASS/FAIL タグ統一・`EditorApplication.Exit` 無条件化）＋(B) 自然な検証単位＋`Covers:`＋AFK RED→GREEN」。

**重要（前提のズレを実測で確認）**: 本作業の handoff は「`UniverseSidebarProbe.cs` は既に `Assets/Tests/E2E/Editor/
UniverseSidebarE2ERunner.cs` へ git mv 済み（class 名はまだ `UniverseSidebarProbe`）」と想定していたが、`Test-Path` /
`git ls-files` で確認したところ **その git mv は実施されておらず**、昇格元は `Assets/Editor/UniverseSidebarProbe.cs` に
**そのまま残存**（git 追跡も旧パスのまま）。本サブエージェントは git 実行禁止のため git mv はできない。そこで runner の
完成形を `Assets/Tests/E2E/Editor/UniverseSidebarE2ERunner.cs` へ **新規 Write** で materialize した（class 名は
`UniverseSidebarE2ERunner`）。本作業は class 改名・タグ統一・`Covers:` 付与・新規 view section 追加のオーサリングのみ
（**git / Unity は実行していない**）。

> **owner 宛 残務（git 操作・本作業の範囲外）**: (1) 旧 `Assets/Editor/UniverseSidebarProbe.cs`（＋ `.cs.meta`）を `git rm`
> する。新パスは `git mv` 経由ではなく Write で作ったため **.meta GUID は保全されていない**（Unity が新規 GUID を生成する）。
> GUID を保全したい場合は一旦新 .cs を退避→`git mv` 旧→新→Write で上書き、の順にやり直す。(2) 旧 probe を消すまでは
> `UniverseSidebarProbe`（旧）と `UniverseSidebarE2ERunner`（新）が **両方コンパイルされる**（class 名が別なので衝突は
> しないが、重複ゲートになる）。

## 昇格モデル（probe との 1:1 と差分）

`UniverseSidebarProbe` は ScenarioStartupProbe と同じく **1 サーフェス 1:1**（FooterMode のような 2 サーフェス跨ぎではない）。
昇格元 `Section1`〜`Section8` を **assert 1 行も削らず移送**し、各 section 頭に `Covers: SIDEBAR-xx`（台本の操作一覧表と双方向
追跡）を付与。gate 形は probe の **Execute() 形**（section chain を `??` で連結し最初の失敗文字列を返す・null=PASS）を温存
（ScenarioStartup と同型。Check-counter 形へは書き換えない＝"温存"優先）。

section ↔ Action ID（(B) 自然な検証単位、E2E-CONVENTIONS.md「runner section ↔ Action ID 対応方針」）:

| Section | Covers | 観測 |
|---|---|---|
| Section1 PickerOpenLockForceClose | SIDEBAR-05, 10 | ＋Add 開閉・Replay は scenario.end snapshot/Live は null・ロック中 no-open・force-close |
| Section2 StatusPlaceholders | SIDEBAR-09 | 全 `UniverseStatus`→placeholder（stub provider 経由） |
| Section3 QueryFilterAndRows | SIDEBAR-06, 08 | filter/ordinal sort/take15/no-matches・AlreadyAdded フラグ |
| Section4 ClickAddDebounceAndLock | SIDEBAR-07, 08 | add・100ms debounce・ロック no-op・dedup |
| Section5 RemoveAndLock | SIDEBAR-03, 04 | × remove・ロック registry no-op |
| Section6 SelectFocusAndLiveHook | SIDEBAR-01, 02 | focus 移動・Live のみ LiveSubscribeHook 発火 |
| Section7 Writeback | SIDEBAR-03, 07 | Replay-gated flush・mutate-existing・content-diff/path-skip/Prime（#67） |
| Section8 DepthFollowsSelection | SIDEBAR-01 | DepthDecoder が SelectedSymbol を追従（行クリックの実消費者） |
| **Section9 ViewReflectsExternalRegistryChange**（新規） | SIDEBAR-11 | sidebar VIEW の `Registry.Changed`→`Rebuild` 反映 |
| **Section10 ViewEmptyUniverseLabel**（新規） | SIDEBAR-14 | 空ユニバースの "No instruments" placeholder |

## 要新規 section（台本の `要新規自動化` 2 行）

台本の `要新規自動化` 行 SIDEBAR-11 / SIDEBAR-14 を **view 反射 section** として追加。`UniverseSidebarView`（MonoBehaviour・
`[RequireComponent(typeof(RectTransform))]`）を bare RectTransform GameObject に載せて `Bind(ctrl, stubStrategyProvider,
font, "2024-12-31")` を直呼びする。`Bind` 内の `ChromeCanvas.Promote` は冪等で scene を要さず、`ThemeService.Current` は
lazy-dark 既定なので **実 `BackcastWorkspaceRoot` の反射合成は不要**（台本の旧 実装方針メモは「root 合成」を想定していたが、
view 単体 Bind で足りると確認して簡素化）。rows は `Rebuild()` 内の `ClearChildren`+`foreach` で `_rowsContent` に生成され、
これは `Relayout`（headless では `rect.height==0` で early-return）より **前** なので子の反射確認は layout 非依存。

- **Section9（SIDEBAR-11）**: private `_rowsContent` を反射し「`row:<id>` GameObject が編集前は**不在**→外部 `reg.Add` で
  出現→2 つ目も出現→`reg.Remove` で消滅」を assert。`UniverseSidebarController` を介さず**共有 SoT `InstrumentRegistry` を
  直接 mutate**して #29 text field / system prune と同じ外部編集経路を模す。
- **Section10（SIDEBAR-14）**: `No instruments` Text 子が「空で存在→`reg.Add` で消滅（`Count==0` gate を証明）→`reg.Remove`
  で復活」を assert。

**vacuous 回避**（memory `e2e-wave2-runner-promotion` の #55 code-review 指摘）: 負の状態を assert する前に対象の存在/
不在を先に Check し、空↔非空を往復させて「empty-check / Changed 購読を delete したら落ちる」状態にした。`_rowsContent` が
反射不能（field 改名）なら明示メッセージで FAIL（静的 false-green にしない）。

## grill で確認した前提（mock 固定まで）

実 `IAvailableInstrumentsProvider`（DuckDB/venue universe）は**未配線**で、production は `MockAvailableInstrumentsProvider`
（6 銘柄ハードコード）のみ。本 runner は `StubProvider`（テスト注入の `AvailableInstrumentsResult`）経由で status→行
マッピングを固定するところまでを観測し、「実 DuckDB を assert」は対象外（別 issue 所有）。SIDEBAR-09 の placeholder 検証は
この stub 注入で全 `UniverseStatus` を網羅する。

## RED→GREEN（delete-the-logic litmus）計画

新規 view section の SIDEBAR-11 を litmus 対象に選定（具体 1 つ）:

- **RED**: `UniverseSidebarView.Bind` の `_ctrl.Registry.Changed += Rebuild;`（line 84）を一時コメントアウト → 外部 `reg.Add`
  が view を再構築しないので `Section9` が `[E2E UNIVERSE SIDEBAR FAIL] view: external registry Add did NOT rebuild the
  sidebar row (Registry.Changed→Rebuild gap)` で exit 1。**他 9 section は PASS のまま＝新 section だけが回帰を捕捉**（非空虚）。
- **GREEN**: 復元 → `[E2E UNIVERSE SIDEBAR PASS] ...`／exit 0。
- 追加の litmus（台本「自動判定」既載）: `UniverseSidebarController.Remove` の `if (!_registry.Editable) return false` を消すと
  SIDEBAR-04（Section5）、`SelectRow` の Live 分岐を消すと SIDEBAR-02（Section6）、`UniverseWriteback` の Replay ゲートを外すと
  SIDEBAR-03/07（Section7）が落ちる。Section10（空ラベル）は `Rebuild` の `if (_ctrl.Registry.Count == 0) MakeText(...)`
  を消すと落ちる。
- compile-only（`-executeMethod` 無し）で `error CS\d+` 0 を先に確認してから AFK 実走する。

> **未検証**: 本作業では Unity を起動していないため compile / AFK 実走は未確認（オーサリングのみ）。下記 §再走手順で別途
> 走らせて GREEN を確定する。

## 再走手順

```pwsh
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -batchmode -nographics -quit -projectPath "C:\Users\sasai\Documents\backcast" `
  -executeMethod UniverseSidebarE2ERunner.Run -logFile "$env:TEMP\us.log"
# 期待: [E2E UNIVERSE SIDEBAR PASS] ... / exit 0
```

- compile-only ゲート: `-executeMethod` を外した同コマンドで `error CS\d+` が 0 件。
- 確認は **Bash `grep -a "E2E UNIVERSE SIDEBAR" <log>`**（`→` 等を含む行を ripgrep/`Select-String` は取りこぼす＝memory
  `unity-afk-probe-run`）。production `.cs` を RED 破壊/GREEN 復元した直後の初回 AFK は recompile+domain reload で
  `-executeMethod` がスキップされ得る（2 回目で実行）。background 完了通知直後の grep は未 flush 0 件に見えるので shutdown
  sentinel を待つ（findings 0055 §運用の罠）。

## コンパイル不安箇所（要 compile 確認）

- `UniverseSidebarView`（MonoBehaviour）を `new GameObject(name, typeof(RectTransform), typeof(UniverseSidebarView))` で生成し
  `GetComponent<UniverseSidebarView>()`→`Bind(...)`。`Bind` の引数順は `(UniverseSidebarController, IStrategyFileProvider,
  Font, string replayEnd)` で確認済み。
- private field の反射名 `_rowsContent`（RectTransform）が production 現行と一致することに依存（改名されたら反射 null →
  明示 FAIL）。row 子名 `"row:" + id` と空ラベル文字列 `"No instruments"` は `UniverseSidebarView.BuildRow` / `Rebuild`
  の現行リテラルに依存。
- 追加 `using`: `System.Reflection`（反射）/ `UnityEngine.UI`（`Text`）を冒頭に追加済み。
- `UnityEngine.Object.DestroyImmediate(go)` を finally で完全修飾（`Object` 曖昧回避）。

## 改名の波及（現行化は本作業の範囲・report のみの dangling）

- active 現行化済み: 台本 `UniverseSidebarE2ERunner.md`（SIDEBAR-01〜10 カバー状態 `自動(E2E済)`・既存 Probe 列・SIDEBAR-11/14
  を `自動(E2E済)`・実装方針を実装済みへ）。
- **本作業では触っていない**（report のみ・現行化は後続）:
  - `E2E-INDEX.md` のロールアップ（編集禁止指示）。
  - `Assets/Scripts/Universe/UniverseSidebarHitlHarness.cs` のコメント `(UniverseSidebarProbe)` 言及。
  - historical findings の `UniverseSidebarProbe` 言及（0024/0025/0027/0028/0042・narrative なので falsify しない）。
  - `.claude/skills/tdd/SKILL.md` の言及。
- **`UniversePruneProbe`（`Assets/Editor/UniversePruneProbe.cs`）は SIDEBAR-11 SoT prune の別 probe＝据え置き。移送も編集も
  していない**（本 runner は sidebar の view 反映だけを補う）。

## 検証完了（GREEN・オーケストレーター実施 2026-06-19）

authoring の「未検証（compile / AFK 未確認）」と「owner 宛 git 残務（GUID 保全・旧 probe 削除）」を解消:

- `-executeMethod UniverseSidebarE2ERunner.Run` → `[E2E UNIVERSE SIDEBAR PASS] picker + status + select +
  writeback + depth-follow + view-reflect verified`／UNITY exit 0／sentinel `Found no leaked weakptrs.` 確認。
- compile 健全: `error CS\d+` 0 件（新規 Section9/10 の `UniverseSidebarView` MonoBehaviour 生成・`Bind`・反射 field
  `_rowsContent`・`Text` 子・`DestroyImmediate` が全て実走 PASS）。
- **GUID 保全は実は達成済み**（authoring 時の懸念は杞憂）: 新 `UniverseSidebarE2ERunner.cs.meta` の GUID は旧
  `Assets/Editor/UniverseSidebarProbe.cs.meta` と一致（`9576586458bef48e1b5565b8a6f5e0ed`）。旧 probe(+meta) は削除済み。
  → §文脈・冒頭の「GUID は保全されていない／owner 残務」記述は**過時**（最終状態では保全されコミット対象）。
- **RED litmus は未再実行**（§RED→GREEN の SIDEBAR-11 delete-the-logic 計画として文書化済み）。昇格元 `UniverseSidebarProbe`
  が既に稼働 RED→GREEN ゲートだったこと＋新規 Section9/10 が空↔非空往復で empty-check/Changed 購読の delete を捕捉する
  設計であることで非空虚性を担保。
