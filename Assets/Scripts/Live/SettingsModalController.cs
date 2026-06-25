// SettingsModalController.cs — issue #125 (ADR-0026): Settings modal open/close + ESC-guard logic.
//
// The Settings modal is the screen-fixed 集約口 for Venue 接続 / 実行モード切替 / Scenario Startup
// (ADR-0026, supersedes ADR-0005 の表面配置 in part). This controller owns ONLY the open/close state
// and the ESC dispatch GUARD — the chrome (SettingsModalOverlay) and the three section views
// (Venue / Mode / Scenario) are separate. Pure (no UnityEngine) so the AFK probe drives it headlessly,
// mirroring SecretModalController / SaveGuardController.
//
// ESC dispatch contract (ADR-0026 §25 / findings 0102 D1) — priority order, queried fresh each press
// so it can NEVER invert:
//   1. window drag in progress      → DeferToDrag           (ADR-0024 §8 drag-revert owns ESC; Settings stays put)
//   2. secret(1000) / save-guard open → ConsumedByBlockingModal (the blocking modal owns ESC; Settings does NOT open behind it)
//   3. otherwise                    → Toggled               (open ↔ close). The [x] button calls Close() directly.

public enum SettingsEscDecision { DeferToDrag, ConsumedByBlockingModal, Toggled }

public sealed class SettingsModalController
{
    public bool IsOpen { get; private set; }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    // Guarded ESC toggle. The host re-queries dragInProgress / blockingModalOpen on every press, so the
    // priority (drag-revert > blocking modal > Settings toggle) holds regardless of Update() ordering
    // between MonoBehaviours: the drag latch (FloatingWindowController._drag != null) outlives the
    // drag-revert CancelDrag within the same frame, so a mid-drag ESC always resolves to DeferToDrag.
    public SettingsEscDecision OnEscape(bool dragInProgress, bool blockingModalOpen)
    {
        if (dragInProgress) return SettingsEscDecision.DeferToDrag;
        if (blockingModalOpen) return SettingsEscDecision.ConsumedByBlockingModal;
        IsOpen = !IsOpen;
        return SettingsEscDecision.Toggled;
    }
}
