// SettingsAutoCloseController.cs — issue #171 (ADR-0039 / findings 0127): auto-close the Settings modal
// when a SINGLE-ACTION section's operation confirms success.
//
// ADR-0026's Settings 集約口 hosts 5 sections in 2 tabs. The 単発アクション系 sections — Venue 接続/切断,
// 実行モード切替 (Replay/LiveManual/LiveAuto), 外観テーマ (Dark/Light) — are commit-and-go: once the action
// CONFIRMS, the chrome should get out of the way. The フォーム系 sections (Scenario Startup / Data) are 据え置き
// (edit→Save As, no single success boundary), so they never route through here.
//
// This is the PURE C# policy seam ADR-0039 requires (host 直書きにしない): it OWNS the decision of whether a
// given action attempt + resulting state is a confirmed success worth closing on, so the AFK probe can drive
// every branch headlessly (mirrors SettingsModalController / SecretModalController — no UnityEngine).
//
// 「確定成功」splits sync vs async (findings 0127 D2):
//   * SYNC   — 外観テーマ, モード Replay (engine can't reject = SwitchImmediate) → close at click (CloseNow).
//   * ASYNC  — モード Manual/Auto (SwitchLockedLive / StopRunThenSwitch lock→poll), Venue Connect (venue_state
//              live 化した poll), Venue Disconnect (非 live 化した poll) → LATCH this click's goal and close only
//              on the poll that reaches it EXACTLY (Wait).
//
// 失敗・拒否・取消・no-op・auto-replay 巻き込み は閉じない (findings 0127 D3): NotifyFailed() drops the latched
// goal so a later confirming poll can't fire; a no-op returns Stay; and a Live mode goal additionally requires the
// venue to be live at the confirming poll, so a venue-drop→auto-replay poll (which momentarily shows
// DisplayMode==target with the lock released) is NOT mistaken for success.

public enum SettingsCloseDecision
{
    Stay,      // no-op / blocked / handled-elsewhere → keep Settings open (any error stays visible)
    CloseNow,  // synchronous confirmed success → host closes immediately
    Wait,      // async dispatched → goal latched; host closes on the confirming poll (OnPoll → true)
}

public sealed class SettingsAutoCloseController
{
    // The async goal this Settings click is waiting on. One goal at a time — a new click overwrites it
    // (only one 単発アクション can be in flight from a modal whose other segments dim/disable while locked).
    public enum Goal { None, Mode, VenueLive, VenueDown }

    Goal _goal = Goal.None;
    string _goalMode;        // for Goal.Mode: the target execution mode ("LiveManual" | "LiveAuto" | "Replay")

    // A Goal.Mode whose target is a LIVE mode → the confirming poll must also see the venue live (derived from
    // _goalMode so the two can never desync). For venue/None goals _goalMode is null → false.
    bool GoalModeIsLive => _goal == Goal.Mode
        && (_goalMode == FooterModeViewModel.LiveManual || _goalMode == FooterModeViewModel.LiveAuto);

    // Probe / host observability (no behaviour). The host gates a redundant Close on SettingsModalController.IsOpen
    // (not on these); these exist so the AFK probe can prove WHICH goal was latched without poking privates.
    public bool IsWaiting => _goal != Goal.None;
    public Goal PendingGoal => _goal;

    void Disarm() { _goal = Goal.None; _goalMode = null; }

    // ── 外観テーマ (sync): SetTheme/Save never fail, so close iff the theme actually CHANGED. A no-op
    //    (re-selecting the active theme) returns Stay — "成功" = "実際に切り替わったとき" だけ (findings 0127 D3). ──
    public SettingsCloseDecision OnThemeSelected(bool changed)
    {
        if (!changed) return SettingsCloseDecision.Stay;
        Disarm();
        return SettingsCloseDecision.CloseNow;
    }

    // ── 実行モード切替: translate the pure FooterModeViewModel.RequestMode verdict into a close decision. ──
    //    Replay (SwitchImmediate) is synchronous (engine can't reject) → CloseNow. A Live target (lock→poll, or
    //    leaving LiveAuto via StopRunThenSwitch) latches Goal.Mode → Wait. Ignore (no-op) / BlockedVenueNotLive
    //    (venue not connected — handled with an error message, never sent) → Stay.
    public SettingsCloseDecision OnModeRequest(FooterModeRequestKind kind, string target)
    {
        switch (kind)
        {
            case FooterModeRequestKind.SwitchImmediate:
                Disarm();
                return SettingsCloseDecision.CloseNow;
            case FooterModeRequestKind.SwitchLockedLive:
            case FooterModeRequestKind.StopRunThenSwitch:
                _goal = Goal.Mode;
                _goalMode = target;
                return SettingsCloseDecision.Wait;
            default:   // Ignore / BlockedVenueNotLive
                return SettingsCloseDecision.Stay;
        }
    }

    // ── Venue Connect/Disconnect: latch the liveness goal (the host has already dispatched the login/logout).
    //    Connect via a second password keeps this goal latched across the secret modal — it closes only after
    //    login completes AND the venue goes live (a password cancel / login failure never reaches venue-live,
    //    and NotifyFailed() drops the goal on an explicit failure). ──
    public void ArmVenueConnect() { _goal = Goal.VenueLive; _goalMode = null; }
    public void ArmVenueDisconnect() { _goal = Goal.VenueDown; _goalMode = null; }

    // ── 失敗・拒否・取消: drop the latched goal so no later poll can close. The lock release + error display
    //    keep Settings open so the user can see the failure and retry (findings 0127 D3). Also called when the
    //    user manually closes Settings mid-flight so an abandoned latch can't auto-close a later re-open. ──
    public void NotifyFailed() => Disarm();

    // ── auto-replay 巻き込み (findings 0127 D3, review fix): a venue drop that yanks a pending LIVE-mode switch
    //    back to Replay makes that Live target unreachable → drop the latch. This is SURGICAL: a Goal.VenueDown
    //    is FULFILLED by the same drop (disconnect succeeded) and a Replay-target Goal.Mode still reaches Replay
    //    via the fallback — NEITHER must be disarmed here, so this only drops a live-mode goal. (The OnPoll
    //    venueLive guard separately blocks the venue-drop poll for the deferred-frame window before this runs.) ──
    public void NotifyLiveModeAbandoned()
    {
        if (GoalModeIsLive) Disarm();
    }

    // ── poll ingest: returns true EXACTLY when the latched goal is reached — never on an unrelated poll or a
    //    state change from another path. The host feeds the fresh poll-derived state (FooterModeViewModel
    //    DisplayMode/Locked + VenueConnectionViewModel.IsConnected). ──
    public bool OnPoll(string displayMode, bool modeLocked, bool venueLive)
    {
        switch (_goal)
        {
            case Goal.Mode:
                // Confirmed when the lock releases AND the display reached the target. For a LIVE target the
                // venue must ALSO be live: a venue-drop poll momentarily shows DisplayMode==target with the lock
                // cleared (FooterModeViewModel.ApplyPoll releases it on polled==PendingTarget) while VenueLive is
                // false and ShouldAutoReplay arms — that is the auto-replay failure, not success, so it must NOT
                // close (findings 0127 D3 auto-replay 巻き込み).
                if (!modeLocked && displayMode == _goalMode && (!GoalModeIsLive || venueLive))
                {
                    Disarm();
                    return true;
                }
                return false;
            case Goal.VenueLive:
                if (venueLive) { Disarm(); return true; }
                return false;
            case Goal.VenueDown:
                if (!venueLive) { Disarm(); return true; }
                return false;
            default:
                return false;
        }
    }
}
