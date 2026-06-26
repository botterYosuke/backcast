# SidebarReloadGuardE2ERunner — issue #147 release-gate slice 台本

`DriveSidebarContext()` が **domain-reload-mid-Play** のフレームで `NullReferenceException` を吐かないことを
正本化する slice runner。`.cs` が自動判定、本 `.md` が仕様・観測点・合格条件の正本。

## 何の挙動を保証するか（1 文の不変条件）

> エディタで Play 中にスクリプトを再コンパイル（Domain Reload）したフレーム —— `_sidebarView` は
> `[SerializeField]` ゆえシリアライズ復元され **非 null**、`_footerMode` は BuildWorkspace 産の runtime
> フィールドゆえ **null**、`_host` / `_scenario` は field initializer ゆえ **非 null** —— で、
> `BackcastWorkspaceRoot.Update()` の `DriveSidebarContext()` が **NRE を吐かず no-op** し、後続
> `DrivePrune` / `DriveDepthLadders`（チェーン末尾）まで Update が到達する。footerMode 再構築後は
> `(mode, scenario.end)` を sidebar view に再 push する（機能退行なし）。

## 原因（issue #147 / findings 0084 / findings 0116）

`DriveSidebarContext()` は `_sidebarView == null` だけをガードしていたが、実際に deref するのは
`_footerMode.DisplayMode`。`_sidebarView` は serialize 復元され reload を跨いで非 null のままなので最初の
ガードは通過し、null の `_footerMode` で初めて落ちる。兄弟ドライバ（`DriveFooter` / `RefreshLiveTiles` /
`DriveStrategyEditor` / `DriveOrderTicket` / `DriveRunResult` / `DriveDepthLadders`）は全て runtime
フィールドをガード済みで reload フレームでは安全に no-op する。修正は `DriveSidebarContext` を兄弟と同じく
`_footerMode` でガードすること（1 行）。

エディタ専用の事象（ビルド済みプレイヤーには再コンパイル＝domain reload が無い）。本番クラッシュではなく
運用テスト中の開発時ノイズだが、`Update` の後続スキップ（サイドバー以降の毎フレーム処理が全滅）の副作用が
あるため修正・gate 化する。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath <abs> \
        -executeMethod SidebarReloadGuardE2ERunner.Run -logFile <abs>
# expect: [E2E SIDEBAR RELOAD GUARD PASS] / exit=0
# rollup: pwsh scripts/run-live-e2e.ps1 -Method SidebarReloadGuardE2ERunner.Run
#   → [PASS] SIDEBAR-RELOAD-01 / -02 / -03
# compile-only ゲート（AC#5）: pwsh scripts/run-live-e2e.ps1 -CompileOnly → error CS\d+ 0 件
```

Python-FREE / scene-FREE: `BackcastWorkspaceRoot` を bare GameObject に `AddComponent` する（edit-mode ゆえ
`Awake` は走らない＝BuildWorkspace 未実行＝`_footerMode` null・field-initialized `_host`/`_scenario`）＝reload
直後・再 BuildWorkspace 前のフィールド状態と同型。`_sidebarView` のみ reflection で実体を注入し「serialize
復元された非 null view」を模す。

## 操作一覧表

| Action ID | 行動（観測する挙動） | 入口(file:line) | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| `SIDEBAR-RELOAD-01` | reload フレーム（sidebarView 非 null・footerMode null・host/scenario 非 null）で `DriveSidebarContext()` を駆動 → NRE を吐かず no-op | `BackcastWorkspaceRoot.DriveSidebarContext` (`Assets/Scripts/Live/BackcastWorkspaceRoot.cs`) | reflection invoke が例外を投げない。空虚回避に `_sidebarView!=null` / `_footerMode==null` / `_host!=null` / `_scenario!=null` を先に assert | `自動(E2E済)` | 自動 |
| `SIDEBAR-RELOAD-02` | `_footerMode` 再構築（LiveManual）後に `DriveSidebarContext()` → view へ (mode=Live) を再 push（機能退行なし・AC#3） | 同上 → `UniverseSidebarView.SetContext` | view の `_mode` が Replay(default) → Live に変わる（非空虚: 常時 return する過剰ガードは RED） | `自動(E2E済)` | 自動 |
| `SIDEBAR-RELOAD-03` | reload フレーム（footerMode null・isOwner true）で実 `Update()` を駆動 → 末尾 `DriveDepthLadders` まで到達（AC#4） | `BackcastWorkspaceRoot.Update` | 事前に `_lastLadderLive=true` をセット → Update 後 false（DriveDepthLadders が走った＝チェーン非中断）。修正前は Update が mid-chain で throw し true 残置 | `自動(E2E済)` | 自動 |
| `SIDEBAR-RELOAD-04-HITL` | 実エディタで Play 中にスクリプト再コンパイル → Console に `DriveSidebarContext` 由来の NRE が出ず、サイドバー [+ Add] が reload 復帰後も動く | （実 Unity Editor・Play→edit→recompile） | owner 目視: Console に NRE 連発が無い・[+ Add] スコープ正常 | — | `HITL専用`（実 domain reload は headless batchmode では発火しない） |

## RED→GREEN litmus

- **RED（修正前）**: `SIDEBAR-RELOAD-01` が `DriveSidebarContext threw NullReferenceException ... issue #147 NRE`
  で FAIL（実測 2026-06-26）。`SIDEBAR-RELOAD-03` も Update が mid-chain で throw し `_lastLadderLive` true 残置。
- **GREEN（修正後）**: `if (_sidebarView == null || _footerMode == null) return;` に変更 → 3 section PASS。
- **逆 litmus**: ガードを `_footerMode` 抜き（`_sidebarView` のみ）へ戻すと 01/03 が RED。ガードを「常時 return」に
  し過ぎると 02 が RED（view に push されない）。

## AC 対応

- AC1（footerMode ガード・null 間 no-op）＝ SIDEBAR-RELOAD-01
- AC2（reload しても DriveSidebarContext 由来 NRE が出ない）＝ SIDEBAR-RELOAD-01（実機目視は -04-HITL）
- AC3（再構築後 [+ Add] スコープが従来どおり反映＝機能退行なし）＝ SIDEBAR-RELOAD-02
- AC4（reload 後フレームで Update が中断されず DrivePrune / DriveDepthLadders まで到達）＝ SIDEBAR-RELOAD-03
- AC5（`-CompileOnly` PASS）＝ compile-only ゲート（error CS 0 件・実測 PASS）
