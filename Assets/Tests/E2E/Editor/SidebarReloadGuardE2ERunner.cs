// SidebarReloadGuardE2ERunner.cs — issue #147 release-gate slice (台本: SidebarReloadGuardE2ERunner.md).
// 回帰ガード: エディタで Play 中にスクリプトを再コンパイル（Domain Reload）したフレームを再現し、
// BackcastWorkspaceRoot.DriveSidebarContext() が NullReferenceException を吐かない（≒後続 DrivePrune /
// DriveDepthLadders まで Update が到達する）ことを pin する。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod SidebarReloadGuardE2ERunner.Run -logFile <abs>
//   # expect: [E2E SIDEBAR RELOAD GUARD PASS] ... / exit=0
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// 何を再現するか（issue #147 / findings 0116。DriveSidebarContext 自体の出自は findings 0084）:
//   Domain Reload は MonoBehaviour の状態をシリアライズ復元する。
//     * `_sidebarView`（[SerializeField]）→ Unity がシリアライズ → reload を跨いで NON-null のまま復元。
//     * `_footerMode`（BuildWorkspace で生成・[Serializable] 無しの runtime フィールド）→ reload 後 null。
//     * `_host` / `_scenario`（readonly = new …() field initializer）→ 再構築され NON-null。
//   旧 DriveSidebarContext は `_sidebarView == null` だけをガードし、実際に deref する `_footerMode` を
//   ガードしないので、上の状態（sidebarView 非 null・footerMode null）で `_footerMode.DisplayMode` が NRE。
//   兄弟ドライバ（DriveFooter / RefreshLiveTiles / DriveStrategyEditor / DriveOrderTicket / DriveRunResult /
//   DriveDepthLadders）は全て runtime フィールド（_footerMode / _windows / _dockWindows / …）でガード済みで、
//   reload フレームでは安全に no-op する —— このゲートは「DriveSidebarContext も兄弟と同じく no-op する」を pin。
//
// Python-FREE / scene-FREE: BackcastWorkspaceRoot を bare GameObject に AddComponent する（edit-mode なので
// Awake は走らない＝BuildWorkspace 未実行＝footerMode null・field initializer 済の _host/_scenario）。これは
// reload 直後・再 BuildWorkspace 前のフレームのフィールド状態と同型。`_sidebarView` のみ reflection で実体を
// 注入して「シリアライズ復元された非 null view」を模す（空虚 RED 回避＝早期 return で bug を隠さないため必須）。

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class SidebarReloadGuardE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        string fail;
        try { fail = RunAll(); }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E SIDEBAR RELOAD GUARD PASS] domain-reload-mid-Play フレーム（_sidebarView 非 null・"
                    + "_footerMode null・_host/_scenario 非 null）で DriveSidebarContext が NRE せず no-op し、"
                    + "Update が DriveDepthLadders（チェーン末尾）まで到達する。footerMode 再構築後は (mode,end) を "
                    + "view に再 push（機能退行なし）。issue #147 / findings 0116。");
            EditorApplication.Exit(0);
        }
        else
        {
            // Emit a hyphenated (regex-matching) Action-ID FAIL token so the rollup (E2ERollup.ps1's
            // `\[E2E ([A-Z0-9][A-Z0-9-]*) (PASS|FAIL|SKIP)\]`) always sees a FAIL — the per-section
            // Fail() tags cover section-returned failures; this umbrella covers the driver-catch path
            // (an unexpected throw) so no failure mode can leave only spaced-token text and read as GREEN.
            Debug.LogError("[E2E SIDEBAR-RELOAD-GUARD FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // Tag a section failure with a rollup-matching per-section FAIL token (rollup ranks FAIL > PASS, so a
    // Section02/03 regression that leaves Section01 passing still flips the merged verdict to FAIL).
    static string Fail(string actionId, string msg)
    {
        Debug.LogError("[E2E " + actionId + " FAIL] " + msg);
        return msg;
    }

    static string RunAll()
    {
        var rootGo = new GameObject("sidebar_reload_guard_root", typeof(BackcastWorkspaceRoot));
        var viewGo = new GameObject("sidebar_reload_guard_view", typeof(RectTransform), typeof(UniverseSidebarView));
        try
        {
            var root = rootGo.GetComponent<BackcastWorkspaceRoot>();
            var view = viewGo.GetComponent<UniverseSidebarView>();
            var ty = typeof(BackcastWorkspaceRoot);

            // Inject the serialized-and-restored sidebar view onto the runtime-fresh root (mirrors the
            // reload state where _sidebarView survives but _footerMode does not).
            var sidebarF = ty.GetField("_sidebarView", BF);
            if (sidebarF == null) return "field _sidebarView missing (renamed?) — cannot reproduce reload state";
            sidebarF.SetValue(root, view);

            string r;
            if ((r = Section01_DriveSidebarContextNoNreInReloadState(root, ty)) != null) return Fail("SIDEBAR-RELOAD-01", r);
            if ((r = Section02_RebuiltFooterModePushesContext(root, ty, view)) != null) return Fail("SIDEBAR-RELOAD-02", r);
            if ((r = Section03_UpdateReachesTailAfterReload(root, ty)) != null) return Fail("SIDEBAR-RELOAD-03", r);
            return null;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(viewGo);
            UnityEngine.Object.DestroyImmediate(rootGo);
        }
    }

    // ---- 01. the bug: DriveSidebarContext() must NOT throw when _footerMode is null (reload runtime field)
    //         while _sidebarView is non-null (serialized, restored). RED before the fix (NRE on
    //         `_footerMode.DisplayMode`), GREEN after the guard is moved to _footerMode. ----
    // Covers: SIDEBAR-RELOAD-01
    static string Section01_DriveSidebarContextNoNreInReloadState(BackcastWorkspaceRoot root, Type ty)
    {
        var footerF = ty.GetField("_footerMode", BF);
        var sidebarF = ty.GetField("_sidebarView", BF);
        var hostF = ty.GetField("_host", BF);
        var scenarioF = ty.GetField("_scenario", BF);
        if (footerF == null) return "field _footerMode missing (renamed?) — cannot pin the reload NRE";
        if (sidebarF == null || hostF == null || scenarioF == null) return "field _sidebarView/_host/_scenario missing (renamed?)";

        // Reproduce the EXACT reload field state described in #147: the deref'd runtime field is null.
        footerF.SetValue(root, null);   // null after reload (the sole NRE trigger)

        // Non-vacuity premise: the OTHER fields the method touches are NON-null (else the old guard would
        // early-return on _sidebarView and hide the bug, or the NRE would come from _host/_scenario instead
        // of _footerMode). _sidebarView is injected in RunAll; _host/_scenario are field-initialized.
        if (sidebarF.GetValue(root) == null)
            return "vacuity: _sidebarView is null — the OLD guard would early-return and hide the bug (not the reload state)";
        if (hostF.GetValue(root) == null)
            return "vacuity: _host is null — issue #147 requires _host non-null (field initializer survives reload)";
        if (scenarioF.GetValue(root) == null)
            return "vacuity: _scenario is null — issue #147 requires _scenario non-null (field initializer survives reload)";

        var drive = ty.GetMethod("DriveSidebarContext", BF);
        if (drive == null) return "method DriveSidebarContext missing (renamed?) — cannot drive the reload frame";

        try { drive.Invoke(root, null); }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            // This is the #147 regression: the method derefs _footerMode without guarding it.
            return "DriveSidebarContext threw " + inner.GetType().Name
                 + " in the reload state (_sidebarView non-null, _footerMode null) — issue #147 NRE: " + inner.Message;
        }

        Debug.Log("[E2E SIDEBAR-RELOAD-01 PASS] DriveSidebarContext() no-ops (no NRE) when _footerMode is null "
                + "and _sidebarView is non-null — the domain-reload-mid-Play field state.");
        return null;
    }

    // ---- 02. functional non-regression (AC#3): once _footerMode is rebuilt, DriveSidebarContext must
    //         again push (mode, scenario.end) into the view via SetContext. Non-vacuous: the view default
    //         is Replay, so a LiveManual footer must flip the view's _mode to Live (a guard that ALWAYS
    //         returns — over-correcting the fix — would leave it Replay and FAIL here). ----
    // Covers: SIDEBAR-RELOAD-02
    static string Section02_RebuiltFooterModePushesContext(BackcastWorkspaceRoot root, Type ty, UniverseSidebarView view)
    {
        var footerF = ty.GetField("_footerMode", BF);
        var fm = new FooterModeViewModel();
        fm.ApplyPoll("{\"execution_mode\":\"LiveManual\"}");   // DisplayMode → LiveManual (a Live shape)
        if (fm.DisplayMode != FooterModeViewModel.LiveManual)
            return "setup: ApplyPoll did not set DisplayMode to LiveManual (got '" + fm.DisplayMode + "')";
        footerF.SetValue(root, fm);

        // sanity: the view starts in its Bind-time default Replay (so a Live push is observable).
        var modeF = typeof(UniverseSidebarView).GetField("_mode", BF);
        if (modeF == null) return "view field _mode missing (renamed?) — cannot observe the pushed context";
        if ((UniverseSourceMode)modeF.GetValue(view) != UniverseSourceMode.Replay)
            return "setup: view _mode did not start at Replay default (got " + modeF.GetValue(view) + ")";

        var drive = ty.GetMethod("DriveSidebarContext", BF);
        try { drive.Invoke(root, null); }
        catch (TargetInvocationException tie) { return "DriveSidebarContext threw with a rebuilt _footerMode: " + (tie.InnerException ?? tie); }

        var pushed = (UniverseSourceMode)modeF.GetValue(view);
        if (pushed != UniverseSourceMode.Live)
            return "DriveSidebarContext did not push the live mode into the view (view _mode=" + pushed
                 + ", expected Live for a LiveManual footer) — the [+ Add] scope would be wrong after reload recovery";

        Debug.Log("[E2E SIDEBAR-RELOAD-02 PASS] after _footerMode rebuild, DriveSidebarContext re-pushes "
                + "(mode=Live) into the sidebar view via SetContext — [+ Add] scope reflects the live mode.");
        return null;
    }

    // ---- 03. AC#4: in the reload frame, the real Update() must run end-to-end — DriveSidebarContext no
    //         longer interrupts the chain, so DrivePrune / DriveDepthLadders (the tail) are reached. We
    //         pre-flip _lastLadderLive=true; a reload frame (footerMode null → isLive=false) drives
    //         DriveDepthLadders which flips it back to false ONLY if Update reached the tail. Non-vacuous:
    //         before the fix Update throws mid-chain at DriveSidebarContext and _lastLadderLive stays true. ----
    // Covers: SIDEBAR-RELOAD-03
    static string Section03_UpdateReachesTailAfterReload(BackcastWorkspaceRoot root, Type ty)
    {
        var footerF = ty.GetField("_footerMode", BF);
        var ownerF = ty.GetField("_isOwner", BF);
        var ladderF = ty.GetField("_lastLadderLive", BF);
        var sidebarF = ty.GetField("_sidebarView", BF);
        if (ownerF == null) return "field _isOwner missing (renamed?) — cannot pass the Update owner gate";
        if (ladderF == null) return "field _lastLadderLive missing (renamed?) — cannot observe the Update tail";

        // Non-vacuity: _sidebarView must still be the injected non-null view. If a refactor cleared it
        // between sections, the OLD `_sidebarView == null` guard would early-return, Update would complete,
        // the tail would clear the sentinel, and this section would pass WITHOUT exercising the #147 path.
        if (sidebarF == null || sidebarF.GetValue(root) == null)
            return "vacuity: _sidebarView is null in Section03 — the OLD guard would early-return and hide the #147 path";

        // Reset to the reload frame: footerMode null again, owner true (a native bool survives the reload
        // backup, so Update passes the `if (!_isOwner) return;` gate — exactly the reported condition).
        footerF.SetValue(root, null);
        ownerF.SetValue(root, true);
        ladderF.SetValue(root, true);   // sentinel: the tail driver must clear this

        var update = ty.GetMethod("Update", BF);
        if (update == null) return "method Update missing (renamed?) — cannot drive the reload frame";
        try { update.Invoke(root, null); }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            return "Update() threw " + inner.GetType().Name + " in the reload frame (issue #147: DriveSidebarContext "
                 + "NRE aborts the frame before DrivePrune/DriveDepthLadders): " + inner.Message;
        }

        if ((bool)ladderF.GetValue(root))
            return "Update() did not reach DriveDepthLadders (_lastLadderLive still true) — the chain was interrupted "
                 + "before the tail (DrivePrune/DriveDepthLadders), the #147 side effect";

        Debug.Log("[E2E SIDEBAR-RELOAD-03 PASS] the real Update() runs end-to-end in the reload frame — "
                + "DriveSidebarContext no-ops and the chain reaches DriveDepthLadders (_lastLadderLive cleared).");
        return null;
    }
}
