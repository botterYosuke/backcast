# findings 0116 — #147 DriveSidebarContext が domain-reload-mid-Play で NRE（_footerMode 未ガード）

## 症状

エディタで **Play 中にスクリプトを再コンパイル（Domain Reload）** すると、`BackcastWorkspaceRoot.Update()`
が毎フレーム `DriveSidebarContext()` で `NullReferenceException` を吐き続ける。NRE が `Update` のそのフレームを
中断するため、後続 `DrivePrune` / `DriveDepthLadders`（サイドバー以降の毎フレーム処理）が全部スキップされる。

エディタ専用（ビルド済みプレイヤーには再コンパイル＝domain reload が無い）。本番クラッシュではないが運用
テスト中の開発ノイズ＋後続スキップの副作用があるため修正。

## 真因

`DriveSidebarContext()` が **誤ったフィールドをガード**していた：

```csharp
void DriveSidebarContext()
{
    if (_sidebarView == null) return;                       // ← serialized field をガード
    var mode = DockShape.IsLiveShape(_footerMode.DisplayMode) // ← 実際に deref するのは _footerMode
        ? UniverseSourceMode.Live : UniverseSourceMode.Replay;
    ...
}
```

Domain Reload は MonoBehaviour 状態をシリアライズ復元する：

| フィールド | 種別 | reload 後 |
|---|---|---|
| `_sidebarView` | `[SerializeField]`（Unity が serialize） | **非 null**（復元される） |
| `_footerMode` | `BuildWorkspace` 産 runtime（`FooterModeViewModel` は `[Serializable]` 無し） | **null** |
| `_host` / `_scenario` | `readonly = new …()` field initializer | **非 null**（再構築） |
| `_isOwner` | native `bool`（reload backup が private も保存） | 実質 true のまま |

→ `_isOwner` ゲートと `_host.*` no-op を通過し、唯一 null の `_footerMode.DisplayMode` で NRE。観測（NRE は
`DriveSidebarContext` の行だけ・兄弟は無傷）と一致。`DriveSidebarContext` は #141-145 / findings 0084 で
後追加されたメソッドで、ガードを `_footerMode` に揃え忘れていた（兄弟 `DriveFooter` / `RefreshLiveTiles` /
`DriveStrategyEditor` / `DriveOrderTicket` / `DriveRunResult` / `DriveDepthLadders` は全て runtime フィールド
ガード済み）。

## 修正（1 行）

`Assets/Scripts/Live/BackcastWorkspaceRoot.cs` の `DriveSidebarContext`：

```csharp
if (_sidebarView == null || _footerMode == null) return;
```

deref する runtime フィールドを兄弟と同じくガード。未構築/再構築直後は安全に no-op し、`_footerMode`
再構築後はサイドバー [+ Add] スコープ（mode + scenario.end）が従来どおり反映される。

## 回帰ゲート（RED→GREEN）

`Assets/Tests/E2E/Editor/SidebarReloadGuardE2ERunner.{cs,md}`（新設 slice runner・台帳 `SIDEBAR-RELOAD-01..03`
+ 04-HITL）。Python-FREE / scene-FREE：`BackcastWorkspaceRoot` を bare GameObject に `AddComponent`（edit-mode
ゆえ `Awake` 非走＝BuildWorkspace 未実行＝`_footerMode` null・field-initialized `_host`/`_scenario`）＝reload
直後・再 BuildWorkspace 前のフィールド状態と同型。`_sidebarView` のみ reflection で実体注入（serialize 復元
された非 null view を模す＝空虚 RED 回避に必須——sidebarView を null にすると旧ガードが早期 return して bug を
隠す）。

- **SIDEBAR-RELOAD-01**（核 RED→GREEN）: reload フィールド状態で `DriveSidebarContext()` を reflection
  invoke → 例外を投げない。空虚回避に `_sidebarView!=null` / `_footerMode==null` / `_host!=null` /
  `_scenario!=null` を先に assert（issue 記載の正確な状態を再現していることを保証）。
- **SIDEBAR-RELOAD-02**（機能非退行 / AC3）: `_footerMode = new FooterModeViewModel()` ＋ `ApplyPoll`
  LiveManual で再構築 → `DriveSidebarContext()` → view の `_mode` が Replay(default) → **Live**（非空虚:
  常時 return する過剰ガードなら Replay 残置で RED）。
- **SIDEBAR-RELOAD-03**（AC4）: reload フレーム（`_footerMode=null`・`_isOwner=true`・`_lastLadderLive=true`）で
  実 `Update()` を invoke → throw せず、末尾 `DriveDepthLadders` が `_lastLadderLive` を false に flip
  （＝チェーンが DriveSidebarContext で中断されず末尾まで到達）。修正前は Update が mid-chain で throw し true 残置。

### 実測（2026-06-26）

- **RED（修正前）**: `[E2E SIDEBAR RELOAD GUARD FAIL] DriveSidebarContext threw NullReferenceException in the
  reload state (_sidebarView non-null, _footerMode null) — issue #147 NRE: Object reference not set to an
  instance of an object`。
- **GREEN（修正後）**: `pwsh scripts/run-live-e2e.ps1 -Method SidebarReloadGuardE2ERunner.Run` →
  `[PASS] SIDEBAR-RELOAD-01 / -02 / -03`・`3 PASS / 0 FAIL`・exit 0。
- **AC5**: `pwsh scripts/run-live-e2e.ps1 -CompileOnly` → `[COMPILE PASS] no 'error CS'`・exit 0。

## 残置 HITL

`SIDEBAR-RELOAD-04-HITL`：実 Unity Editor で Play 中にスクリプト再コンパイルし、Console に `DriveSidebarContext`
由来 NRE が出ず [+ Add] が reload 復帰後も動くことの owner 目視。実 domain reload は headless batchmode では
発火しないため AFK 不可（field-state 再現で機械担保し、実 reload 経路のみ HITL）。

## レビュー（2026-06-26・4 軸 orchestrated）

実装後に専門 Agent 4 体で dead-code / simplify / regression / test-faithfulness を並行レビュー：

- **build**: `pwsh scripts/run-live-e2e.ps1 -CompileOnly` → exit 0・`error CS` 0 件（実測再確認）。
- **regression（correctness）**: fix は完全。唯一の真リスク＝兄弟 `DrivePrune` も `_footerMode.DisplayMode` を
  **ガードせず** deref（`:1468`）するが、`_pruneDriver`（field initializer 無し・`:114`）が reload 後 null で
  `:1460` で先に return するため reload フレームでは到達しない＝チェーン末尾 `DriveDepthLadders` まで真に到達。
  他の兄弟ドライバに同型の latent NRE 無し（全て no-initializer runtime field か `_host` 経由で reload-safe）。
- **dead-code / simplify**: 新規シンボルに未使用無し。fix は最小・兄弟 `DriveFooter`（`:2197`）と同一 idiom。
- **test-faithfulness**: 3 section とも実 `DriveSidebarContext` / `Update` を reflection invoke する faithful・
  非空虚ゲート（Section01 は fix revert で実 RED）。

レビューで検出し修正した点：

1. **High（rollup verdict-masking・修正済）**: 失敗 section は regex 非一致の umbrella タグ（空白入り
   `[E2E SIDEBAR RELOAD GUARD FAIL]`）しか出さず、`E2ERollup.ps1` の `\[E2E ([A-Z0-9][A-Z0-9-]*) (PASS|FAIL|SKIP)\]`
   に当たらない。Section01 PASS のまま Section02/03（AC3/AC4）が将来退行すると `run-live-e2e.ps1` が exit 0 を
   返し **false GREEN** になり得た。→ **per-section `Fail("SIDEBAR-RELOAD-0X", …)` タグ**＋ umbrella を
   ハイフン化（`[E2E SIDEBAR-RELOAD-GUARD FAIL]`・driver-catch path 用）で、どの失敗経路でも rollup が FAIL を見る。
2. **Low（修正済）**: production コメントが `RefreshLiveTiles` を `_footerMode` ガード兄弟と誤記（実際は
   `_footerMode` 不参照）→ `DriveOrderTicket` に訂正。
3. **Low（修正済）**: Section03 に `_sidebarView != null` 再 assert を追加（section 間で view が null 化される
   refactor に対する空虚 GREEN 防御）。

修正後 `pwsh scripts/run-live-e2e.ps1 -Method SidebarReloadGuardE2ERunner.Run` → `[PASS] SIDEBAR-RELOAD-01/02/03`・
`[E2E PASS]`・exit 0 を再確認。Medium+ 残 0。

## 教訓

**runtime フィールドを deref するドライバは「deref する当のフィールド」をガードせよ**——serialize 区分の
違う sibling フィールド（`_sidebarView` は serialized・`_footerMode` は runtime）でガードすると、domain reload
の「serialized は復元・runtime は null」非対称で早期 return が外れて NRE。後追加メソッドは兄弟ドライバの
ガード対象フィールドと突き合わせる（findings 0110 §7.3a の「persistence 区分が一致する sibling を鏡映」と同根）。
gate は **issue が記載するフィールド状態を一字一句再現**し（host/scenario 非 null・footerMode のみ null）、
空虚 RED 回避に「再現できているか」自体を assert する。
