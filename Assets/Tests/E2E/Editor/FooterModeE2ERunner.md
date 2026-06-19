# FooterModeE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`FooterModeE2ERunner.cs`（第二波 2本目・実装済み・35 Check AFK GREEN）が自動検証する **ワークスペース footer（実行モード）サーフェス**の台本。
実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、**この サーフェスでユーザーができる行動すべての
網羅台帳と、E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通
規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（footer 上でユーザーができる mode 切替を網羅する回帰ゲート）。
> 実 venue 接続 → 実 engine round-trip（register→start→fill→teardown）をまたぐ実ストーリーは *Journey E2E* /
> `ProductionLiveShell` の MOCK/HITL が担う。本 Surface 台本は「セグメント押下が正しい `FooterModeRequest`・lock・
> poll 反映・auto-replay 判定を起こすか」までを観測する（host が叩く `set_execution_mode` / `stop_live_strategy` の
> 実 RPC は対象外、判定の入口まで）。

## 対象サーフェス

画面固定 footer の実行モードセグメント（`WorkspaceFooterView` ＋ 頭脳 `FooterModeViewModel`、findings 0025 §3・
#76 S6b-β-clean U4）。Replay / Manual / Auto の 3 セグメント＋ status line。ExecutionMode は engine 正準の
**`Replay` / `LiveManual` / `LiveAuto`** の 3 値（口語 "Manual"＝`LiveManual` 実発注・手動、"Auto"＝`LiveAuto`
実発注・自律＝本丸）。footer は transport（▶/⏸/step/speed）を持たず（#76 で退役）、mode 切替のみ。**重要不変条件**:
Live セグメントは venue が live（`CONNECTED`/`SUBSCRIBED`/`RECONNECTING` を `VenueLive`、precondition は
`CONNECTED`/`SUBSCRIBED`）のときだけ可視・送信可（`ModeManager` が他を `EXECUTION_MODE_PRECONDITION` で拒否）。
LiveAuto run 稼働中に離脱するときは footer が先に `stop_live_strategy(run_id)` を呼び、成功時のみ mode 切替（D2・
`set_execution_mode` は run を止めない＝findings 0017）。VM は**判定のみ**で Python に触れない（host が GIL 越しに
marshal）。

## 対象ユーザー行動

Replay セグメント押下（即時切替）、Manual/Auto 押下（lock ＋ poll/ack 待ち）、venue 未接続で Live 押下（observable
no-op）、LiveAuto run 稼働中の離脱（stop-then-switch）、同一 mode 再選択（no-op）。poll による表示上書き・venue drop
時の auto-replay・engine 拒否時の lock 解放は host 駆動だが footer の不変条件なので行として載せる。実 venue への実 Live
切替（実 engine RPC）は HITL。status line は read-only 観測点。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| FOOTER-01 | Replay セグメント押下（即時切替 D1） | `WorkspaceFooterView.cs:57,70`→`FooterModeViewModel.cs:125` | `RequestMode("Replay")`→`SwitchImmediate`、`DisplayMode` を即 flip・`Locked=false`（engine は拒否不能の D1 単一逸脱）。host は `SetExecutionMode(Replay)` 送信 | `FooterModeE2ERunner`（m3）を昇格、Kind＋即時 flip＋非ロックを assert | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-02 | Manual（`LiveManual`）押下（lock ＋ ack 待ち） | `WorkspaceFooterView.cs:58,70`→`FooterModeViewModel.cs:157` | `SwitchLockedLive`、`Locked=true`＋`PendingTarget`、表示は楽観 flip しない（拒否され得る）。poll が target に追いつくと lock 解放（`:105`） | `FooterModeE2ERunner`（m2）を昇格、lock→poll catch-up 解放を assert | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-03 | Auto（`LiveAuto`）押下（lock ＋ ack 待ち） | `WorkspaceFooterView.cs:59,70`→`FooterModeViewModel.cs:157` | FOOTER-02 と同じ `SwitchLockedLive` 経路（target=`LiveAuto`） | `FooterModeE2ERunner` の Live-lock 経路を `LiveAuto` でも assert | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-04 | venue 未接続で Live 押下 → 警告（RPC 送らず） | `FooterModeViewModel.cs:133` | `targetLive && !VenueLive` → `BlockedVenueNotLive`、Message="Connect a venue…"、`Locked` 不変、送信なし（TTWR observable no-op） | `FooterModeE2ERunner`（m2 DISCONNECTED）を昇格 | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-05 | LiveAuto run 稼働中に離脱（Replay/Manual へ） | `FooterModeViewModel.cs:143` | `DisplayMode==LiveAuto && target!=LiveAuto && hasActiveLiveAutoRun` → `StopRunThenSwitch`、`Locked=true`・表示は LiveAuto 維持。host が `stop_live_strategy` を先に呼び成功時のみ切替（D2） | `FooterModeE2ERunner`（m4・→Replay/→LiveManual 両方）を昇格 | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-06 | lock 中のセグメント再クリック抑止 | `WorkspaceFooterView.cs:105`（`btn.interactable=!locked`） | `Locked` 中は全セグメント `interactable=false`＋alpha 0.35（2nd クリックが engine 答えと race しない）。`Locked` 判定は VM | VM の `Locked` を assert（disable 描画は uGUI＝観測点） | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-07 | Manual/Auto セグメントの venue-gated 可視性 | `WorkspaceFooterView.cs:93`→`FooterModeViewModel.cs:72`-`73` | `ShowReplaySegment` 常時 true、`ShowManualAutoSegments=VenueLive`（TTWR `apply_venue_live_button_visibility_system`）。venue down で Manual/Auto を `SetActive(false)` | `FooterModeE2ERunner`（visibility）を昇格 | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-08 | Live 切替を engine が拒否 → lock 解放 | `FooterModeViewModel.cs:171` | `NotifyModeResult(false)` で `Locked=false`＋`PendingTarget=null`（poll が authoritative 表示を保つ）。成功は poll catch-up で解放 | `FooterModeE2ERunner`（m2 拒否経路）を昇格 | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-09 | venue drop while Live → auto-replay 要求 | `FooterModeViewModel.cs:112`（`ApplyPoll`） | poll で `!VenueLive && (LiveManual\|LiveAuto)` → `ShouldAutoReplay=true`（one-shot `ConsumeAutoReplay`）。venue drop は run を止めないので host は LiveAuto run があれば stop 先行（G1） | `FooterModeE2ERunner`（G1）を昇格、`ShouldAutoReplay`＋run 生存を assert | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-10 | 同一 mode 再選択 → no-op | `FooterModeViewModel.cs:127` | `target==DisplayMode`（or 空）→ `Ignore`、送信なし・`Locked` 不変 | `RequestMode` の Ignore 分岐を assert（要新規の薄い昇格） | 自動(E2E済) | `FooterModeE2ERunner` |
| FOOTER-11 | 実 venue で実 Live 切替（実 engine RPC） | `WorkspaceFooterView.cs:70`→host `SetExecutionMode` | 実 kabu/立花 接続中に `LiveManual`/`LiveAuto` 切替、`ModeManager` precondition 通過、status line が live run を表示 | — | HITL専用（実 venue 接続・実 engine `set_execution_mode` RPC・外部認証依存） | `FooterModeE2ERunner`（MOCK poll で判定のみ） |
| FOOTER-12 | テーマ切替で footer 再描画 | `WorkspaceFooterView.cs:117`（`ApplyTheme`） | bar/btn/text 色を `ThemeService.Current` で再塗り、`Refresh()` で選択ハイライト再導出 | — | 対象外（テーマ系の pure-UI 再描画・footer 入力ではない。テーマ回帰は別サーフェスが所有） | — |

> status line（`WorkspaceFooterView.cs:109` `StatusText`：`DisplayMode` / `"(switching…)"` / `"LiveAuto: <runId>"`）は
> 入力のない **read-only 表示**なので独立行にせず、FOOTER-01/02/05 の観測点として文字列を併せて確認する。
> 同様にセグメント選択ハイライト（`element_selected`）は FOOTER-01〜03 の観測点。

## 観測点（詳細）

- **D1 poll authority（FOOTER-01/02/03 の土台）**: `ApplyPoll`（`:88`）は `execution_mode` を自前の最小 DTO で
  parse し、楽観表示を**常に上書き**する（engine の `get_current_state` が `mode_manager.current_mode` を載せるのが
  正準）。Replay は engine が拒否不能なので即時 flip（D1 単一逸脱・desync しないのは poll が同値を返すから）。Live は
  拒否され得るので lock し poll/ack を待つ。
- **FOOTER-04（venue 未接続 Live）**: `VenueLive` は `venue_state` ∈ `{CONNECTED, SUBSCRIBED, RECONNECTING}`
  （badge flap 防止で RECONNECTING を含む）。Live target ＋ `!VenueLive` は warn-only で RPC を送らない。
- **FOOTER-05/09（D2 / G1 stop-then-switch）**: LiveAuto run 稼働中の離脱・venue drop の双方で「engine/venue は run を
  止めない」ため、footer/host が `stop_live_strategy(run_id)` を**先に**呼び成功時のみ mode 切替。これを怠ると Replay
  表示下に zombie run が残り reconnect で復活する（D2 が禁ずる orphan）。`hasActiveLiveAutoRun` は
  `LiveAutoTransportViewModel.HasActiveRun` から供給（VM 単一責務）。
- **FOOTER-08（拒否時 lock 解放）**: host が `SetExecutionMode`（or stop+switch）の結果を `NotifyModeResult(ok)` で
  返す。失敗で lock を解放し、表示は poll が authoritative に保つ。
- **要確認**: 実 `set_execution_mode` / `stop_live_strategy` の RPC 実体（host の `ProductionLiveShell` marshal）は
  本 Surface 台本の対象外。VM は判定のみ。

## 自動判定（合格条件）

- ログに `[E2E FOOTER MODE PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+`
  が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E FOOTER MODE FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus:
  - `ApplyPoll` の `DisplayMode = polled`（`:101`）を消すと FOOTER-01/02 の poll-overwrite assert が落ちる。
  - `RequestMode` の `if (DisplayMode==LiveAuto && ... hasActiveLiveAutoRun)` 分岐（`:143`）を消すと FOOTER-05 が落ちる。
  - `if (targetLive && !VenueLive)` ガード（`:133`）を消すと FOOTER-04 が落ちる。
  - `ShouldAutoReplay=true`（`:113`）を消すと FOOTER-09 が落ちる。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `FooterModeE2ERunner` | EditMode・pure 決定ロジック（Python-FREE） | FOOTER-01〜10 の正本。`FooterModeViewModel` 部分（D1 poll authority、Replay-immediate / Live-lock、visibility、BlockedVenueNotLive、D2 stop-then-switch、拒否時 lock 解放、G1 auto-replay）を E2ERunner へ昇格。`LiveAutoTransportViewModel`（▶ Start/Pause/Resume・double-press guard・G2）は **Live 運転コントロール側のサーフェス**の責務なので、本 footer-mode 台本では `hasActiveLiveAutoRun` 供給元としてのみ参照 |

## `FooterModeE2ERunner.cs` 実装方針（第二波・2本目）

> 型は findings 0054（ScenarioStartup）で確立。section は **Action ID ごとに人工分割せず自然な検証単位**で温存し、
> 各 section header に `Covers: FOOTER-xx` を明記する（E2E-CONVENTIONS.md「runner の section ↔ Action ID 対応方針」）。

- **昇格元 `FooterModeE2ERunner.cs` は footer-mode と Live 運転コントロールの 2 サーフェスをまたぐ**
  （ScenarioStartupProbe と違い 1:1 でない）。`git mv FooterModeE2ERunner.cs →
  Assets/Tests/E2E/Editor/FooterModeE2ERunner.cs`（.meta も移して GUID 保全）→ class 改名・最終サマリを
  `[E2E FOOTER MODE PASS/FAIL]` に・旧 Probe 削除（先例 ScenarioStartup）。
- **`FooterModeViewModel` ブロック（probe line 48-98）の `Check` body は 1 行も削らず温存**し、自然な検証単位ごとに
  `// === FOOTER-0X … (Covers: …) ===` で区切って Covers を明記（`Check`-counter 形は実証済みなので Execute 形へは
  書き換えない＝"温存"）。対応:
  - D1 poll authority（Replay→LiveAuto→Replay の overwrite）`Covers: FOOTER-01, FOOTER-02, FOOTER-03`
  - segment visibility（DISCONNECTED/CONNECTED の Manual/Auto 表示）`Covers: FOOTER-07`（VM 側）
  - Live-needs-venue（`BlockedVenueNotLive`）`Covers: FOOTER-04`
  - Live-lock ＋ poll catch-up 解放 `Covers: FOOTER-02, FOOTER-03`
  - rejection lock 解放（`NotifyModeResult(false)`）`Covers: FOOTER-08`
  - Replay immediate（D1 単一逸脱）`Covers: FOOTER-01`
  - D2 stop-then-switch（→Replay/→LiveManual）`Covers: FOOTER-05`
  - G1 venue-drop → `ShouldAutoReplay`（VM 側）`Covers: FOOTER-09`
  - FOOTER-10（同一 mode 再選択 = `Ignore`）は probe 未カバー → **薄い新規 Check を追加**。
- **FOOTER-06/07 の view 反映を薄い uGUI section で追加**: `WorkspaceFooterView` を bare `bar` RectTransform で
  `Build` 直呼びし、private `_modeSegs` の `Button.interactable`（lock 中 false）と Manual/Auto の
  `gameObject.activeSelf`（venue-gated）を反射確認。teardown は `DestroyImmediate`。root 合成も Python も不要。
- **`LiveAutoTransportViewModel` ブロック（probe line 100-173: ▶ Start/Pause/Resume・pre-flight gates・double-press
  guard・G2）は footer-mode サーフェス外**（Live 運転コントロールの責務・上「既存 Probe との対応」参照）。今 relocate せず
  **supporting pin として温存**（回帰網を落とさない）し、`// SUPPORTING PIN（Live 運転コントロール surface — 専用 runner
  著述時に移送）` と明記。FOOTER の Action 行には数えない。※G1 のうち `FooterModeViewModel.ShouldAutoReplay` は FOOTER-09
  として数え、run 生存（`LiveAutoTransportViewModel.HasActiveRun`）の確認は pin 側に属する。
- 実行: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod FooterModeE2ERunner.Run -logFile <log>`。
  compile-only は `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。**実走確認は Bash `grep -a "E2E FOOTER MODE"`**
  （Select-String も ripgrep Grep ツールも `→` 入り行を取りこぼす＝memory `unity-afk-probe-run`）。
