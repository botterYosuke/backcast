# findings 0062 — E2E 第二波9本目: SecretModalE2ERunner 昇格

**日付**: 2026-06-19
**issue**: #94（E2E 第二波 runner 昇格トラッカー）
**対象サーフェス**: venue ログイン第二暗証 modal（`SecretModalController` の平文ライフタイム契約・findings 0012 D5）
**台本**: `Assets/Tests/E2E/Editor/SecretModalE2ERunner.md`

## やったこと

throwaway AFK probe `Assets/Editor/SecretModalM2Probe.cs`（issue #21 M2 focused gate）を
`Assets/Tests/E2E/Editor/SecretModalE2ERunner.cs` へ **git mv＋改名**で昇格（ADR-0015 命名規約。
先例 ScenarioStartup=0054 / FooterMode=0055 / InfiniteCanvas=0056 / FloatingWindow=0057 / UniverseSidebar=0058 /
DepthLadder=0059 / Hakoniwa=0060 / StrategyEditorNotebook=0061）。`.cs.meta` も git mv で GUID
（80d819f53a72484da3b21733494d0c20）保全。

- クラス `SecretModalM2Probe` → `SecretModalE2ERunner`、`-executeMethod` 名も同改名。
- PASS/FAIL タグ: `[SECRET MODAL M2 PASS/FAIL]` → `[E2E SECRET MODAL PASS/FAIL]`（台本 .md の自動判定に一致）。
- 5 section（KeyboardDrainAndMask / SubmitHandsPayloadAndZeroizes / CancelZeroizes / AbsoluteTimeoutFiresBefore30s /
  ContainmentInvariant）を **assert 1 行も削らず verbatim 移送**。各 section に台本 Action ID を `Covers:` で付与。
- gate 形は probe の Check-counter 形（`_fail` 累積→Exit）を温存。`EditorApplication.Exit` は self-failing gate として
  無条件（PASS=Exit(0) / FAIL・例外=Exit(1)。元々無条件のため温存）。pure-logic（`SecretModalController` を直接 new し
  `nowSeconds` 注入）＝Python/venue/pythonnet 不要。

## Covers マッピング（section ↔ Action ID）

| Section | Covers | 備考 |
|---|---|---|
| KeyboardDrainAndMask | SECRET-01,02,06 | 1文字 char[] 入力 / backspace / masked dot-count |
| SubmitHandsPayloadAndZeroizes | SECRET-03,10 | submit→one-shot char[]＋自バッファ zeroize / BufferIsZeroed no-leak audit |
| CancelZeroizes | SECRET-04,10 | cancel/close zeroize＋RequestId クリア / BufferIsZeroed |
| AbsoluteTimeoutFiresBefore30s | SECRET-05 | 25s 絶対タイムアウト・idle 非延長・閉じた modal で再発火なし |
| ContainmentInvariant | SECRET-11 | 25s<30s<40s（modal が backend より先に閉じ zeroize） |

## 据え置き / 仕分け

- **SECRET-07（focus drop）/ SECRET-08（open gate）/ SECRET-09（open-time id バインド）= 要新規自動化のまま据え置き**。
  実 `BackcastWorkspaceRoot` を反射合成（OpenScene→ResolvePaths→BuildWorkspace）して `DriveSecretModal`/`SubmitSecret`/
  `CancelSecret` を反射駆動する harness を要し、pure-logic probe の verbatim 移送に収まらない。「安い昇格」方針に沿い
  本昇格では追加しない（StrategyEditorNotebook の STRATEGY-11 と同方針）。将来 root harness slice で昇格。
- **SECRET-03（lane roundtrip）/ SECRET-10（wire no-leak）の別レグ** = `VenueLoginSecretProbe`（pythonnet・mock venue）が
  正本のまま据え置き。本 runner は controller leg（submit→zeroize / BufferIsZeroed audit）を昇格。
- **SECRET-12（実 venue 認証）/ SECRET-13（実キーボード device drain）** = HITL専用（`VenueLoginSecretHitlMenu` が記録）。

## カバー状態 rollup の変化

`SECRET-01..13`（E2E-INDEX）: `13 | 0 | 7 | 4 | 2 | 0` → `13 | 8 | 0 | 3 | 2 | 0`。
（自動E2E済 0→8: SECRET-01/02/03/04/05/06/10/11 昇格。自動(Probe有・要昇格) 列は台本正本では 8 行だったが INDEX 旧値が
7 と drift していたため、昇格に合わせて 0 へ整合。要新規 4→3: 台本正本の SECRET-07/08/09＝3 行へ整合。）

## 参照更新

- `Assets/Tests/E2E/Editor/SecretModalE2ERunner.md`: 操作一覧表のカバー状態 `自動(Probe有・要昇格)`→`自動(E2E済)`＋
  既存Probe列を `SecretModalE2ERunner`(section) へ（SECRET-01/02/03/04/05/06/10/11）。既存Probe対応表・将来実装方針節も現行化。
- `Assets/Tests/E2E/Editor/TachibanaLiveE2ERunner.md`: 「`SecretModalM2Probe` が unit でカバー」→ `SecretModalE2ERunner`（旧名併記）。
- `Assets/Tests/E2E/Editor/E2E-INDEX.md`: rollup（✅・件数 `13 | 8 | 0 | 3 | 2 | 0`）＋prose（9本目追記・残り未昇格から SecretModal を除去）。
- 旧 findings（0012/0014）は append-only 履歴のため改変せず（`SecretModalM2Probe` の歴史的言及は温存）。本 findings が改名を記録。

## 検証

- compile-only ゲート: `<Unity> -batchmode -nographics -quit -projectPath . -logFile <log>` で `error CS\d+` **0 件**・
  `Exiting batchmode successfully` / return code 0（2026-06-19 lead 実行・確定）。
- AFK GREEN: 上記＋`-executeMethod SecretModalE2ERunner.Run`。`[E2E SECRET MODAL PASS]`（SECRET-01/02/03/04/05/06/
  10/11）を bash `grep -a` で **1 件確認**・FAIL タグ 0 件・sentinel（`Found no leaked weakptrs`）あり＝executeMethod 実走・
  exit 0（2026-06-19 lead 直列 AFK 実行・GREEN 確定）。
- vacuity: verbatim 移送のため新規 vacuous section 無し＝RED litmus 不要。delete-the-logic litmus（台本 §自動判定）:
  `ZeroBuffer`/`CloseAndZero` を no-op 化 → SECRET-03/04/05/10 の `BufferIsZeroed()` assert RED / `TickExpire` の絶対
  25s 比較を撤去 → SECRET-05 RED / `Submit` の空チェック撤去 → empty-submit null assert RED。
