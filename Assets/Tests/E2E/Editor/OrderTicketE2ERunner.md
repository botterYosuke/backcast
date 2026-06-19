# OrderTicketE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`OrderTicketE2ERunner.cs`（第二波で実装）が自動検証する **注文チケット サーフェス**の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**この サーフェスでユーザーができる行動すべての網羅台帳と、
E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 注文チケットは入力フォーム＋発注経路なので、本 Surface 台本は「フォーム操作 → 検証ゲート → host RPC（lane）
> 呼び出し・status 反映」までを観測する。**実 venue での約定**（注文が実取引所に通って FILLED になる）は外部認証・
> 実資金依存なので *HITL*（`ORDER-16`）。lane 越しの place→fill / serialization の機構自体は `WorkspaceLiveSeamProbe`
> `VenueLoginSecretProbe` が別途証明済みで、本台本はその手前の **view 側フォーム＋検証ゲート**を担う。

## 対象サーフェス

手動 Order ticket（`OrderTicketView` ＋ 発注の頭脳 `BackcastWorkspaceRoot.OnManualPlace/OnManualCancel`）。
`KIND_ORDER` フローティング窓に adopt され（findings 0025 §8 / 0014 RH4・破棄＋再生成しない）、**footer mode が
LiveManual のときだけ可視**（`DriveOrderTicket`）。view は FORM ウィジェットだけを持ち、`PlaceRequested` /
`CancelRequested` を上げるのみ——**検証も RPC も行わない**。qty/price 検証と lane への marshalling は root が持つ
（footer/menu View が logic-free で root が host seam を持つのと同じ分業・`OrderTicketView.cs:7-11`）。

## 対象ユーザー行動

BUY/SELL トグル、MARKET/LIMIT トグル（price 行の表示/非表示）、Qty 編集、Limit price 編集、Place クリック
（qty/price 検証 → `SubmitPlaceOrder` RPC）、Cancel last クリック（`SubmitCancelOrder` RPC・取消受付）。
instrument 表示（sidebar `SelectedSymbol` 同期）・status 表示・interactable ゲート・窓可視性は入力のない
**表示/状態**だが、行動の観測点として台帳に載せる。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| ORDER-01 | BUY/SELL トグル | `OrderTicketView.cs:44` | `_sideBuy` が反転、ボタン文字が `BUY`⇄`SELL`、`SideBuy` prop が追従 | view を Build → `_sideBtn.onClick` 駆動 → `SideBuy` を反射 assert | 要新規自動化 | — |
| ORDER-02 | MARKET/LIMIT トグル（price 行 表示/非表示） | `OrderTicketView.cs:49,53` | `_limit` が反転、`_priceRow.SetActive(_limit)`、`Limit` prop 追従、文字 `MARKET`⇄`LIMIT` | `_typeBtn.onClick` 駆動 → `Limit` ＋ price 行 activeSelf を assert | 要新規自動化 | — |
| ORDER-03 | Qty 編集 | `OrderTicketView.cs:57` | `Qty` prop が field text を返す（read-only 公開） | field.text を設定 → `Qty` を assert | 要新規自動化 | — |
| ORDER-04 | Limit price 編集 | `OrderTicketView.cs:61` | `Price` prop が field text を返す | LIMIT 時に price field.text 設定 → `Price` を assert | 要新規自動化 | — |
| ORDER-05 | Place（妥当 → 発注） | `BackcastWorkspaceRoot.cs:1150,1171` | `OnManualPlace` が side/qty/price/type を組み `SubmitPlaceOrder(venue,iid,…,"DAY")`、status="placing …"、result 後 `_manualOrderId`／status 更新 | MOCK lane（`InitializePython("MOCK")`）で `PlaceRequested` 発火 → ACK/status を assert | 要新規自動化 | `WorkspaceLiveSeamProbe`（lane place→fill のみ） |
| ORDER-06 | Place 拒否（qty 不正） | `BackcastWorkspaceRoot.cs:1155` | `double.TryParse(InvariantCulture)` 失敗 or `qty<=0` → status="invalid qty"、**RPC 発火せず** | qty="0"/"abc" で place → status＝"invalid qty" ＋ lane 未呼び出しを assert | 要新規自動化 | — |
| ORDER-07 | Place 拒否（LIMIT 価格不正） | `BackcastWorkspaceRoot.cs:1160` | LIMIT かつ price parse 失敗 or `p<=0` → status="invalid limit price"、RPC なし | LIMIT＋price="" で place → status を assert | 要新規自動化 | — |
| ORDER-08 | Place 拒否（未接続） | `BackcastWorkspaceRoot.cs:1164` | `!ServerReady‖!Conn.IsConnected‖Lanes==null` → status="connect a venue first" | 未接続 root で place → status を assert | 要新規自動化 | — |
| ORDER-09 | Place 拒否（instrument 未解決） | `BackcastWorkspaceRoot.cs:1167,1200` | `ManualInstrument()` が空 → status="select an instrument …"、任意銘柄へ流さない（live-order safety） | universe 空＋未選択で place → status を assert | 要新規自動化 | — |
| ORDER-10 | Cancel last（取消） | `BackcastWorkspaceRoot.cs:1180,1188` | oid = `_manualOrderId` ∨ `Panel.LatestOrder.OrderId`、`SubmitCancelOrder(venue,oid)`、status="cancel …"、結果は `PENDING_CANCEL`＝取消受付（終端 `CANCELED` は poll 後追い） | MOCK lane で place→cancel → oid 解決＋ACK を assert | 要新規自動化 | `VenueLoginSecretProbe`（lane serialization のみ） |
| ORDER-11 | Cancel 拒否（対象なし／未接続） | `BackcastWorkspaceRoot.cs:1182,1185` | `Lanes==null`→"not connected"、oid 解決不能→"no order to cancel"、RPC なし | order 無しで cancel → status を assert | 要新規自動化 | — |
| ORDER-12 | instrument 表示（sidebar 同期） | `BackcastWorkspaceRoot.cs:1141,1200` | `ManualInstrument()` = `_footerSelected`（共有 SelectedSymbol）∨ `Universe.Ids[0]`、`SetInstrument` が "instrument: {iid}" を表示・unchanged はスキップ | sidebar 選択を変えて `DriveOrderTicket` → 表示文字列を assert | 要新規自動化 | — |
| ORDER-13 | interactable ゲート | `OrderTicketView.cs:82`／`BackcastWorkspaceRoot.cs:1142` | `SetInteractable(ServerReady && Conn.IsConnected && !TeardownComplete)` で Place/Cancel ボタンの `interactable` が追従 | 接続フラグを切替えて `DriveOrderTicket` → ボタン enabled を assert | 要新規自動化 | — |
| ORDER-14 | チケット可視性（LiveManual のみ） | `BackcastWorkspaceRoot.cs:1139` | `_orderWindow.activeSelf` が `footerMode==LiveManual` に一致（Replay/LiveAuto では非表示） | footer mode を切替えて `DriveOrderTicket` → window activeSelf を assert | 要新規自動化 | — |
| ORDER-15 | status 表示（worker→main 反映） | `OrderTicketView.cs:88`／`BackcastWorkspaceRoot.cs:1143` | result callback が `_manualStatusLine`＋`_manualStatusDirty` に積み、`DriveOrderTicket` が main で `SetStatus` → "last order: …" | dirty フラグ経由の status marshal を assert | 要新規自動化 | — |
| ORDER-16 | Place（実 venue 約定） | `BackcastWorkspaceRoot.cs:1171` | 実 kabu/立花へ `place_order`、約定が FILLED、status に実 OrderId | — | HITL専用（実 venue 接続・外部認証/秘密情報依存・実資金） | `VenueLoginSecretHitlMenu` |

> instrument/status/可視性/interactable（ORDER-12〜15）は入力の無い**表示/状態**だが、フォーム行動が正しい
> host 呼び出し・状態遷移を起こすかの観測点として台帳に載せる（MenuBar の badge 行と同じ扱い）。

## 観測点（詳細）

- **ORDER-05/06/07（発注検証ゲート）**: `OnManualPlace` は `NumberStyles.Float, CultureInfo.InvariantCulture` で
  qty/price を parse する（マシン locale に依らず `.` を小数点とみなす wire 規約・`BackcastWorkspaceRoot.cs:1153`）。
  delete-the-logic litmus の中核——qty<=0 / LIMIT price<=0 / parse 失敗のいずれの分岐を消しても、対応する status
  と「RPC が発火しない」観測が必ず落ちること。invariant-culture を `CultureInfo.CurrentCulture` に変えると、
  `,` 小数点 locale で誤 parse する回帰を捕える（"1,5" を別解釈しない）。
- **ORDER-09（instrument 未解決の拒否）**: live-order safety——銘柄が解決できないとき既定値に流さず**拒否**する
  （`ManualInstrument()` の空文字 → "select an instrument …"）。`Universe.Ids[0]` フォールバックと
  `_footerSelected` 優先の両経路を観測する。
- **ORDER-10（取消の受付/確定）**: `SubmitCancelOrder` の ACK は **取消受付**（`PENDING_CANCEL`）であって終端では
  ない（CONTEXT.md「取消受付 / 取消確定」）。本 Surface 台本は「cancel が正しい oid で lane に渡り受付 status が
  返る」までを観測し、`PENDING_CANCEL→CANCELED` の poll 後追い終端は host/lane 側の責務とする。
- **ORDER-16（実約定）**: place が実取引所に通る経路は HITL。MOCK lane では ACK/FILLED を機構的に確認できるが、
  実 venue 約定・実資金は HITL ハーネス（`VenueLoginSecretHitlMenu` の実 venue 経路）で記録する。

## 自動判定（合格条件）

- ログに `[E2E ORDER TICKET PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `要新規自動化` 行の観測点を 1 つでも落としたら `[E2E ORDER TICKET FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `OnManualPlace` の qty/price 検証分岐・`ManualInstrument` の空拒否・
  `DriveOrderTicket` の `liveManual` 可視性ゲート・`SetInteractable` の gate を消すと、対応 assert が必ず落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `WorkspaceLiveSeamProbe` | batchmode・pythonnet・root 合成 | **lane の** place→fill 機構の証明。本台本の view 側フォーム＋検証ゲートは**未カバー**——ORDER-05/06/07/09 を新規に書く |
| `VenueLoginSecretProbe` | batchmode・pythonnet・3 lane | **lane の** place serialization / SubmitCancel / secret 連動の証明。view の Cancel 経路（oid 解決・拒否）は ORDER-10/11 で新規 |
| `VenueLoginSecretHitlMenu` | HITL ハーネス | ORDER-16（実 venue 約定）の記録用に**探索 Probe として残す** |

> 注: `OrderTicketView` 単体および `OnManualPlace/OnManualCancel` の view 側検証ゲートに対応する Probe は**存在しない**
> （既存 Probe は lane 機構を直接叩く）。ORDER-01〜04・06〜15 は新規自動化が必要。

## 将来の `OrderTicketE2ERunner.cs` 実装方針（第二波）

- `MenuBarCutoverProbe`/`WorkspaceDepthLadderProbe` と同型に **実 `BackcastWorkspaceRoot` を反射合成**
  （`OpenScene` → `SetSynthesizer(FakeMarimoSynthesizer)` → `ResolvePaths` → `BuildWorkspace`）。Python-FREE を
  既定とし、フォーム操作・検証ゲート（ORDER-01〜04・06〜09・11〜15）は `_orderTicket` / private seam の反射で
  完結する（lane 不要）。
- MOCK 接続を要する ORDER-05/10（実 ACK/status）のみ `host.InitializePython("MOCK")` を**直呼び**（batchmode の
  所有権スキップを迂回する正当手・`ReplayToHakoniwaE2ERunner` と同型）。teardown は `host?.Stop()`（MOCK を
  起こした場合のみ）。
- 発注は view の `PlaceRequested`/`CancelRequested` を発火させる（`_placeBtn.onClick`/`_cancelBtn.onClick` を
  反射 invoke、または event を直接 raise）。private 検証・状態は `OnManualPlace`/`ManualInstrument`/`DriveOrderTicket`
  を反射 invoke し、`_manualStatusLine`/`_manualOrderId`/`_orderWindow.activeSelf` を反射確認。
- セクション構成は操作一覧表の `要新規自動化` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す
  `Execute()`（null=PASS）パターン。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod OrderTicketE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**。
