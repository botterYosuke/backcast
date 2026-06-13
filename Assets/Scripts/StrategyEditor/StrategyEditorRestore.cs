// StrategyEditorRestore.cs — issue #16 "Strategy Editor" (DURABLE tier, PURE CORE)
//
// The restore-boundary decision for a strategy_editor window's CONTENT (findings 0010 §7,
// owner-locked, full-replacement semantics). Kept UnityEngine-free so the AFK gate proves the
// document state transition directly; the view layers history-clear + InputField refresh on top.
//
// FULL REPLACEMENT (so the live document can never disagree with the applied layout):
//   * state == null            -> reset the editor to UNBOUND-EMPTY.
//   * state with a filePath    -> reset to unbound-empty FIRST, then Open(filePath).
//       - Open success         -> disk content, dirty=false (Open clears it).
//       - Open failure          -> stays UNBOUND-EMPTY; the window itself is NOT removed (that is
//                                 FloatingWindowController.Apply's job) and the persisted entry is
//                                 NOT dropped (LayoutStore keeps it).
// The reset is NOT a new-file creation and NOT a normal Open-failure path (which leaves a
// document unchanged): it is the restore-only "content-not-restored" state. Returns whether the
// editor ended up bound to a file (true) or unbound-empty (false) — for the view to refresh.

public static class StrategyEditorRestore
{
    public static bool Apply(StrategyDocument doc, StrategyEditorState state)
    {
        if (doc == null) return false;

        doc.ResetUnboundEmpty();
        if (state == null || string.IsNullOrEmpty(state.filePath)) return false;

        return doc.Open(state.filePath);   // failure leaves the document unbound-empty
    }
}
