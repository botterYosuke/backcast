# SecretModalE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`SecretModalE2ERunner.cs`（第二波で実装）が自動検証する **venue ログイン第二暗証 modal サーフェス**の台本。
実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、**この サーフェスでユーザーができる行動すべての
網羅台帳と、E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の
共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*。バッファ操作・mask・zeroize・25s タイムアウト・focus drop は
> 純ロジック（`SecretModalController` は `nowSeconds` を渡す pure logic）なので**自動可**。**実 venue の第二暗証
> 認証**（実暗証を打ち込み `submit_secret` が実取引所に通って FILLED）は外部認証・秘密情報依存なので *HITL*
> （`SECRET-12`）。lane 越しの secret roundtrip / SECRET_TIMEOUT / no-leak の機構自体は `VenueLoginSecretProbe`
> が mock venue で証明済みで、本台本はその手前の **controller の平文ライフタイム契約**を担う。
> **不可侵**: 平文を env/log/managed string に残さない（findings 0012 D5）。

## 対象サーフェス

第二暗証 modal（頭脳 `SecretModalController` ＋ chrome `SecretModalOverlay` ＋ root 連携 `BackcastWorkspaceRoot.
DriveSecretModal/SubmitSecret/CancelSecret`）。controller は tachibana 第二パスワードの**平文ライフタイムを所有**
（findings 0012 D5）——Python の SecretVault TTL ではなく C# 側が submit 前の平文を持つ。overlay は ScreenSpaceOverlay
の最前面 chrome（sort 1000）で、New Input System `Keyboard.current.onTextInput` から 1 文字ずつ drain する
（`Input.inputString` や `InputField` は使わない・平文を managed string にしない）。

## 対象ユーザー行動

1 文字ずつ入力（`AppendChar`・char[] バッファ）、backspace、submit（urgent-secret lane へ char[] を渡し zeroize）、
cancel/close（zeroize）。加えて 25s 絶対タイムアウト（zeroize＋notice）、modal open 時の focus drop、masked 表示、
SecretRequired での open、open 時の request id バインドは入力起点ではないが状態遷移として台帳に載せる。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 / 状態遷移 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| SECRET-01 | 1 文字ずつ入力（char[]・managed string 不使用） | `SecretModalController.cs:61`／`SecretModalOverlay.cs:114`／`BackcastWorkspaceRoot.cs:1235` | `AppendChar(c)` が `_buf[_len++]`、`'\0'`/control char 無視、`MaxLen=64` cap、`Length` 増加 | open → `AppendChar` 連打 → `Length`/`MaskedDisplay` を assert | 自動(E2E済) | `SecretModalE2ERunner`(KeyboardDrainAndMask) |
| SECRET-02 | backspace（1 文字削除） | `SecretModalController.cs:74`／`SecretModalOverlay.cs:91` | `Backspace()` が `_buf[--_len]='\0'`、空なら no-op | 入力後 `Backspace` → `Length` 減・末尾 zero を assert | 自動(E2E済) | `SecretModalE2ERunner`(KeyboardDrainAndMask) |
| SECRET-03 | submit（lane へ char[]・自バッファ zeroize） | `SecretModalController.cs:93`／`BackcastWorkspaceRoot.cs:1238` | `Submit()` が one-shot `char[]` を返し `CloseAndZero`、空なら null、`SubmitSecret` が **open 時の reqId** で `SubmitSecret(reqId,payload)`、lane 不在なら `Array.Clear` | type→Submit → payload 内容＋`BufferIsZeroed()`＋`!IsOpen` を assert | 自動(E2E済) | `SecretModalE2ERunner`(SubmitHandsPayloadAndZeroizes・controller leg)／`VenueLoginSecretProbe`（lane roundtrip・据え置き） |
| SECRET-04 | cancel/close（zeroize） | `SecretModalController.cs:103`／`BackcastWorkspaceRoot.cs:1254` | `Cancel()` が `CloseAndZero`、`CancelCount++`、`RequestId=null`、`Coord.SetSecretModalOpen(false)` | type→Cancel → `BufferIsZeroed()`＋`!IsOpen`＋id クリアを assert | 自動(E2E済) | `SecretModalE2ERunner`(CancelZeroizes) |
| SECRET-05 | 25s 絶対タイムアウト（zeroize＋notice） | `SecretModalController.cs:81`／`BackcastWorkspaceRoot.cs:1221` | `TickExpire` が open+25s で発火（idle 延長しない・絶対）、`TimedOut`＋zeroize、root が menu notice 表示、閉じた modal で再発火しない | open(t0)→type→`TickExpire(t0+24.9)`=false／`(t0+25)`=true を assert | 自動(E2E済) | `SecretModalE2ERunner`(AbsoluteTimeoutFiresBefore30s) |
| SECRET-06 | masked 表示（•••・平文を出さない） | `SecretModalController.cs:46`／`SecretModalOverlay.cs:82`／`BackcastWorkspaceRoot.cs:1231` | `MaskedDisplay` = `_len` 個の `•` のみ（平文を返さない）、overlay は "secret: ••••"、char 数だけ追従 | 入力長＝dot 数、`MaskedDisplay` に平文が混じらないことを assert | 自動(E2E済) | `SecretModalE2ERunner`(KeyboardDrainAndMask) |
| SECRET-07 | modal open 時の focus drop | `BackcastWorkspaceRoot.cs:1219` | open 直後 `EventSystem.SetSelectedGameObject(null)`——order qty / strategy editor の InputField から focus を外し device 入力が `.text` に二重着弾しない | InputField を選択 → `DriveSecretModal(newSecret=true)` → `EventSystem.current.currentSelectedGameObject==null` を assert | 要新規自動化 | — |
| SECRET-08 | SecretRequired で open（gate 連動） | `BackcastWorkspaceRoot.cs:1214,1225` | `newSecret && !IsOpen` → `Open(LatestSecretRequired, now)`、`Coord.SetSecretModalOpen(true)`（待機中は logout 抑止） | `DriveSecretModal(true)` → `IsOpen` ＋ coord gate を assert | 要新規自動化 | `VenueLoginSecretProbe`（logout gate） |
| SECRET-09 | submit は open 時の request id にバインド | `BackcastWorkspaceRoot.cs:1241-1243` | 入力中に新 SecretRequired が来ても、submit は modal が **開いた時の `RequestId`** に対して行う（誤 id への誤 submit 防止） | open(reqA)→type→`LatestSecretRequired`=reqB に差替→Submit → reqA で送るを assert | 要新規自動化 | — |
| SECRET-10 | 平文の非漏洩（managed string/log/env に残さない） | `SecretModalController.cs:70,112`／`VenueLoginSecretProbe.cs:107,156` | `AppendInput(string)` entry point が**無い**（char[] のみ）、`BufferIsZeroed()` audit、drain した wire event に平文が出ない | submit/cancel/timeout 後 `BufferIsZeroed()`＋wire no-leak を assert | 自動(E2E済) | `SecretModalE2ERunner`（BufferIsZeroed leg: Submit/Cancel/Timeout）／`VenueLoginSecretProbe`（wire no-leak・据え置き） |
| SECRET-11 | containment invariant 25s < 30s < 40s | `SecretModalController.cs:26` | modal 絶対 25s < backend secret wait 30s < order-write 40s（modal が先に閉じて zeroize） | `AbsoluteTimeoutSeconds < 30` を assert | 自動(E2E済) | `SecretModalE2ERunner`(ContainmentInvariant) |
| SECRET-12 | 実 venue 第二暗証認証 | `BackcastWorkspaceRoot.cs:1250`（実 venue 経路） | 実暗証を打ち込み urgent-secret lane で `submit_secret` → 実 place が FILLED、modal が閉じ平文 zeroize | — | HITL専用（実 venue 接続・外部認証/秘密情報依存） | `VenueLoginSecretHitlMenu` |
| SECRET-13 | onTextInput subscribe/unsubscribe 安全 | `SecretModalOverlay.cs:94,103` | visible 中のみ hook した**同一 device** から detach（device 再列挙でも漏らさない）、Hide/OnDisable/OnDestroy で unsubscribe、control char は転送しない | — | HITL専用（実キーボードデバイス・New Input System 依存・headless では device 無し） | `VenueLoginSecretHitlMenu` |

> SECRET-05/08/11 は入力起点でない**状態遷移**だが、modal のライフサイクル不変条件として台帳に載せる。

## 観測点（詳細）

- **SECRET-01/03/10（平文ライフタイム D5）**: 平文は zeroable な `char[] _buf` だけに存在し、**managed string を
  決して経由しない**（`AppendInput(string)` は故意に未実装・`SecretModalController.cs:69-72`）。`Submit()` は
  one-shot payload を urgent-secret lane に渡して自バッファを zeroize し、lane 側が `Array.Clear(payload)` する
  契約（lane 不在時は root が clear・`BackcastWorkspaceRoot.cs:1251`）。唯一不可避の平文は call site の pythonnet
  string 引数のみ（field/log/view-model に保持しない）。delete-the-logic litmus: `ZeroBuffer`/`CloseAndZero` を
  no-op にすると `BufferIsZeroed()` assert が落ちること。
- **SECRET-05/11（タイムアウト・containment）**: 25s は **open からの絶対**タイムアウトで idle 延長しない
  （type しても open+25s で失効）。`25s < 30s（backend secret wait） < 40s（order write）` を保ち、modal が
  backend より先に閉じて zeroize する。
- **SECRET-07/09（focus drop・id バインド）**: open 時に `EventSystem.SetSelectedGameObject(null)` で focused
  InputField を外し device-level keystroke が `.text` に二重着弾するのを防ぐ。submit は `LatestSecretRequired`
  ではなく **modal が開いた時の `RequestId`** に対して行う——入力中に来た 2 つ目の SecretRequired で誤 id へ
  submit しない。
- **SECRET-12/13（HITL）**: 実暗証入力・実 venue 認証、および New Input System の実キーボード device drain は
  HITL（headless batchmode に keyboard device が無く `Subscribe()` は no-op・`SecretModalOverlay.cs:97-98`）。
  lane roundtrip 機構そのものは `VenueLoginSecretProbe` が SecretMockAdapter で証明済み。

## 自動判定（合格条件）

- ログに `[E2E SECRET MODAL PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)`/`要新規自動化` 行の観測点を 1 つでも落としたら `[E2E SECRET MODAL FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `ZeroBuffer`/`CloseAndZero`・`TickExpire` の絶対 25s 比較・`SubmitSecret` の
  open-time reqId 採用・`DriveSecretModal` の focus drop を消すと、対応 assert が必ず落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `SecretModalE2ERunner`（旧 `SecretModalM2Probe`） | EditMode・pure C#（time 注入） | **SECRET-01/02/04/05/06/11＋SECRET-03/10 の controller leg の正本**。keyboard-drain char[] / mask / 25s 絶対 / zeroize / containment を E2ERunner へ昇格済（git mv＋改名・findings 0062） |
| `VenueLoginSecretProbe` | batchmode・pythonnet・3 lane | SECRET-03（lane roundtrip）/SECRET-08（logout gate）/SECRET-10（no-leak）の昇格元。secret→submit_secret→FILLED と SECRET_TIMEOUT を mock venue で証明 |
| `VenueLoginSecretHitlMenu` | HITL ハーネス（playmode・OnGUI） | SECRET-12（実 venue 認証）/SECRET-13（実キーボード drain）の記録用に**探索 Probe として残す** |
| `VenueMenuM3Probe` | venue 接続（mock） | secret modal を起こす connect の上流。modal 詳細は本台本、connect は `MenuBarE2ERunner` MENU-11/14 |

> root 連携（SECRET-07 focus drop・SECRET-08 open gate・SECRET-09 open-time id バインド）は既存 Probe が
> `BackcastWorkspaceRoot` 経由で直接 assert していない——新規に書く。

## 将来の `SecretModalE2ERunner.cs` 実装方針（第二波）

> **実装済み（第二波9本目・findings 0062）**: `SecretModalE2ERunner`（旧 `SecretModalM2Probe`）を git mv＋改名で昇格。
> controller 単体の不変条件（SECRET-01〜06/10/11）= 5 section を assert verbatim 移送。root 連携 SECRET-07/08/09 は
> 要新規自動化 のまま据え置き（実 BackcastWorkspaceRoot 反射 harness を要する）。

- controller 単体の不変条件（SECRET-01〜06/10/11）は `SecretModalE2ERunner`（旧 `SecretModalM2Probe`）が昇格済——`SecretModalController`
  を直接 new し `nowSeconds` を注入する pure-logic セクション（root 合成も Python も不要）。`BufferIsZeroed()` /
  `MaskedDisplay` / `SubmitCount`/`CancelCount`/`TimeoutCount` の audit seam で assert。
- root 連携（SECRET-07/08/09）は **実 `BackcastWorkspaceRoot` を反射合成**（`OpenScene` → `ResolvePaths` →
  `BuildWorkspace`）し、`_host.Panel.LatestSecretRequired` に SecretRequired を仕込んで `DriveSecretModal`/
  `SubmitSecret`/`CancelSecret` を反射 invoke。`EventSystem.current.currentSelectedGameObject` と
  `_host.Modal.RequestId` を反射確認（Python-FREE で完結・lane は不要、SubmitSecret は lane 不在分岐で `Array.Clear`）。
- SECRET-12/13 は HITL のため runner は載せない（`VenueLoginSecretHitlMenu` が記録）。
- セクション構成は操作一覧表の `自動(*)`/`要新規自動化` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを
  返す `Execute()`（null=PASS）パターン。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod SecretModalE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**。**平文を log/env に出さない**——assert メッセージにも平文を含めない（mask 数のみ）。
